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
    public async Task EnsureSubscribedAsync_SdkThrows_DoesNotPoisonCache_NextCallRetries()
    {
        // Pass-3 review (#294) P1. A failed initial Subscribe used to mark
        // the symbol in the dedup set BEFORE calling the SDK and swallow
        // the exception — so a single not-ready / quota / unknown-symbol
        // error at cold boot would permanently poison the cache and the
        // algo would never receive trades. New contract: on failure the
        // entry is cleared and the next EnsureSubscribedAsync(key) call
        // retries the SDK call. Exceptions remain swallowed so the
        // creation flow isn't broken.
        var subscriber = new FakeSubscriber { ThrowOnSubscribe = true };
        var pump = new MarketDataVolumePump(subscriber, new VolumeCurveEstimator());

        await pump.EnsureSubscribedAsync("PETR4");
        await pump.EnsureSubscribedAsync("PETR4");
        await pump.EnsureSubscribedAsync("PETR4");

        Assert.Equal(3, subscriber.SubscribeCallsFor("PETR4"));

        // Once the SDK recovers, the next call succeeds and the dedup
        // marker sticks — further calls short-circuit.
        subscriber.ThrowOnSubscribe = false;
        await pump.EnsureSubscribedAsync("PETR4");
        await pump.EnsureSubscribedAsync("PETR4");

        Assert.Equal(4, subscriber.SubscribeCallsFor("PETR4"));
    }

    [Fact]
    public async Task EnsureSubscribedAsync_ConcurrentFirstCall_IssuesExactlyOneSdkCall()
    {
        // Pass-3 review (#294) P1. Two threads calling EnsureSubscribedAsync
        // for the same not-yet-subscribed symbol must coalesce to exactly
        // one SDK Subscribe — Lazy<Task> guarantees the inner factory
        // (SubscribeOnceAsync) runs once even if GetOrAdd's value factory
        // is invoked multiple times under contention.
        var gate = new TaskCompletionSource();
        var subscriber = new FakeSubscriber { SubscribeGate = gate.Task };
        var pump = new MarketDataVolumePump(subscriber, new VolumeCurveEstimator());

        const int racers = 32;
        var tasks = new Task[racers];
        using var startBarrier = new Barrier(racers);
        for (var i = 0; i < racers; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                startBarrier.SignalAndWait();
                await pump.EnsureSubscribedAsync("PETR4");
            });
        }

        // Give racers a moment to all hit GetOrAdd before we let the SDK
        // call complete. Without the gate the first racer might finish
        // before the others arrive and the test would no longer exercise
        // the coalescing path.
        await Task.Delay(50);
        gate.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(1, subscriber.SubscribeCallsFor("PETR4"));
        Assert.Equal(1, subscriber.TotalSubscribeCalls);
    }

    private sealed class FakeSubscriber : IMarketDataSubscriber
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _subs =
            new(StringComparer.OrdinalIgnoreCase);

        public bool ThrowOnSubscribe { get; set; }
        public Task? SubscribeGate { get; init; }
        public int TotalSubscribeCalls => _subs.Values.Sum();
        public int SubscribeCallsFor(string symbol) =>
            _subs.TryGetValue(symbol, out var n) ? n : 0;

        public MarketDataConnectionState State => MarketDataConnectionState.Connected;
        public long DroppedEventCount => 0;
        #pragma warning disable CS0067
    public event Action<MarketTrade>? Trade;
#pragma warning disable CS0067 // unused in this test
        public event Action<MarketInfoSnapshot>? InfoSnapshot;
        public event Action<MarketDataConnectionState>? ConnectionStateChanged;
        public event Action<MarketSubscribeError>? SubscribeError;
        public event Action<MarketTheoreticalOpening>? TheoreticalOpening;
        public event Action<MarketAuctionImbalance>? AuctionImbalance;
        public event Action<MarketAuctionPrint>? AuctionPrint;
        public event Action<MarketBookSnapshot>? BookSnapshot;
        public event Action<MarketOrderAdded>? OrderAdded;
        public event Action<MarketOrderUpdated>? OrderUpdated;
        public event Action<MarketOrderDeleted>? OrderDeleted;
        public event Action<MarketBookCleared>? BookCleared;
    #pragma warning restore CS0067
#pragma warning restore CS0067

        public void RaiseTrade(MarketTrade t) => Trade?.Invoke(t);
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public async ValueTask SubscribeAsync(string symbol, CancellationToken ct = default)
        {
            _subs.AddOrUpdate(symbol, 1, (_, n) => n + 1);
            if (SubscribeGate is not null)
                await SubscribeGate.ConfigureAwait(false);
            if (ThrowOnSubscribe)
                throw new InvalidOperationException("simulated SDK Subscribe failure");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
