using B3.EntryPoint.Client.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.MarketMakerBot.Tests;

/// <summary>
/// Exercises <see cref="MarketMakerWorker"/>'s event-handling logic
/// through the internal <see cref="MarketMakerWorker.HandleEventAsync"/>/
/// <see cref="MarketMakerWorker.QuoteSideAsync"/> seam (see #709) against
/// a <see cref="FakeEntryPointClient"/>. This is the deterministic
/// coverage that #707's investigation had to substitute with a live
/// Docker soak test before <c>IEntryPointClient</c> was adopted here.
/// </summary>
public class MarketMakerWorkerTests
{
    private static (MarketMakerWorker Worker, OrderTracker Tracker, FakeEntryPointClient Client, InstrumentConfig Instrument) CreateWorker()
    {
        var instrument = new InstrumentConfig
        {
            Symbol = "PETR4",
            SecurityId = 1,
            RefPrice = 30m,
            TickSize = 0.01m,
            LotSize = 100,
            QuoteLots = 1,
            SpreadTicks = 5,
        };
        var options = new MarketMakerBotOptions
        {
            EnteringFirm = 1,
            SessionId = 1,
            AccessKey = "test",
            Instruments = [instrument],
        };
        var tracker = new OrderTracker();
        var priceTracker = new MarketPriceTracker();
        var loggerFactory = NullLoggerFactory.Instance;
        var marketData = new MarketDataFeed(priceTracker, NullLogger.Instance);
        var worker = new MarketMakerWorker(Options.Create(options), tracker, priceTracker, marketData,
            loggerFactory, NullLogger<MarketMakerWorker>.Instance);
        return (worker, tracker, new FakeEntryPointClient(), instrument);
    }

    [Fact]
    public async Task QuoteSideAsync_SubmitsOneRestingOrderPerSide()
    {
        var (worker, tracker, client, instrument) = CreateWorker();

        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        await worker.QuoteSideAsync(client, instrument, isBuy: false, CancellationToken.None);

        Assert.Equal(2, client.SubmittedOrders.Count);
        Assert.True(tracker.HasOpenSide("PETR4", isBuy: true));
        Assert.True(tracker.HasOpenSide("PETR4", isBuy: false));
    }

    [Fact]
    public async Task QuoteSideAsync_SideAlreadyOpen_DoesNotSubmitDuplicate()
    {
        var (worker, _, client, instrument) = CreateWorker();
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);

        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);

        Assert.Single(client.SubmittedOrders);
    }

    [Fact]
    public async Task HandleEventAsync_OrderAccepted_NullLeavesQty_DoesNotFreeReservation()
    {
        // Regression test for #707: the real venue's OrderAccepted
        // (ExecType=New) omits LeavesQty entirely. This must NOT be
        // misread as "fully filled" — the reservation must stay held so
        // a subsequent quote attempt for the same side is a no-op,
        // instead of submitting a duplicate order alongside the still
        // -resting original.
        var (worker, tracker, client, instrument) = CreateWorker();
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var clOrdId = client.SubmittedOrders[0].ClOrdID.Value;

        await worker.HandleEventAsync(client, new OrderAccepted
        {
            ClOrdID = new ClOrdID(clOrdId),
            OrderId = 100,
            SecurityId = instrument.SecurityId,
            Side = Side.Buy,
            OrderStatus = OrderStatus.New,
            LeavesQty = null,
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.True(tracker.HasOpenSide("PETR4", isBuy: true));

        // The reconcile loop's own gate (!HasOpenSide) would now
        // correctly skip re-quoting this side — simulate that directly:
        // a second QuoteSideAsync call must not submit a duplicate.
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        Assert.Single(client.SubmittedOrders);
    }

    [Fact]
    public async Task HandleEventAsync_OrderTrade_FilledWithNullLeaves_ClosesAndRequotes()
    {
        var (worker, tracker, client, instrument) = CreateWorker();
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var clOrdId = client.SubmittedOrders[0].ClOrdID.Value;

        await worker.HandleEventAsync(client, new OrderTrade
        {
            ClOrdID = new ClOrdID(clOrdId),
            OrderId = 100,
            TradeId = 1,
            OrderStatus = OrderStatus.Filled,
            LastPx = 30m,
            LastQty = 100,
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
            LeavesQty = null,
        }, CancellationToken.None);

        // Closed and immediately re-quoted: the side is open again but
        // via a NEW ClOrdId (the fresh resting order), not the old one.
        Assert.True(tracker.HasOpenSide("PETR4", isBuy: true));
        Assert.Equal(2, client.SubmittedOrders.Count);
        Assert.NotEqual(clOrdId, client.SubmittedOrders[1].ClOrdID.Value);
    }

    [Fact]
    public async Task HandleEventAsync_OrderTrade_PartiallyFilledWithNullLeaves_StaysOpenWithoutRequote()
    {
        // Regression guard mirroring the OrderAccepted case: a
        // PartiallyFilled trade with an absent LeavesQty must not be
        // misread as "fully filled" either.
        var (worker, tracker, client, instrument) = CreateWorker();
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var clOrdId = client.SubmittedOrders[0].ClOrdID.Value;

        await worker.HandleEventAsync(client, new OrderTrade
        {
            ClOrdID = new ClOrdID(clOrdId),
            OrderId = 100,
            TradeId = 1,
            OrderStatus = OrderStatus.PartiallyFilled,
            LastPx = 30m,
            LastQty = 50,
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
            LeavesQty = null,
        }, CancellationToken.None);

        Assert.True(tracker.HasOpenSide("PETR4", isBuy: true));
        Assert.Single(client.SubmittedOrders); // no requote — still the same resting order.
    }

    [Fact]
    public async Task HandleEventAsync_OrderModified_DoesNotFreeReservationOrRequote()
    {
        // The bot never sends ReplaceOrderRequest, so any OrderModified
        // it receives is unsolicited from its own perspective — it must
        // be treated as a no-op for tracker state (see
        // MarketMakerWorker.HandleEventAsync's OrderModified case).
        var (worker, tracker, client, instrument) = CreateWorker();
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var clOrdId = client.SubmittedOrders[0].ClOrdID.Value;

        await worker.HandleEventAsync(client, new OrderModified
        {
            ClOrdID = new ClOrdID(clOrdId),
            OrigClOrdID = new ClOrdID(clOrdId),
            OrderId = 100,
            OrderStatus = OrderStatus.Replaced,
            LeavesQty = null,
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.True(tracker.HasOpenSide("PETR4", isBuy: true));
        Assert.Single(client.SubmittedOrders);
    }
}
