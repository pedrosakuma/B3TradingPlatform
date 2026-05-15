using B3.Trading.Application.MarketData;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace B3.Trading.Application.Tests;

public class AuctionStateStoreTests
{
    private static AuctionStateStore Build(out FakeMarketDataSubscriber sub)
    {
        sub = new FakeMarketDataSubscriber();
        return new AuctionStateStore(sub, TimeProvider.System, NullLogger<AuctionStateStore>.Instance);
    }

    [Fact]
    public void Phase_unknown_for_unseen_symbol()
    {
        var store = Build(out _);
        Assert.Equal(TradingPhase.Unknown, store.GetPhase("PETR4"));
        Assert.Equal(TradingPhase.Unknown, store.GetPhase("anything"));
        Assert.Equal(TradingPhase.Unknown, store.GetPhase(""));
    }

    [Fact]
    public void TheoreticalOpening_transitions_to_OpeningCall_and_updates_top()
    {
        var store = Build(out var sub);
        var phases = new List<PhaseChange>();
        store.PhaseChanged += phases.Add;

        var t = DateTimeOffset.UtcNow;
        sub.RaiseTheoreticalOpening("PETR4", 30.5m, 1000, t);

        Assert.Equal(TradingPhase.OpeningCall, store.GetPhase("PETR4"));
        Assert.True(store.TryGetTop("PETR4", out var top));
        Assert.Equal(30.5m, top!.Top);
        Assert.Equal(1000, top.IndicativeMatchQty);
        Assert.Single(phases);
        Assert.Equal(TradingPhase.OpeningCall, phases[0].Phase);
    }

    [Fact]
    public void Repeated_TheoreticalOpening_does_not_re_emit_phase_change()
    {
        var store = Build(out var sub);
        var phases = new List<PhaseChange>();
        store.PhaseChanged += phases.Add;

        var t = DateTimeOffset.UtcNow;
        sub.RaiseTheoreticalOpening("PETR4", 30m, 100, t);
        sub.RaiseTheoreticalOpening("PETR4", 30.1m, 200, t);
        sub.RaiseTheoreticalOpening("PETR4", 30.2m, 300, t);

        Assert.Single(phases);
    }

    [Fact]
    public void AuctionImbalance_updates_top_state_without_phase_change()
    {
        var store = Build(out var sub);
        var phases = new List<PhaseChange>();
        store.PhaseChanged += phases.Add;
        var tops = new List<AuctionTopState>();
        store.ImbalanceUpdated += tops.Add;

        var t = DateTimeOffset.UtcNow;
        sub.RaiseAuctionImbalance("PETR4", 5000, OrderSide.Buy, t);

        Assert.True(store.TryGetTop("PETR4", out var top));
        Assert.Equal(5000, top!.Imbalance);
        Assert.Equal(OrderSide.Buy, top.ImbalanceSide);
        Assert.Empty(phases);
        Assert.Single(tops);
    }

    [Fact]
    public void AuctionPrint_Opening_transitions_to_Open_and_emits_print()
    {
        var store = Build(out var sub);
        var phases = new List<PhaseChange>();
        var prints = new List<AuctionPrint>();
        store.PhaseChanged += phases.Add;
        store.PrintReceived += prints.Add;

        var t = DateTimeOffset.UtcNow;
        sub.RaiseTheoreticalOpening("PETR4", 30m, 100, t);
        sub.RaiseAuctionPrint("PETR4", AuctionPrintKind.Opening, 30.25m, 5000, t);

        Assert.Equal(TradingPhase.Open, store.GetPhase("PETR4"));
        Assert.Equal(2, phases.Count);
        Assert.Equal(TradingPhase.OpeningCall, phases[0].Phase);
        Assert.Equal(TradingPhase.Open, phases[1].Phase);
        Assert.Single(prints);
        Assert.Equal(AuctionPrintKind.Opening, prints[0].Kind);
        Assert.Equal(30.25m, prints[0].Price);
    }

