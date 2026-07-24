using B3.EntryPoint.Client.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace B3.Trading.MarketMakerBot.Tests;

/// <summary>
/// Exercises <see cref="MarketMakerWorker"/>'s event-handling logic
/// through the internal <see cref="MarketMakerWorker.HandleEventAsync"/>/
/// <see cref="MarketMakerWorker.QuoteSideAsync"/> seam (see #709) against
/// a <see cref="FakeEntryPointClient"/>. This is the deterministic
/// coverage that #707's investigation had to substitute with a live
/// Docker soak test before <c>IEntryPointClient</c> was adopted here.
/// </summary>
public class MarketMakerWorkerTests : IDisposable
{
    private readonly List<MarketMakerMetrics> _metrics = [];

    public void Dispose()
    {
        foreach (var metrics in _metrics)
            metrics.Dispose();
    }

    private (MarketMakerWorker Worker, OrderTracker Tracker, FakeEntryPointClient Client, InstrumentConfig Instrument)
        CreateWorker() => CreateWorker(out _);

    private (MarketMakerWorker Worker, OrderTracker Tracker, FakeEntryPointClient Client, InstrumentConfig Instrument)
        CreateWorker(out MarketMakerPnlLedger pnlLedger, Action<MarketMakerBotOptions>? configure = null)
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
        configure?.Invoke(options);
        var tracker = new OrderTracker();
        var priceTracker = new MarketPriceTracker();
        pnlLedger = new MarketMakerPnlLedger();
        var metrics = new MarketMakerMetrics(pnlLedger, priceTracker, Options.Create(options));
        _metrics.Add(metrics);
        var loggerFactory = NullLoggerFactory.Instance;
        var marketData = new MarketDataFeed(priceTracker, NullLogger.Instance);
        var worker = new MarketMakerWorker(Options.Create(options), tracker, priceTracker, pnlLedger, metrics,
            marketData, loggerFactory, NullLogger<MarketMakerWorker>.Instance);
        return (worker, tracker, new FakeEntryPointClient(), instrument);
    }

    /// <summary>
    /// Variant of <see cref="CreateWorker"/> for
    /// <see cref="MarketMakerWorker.CancelStaleOrdersAsync"/>/<see
    /// cref="MarketMakerWorker.ReactToBookChangeAsync"/> tests: takes an
    /// explicit <see cref="TimeProvider"/> (wired into the
    /// <see cref="OrderTracker"/>, P&amp;L ledger, and price tracker) and
    /// an <paramref name="configure"/> callback for the staleness/requote
    /// tunables those two methods depend on, and also hands back the
    /// <see cref="MarketPriceTracker"/> so tests can move the live
    /// reference price.
    /// </summary>
    private (MarketMakerWorker Worker, OrderTracker Tracker, FakeEntryPointClient Client,
        InstrumentConfig Instrument, MarketPriceTracker PriceTracker) CreateWorker(
        TimeProvider clock, Action<MarketMakerBotOptions>? configure = null)
    {
        return CreateWorker(clock, configure, out _);
    }

    private (MarketMakerWorker Worker, OrderTracker Tracker, FakeEntryPointClient Client,
        InstrumentConfig Instrument, MarketPriceTracker PriceTracker) CreateWorker(
        TimeProvider clock,
        Action<MarketMakerBotOptions>? configure,
        out MarketMakerPnlLedger pnlLedger)
    {
        return CreateWorker(clock, configure, out pnlLedger, out _);
    }

    private (MarketMakerWorker Worker, OrderTracker Tracker, FakeEntryPointClient Client,
        InstrumentConfig Instrument, MarketPriceTracker PriceTracker) CreateWorker(
        TimeProvider clock,
        Action<MarketMakerBotOptions>? configure,
        out MarketMakerPnlLedger pnlLedger,
        out MarketDataFeed marketData)
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
        configure?.Invoke(options);
        var tracker = new OrderTracker(clock);
        var priceTracker = new MarketPriceTracker(clock);
        pnlLedger = new MarketMakerPnlLedger(clock);
        var metrics = new MarketMakerMetrics(pnlLedger, priceTracker, Options.Create(options));
        _metrics.Add(metrics);
        var loggerFactory = NullLoggerFactory.Instance;
        marketData = new MarketDataFeed(priceTracker, NullLogger.Instance);
        var worker = new MarketMakerWorker(Options.Create(options), tracker, priceTracker, pnlLedger, metrics,
            marketData, loggerFactory, NullLogger<MarketMakerWorker>.Instance);
        return (worker, tracker, new FakeEntryPointClient(), instrument, priceTracker);
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
    public async Task QuoteSideAsync_SubmitPriceMatchesUnifiedDecision()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, _, client, instrument, priceTracker) = CreateWorker(clock);
        priceTracker.SetConnected(true);
        priceTracker.OnTrade(instrument.Symbol, 31m);

        var decision = worker.BuildQuoteDecision(instrument, isBuy: true);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);

        Assert.True(decision.ShouldQuote);
        Assert.Equal(30.95m, decision.Price);
        Assert.Equal(QuoteReferenceSource.LiveMarketData, decision.ReferenceSource);
        Assert.Equal(0m, decision.InventoryMidShift);
        Assert.Equal(decision.ConfiguredHalfSpread, decision.EffectiveHalfSpread);
        Assert.Equal(decision.Price, Assert.Single(client.SubmittedOrders).Price);
    }

    [Fact]
    public void BuildQuoteDecision_InventorySkewDisabled_PreservesPricesWithNonzeroInventory()
    {
        var (worker, _, _, instrument) = CreateWorker(out var ledger);
        Assert.Equal(FillApplyStatus.Applied, ledger.Apply(new OwnFill(
            1, 1, instrument.Symbol, true, 500, 30m, 500, 500, 0, true)).Status);

        var bid = worker.BuildQuoteDecision(instrument, isBuy: true);
        var ask = worker.BuildQuoteDecision(instrument, isBuy: false);

        Assert.Equal(29.95m, bid.Price);
        Assert.Equal(30.05m, ask.Price);
        Assert.Equal(0m, bid.InventoryMidShift);
        Assert.Equal(0m, bid.InventorySkewTicks);
    }

    [Fact]
    public async Task QuoteSideAsync_InventorySkewedSubmitMatchesUnifiedDecision()
    {
        var (worker, _, client, instrument) = CreateWorker(out var ledger, EnableInventorySkew);
        Assert.Equal(FillApplyStatus.Applied, ledger.Apply(new OwnFill(
            1, 1, instrument.Symbol, true, 50, 30m, 50, 50, 0, true)).Status);

        var decision = worker.BuildQuoteDecision(instrument, isBuy: true);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);

        Assert.Equal(2.5m, decision.InventorySkewTicks);
        Assert.Equal(-0.025m, decision.InventoryMidShift);
        Assert.Equal(29.93m, decision.Price);
        Assert.Equal(decision.Price, Assert.Single(client.SubmittedOrders).Price);
    }

    [Fact]
    public void BuildQuoteDecision_DefaultContextPreservesStaticBehaviorAndDelistSuppression()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, _, _, instrument, priceTracker) = CreateWorker(clock);

        var defaultDecision = worker.BuildQuoteDecision(instrument, isBuy: false);

        Assert.True(defaultDecision.ShouldQuote);
        Assert.Equal(30.05m, defaultDecision.Price);
        Assert.Equal(30m, defaultDecision.ReferencePrice);
        Assert.Equal(QuoteReferenceSource.ConfiguredRefPrice, defaultDecision.ReferenceSource);
        Assert.Equal(QuoteSuppressionReason.None, defaultDecision.SuppressionReason);

        priceTracker.OnSymbolDelisted(instrument.Symbol);
        var delistedDecision = worker.BuildQuoteDecision(instrument, isBuy: false);

        Assert.False(delistedDecision.ShouldQuote);
        Assert.Null(delistedDecision.Price);
        Assert.Equal(QuoteSuppressionReason.InstrumentDelisted, delistedDecision.SuppressionReason);
    }

    [Fact]
    public async Task ReactToBookChangeAsync_UsesSameRoundedDecisionAsSubmit()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, _, client, instrument, priceTracker) = CreateWorker(clock,
            options => options.MinRequoteInterval = TimeSpan.Zero);
        priceTracker.SetConnected(true);
        priceTracker.OnTrade(instrument.Symbol, 31.001m);
        var submittedDecision = worker.BuildQuoteDecision(instrument, isBuy: true);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(1));
        await worker.ReactToBookChangeAsync(client, instrument.Symbol, CancellationToken.None);

        Assert.Equal(submittedDecision.Price,
            client.SubmittedOrders.Single(order => order.Side == Side.Buy).Price);
        Assert.Equal(2, client.SubmittedOrders.Count);
        Assert.Empty(client.SubmittedCancels);
    }

    [Fact]
    public async Task ReactToBookChangeAsync_InventorySkewedTargetMatchesSubmitDecision()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, _, client, instrument, _) = CreateWorker(
            clock,
            EnableInventorySkew,
            out var ledger);
        Assert.Equal(FillApplyStatus.Applied, ledger.Apply(new OwnFill(
            1, 1, instrument.Symbol, true, 50, 30m, 50, 50, 0, true)).Status);

        var submittedDecision = worker.BuildQuoteDecision(instrument, isBuy: true);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        await worker.ReactToBookChangeAsync(client, instrument.Symbol, CancellationToken.None);

        Assert.Equal(submittedDecision.Price,
            client.SubmittedOrders.Single(order => order.Side == Side.Buy).Price);
        Assert.Equal(2, client.SubmittedOrders.Count);
        Assert.Empty(client.SubmittedCancels);
    }

    [Fact]
    public async Task ReactToPricingContextChangeAsync_SuppressedSide_IsRestoredImmediatelyByLaterFill()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, tracker, client, instrument, _) = CreateWorker(
            clock,
            options =>
            {
                EnableInventorySkew(options);
                options.Instruments[0].RefPrice = 0.10m;
                options.Instruments[0].InventorySkew.MaxSkewTicks = 10m;
            },
            out var ledger);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var original = Assert.Single(client.SubmittedOrders);
        Assert.Equal(0.05m, original.Price);
        Assert.Equal(FillApplyStatus.Applied, ledger.Apply(new OwnFill(
            900, 900, instrument.Symbol, true, 100, 0.10m, 100, 100, 0, true)).Status);

        var suppressed = worker.BuildQuoteDecision(instrument, isBuy: true);
        Assert.False(suppressed.ShouldQuote);
        await worker.ReactToPricingContextChangeAsync(
            client,
            instrument.Symbol,
            CancelReason.InventoryStrategy,
            CancellationToken.None);

        var cancel = Assert.Single(client.SubmittedCancels);
        Assert.Equal(original.ClOrdID.Value, cancel.OrigClOrdID.Value);
        await worker.HandleEventAsync(client, new OrderCancelled
        {
            ClOrdID = cancel.ClOrdID,
            OrigClOrdID = cancel.OrigClOrdID,
            OrderId = 100,
            OrderStatus = OrderStatus.Cancelled,
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.Equal(2, client.SubmittedOrders.Count);
        Assert.False(tracker.HasOpenSide(instrument.Symbol, isBuy: true));

        var buyRestored = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SubmitHandler = (request, _) =>
        {
            if (request.Side == Side.Buy && request.ClOrdID.Value != original.ClOrdID.Value)
                buyRestored.TrySetResult(true);
            return Task.CompletedTask;
        };
        using var cts = new CancellationTokenSource();
        var reactionLoop = worker.PricingContextReactionLoopAsync(client, cts.Token);
        Assert.Equal(FillApplyStatus.Applied, ledger.Apply(new OwnFill(
            901, 901, instrument.Symbol, false, 100, 0.10m, 100, 100, 0, true)).Status);
        worker.SignalPricingContextChanged(instrument.Symbol, CancelReason.InventoryStrategy);

        await buyRestored.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await reactionLoop;

        Assert.Equal(3, client.SubmittedOrders.Count);
        Assert.Equal(0.05m, client.SubmittedOrders.Last(order => order.Side == Side.Buy).Price);
        Assert.True(tracker.HasOpenSide(instrument.Symbol, isBuy: true));
    }

    [Fact]
    public async Task SymbolDelistedSignal_CancelsBothSidesAndNeverReplacesThem()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, tracker, client, instrument, _) = CreateWorker(
            clock,
            options => options.MinRequoteInterval = TimeSpan.Zero,
            out _,
            out var marketData);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        await worker.QuoteSideAsync(client, instrument, isBuy: false, CancellationToken.None);
        var originalIds = client.SubmittedOrders.Select(order => order.ClOrdID.Value).ToHashSet();

        var bothSidesCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.CancelHandler = (_, _) =>
        {
            if (client.SubmittedCancels.Count == 2)
                bothSidesCancelled.TrySetResult(true);
            return Task.CompletedTask;
        };
        marketData.SymbolAvailabilityChanged += worker.OnSymbolAvailabilityChanged;
        using var cts = new CancellationTokenSource();
        var reactionLoop = worker.PricingContextReactionLoopAsync(client, cts.Token);
        try
        {
            marketData.NotifySymbolDelisted(instrument.Symbol);
            await bothSidesCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(originalIds,
                client.SubmittedCancels.Select(cancel => cancel.OrigClOrdID.Value).ToHashSet());
            foreach (var cancel in client.SubmittedCancels.ToArray())
            {
                await worker.HandleEventAsync(client, new OrderCancelled
                {
                    ClOrdID = cancel.ClOrdID,
                    OrigClOrdID = cancel.OrigClOrdID,
                    OrderId = 100,
                    OrderStatus = OrderStatus.Cancelled,
                    SeqNum = 1,
                    SendingTime = DateTimeOffset.UtcNow,
                }, CancellationToken.None);
            }

            await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
            await worker.QuoteSideAsync(client, instrument, isBuy: false, CancellationToken.None);

            Assert.Equal(2, client.SubmittedOrders.Count);
            Assert.False(tracker.HasOpenSide(instrument.Symbol, isBuy: true));
            Assert.False(tracker.HasOpenSide(instrument.Symbol, isBuy: false));
        }
        finally
        {
            marketData.SymbolAvailabilityChanged -= worker.OnSymbolAvailabilityChanged;
            cts.Cancel();
            await reactionLoop;
        }
    }

    [Fact]
    public async Task CancelReject_ReplaysInventoryContextThatArrivedWhileCancelWasPending()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, tracker, client, instrument, _) = CreateWorker(
            clock,
            EnableInventorySkew,
            out var ledger);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        await worker.QuoteSideAsync(client, instrument, isBuy: false, CancellationToken.None);
        var originalClOrdId = client.SubmittedOrders.Single(order => order.Side == Side.Buy).ClOrdID.Value;
        const ulong firstCancelClOrdId = 999_001;
        tracker.RegisterCancelAttempt(firstCancelClOrdId, originalClOrdId, CancelReason.PriceDrift);
        tracker.RegisterCancelAttempt(
            999_002,
            client.SubmittedOrders.Single(order => order.Side == Side.Sell).ClOrdID.Value,
            CancelReason.PriceDrift);
        Assert.Equal(FillApplyStatus.Applied, ledger.Apply(new OwnFill(
            900, 900, instrument.Symbol, true, 50, 30m, 50, 50, 0, true)).Status);
        await worker.ReactToPricingContextChangeAsync(
            client,
            instrument.Symbol,
            CancelReason.InventoryStrategy,
            CancellationToken.None);

        var retrySubmitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.CancelHandler = (_, _) =>
        {
            if (client.SubmittedCancels.Count == 1)
                retrySubmitted.TrySetResult(true);
            return Task.CompletedTask;
        };
        using var cts = new CancellationTokenSource();
        var reactionLoop = worker.PricingContextReactionLoopAsync(client, cts.Token);
        await worker.HandleEventAsync(client, new OrderRejected
        {
            ClOrdID = new ClOrdID(firstCancelClOrdId),
            OrderId = 0,
            RejectCode = 1,
            Reason = "test reject",
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        await retrySubmitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await reactionLoop;

        var retryCancel = Assert.Single(client.SubmittedCancels);
        Assert.All(client.SubmittedCancels,
            cancel => Assert.Equal(originalClOrdId, cancel.OrigClOrdID.Value));
        Assert.True(tracker.TryResolveCancelAttempt(
            retryCancel.ClOrdID.Value,
            out _,
            out var reason));
        Assert.Equal(CancelReason.InventoryStrategy, reason);
    }

    [Fact]
    public async Task CancelSubmitFailure_ReplaysInventoryContextOnceAndRegistersRetry()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, tracker, client, instrument, _) = CreateWorker(
            clock,
            EnableInventorySkew,
            out var ledger);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var originalClOrdId = Assert.Single(client.SubmittedOrders).ClOrdID.Value;
        Assert.Equal(FillApplyStatus.Applied, ledger.Apply(new OwnFill(
            900, 900, instrument.Symbol, true, 50, 30m, 50, 50, 0, true)).Status);

        var attempt = 0;
        var retrySubmitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.CancelHandler = (_, _) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
                throw new InvalidOperationException("test transport failure");
            retrySubmitted.TrySetResult(true);
            return Task.CompletedTask;
        };

        await worker.ReactToPricingContextChangeAsync(
            client,
            instrument.Symbol,
            CancelReason.InventoryStrategy,
            CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var reactionLoop = worker.PricingContextReactionLoopAsync(client, cts.Token);
        await retrySubmitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await reactionLoop;

        Assert.Equal(2, client.SubmittedCancels.Count);
        Assert.All(client.SubmittedCancels,
            cancel => Assert.Equal(originalClOrdId, cancel.OrigClOrdID.Value));
        Assert.True(tracker.TryResolveCancelAttempt(
            client.SubmittedCancels[^1].ClOrdID.Value,
            out _,
            out var reason));
        Assert.Equal(CancelReason.InventoryStrategy, reason);
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
    public async Task HandleEventAsync_PartialFill_ReevaluatesBothSidesAndRequotesAfterCancelAcks()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, _, client, instrument, _) = CreateWorker(
            clock,
            EnableInventorySkew,
            out _);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        await worker.QuoteSideAsync(client, instrument, isBuy: false, CancellationToken.None);
        var originalIds = client.SubmittedOrders.Select(order => order.ClOrdID.Value).ToHashSet();

        var bothSidesCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.CancelHandler = (_, _) =>
        {
            if (client.SubmittedCancels.Count == 2)
                bothSidesCancelled.TrySetResult(true);
            return Task.CompletedTask;
        };
        using var cts = new CancellationTokenSource();
        var reactionLoop = worker.PricingContextReactionLoopAsync(client, cts.Token);

        var buyClOrdId = client.SubmittedOrders.Single(order => order.Side == Side.Buy).ClOrdID.Value;
        await worker.HandleEventAsync(client, new OrderTrade
        {
            ClOrdID = new ClOrdID(buyClOrdId),
            OrderId = 100,
            TradeId = 1,
            OrderStatus = OrderStatus.PartiallyFilled,
            LastPx = 29.95m,
            LastQty = 50,
            CumQty = 50,
            LeavesQty = 50,
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        await bothSidesCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(originalIds,
            client.SubmittedCancels.Select(cancel => cancel.OrigClOrdID.Value).ToHashSet());
        Assert.Equal(2, client.SubmittedOrders.Count);

        foreach (var cancel in client.SubmittedCancels.ToArray())
        {
            await worker.HandleEventAsync(client, new OrderCancelled
            {
                ClOrdID = cancel.ClOrdID,
                OrigClOrdID = cancel.OrigClOrdID,
                OrderId = 100,
                OrderStatus = OrderStatus.Cancelled,
                SeqNum = 2,
                SendingTime = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }

        cts.Cancel();
        await reactionLoop;
        Assert.Equal(4, client.SubmittedOrders.Count);
        Assert.Equal(29.93m, client.SubmittedOrders.Last(order => order.Side == Side.Buy).Price);
        Assert.Equal(30.03m, client.SubmittedOrders.Last(order => order.Side == Side.Sell).Price);
    }

    [Fact]
    public async Task HandleEventAsync_FullFill_RequotesFilledSideAndReevaluatesOtherSideWithoutDuplicates()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, _, client, instrument, _) = CreateWorker(
            clock,
            EnableInventorySkew,
            out _);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        await worker.QuoteSideAsync(client, instrument, isBuy: false, CancellationToken.None);
        var buyClOrdId = client.SubmittedOrders.Single(order => order.Side == Side.Buy).ClOrdID.Value;
        var sellClOrdId = client.SubmittedOrders.Single(order => order.Side == Side.Sell).ClOrdID.Value;

        var otherSideCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.CancelHandler = (_, _) =>
        {
            otherSideCancelled.TrySetResult(true);
            return Task.CompletedTask;
        };
        using var cts = new CancellationTokenSource();
        var reactionLoop = worker.PricingContextReactionLoopAsync(client, cts.Token);
        var trade = new OrderTrade
        {
            ClOrdID = new ClOrdID(buyClOrdId),
            OrderId = 100,
            TradeId = 1,
            OrderStatus = OrderStatus.Filled,
            LastPx = 29.95m,
            LastQty = 100,
            CumQty = 100,
            LeavesQty = 0,
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
        };

        await worker.HandleEventAsync(client, trade, CancellationToken.None);
        await otherSideCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.HandleEventAsync(client, trade, CancellationToken.None);

        var cancel = Assert.Single(client.SubmittedCancels);
        Assert.Equal(sellClOrdId, cancel.OrigClOrdID.Value);
        Assert.Equal(3, client.SubmittedOrders.Count);
        Assert.Equal(29.90m, client.SubmittedOrders.Last(order => order.Side == Side.Buy).Price);

        await worker.HandleEventAsync(client, new OrderCancelled
        {
            ClOrdID = cancel.ClOrdID,
            OrigClOrdID = cancel.OrigClOrdID,
            OrderId = 101,
            OrderStatus = OrderStatus.Cancelled,
            SeqNum = 2,
            SendingTime = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        cts.Cancel();
        await reactionLoop;
        Assert.Equal(4, client.SubmittedOrders.Count);
        Assert.Equal(30.00m, client.SubmittedOrders.Last(order => order.Side == Side.Sell).Price);
    }

    [Fact]
    public async Task HandleEventAsync_KnownOwnTrade_AppliesLedgerAndDuplicateIsIdempotent()
    {
        var (worker, _, client, instrument) = CreateWorker(out var ledger);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var clOrdId = client.SubmittedOrders[0].ClOrdID.Value;
        var trade = new OrderTrade
        {
            ClOrdID = new ClOrdID(clOrdId),
            OrderId = 100,
            TradeId = 55,
            OrderStatus = OrderStatus.Filled,
            LastPx = 30m,
            LastQty = 100,
            CumQty = 100,
            LeavesQty = 0,
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
        };

        await worker.HandleEventAsync(client, trade, CancellationToken.None);
        await worker.HandleEventAsync(client, trade, CancellationToken.None);

        Assert.True(ledger.TryGetSnapshot("PETR4", out var snapshot));
        Assert.Equal(100, snapshot.Position);
        Assert.Equal(30m, snapshot.AverageCost);
        Assert.Equal(0m, snapshot.RealizedPnl);
        Assert.True(ledger.IsTerminalOrderState(clOrdId));
    }

    [Fact]
    public async Task HandleEventAsync_UnknownOrderTrade_DoesNotCreateLedgerState()
    {
        var (worker, _, client, _) = CreateWorker(out var ledger);

        await worker.HandleEventAsync(client, new OrderTrade
        {
            ClOrdID = new ClOrdID(999),
            OrderId = 100,
            TradeId = 55,
            OrderStatus = OrderStatus.Filled,
            LastPx = 30m,
            LastQty = 100,
            CumQty = 100,
            LeavesQty = 0,
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.Empty(ledger.SnapshotAll());
    }

    [Fact]
    public async Task HandleEventAsync_ForwardCumulativeJump_BooksAuthoritativeDelta()
    {
        var (worker, _, client, instrument) = CreateWorker(out var ledger);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var clOrdId = client.SubmittedOrders[0].ClOrdID.Value;

        await worker.HandleEventAsync(client, new OrderTrade
        {
            ClOrdID = new ClOrdID(clOrdId),
            OrderId = 100,
            TradeId = 55,
            OrderStatus = OrderStatus.PartiallyFilled,
            LastPx = 30m,
            LastQty = 20,
            CumQty = 60,
            LeavesQty = 40,
            SeqNum = 3,
            SendingTime = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.True(ledger.TryGetSnapshot("PETR4", out var snapshot));
        Assert.Equal(60, snapshot.Position);
        Assert.Equal(30m, snapshot.AverageCost);
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

    [Fact]
    public async Task HandleEventAsync_OrderCancelled_WithOrigClOrdID_ClosesAndRequotes()
    {
        // The common case: OrigClOrdID is populated because the cancel
        // was in response to an explicit CancelOrderRequest.
        var (worker, tracker, client, instrument) = CreateWorker();
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var clOrdId = client.SubmittedOrders[0].ClOrdID.Value;

        await worker.HandleEventAsync(client, new OrderCancelled
        {
            ClOrdID = new ClOrdID(clOrdId + 1000), // the cancel request's own id
            OrigClOrdID = new ClOrdID(clOrdId),
            OrderId = 100,
            OrderStatus = OrderStatus.Cancelled,
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        // Closed and immediately re-quoted, same as a fill.
        Assert.False(tracker.TryGet(clOrdId, out var stale) && stale.IsOpen);
        Assert.True(tracker.HasOpenSide("PETR4", isBuy: true));
        Assert.Equal(2, client.SubmittedOrders.Count);
        Assert.NotEqual(clOrdId, client.SubmittedOrders[1].ClOrdID.Value);
    }

    [Fact]
    public async Task HandleEventAsync_OrderCancelled_MissingOrigClOrdID_ResolvesViaCancelAttemptCorrelation()
    {
        // Some gateway paths drop OrigClOrdID on the cancel ack entirely
        // (see ExecutionReportProcessorTests.Cancel_WithMissingOrigClOrdId_ResolvesViaCancelLink
        // for the trading-host side of the same class of bug). The bot
        // must fall back to its own RegisterCancelAttempt correlation
        // table instead of assuming ClOrdID IS the original order.
        var (worker, tracker, client, instrument) = CreateWorker();
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var clOrdId = client.SubmittedOrders[0].ClOrdID.Value;
        var cancelClOrdId = clOrdId + 1000;
        tracker.RegisterCancelAttempt(cancelClOrdId, clOrdId);

        await worker.HandleEventAsync(client, new OrderCancelled
        {
            ClOrdID = new ClOrdID(cancelClOrdId),
            OrigClOrdID = null,
            OrderId = 100,
            OrderStatus = OrderStatus.Cancelled,
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.False(tracker.TryGet(clOrdId, out var stale) && stale.IsOpen);
        Assert.True(tracker.HasOpenSide("PETR4", isBuy: true));
        Assert.Equal(2, client.SubmittedOrders.Count);
    }

    [Fact]
    public async Task HandleEventAsync_OrderCancelled_SpontaneousCancel_TreatsClOrdIdAsOriginal()
    {
        // No OrigClOrdID and no cancel-attempt correlation row at all: a
        // venue-initiated spontaneous cancel (e.g. Day expiry). ClOrdID
        // must be assumed to BE the original order's id.
        var (worker, tracker, client, instrument) = CreateWorker();
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var clOrdId = client.SubmittedOrders[0].ClOrdID.Value;

        await worker.HandleEventAsync(client, new OrderCancelled
        {
            ClOrdID = new ClOrdID(clOrdId),
            OrigClOrdID = null,
            OrderId = 100,
            OrderStatus = OrderStatus.Cancelled,
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.False(tracker.TryGet(clOrdId, out var stale) && stale.IsOpen);
        Assert.True(tracker.HasOpenSide("PETR4", isBuy: true));
        Assert.Equal(2, client.SubmittedOrders.Count);
    }

    [Theory]
    [InlineData(CancelReason.StaleOrder)]
    [InlineData(CancelReason.PriceDrift)]
    [InlineData(CancelReason.InventoryStrategy)]
    [InlineData(CancelReason.FeedUnavailable)]
    public async Task HandleEventAsync_OrderRejected_CancelAttemptRejected_LeavesOriginalOrderUntouched(
        CancelReason cancelReason)
    {
        // A rejected cancel (book-driven requote or staleness guard —
        // both share SubmitCancelAsync) must NOT free the original
        // order's reservation: if it's still genuinely resting, doing so
        // would let the next reconcile tick submit a duplicate order
        // alongside it — the exact bug #707 fixed for OrderAccepted.
        var (worker, tracker, client, instrument) = CreateWorker();
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var clOrdId = client.SubmittedOrders[0].ClOrdID.Value;
        var cancelClOrdId = clOrdId + 1000;
        tracker.RegisterCancelAttempt(cancelClOrdId, clOrdId, cancelReason);

        await worker.HandleEventAsync(client, new OrderRejected
        {
            ClOrdID = new ClOrdID(cancelClOrdId),
            OrderId = 0,
            RejectCode = 1,
            Reason = "test reject",
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.True(tracker.TryGet(clOrdId, out var order) && order.IsOpen);
        Assert.True(tracker.HasOpenSide("PETR4", isBuy: true));
        // The pending-cancel marker is cleared so a later reconcile tick
        // / book delta can retry the cancel instead of treating one as
        // permanently outstanding.
        Assert.Null(order.PendingCancelClOrdId);
        // No requote — the order was never actually closed.
        Assert.Single(client.SubmittedOrders);
    }

    [Fact]
    public async Task HandleEventAsync_OrderRejected_NewOrderReject_ClosesReservationWithoutImmediateRequote()
    {
        // A reject of a plain NEW order submit (not a cancel attempt):
        // the reservation must close so the side isn't stuck forever,
        // but must NOT be re-quoted immediately (an instrument-level
        // reject would otherwise repeat identically forever) — the
        // low-frequency ReconcileLoopAsync is the intended retry path.
        var (worker, tracker, client, instrument) = CreateWorker();
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var clOrdId = client.SubmittedOrders[0].ClOrdID.Value;

        await worker.HandleEventAsync(client, new OrderRejected
        {
            ClOrdID = new ClOrdID(clOrdId),
            OrderId = 0,
            RejectCode = 2,
            Reason = "instrument halted",
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.False(tracker.HasOpenSide("PETR4", isBuy: true));
        Assert.Single(client.SubmittedOrders); // no immediate requote.
    }

    [Fact]
    public async Task CancelStaleOrdersAsync_OrderOlderThanMaxAge_SubmitsCancel()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, tracker, client, instrument, _) = CreateWorker(clock,
            o => o.MaxOrderAge = TimeSpan.FromMinutes(5));
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var clOrdId = client.SubmittedOrders[0].ClOrdID.Value;

        clock.Advance(TimeSpan.FromMinutes(6));
        await worker.CancelStaleOrdersAsync(client, CancellationToken.None);

        Assert.Single(client.SubmittedCancels);
        Assert.Equal(clOrdId, client.SubmittedCancels[0].OrigClOrdID.Value);
        Assert.True(tracker.TryResolveCancelAttempt(
            client.SubmittedCancels[0].ClOrdID.Value,
            out _,
            out var cancelReason));
        Assert.Equal(CancelReason.StaleOrder, cancelReason);
        // Still open — only cancelled, not yet closed (that's the venue's
        // OrderCancelled/OrderRejected ER via HandleEventAsync).
        Assert.True(tracker.HasOpenSide("PETR4", isBuy: true));
        Assert.True(tracker.TryGet(clOrdId, out var order));
        Assert.NotNull(order.PendingCancelClOrdId);
    }

    [Fact]
    public async Task CancelStaleOrdersAsync_OrderWithinMaxAge_DoesNotCancel()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, _, client, instrument, _) = CreateWorker(clock,
            o => o.MaxOrderAge = TimeSpan.FromMinutes(5));
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(1));
        await worker.CancelStaleOrdersAsync(client, CancellationToken.None);

        Assert.Empty(client.SubmittedCancels);
    }

    [Fact]
    public async Task ReactToBookChangeAsync_PriceDriftedPastDeviation_CancelsRestingOrder()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, tracker, client, instrument, priceTracker) = CreateWorker(clock,
            o => o.MinRequoteInterval = TimeSpan.Zero);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var clOrdId = client.SubmittedOrders[0].ClOrdID.Value;
        Assert.Equal(29.95m, tracker.TryGet(clOrdId, out var resting) ? resting.Price : 0m);

        // Reference price moves far enough that the buy target (refPrice
        // - 5 ticks) now sits well past RequoteDeviationTicks (2 ticks =
        // 0.02) away from the still-resting 29.95 quote.
        priceTracker.SetConnected(true);
        priceTracker.OnTrade(instrument.Symbol, 31m);
        clock.Advance(TimeSpan.FromMinutes(1)); // clear MinRequoteInterval's own throttle path.
        await worker.ReactToBookChangeAsync(client, instrument.Symbol, CancellationToken.None);

        Assert.Single(client.SubmittedCancels);
        Assert.Equal(clOrdId, client.SubmittedCancels[0].OrigClOrdID.Value);
        Assert.True(tracker.TryResolveCancelAttempt(
            client.SubmittedCancels[0].ClOrdID.Value,
            out _,
            out var cancelReason));
        Assert.Equal(CancelReason.PriceDrift, cancelReason);
        Assert.True(tracker.TryGet(clOrdId, out var order) && order.PendingCancelClOrdId is not null);
    }

    [Fact]
    public async Task ReactToBookChangeAsync_PriceDrift_ReevaluatesAndCancelsBothSides()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, tracker, client, instrument, priceTracker) = CreateWorker(clock,
            options => options.MinRequoteInterval = TimeSpan.Zero);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        await worker.QuoteSideAsync(client, instrument, isBuy: false, CancellationToken.None);
        var originalIds = client.SubmittedOrders.Select(order => order.ClOrdID.Value).ToHashSet();

        priceTracker.SetConnected(true);
        priceTracker.OnTrade(instrument.Symbol, 31m);
        clock.Advance(TimeSpan.FromMinutes(1));
        await worker.ReactToBookChangeAsync(client, instrument.Symbol, CancellationToken.None);

        Assert.Equal(2, client.SubmittedCancels.Count);
        Assert.Equal(originalIds,
            client.SubmittedCancels.Select(cancel => cancel.OrigClOrdID.Value).ToHashSet());
        Assert.Equal(2, client.SubmittedOrders.Count);
    }

    [Fact]
    public async Task PricingContextSignals_ConcurrentBurstCoalescesAndReevaluatesBothSides()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, tracker, client, instrument, priceTracker) = CreateWorker(clock,
            options => options.MinRequoteInterval = TimeSpan.Zero);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        await worker.QuoteSideAsync(client, instrument, isBuy: false, CancellationToken.None);
        priceTracker.SetConnected(true);
        priceTracker.OnTrade(instrument.Symbol, 31m);
        clock.Advance(TimeSpan.FromMinutes(1));

        var queued = 0;
        Parallel.For(0, 100, _ =>
        {
            if (worker.SignalPricingContextChanged(instrument.Symbol, CancelReason.PriceDrift))
                Interlocked.Increment(ref queued);
        });
        Assert.Equal(1, queued);
        Assert.False(worker.SignalPricingContextChanged("UNKNOWN", CancelReason.PriceDrift));

        var bothSidesCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.CancelHandler = (_, _) =>
        {
            if (client.SubmittedCancels.Count == 2)
                bothSidesCancelled.TrySetResult(true);
            return Task.CompletedTask;
        };
        using var cts = new CancellationTokenSource();
        var reactionLoop = worker.PricingContextReactionLoopAsync(client, cts.Token);

        await bothSidesCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await reactionLoop;

        Assert.Equal(2, client.SubmittedCancels.Count);
        Assert.Equal(2, client.SubmittedOrders.Count);
        Assert.All(client.SubmittedCancels, cancel =>
        {
            Assert.True(tracker.TryResolveCancelAttempt(cancel.ClOrdID.Value, out _, out var reason));
            Assert.Equal(CancelReason.PriceDrift, reason);
        });
    }

    [Theory]
    [InlineData(CancelReason.StaleOrder, "bot.orders.stale_cancel_rejected")]
    [InlineData(CancelReason.PriceDrift, "bot.orders.book_driven_requote_cancel_rejected")]
    public async Task HandleEventAsync_CancelReject_PreservesReasonSpecificMetric(
        CancelReason cancelReason,
        string expectedMetric)
    {
        var (worker, tracker, client, instrument) = CreateWorker();
        var metrics = _metrics[^1];
        using var listener = new MeterListener();
        var measurements = new ConcurrentBag<string>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter))
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (value == 1)
                measurements.Add(instrument.Name);
        });
        listener.Start();

        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var originalClOrdId = client.SubmittedOrders[0].ClOrdID.Value;
        var cancelClOrdId = originalClOrdId + 1000;
        tracker.RegisterCancelAttempt(cancelClOrdId, originalClOrdId, cancelReason);

        await worker.HandleEventAsync(client, new OrderRejected
        {
            ClOrdID = new ClOrdID(cancelClOrdId),
            OrderId = 0,
            RejectCode = 1,
            Reason = "test reject",
            SeqNum = 1,
            SendingTime = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.Contains(expectedMetric, measurements);
        var unexpectedMetric = cancelReason == CancelReason.StaleOrder
            ? "bot.orders.book_driven_requote_cancel_rejected"
            : "bot.orders.stale_cancel_rejected";
        Assert.DoesNotContain(unexpectedMetric, measurements);
    }

    [Fact]
    public async Task ReactToBookChangeAsync_PriceWithinDeviation_DoesNotCancel()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, _, client, instrument, priceTracker) = CreateWorker(clock,
            o => o.MinRequoteInterval = TimeSpan.Zero);
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);

        // A tiny move that rounds back to the exact same tick price
        // shouldn't trigger a cancel at all.
        priceTracker.SetConnected(true);
        priceTracker.OnTrade(instrument.Symbol, 30.001m);
        clock.Advance(TimeSpan.FromMinutes(1));
        await worker.ReactToBookChangeAsync(client, instrument.Symbol, CancellationToken.None);

        Assert.Empty(client.SubmittedCancels);
    }

    [Fact]
    public async Task ReactToBookChangeAsync_WithinMinRequoteInterval_ThrottlesCancel()
    {
        // A big price move right after submission must not immediately
        // cancel a quote that hasn't even settled yet — the same
        // venue-flooding shape RFC #703 exists to prevent.
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var (worker, tracker, client, instrument, priceTracker) = CreateWorker(clock,
            o => o.MinRequoteInterval = TimeSpan.FromSeconds(30));
        await worker.QuoteSideAsync(client, instrument, isBuy: true, CancellationToken.None);
        var clOrdId = client.SubmittedOrders[0].ClOrdID.Value;

        priceTracker.SetConnected(true);
        priceTracker.OnTrade(instrument.Symbol, 31m);
        // No clock advance: still inside MinRequoteInterval of the
        // original submission.
        await worker.ReactToBookChangeAsync(client, instrument.Symbol, CancellationToken.None);

        Assert.Empty(client.SubmittedCancels);
        Assert.True(tracker.TryGet(clOrdId, out var order) && order.PendingCancelClOrdId is null);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private static void EnableInventorySkew(MarketMakerBotOptions options)
    {
        options.MinRequoteInterval = TimeSpan.Zero;
        options.Instruments[0].InventorySkew = new InventorySkewConfig
        {
            Enabled = true,
            FullSkewAtLots = 1,
            MaxSkewTicks = 5m,
        };
    }
}
