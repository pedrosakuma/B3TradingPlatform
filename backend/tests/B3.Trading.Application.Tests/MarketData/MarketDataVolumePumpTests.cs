using B3.Trading.Application.MarketData;
using Xunit;

namespace B3.Trading.Application.Tests.MarketData;

/// <summary>
/// Pass-1 review (#294) P1#1A. The volume-curve estimator must see
/// every live trade for the symbols the VWAP engine cares about. These
/// tests assert the wiring shape — synthesise <see cref="MarketTrade"/>
/// events through a fake <see cref="IMarketDataSubscriber"/> and check
/// they accrue into the estimator's per-symbol buckets.
/// </summary>
public class MarketDataVolumePumpTests
{
    [Fact]
    public async Task Trades_RaisedBySubscriber_AccrueIntoEstimator()
    {
        var subscriber = new FakeSubscriber();
        var estimator = new VolumeCurveEstimator();
        var pump = new MarketDataVolumePump(subscriber, estimator);
        await pump.StartAsync(default);

        var t0 = new DateTimeOffset(DateTime.UtcNow.Date.AddHours(13), TimeSpan.Zero);
        subscriber.RaiseTrade(new MarketTrade("PETR4", 1UL, 30m, 100L, t0));
        subscriber.RaiseTrade(new MarketTrade("PETR4", 1UL, 31m, 200L, t0.AddMinutes(1)));
        subscriber.RaiseTrade(new MarketTrade("VALE3", 2UL, 60m, 50L, t0));

        Assert.Equal(300, estimator.VolumeBetween("PETR4", t0, t0.AddMinutes(5)));
        Assert.Equal(50, estimator.VolumeBetween("VALE3", t0, t0.AddMinutes(5)));

        // After StopAsync the pump unsubscribes — further trades should
        // not accrue. Catches a regression where the handler is wired
        // twice (StartAsync re-adds) or never detached.
        await pump.StopAsync(default);
        subscriber.RaiseTrade(new MarketTrade("PETR4", 1UL, 30m, 999L, t0.AddMinutes(2)));
        Assert.Equal(300, estimator.VolumeBetween("PETR4", t0, t0.AddMinutes(5)));
    }

    [Fact]
    public async Task NonPositiveQty_IsIgnored_BySubscriberContract()
    {
        // The estimator already guards against non-positive qty; this
        // exercises the integrated path so a future refactor that drops
        // the guard surfaces here.
        var subscriber = new FakeSubscriber();
        var estimator = new VolumeCurveEstimator();
        var pump = new MarketDataVolumePump(subscriber, estimator);
        await pump.StartAsync(default);

        var t0 = new DateTimeOffset(DateTime.UtcNow.Date.AddHours(13), TimeSpan.Zero);
        subscriber.RaiseTrade(new MarketTrade("PETR4", 1UL, 30m, 0L, t0));
        subscriber.RaiseTrade(new MarketTrade("PETR4", 1UL, 30m, -10L, t0));

        Assert.Equal(0, estimator.VolumeBetween("PETR4", t0, t0.AddMinutes(5)));
    }

    [Fact]
    public async Task EnsureSubscribedAsync_IsIdempotent_OneSdkCallPerSymbol()
    {
        // Pass-2 review (#294) P1. EnsureSubscribedAsync is the demand-
        // subscribe path used by AlgoEngine.OnCreatedAsync for VWAP
        // parents. Repeated calls (reactor re-evaluation, several VWAP
        // parents on the same symbol) must collapse to exactly one
        // SDK Subscribe per (symbol, process).
        var subscriber = new FakeSubscriber();
        var estimator = new VolumeCurveEstimator();
        var pump = new MarketDataVolumePump(subscriber, estimator);

        await pump.EnsureSubscribedAsync("PETR4");
        await pump.EnsureSubscribedAsync("PETR4");
        await pump.EnsureSubscribedAsync("petr4"); // case-insensitive dedup
        await pump.EnsureSubscribedAsync("PETR4");

        Assert.Equal(1, subscriber.SubscribeCallsFor("PETR4"));
        Assert.Equal(1, subscriber.TotalSubscribeCalls);
    }

    [Fact]
    public async Task EnsureSubscribedAsync_DistinctSymbols_SubscribeIndependently()
    {
        var subscriber = new FakeSubscriber();
        var estimator = new VolumeCurveEstimator();
        var pump = new MarketDataVolumePump(subscriber, estimator);

        await pump.EnsureSubscribedAsync("PETR4");
        await pump.EnsureSubscribedAsync("VALE3");
        await pump.EnsureSubscribedAsync("PETR4"); // duplicate of first
        await pump.EnsureSubscribedAsync("ITUB4");

        Assert.Equal(1, subscriber.SubscribeCallsFor("PETR4"));
        Assert.Equal(1, subscriber.SubscribeCallsFor("VALE3"));
        Assert.Equal(1, subscriber.SubscribeCallsFor("ITUB4"));
        Assert.Equal(3, subscriber.TotalSubscribeCalls);
    }

    [Fact]
    public async Task EnsureSubscribedAsync_NullOrWhitespace_IsNoOp()
    {
        var subscriber = new FakeSubscriber();
        var pump = new MarketDataVolumePump(subscriber, new VolumeCurveEstimator());

        await pump.EnsureSubscribedAsync("");
        await pump.EnsureSubscribedAsync("   ");
        await pump.EnsureSubscribedAsync(null!);

        Assert.Equal(0, subscriber.TotalSubscribeCalls);
    }

    [Fact]
    public async Task EnsureSubscribedAsync_SdkThrows_KeepsDedupAndDoesNotPropagate()
    {
        // SDK failures must not abort the VWAP creation path — the SDK's
        // auto-resubscribe-on-reconnect will retry. The dedup marker is
        // kept so we don't hammer the SDK on every reactor tick.
        var subscriber = new FakeSubscriber { ThrowOnSubscribe = true };
        var pump = new MarketDataVolumePump(subscriber, new VolumeCurveEstimator());

        await pump.EnsureSubscribedAsync("PETR4");
        await pump.EnsureSubscribedAsync("PETR4");

        Assert.Equal(1, subscriber.SubscribeCallsFor("PETR4"));
    }

    private sealed class FakeSubscriber : IMarketDataSubscriber
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _subs =
            new(StringComparer.OrdinalIgnoreCase);

        public bool ThrowOnSubscribe { get; init; }
        public int TotalSubscribeCalls => _subs.Values.Sum();
        public int SubscribeCallsFor(string symbol) =>
            _subs.TryGetValue(symbol, out var n) ? n : 0;

        public MarketDataConnectionState State => MarketDataConnectionState.Connected;
        public long DroppedEventCount => 0;
        public event Action<MarketTrade>? Trade;
#pragma warning disable CS0067 // unused in this test
        public event Action<MarketInfoSnapshot>? InfoSnapshot;
        public event Action<MarketDataConnectionState>? ConnectionStateChanged;
        public event Action<MarketSubscribeError>? SubscribeError;
        public event Action<MarketTheoreticalOpening>? TheoreticalOpening;
        public event Action<MarketAuctionImbalance>? AuctionImbalance;
        public event Action<MarketAuctionPrint>? AuctionPrint;
#pragma warning restore CS0067

        public void RaiseTrade(MarketTrade t) => Trade?.Invoke(t);
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask SubscribeAsync(string symbol, CancellationToken ct = default)
        {
            _subs.AddOrUpdate(symbol, 1, (_, n) => n + 1);
            if (ThrowOnSubscribe)
                throw new InvalidOperationException("simulated SDK Subscribe failure");
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
