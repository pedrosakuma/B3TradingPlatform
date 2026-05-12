using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// RFC §4.2 — durability semantics. The contract under test is the
/// "ack-after-enqueue+apply" rule: for any sequence of events that
/// completed <see cref="EventDispatcher.Dispatch"/> AND were flushed
/// to the WAL prior to a synthetic crash, recovery
/// (<see cref="PersistenceRecovery"/>) must rebuild byte-identical
/// in-memory state from the WAL alone.
///
/// <para>The generator emits a randomised mixed workload of submits
/// and execution reports across multiple owners + symbols, then picks
/// a synthetic "crash point" <c>K ∈ [0, N]</c>. The harness dispatches
/// events 1..K, flushes (sealing the ack horizon at K), disposes the
/// store (simulating a process kill that loses any in-memory state),
/// then reopens with fresh state and runs recovery. A parallel
/// "oracle" replays exactly the same first <c>K</c> events into a
/// second independent state graph; recovered and oracle states must
/// match on every observable dimension (orders, ownership, positions,
/// ClOrdId watermark).</para>
///
/// <para>Determinism: the property uses a fixed FsCheck seed and the
/// underlying <see cref="FileEventStore"/> is configured with
/// <c>FsyncOnFlush = false</c>; the harness explicitly awaits
/// <see cref="FileEventStore.FlushAsync"/> before disposing, which
/// drains the writer channel — so the ack horizon for the test is
/// "everything dispatched-and-flushed", matching the §4.2 wording.
/// FsCheck prints the seed on failure for byte-identical replay.</para>
/// </summary>
[Properties(
    Arbitrary = new[] { typeof(PropertyDurabilityTests.DurabilityGenerators) },
    MaxTest = 100,
    QuietOnSuccess = true)]
public class PropertyDurabilityTests
{
    /// <summary>
    /// §4.2 — for any generated event sequence and crash point K,
    /// recovery rebuilds exactly the state produced by replaying the
    /// first K events. K=0 covers the empty-WAL edge; K=N covers
    /// the no-loss edge. Mid-K covers the "crash mid-stream" case.
    /// </summary>
    [Property(DisplayName = "§4.2 recovery rebuilds exactly the ack'd-and-flushed prefix")]
    public Property Durability_Recovery_RebuildsExactlyAckedAndFlushedPrefix(DurabilityWorkload workload)
    {
        using var live = new TempDir();
        var (events, crashAtK) = (workload.Events, workload.CrashAtK);

        ApplyAndFlushFirstKEvents(live.Path, events, crashAtK);

        var recovered = OpenAndRecover(live.Path);
        var oracle = BuildOracleByDirectReplay(events, crashAtK);

        return StatesMatch(recovered, oracle)
            .Label($"N={events.Count}; K={crashAtK}; submits={events.Count(e => e.Kind == DurEventKind.Submit)}; ers={events.Count(e => e.Kind == DurEventKind.ExecutionReport)}");
    }

    /// <summary>
    /// RFC stress floor: 500-event workload sampled at 25 evenly-spaced
    /// crash points. Pinned as a deterministic <see cref="FactAttribute"/>
    /// so a CI regression surfaces even if no property draw happens to
    /// land on this corner. Runtime: ~3s on a CI box.
    /// </summary>
    [Fact(DisplayName = "§4.2 stress: 500 events × 25 crash points; recovery == oracle")]
    public void Stress_Durability_500Events_25CrashPoints_RecoveryMatchesOracle()
    {
        var rng = new Random(0xD0EDA71L.GetHashCode());
        var events = GenerateMixedEvents(rng, eventCount: 500, ownerCount: 8, symbolCount: 4);

        for (var step = 0; step <= 24; step++)
        {
            var crashAtK = (int)((long)events.Count * step / 24);
            using var live = new TempDir();
            ApplyAndFlushFirstKEvents(live.Path, events, crashAtK);
            var recovered = OpenAndRecover(live.Path);
            var oracle = BuildOracleByDirectReplay(events, crashAtK);
            var (ok, label) = StatesMatchCore(recovered, oracle);
            Assert.True(ok, $"recovery diverged at crash point K={crashAtK} of {events.Count}: {label}");
        }
    }

    // ---- harness ---------------------------------------------------------