    [Fact]
    public void AuctionPrint_Closing_transitions_to_Close()
    {
        var store = Build(out var sub);
        var t = DateTimeOffset.UtcNow;
        sub.RaiseAuctionPrint("PETR4", AuctionPrintKind.Closing, 30.5m, 9000, t);
        Assert.Equal(TradingPhase.Close, store.GetPhase("PETR4"));
    }

    [Fact]
    public void Phase_is_case_insensitive_on_symbol()
    {
        var store = Build(out var sub);
        sub.RaiseTheoreticalOpening("PETR4", 30m, 100, DateTimeOffset.UtcNow);
        Assert.Equal(TradingPhase.OpeningCall, store.GetPhase("petr4"));
    }

    [Fact]
    public void SnapshotTops_returns_only_symbols_with_observed_state()
    {
        var store = Build(out var sub);
        var t = DateTimeOffset.UtcNow;
        sub.RaiseTheoreticalOpening("PETR4", 30m, 100, t);
        sub.RaiseAuctionImbalance("VALE3", 200, OrderSide.Sell, t);

        var snap = store.SnapshotTops();
        Assert.Equal(2, snap.Count);
        Assert.True(snap.ContainsKey("PETR4"));
        Assert.True(snap.ContainsKey("VALE3"));
    }

    [Fact]
    public async Task Concurrent_writers_do_not_lose_phase_transitions()
    {
        var store = Build(out var sub);
        const int threads = 32;
        const int perThread = 200;
        var symbols = Enumerable.Range(0, threads).Select(i => $"SYM{i:D2}").ToArray();

        // Each thread drives its own symbol through the full
        // OpeningCall → Open transition many times. Different symbols
        // ⇒ disjoint per-symbol locks ⇒ contention is only on the
        // outer ConcurrentDictionary.
        var tasks = symbols.Select(sym => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
            {
                sub.RaiseTheoreticalOpening(sym, 30m + i * 0.01m, 10 + i, DateTimeOffset.UtcNow);
                sub.RaiseAuctionImbalance(sym, 1000 + i, i % 2 == 0 ? OrderSide.Buy : OrderSide.Sell, DateTimeOffset.UtcNow);
            }
            sub.RaiseAuctionPrint(sym, AuctionPrintKind.Opening, 31m, 5000, DateTimeOffset.UtcNow);
        })).ToArray();

        await Task.WhenAll(tasks);

        foreach (var sym in symbols)
        {
            Assert.Equal(TradingPhase.Open, store.GetPhase(sym));
            Assert.True(store.TryGetTop(sym, out var top));
            Assert.NotNull(top);
        }
    }

    [Fact]
    public async Task Concurrent_writers_and_readers_remain_consistent()
    {
        var store = Build(out var sub);
        const int symCount = 16;
        var symbols = Enumerable.Range(0, symCount).Select(i => $"SYM{i:D2}").ToArray();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Continuous writer: every symbol gets a stream of imbalance
        // updates; concurrent readers snapshot them. Asserts there is
        // never a torn record (Symbol non-empty + Imbalance ≥ 0 since
        // we only write non-negative qtys).
        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!cts.IsCancellationRequested)
            {
                var sym = symbols[i % symCount];
                sub.RaiseAuctionImbalance(sym, i, OrderSide.Buy, DateTimeOffset.UtcNow);
                i++;
            }
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                foreach (var sym in symbols)
                {
                    if (store.TryGetTop(sym, out var top) && top is not null)
                    {
                        Assert.Equal(sym, top.Symbol);
                        Assert.True(top.Imbalance >= 0);
                    }
                }
            }
        })).ToArray();

        await Task.WhenAll(new[] { writer }.Concat(readers));
    }

    [Fact]
    public async Task StopAsync_unsubscribes_handlers()
    {
        var store = Build(out var sub);
        var phases = new List<PhaseChange>();
        store.PhaseChanged += phases.Add;

        sub.RaiseTheoreticalOpening("PETR4", 30m, 100, DateTimeOffset.UtcNow);
        Assert.Single(phases);

        await store.StopAsync(default);

        // After stop, additional events fired by the SDK are ignored.
        sub.RaiseAuctionPrint("PETR4", AuctionPrintKind.Opening, 30m, 100, DateTimeOffset.UtcNow);
        Assert.Single(phases);
    }
}
