using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.MarketData;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Pass-1 review (#278) P1#3. Regression coverage for the
/// refprice → <c>pnl.me</c> WS fan-out: a subscriber holding a
/// position in a symbol must receive a fresh <see cref="PnlTodayDto"/>
/// delta when the symbol's reference price changes, even with no fill
/// in between.
/// </summary>
public class PnlRefPriceFanOutTests
{
    private sealed class StaticFallback : IReferencePrice
    {
        public bool TryGet(string symbol, out decimal price) { price = 0m; return false; }
    }

    private sealed class FakeSubscriber : IMarketDataSubscriber
    {
        public event Action<MarketTrade>? Trade;
#pragma warning disable CS0067
        public event Action<MarketInfoSnapshot>? InfoSnapshot;
        public event Action<MarketDataConnectionState>? ConnectionStateChanged;
        public event Action<MarketSubscribeError>? SubscribeError;
        public event Action<MarketTheoreticalOpening>? TheoreticalOpening;
        public event Action<MarketAuctionImbalance>? AuctionImbalance;
        public event Action<MarketAuctionPrint>? AuctionPrint;
#pragma warning restore CS0067

        public MarketDataConnectionState State => MarketDataConnectionState.Connected;
        public long DroppedEventCount => 0;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask SubscribeAsync(string symbol, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void RaiseTrade(string symbol, decimal price, DateTimeOffset ts) =>
            Trade?.Invoke(new MarketTrade(symbol, 0UL, price, 0L, ts));
    }

    [Fact]
    public async Task PriceChange_PublishesPnlDeltaToSubscriber()
    {
        var positions = new PositionKeeper();
        var pnl = new PnlKeeper();
        var owner = new EndClientId("alice");
        positions.ApplyFill(owner, "PETR4", OrderSide.Buy, 100, 30m);
        pnl.ApplyFillToAvgCost(owner.Value, "PETR4", OrderSide.Buy, 100, 30m);

        var sub = new FakeSubscriber();
        var clock = TimeProvider.System;
        var refPrice = new MarketDataReferencePrice(
            sub,
            new StaticFallback(),
            Options.Create(new MarketDataOptions { WsUrl = "ws://test", Symbols = Array.Empty<string>() }),
            clock,
            NullLogger<MarketDataReferencePrice>.Instance);

        var subs = new SubscriptionManager(new WorkingOrderBook(), positions, new AlgoBook(), pnl, refPrice);
        var client = new SubscribedClient(owner, "TEST");
        subs.Add(client);
        subs.SubscribeWithSnapshot(client, Channels.PnlMe);
        // Drain the snapshot frame so we only observe the delta.
        Assert.True(client.Reader.TryRead(out _));

        var fanOut = new PnlRefPriceFanOut(refPrice, subs, pnl, positions, refPrice, clock,
            NullLogger<PnlRefPriceFanOut>.Instance);
        await fanOut.StartAsync(CancellationToken.None);
        try
        {
            // First refprice tick — should fan-out a delta.
            sub.RaiseTrade("PETR4", 31m, DateTimeOffset.UtcNow);

            // Drain background channel + publish thread; bounded wait.
            var deadline = DateTime.UtcNow.AddSeconds(2);
            OutboundMessage? msg = null;
            while (DateTime.UtcNow < deadline)
            {
                if (client.Reader.TryRead(out msg)) break;
                await Task.Delay(20);
            }
            Assert.NotNull(msg);
            Assert.Equal("delta", msg!.Type);
            Assert.Equal(Channels.PnlMe, msg.Channel);
            var dto = Assert.IsType<PnlTodayDto>(msg.Data);
            var unr = Assert.Single(dto.Unrealized);
            Assert.Equal("PETR4", unr.Symbol);
            Assert.Equal(31m, unr.RefPrice);
            Assert.Equal(100, unr.Position);
            Assert.Equal(30m, unr.AvgPrice);
            Assert.Equal((31m - 30m) * 100, unr.Value);
            Assert.Equal((31m - 30m) * 100, dto.TotalUnrealized);
        }
        finally
        {
            await fanOut.StopAsync(CancellationToken.None);
        }
    }
}