    private static void ApplyAndFlushFirstKEvents(string root, IReadOnlyList<DurEvent> events, int k)
    {
        var opts = NewOpts(root);
        var store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance);
        try
        {
            var graph = BuildLiveGraph(store);
            for (var i = 0; i < k; i++) DispatchEvent(graph, events[i]);
            store.FlushAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static OracleState OpenAndRecover(string root)
    {
        var opts = NewOpts(root);
        var store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance);
        try
        {
            var (book, positions, ownership, clOrdIds, replayer) = BuildRecoveryGraph(store);
            var snapStore = new SnapshotStore(root, opts.FirmId);
            var snapshotter = new StateSnapshotter(book, positions, new KillSwitchService(),
                new SymbolHaltService(), new SessionPhaseService(), clOrdIds, ownership,
                new AlgoBook(), new AlgoIdRegistry(), new CashLedger());
            var recovery = new PersistenceRecovery(store, snapshotter, replayer, snapStore,
                NullLogger<PersistenceRecovery>.Instance);
            recovery.RunAsync().GetAwaiter().GetResult();
            return Capture(book, ownership, positions, clOrdIds);
        }
        finally
        {
            store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static OracleState BuildOracleByDirectReplay(IReadOnlyList<DurEvent> events, int k)
    {
        // A parallel state graph that consumes the WalEvent stream
        // directly via EventReplayer.Apply — bypassing the WAL entirely.
        // This is the ground truth: "what state should K acked events
        // produce?" — independent of file I/O, channel draining, or
        // segment rotation.
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var positions = new PositionKeeper();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var processor = new ExecutionReportProcessor(ownership, book, positions,
            new NoOpExecutionEventSink(), new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        var replayer = new EventReplayer(book, ownership, new KillSwitchService(),
            new SymbolHaltService(), new SessionPhaseService(), processor, new AlgoBook(),
            clOrdIds, new AlgoIdRegistry());

        for (var i = 0; i < k; i++)
            replayer.Apply(ToWal(events[i]));

        return Capture(book, ownership, positions, clOrdIds);
    }

    private static (WorkingOrderBook, PositionKeeper, OrderOwnershipMap, ClOrdIdPrefixRegistry,
        EventReplayer) BuildRecoveryGraph(IEventStore store)
    {
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var positions = new PositionKeeper();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var processor = new ExecutionReportProcessor(ownership, book, positions,
            new NoOpExecutionEventSink(), new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        var replayer = new EventReplayer(book, ownership, new KillSwitchService(),
            new SymbolHaltService(), new SessionPhaseService(), processor, new AlgoBook(),
            clOrdIds, new AlgoIdRegistry());
        return (book, positions, ownership, clOrdIds, replayer);
    }

    private static LiveGraph BuildLiveGraph(IEventStore store)
    {
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var positions = new PositionKeeper();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var processor = new ExecutionReportProcessor(ownership, book, positions,
            new NoOpExecutionEventSink(), new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        var dispatcher = new EventDispatcher(store);
        return new LiveGraph(dispatcher, book, ownership, positions, processor, clOrdIds);
    }

    private static void DispatchEvent(LiveGraph g, DurEvent ev)
    {
        if (ev.Kind == DurEventKind.Submit)
        {
            var owner = new EndClientId(ev.Owner);
            g.Dispatcher.Dispatch(
                new OrderSubmittedEvent
                {
                    ClOrdId = ev.ClOrdId,
                    EndClientId = ev.Owner,
                    FirmId = "TEST",
                    Symbol = ev.Symbol,
                    SecurityId = 4321UL,
                    Side = OrderSide.Buy.ToString(),
                    Type = OrderType.Limit.ToString(),
                    Quantity = ev.Quantity,
                    Price = ev.Price,
                },
                () =>
                {
                    g.Book.TryAdd(new Order(ev.ClOrdId, owner, ev.Symbol, 4321UL,
                        OrderSide.Buy, OrderType.Limit, ev.Quantity, ev.Price));
                    g.Ownership.Register(ev.ClOrdId, owner);
                    g.ClOrdIds.AdvanceCounterTo(owner, ev.ClOrdId);
                });
        }
        else
        {
            g.Dispatcher.Dispatch(
                new ExecutionReportReceivedEvent
                {
                    ClOrdId = ev.ClOrdId,
                    ExecKind = ev.ExecKind.ToString(),
                    LeavesQuantity = ev.LeavesQuantity,
                    CumulativeQuantity = ev.CumulativeQuantity,
                    LastQuantity = ev.LastQuantity,
                    LastPrice = ev.Price,
                    Synthetic = false,
                },
                () => g.Processor.Apply(ev.ClOrdId, ev.ExecKind, ev.LeavesQuantity,
                    ev.CumulativeQuantity, ev.LastQuantity, ev.Price, null));
        }
    }

    private static WalEvent ToWal(DurEvent ev) => ev.Kind == DurEventKind.Submit
        ? new OrderSubmittedEvent
        {
            ClOrdId = ev.ClOrdId,
            EndClientId = ev.Owner,
            FirmId = "TEST",
            Symbol = ev.Symbol,
            SecurityId = 4321UL,
            Side = OrderSide.Buy.ToString(),
            Type = OrderType.Limit.ToString(),
            Quantity = ev.Quantity,
            Price = ev.Price,
        }
        : new ExecutionReportReceivedEvent
        {
            ClOrdId = ev.ClOrdId,
            ExecKind = ev.ExecKind.ToString(),
            LeavesQuantity = ev.LeavesQuantity,
            CumulativeQuantity = ev.CumulativeQuantity,
            LastQuantity = ev.LastQuantity,
            LastPrice = ev.Price,
            Synthetic = false,
        };

    // ---- oracle comparison ----------------------------------------------

    private static OracleState Capture(WorkingOrderBook book, OrderOwnershipMap ownership,
        PositionKeeper positions, ClOrdIdPrefixRegistry clOrdIds)
    {
        var snap = clOrdIds.Snapshot();
        var orders = book.Snapshot()
            .OrderBy(o => o.ClOrdId)
            .Select(o => new OracleOrder(o.ClOrdId, o.EndClientId, o.Symbol,
                o.Quantity, o.Price, o.CumulativeQuantity, o.LeavesQuantity, o.Status))
            .ToList();
        var allOwners = orders.Select(o => o.Owner).Distinct().OrderBy(o => o).ToList();
        var poss = allOwners
            .SelectMany(o => positions.ForEndClient(new EndClientId(o))
                .Select(p => new OraclePosition(o, p.Symbol, p.NetQuantity)))
            .OrderBy(p => p.Owner).ThenBy(p => p.Symbol)
            .ToList();
        var clCounters = snap.Counters
            .Select(c => (c.EndClientId, c.Counter))
            .OrderBy(t => t.EndClientId)
            .ToList();
        return new OracleState(orders, poss, clCounters);
    }

    private static (bool Ok, string Label) StatesMatchCore(OracleState a, OracleState b)
    {
        var ok = a.Orders.SequenceEqual(b.Orders)
            && a.Positions.SequenceEqual(b.Positions)
            && a.ClOrdCounters.SequenceEqual(b.ClOrdCounters);
        var label = $"orders={a.Orders.Count}/{b.Orders.Count}; positions={a.Positions.Count}/{b.Positions.Count}; counters={a.ClOrdCounters.Count}/{b.ClOrdCounters.Count}";
        return (ok, label);
    }

    private static Property StatesMatch(OracleState a, OracleState b)
    {
        var (ok, label) = StatesMatchCore(a, b);
        return ok.Label(label);
    }

    // ---- generators ------------------------------------------------------

    public sealed record DurabilityWorkload(IReadOnlyList<DurEvent> Events, int CrashAtK);
    public sealed record DurEvent(DurEventKind Kind, string Owner, string Symbol,
        ulong ClOrdId, long Quantity, decimal Price,
        ExecKind ExecKind, long LeavesQuantity, long CumulativeQuantity, long LastQuantity);
    public enum DurEventKind { Submit, ExecutionReport }

    public static class DurabilityGenerators
    {
        // (events 1..60) × (crashAtK ∈ [0..N]). Bounded so that the
        // entire property completes well under the 30s suite budget on
        // CI (≈12s for 100 draws at N=60 average).
        public static Arbitrary<DurabilityWorkload> Workload() =>
            Arb.From(
                from n in Gen.Choose(1, 60)
                from seed in Gen.Choose(int.MinValue, int.MaxValue)
                from kFrac in Gen.Choose(0, 100)
                select Build(n, seed, kFrac),
                Shrink);

        private static DurabilityWorkload Build(int n, int seed, int kFrac)
        {
            var rng = new Random(seed);
            var events = GenerateMixedEvents(rng, eventCount: n, ownerCount: 3, symbolCount: 2);
            var k = (int)((long)n * kFrac / 100);
            return new DurabilityWorkload(events, k);
        }

        private static IEnumerable<DurabilityWorkload> Shrink(DurabilityWorkload w)
        {
            // Halve the event list (keep prefix). Standard FsCheck shape:
            // shrink toward the smallest reproducer.
            if (w.Events.Count > 1)
                yield return w with
                {
                    Events = w.Events.Take(w.Events.Count / 2).ToList(),
                    CrashAtK = Math.Min(w.CrashAtK, w.Events.Count / 2),
                };
            if (w.CrashAtK > 0)
                yield return w with { CrashAtK = w.CrashAtK / 2 };
            if (w.CrashAtK < w.Events.Count)
                yield return w with { CrashAtK = w.CrashAtK + 1 };
        }
    }

    private static List<DurEvent> GenerateMixedEvents(Random rng, int eventCount, int ownerCount, int symbolCount)
    {
        var owners = Enumerable.Range(0, ownerCount).Select(i => $"owner{i}").ToArray();
        var symbols = Enumerable.Range(0, symbolCount).Select(i => $"SYM{i}").ToArray();
        var perOwnerNextClOrdCounter = new Dictionary<string, ulong>();
        for (var i = 0; i < ownerCount; i++)
        {
            // Bits 40..60 = per-deployment prefix; we synthesise unique
            // packed ClOrdIds whose counter portion (bottom 40 bits) is
            // monotonic per owner. Matches ClOrdIdPrefixRegistry.Generate
            // layout so AdvanceCounterTo (which validates prefix ranges)
            // accepts them.
            perOwnerNextClOrdCounter[owners[i]] = 1;
        }
        var openOrdersByOwner = new Dictionary<string, List<(ulong ClOrdId, string Symbol, long Leaves)>>();
        foreach (var o in owners) openOrdersByOwner[o] = new();

        var events = new List<DurEvent>(eventCount);
        for (var step = 0; step < eventCount; step++)
        {
            var owner = owners[rng.Next(owners.Length)];
            // 60% submits while empty, 40% ER when open orders exist.
            var preferEr = openOrdersByOwner[owner].Count > 0 && rng.NextDouble() < 0.5;
            if (preferEr)
            {
                var idx = rng.Next(openOrdersByOwner[owner].Count);
                var (clOrdId, sym, leaves) = openOrdersByOwner[owner][idx];
                var fill = Math.Max(1, leaves / 2);
                var leavesAfter = leaves - fill;
                var kind = leavesAfter == 0 ? ExecKind.Fill : ExecKind.PartialFill;
                var prior = events.Where(e => e.ClOrdId == clOrdId && e.Kind == DurEventKind.ExecutionReport)
                    .Sum(e => e.LastQuantity);
                var cum = prior + fill;
                events.Add(new DurEvent(DurEventKind.ExecutionReport, owner, sym,
                    clOrdId, 0, 30m + (clOrdId % 7), kind, leavesAfter, cum, fill));
                if (leavesAfter == 0) openOrdersByOwner[owner].RemoveAt(idx);
                else openOrdersByOwner[owner][idx] = (clOrdId, sym, leavesAfter);
            }
            else
            {
                var ownerIdx = Array.IndexOf(owners, owner);
                var counter = perOwnerNextClOrdCounter[owner]++;
                var clOrdId = ((ulong)ownerIdx << ClOrdIdPrefixRegistry.CounterBits) | counter;
                var sym = symbols[rng.Next(symbols.Length)];
                var qty = 10 * (1 + rng.Next(5));
                events.Add(new DurEvent(DurEventKind.Submit, owner, sym, clOrdId, qty,
                    30m + (clOrdId % 7), ExecKind.New, 0, 0, 0));
                openOrdersByOwner[owner].Add((clOrdId, sym, qty));
            }
        }
        return events;
    }

    // ---- supporting types ------------------------------------------------

    private sealed record OracleState(
        IReadOnlyList<OracleOrder> Orders,
        IReadOnlyList<OraclePosition> Positions,
        IReadOnlyList<(string EndClientId, long Counter)> ClOrdCounters);
    private sealed record OracleOrder(ulong ClOrdId, string Owner, string Symbol,
        long Quantity, decimal? Price, long CumulativeQuantity, long LeavesQuantity, string Status);
    private sealed record OraclePosition(string Owner, string Symbol, long NetQuantity);

    private sealed record LiveGraph(EventDispatcher Dispatcher, WorkingOrderBook Book,
        OrderOwnershipMap Ownership, PositionKeeper Positions,
        ExecutionReportProcessor Processor, ClOrdIdPrefixRegistry ClOrdIds);

    private static PersistenceOptions NewOpts(string root) => new()
    {
        DataDirectory = root,
        FirmId = "test",
        ChannelCapacity = 4096,
        GroupCommitMaxRecords = 64,
        GroupCommitWindow = TimeSpan.FromMilliseconds(2),
        FsyncOnFlush = false,
    };

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "b3tp-prop-dur-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
