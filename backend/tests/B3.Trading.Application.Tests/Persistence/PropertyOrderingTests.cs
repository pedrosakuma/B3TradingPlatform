using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// RFC §7.4 — property-based ordering tests for the post-P3/P4/P6
/// dispatcher. Two invariants are pinned across randomised concurrent
/// workloads:
/// <list type="bullet">
///   <item><b>§4.1 Total WAL ordering.</b> For any concurrent stream of
///   <see cref="EventDispatcher.Dispatch(WalEvent, Action{ExecutionFanOut})"/>
///   calls, the per-sink observation sequence is a strictly-increasing
///   subsequence of WAL seq order. Because the workloads here always
///   capture exactly one ER per dispatch and target every sink, the
///   per-sink seq sequence is exactly <c>1..N</c>.</item>
///   <item><b>§4.3 Snapshot consistency.</b> For any concurrent stream
///   of dispatches plus concurrent <c>Capture()</c> reads, every
///   captured snapshot is a prefix of the WAL: no half-applied event,
///   no missing applied event. We pin this on a single order whose
///   <c>CumulativeQuantity</c> is a deterministic function of seq —
///   any leak would surface as <c>Cum != Seq − 1</c>.</item>
/// </list>
///
/// <para>FsCheck.Xunit drives ≥100 randomised cases per property; the
/// generators cover edge corners (single thread, max thread count,
/// single event, max events). On failure FsCheck prints the seed in
/// the test output for byte-identical reproduction. A separate
/// <see cref="Stress_WalOrdering_8Threads_200Events_PerSinkSeqMonotonic"/>
/// fact pins the explicit ≥8 × ≥200 stress configuration the RFC asks
/// for at every CI run.</para>
/// </summary>
[Properties(
    Arbitrary = new[] { typeof(PropertyOrderingTests.WorkloadGenerators) },
    MaxTest = 100,
    QuietOnSuccess = true)]
public class PropertyOrderingTests
{
    /// <summary>
    /// §4.1 — for any randomised <see cref="WalOrderingWorkload"/>,
    /// every registered sink observes exactly the WAL seqs <c>1..N</c>
    /// in strictly increasing order.
    /// </summary>
    [Property(DisplayName = "§4.1 per-sink seq order matches WAL append order")]
    public Property WalOrdering_PerSinkSequence_IsStrictlyIncreasingWalPrefix(WalOrderingWorkload workload)
    {
        var (threads, perThread) = (workload.Threads, workload.PerThread);
        var store = new RecordingStore();
        var ws = new RecordingSink(ExecutionFanOutTargets.WsHub);
        var bot = new RecordingSink(ExecutionFanOutTargets.BotRouter);
        var dispatcher = new EventDispatcher(store, new IExecutionFanOutSink[] { ws, bot });

        RunDispatchWorkload(dispatcher, threads, perThread);

        var expected = threads * perThread;
        return (CheckMonotonicWalPrefix(ws.Items, expected) && CheckMonotonicWalPrefix(bot.Items, expected))
            .Label($"workload=({threads}x{perThread}); ws.count={ws.Items.Count}; bot.count={bot.Items.Count}");
    }

    /// <summary>
    /// §4.3 — for any randomised <see cref="SnapshotWorkload"/>, every
    /// snapshot taken concurrently with the dispatch stream represents
    /// a strict WAL prefix. We pin the invariant on a single order
    /// whose <c>CumulativeQuantity</c> is a pure function of seq:
    /// after the seq-1 submit, every fill applied at seq <c>S</c>
    /// advances Cum to <c>S − 1</c>. A snapshot at <c>snap.Seq</c>
    /// must therefore observe <c>Cum == snap.Seq − 1</c> exactly. A
    /// leak (projection reading a live aggregate after lock release)
    /// would surface as <c>Cum &gt; snap.Seq − 1</c>; a missed apply
    /// would surface as <c>Cum &lt; snap.Seq − 1</c>.
    /// </summary>
    [Property(DisplayName = "§4.3 every snapshot is a WAL prefix")]
    public Property SnapshotConsistency_EverySnapshot_IsAStrictWalPrefix(SnapshotWorkload workload)
    {
        var (writers, perWriter, readers) = (workload.Writers, workload.PerWriter, workload.Readers);
        var (dispatcher, snapshotter, book, clOrdId) = BuildSnapshotHarness();

        var observed = RunDispatchPlusSnapshotWorkload(dispatcher, snapshotter, clOrdId, book, writers, perWriter, readers);

        var totalFills = (long)writers * perWriter;
        var finalSeqOk = dispatcher.CurrentSeq == totalFills + 1;
        var allObservedOk = observed.All(o => o.Cum == o.Seq - 1);

        return (finalSeqOk && allObservedOk)
            .Label($"workload=({writers}x{perWriter}, readers={readers}); finalSeq={dispatcher.CurrentSeq}; observed={observed.Count}; bad={observed.Count(o => o.Cum != o.Seq - 1)}");
    }

    /// <summary>
    /// RFC §7.4 mandates an explicit ≥8 × ≥200 stress configuration in
    /// addition to the randomised property. Pinned here as a
    /// deterministic <see cref="FactAttribute"/> so CI fails on
    /// regression even if a property seed never happens to draw the
    /// max-corner workload.
    /// </summary>
    [Fact(DisplayName = "§4.1 stress: 8 threads × 200 events; per-sink seq monotonic")]
    public void Stress_WalOrdering_8Threads_200Events_PerSinkSeqMonotonic()
    {
        const int threads = 8;
        const int perThread = 200;
        var store = new RecordingStore();
        var ws = new RecordingSink(ExecutionFanOutTargets.WsHub);
        var bot = new RecordingSink(ExecutionFanOutTargets.BotRouter);
        var dispatcher = new EventDispatcher(store, new IExecutionFanOutSink[] { ws, bot });

        RunDispatchWorkload(dispatcher, threads, perThread);

        Assert.True(CheckMonotonicWalPrefix(ws.Items, threads * perThread));
        Assert.True(CheckMonotonicWalPrefix(bot.Items, threads * perThread));
    }

    /// <summary>
    /// Snapshot-consistency analogue of the §4.1 stress test. 8 writers
    /// × 200 fills + 4 concurrent readers — exceeds the RFC §7.4 floor
    /// and runs in well under a second on the CI box.
    /// </summary>
    [Fact(DisplayName = "§4.3 stress: 8 writers × 200 fills + 4 readers; every snapshot is a WAL prefix")]
    public void Stress_SnapshotConsistency_8Writers_200Fills_4Readers()
    {
        const int writers = 8;
        const int perWriter = 200;
        const int readers = 4;
        var (dispatcher, snapshotter, book, clOrdId) = BuildSnapshotHarness();

        var observed = RunDispatchPlusSnapshotWorkload(dispatcher, snapshotter, clOrdId, book, writers, perWriter, readers);

        Assert.Equal((long)writers * perWriter + 1, dispatcher.CurrentSeq);
        Assert.NotEmpty(observed);
        Assert.All(observed, o => Assert.Equal(o.Seq - 1, o.Cum));
    }

    // ---- workload runners ------------------------------------------------

    private static void RunDispatchWorkload(EventDispatcher dispatcher, int threads, int perThread)
    {
        var workers = new Thread[threads];
        for (var t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    dispatcher.Dispatch(NewWalEvent(), fanOut => fanOut.Add(NewExecutionEvent()));
                }
            });
        }
        foreach (var w in workers) w.Start();
        foreach (var w in workers) w.Join();
    }

    private static List<(long Seq, long Cum)> RunDispatchPlusSnapshotWorkload(
        EventDispatcher dispatcher,
        StateSnapshotter snapshotter,
        ulong clOrdId,
        WorkingOrderBook book,
        int writers,
        int perWriter,
        int readers)
    {
        using var cts = new CancellationTokenSource();
        var writerTasks = new Task[writers];
        for (var w = 0; w < writers; w++)
        {
            writerTasks[w] = Task.Run(() =>
            {
                for (var i = 0; i < perWriter; i++)
                {
                    dispatcher.Dispatch(
                        new ExecutionReportReceivedEvent
                        {
                            ClOrdId = clOrdId,
                            ExecKind = ExecKind.PartialFill.ToString(),
                            LeavesQuantity = 0,
                            CumulativeQuantity = 0,
                            LastQuantity = 1,
                            LastPrice = 30m,
                            Synthetic = false,
                        },
                        () =>
                        {
                            if (!book.TryGet(clOrdId, out var ord) || ord is null) return;
                            ord.ApplyCumulativeFill(ord.CumulativeQuantity + 1);
                        });
                }
            });
        }

        var observed = new ConcurrentBag<(long Seq, long Cum)>();
        var readerTasks = new Task[readers];
        for (var r = 0; r < readers; r++)
        {
            readerTasks[r] = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    RawPlatformSnapshot? raw = null;
                    dispatcher.WithSnapshotLock(seq => raw = snapshotter.CaptureRaw(seq));
                    if (raw is null) continue;
                    var snap = StateSnapshotter.Project(raw);
                    var ord = snap.WorkingOrders.FirstOrDefault(o => o.ClOrdId == clOrdId);
                    if (ord is null) continue; // pre-submit; only possible if reader started before submit (we don't here).
                    observed.Add((snap.Seq, ord.CumulativeQuantity));
                }
            });
        }

        // Ensure readers are always cancelled, even if a writer task
        // faults — otherwise the busy-spin reader loop would survive
        // past the failing test and burn CPU for the rest of the run.
        try
        {
            Task.WaitAll(writerTasks);
        }
        finally
        {
            cts.Cancel();
            try { Task.WaitAll(readerTasks); } catch { /* surface writer fault, not reader cancellation */ }
        }
        return observed.ToList();
    }

    private static (EventDispatcher Dispatcher, StateSnapshotter Snapshotter, WorkingOrderBook Book, ulong ClOrdId) BuildSnapshotHarness()
    {
        var store = new RecordingStore();
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var positions = new PositionKeeper();
        var snapshotter = new StateSnapshotter(book, positions, new KillSwitchService(),
            new SymbolHaltService(), new SessionPhaseService(),
            new ClOrdIdPrefixRegistry(), ownership, new AlgoBook(),
            new AlgoIdRegistry(), new CashLedger());
        var dispatcher = new EventDispatcher(store);

        var alice = new EndClientId("alice");
        const ulong clOrdId = 1UL;
        const long quantity = 10_000_000L;

        // Seq 1: submit. After this, CurrentSeq == 1, CumQ == 0. The
        // invariant Cum == Seq − 1 holds trivially at seq 1.
        dispatcher.Dispatch(
            new OrderSubmittedEvent
            {
                ClOrdId = clOrdId,
                EndClientId = "alice",
                FirmId = "TEST",
                Symbol = "PETR4",
                SecurityId = 4321UL,
                Side = "Buy",
                Type = "Limit",
                Quantity = quantity,
                Price = 30m,
            },
            () =>
            {
                book.TryAdd(new Order(clOrdId, alice, "PETR4", 4321UL,
                    OrderSide.Buy, OrderType.Limit, quantity, 30m));
                ownership.Register(clOrdId, alice);
            });

        return (dispatcher, snapshotter, book, clOrdId);
    }

    // ---- invariant checks ------------------------------------------------

    private static bool CheckMonotonicWalPrefix(IReadOnlyList<(long Seq, ExecutionEvent Ev)> items, int expectedCount)
    {
        if (items.Count != expectedCount) return false;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Seq != i + 1) return false;
        }
        return true;
    }

    // ---- generators ------------------------------------------------------

    public sealed record WalOrderingWorkload(int Threads, int PerThread);

    public sealed record SnapshotWorkload(int Writers, int PerWriter, int Readers);

    public static class WorkloadGenerators
    {
        // §4.1 generator: covers (1..8) × (1..50). Includes the corners
        // FsCheck explicitly draws (smallest = 1×1; largest = 8×50)
        // plus the single-thread (T=1) and single-event (E=1) edges.
        // We supply an explicit shrinker so failing cases collapse
        // toward the smallest reproducer (Threads=1, PerThread=1)
        // rather than reporting the original randomly-sized workload —
        // `Gen.Choose(...).ToArbitrary()` would otherwise build a
        // shrinker-less Arbitrary (FsCheck 3 docs: "Shrink is not
        // supported for this type").
        public static Arbitrary<WalOrderingWorkload> WalOrdering() =>
            Arb.From(
                from t in Gen.Choose(1, 8)
                from e in Gen.Choose(1, 50)
                select new WalOrderingWorkload(t, e),
                ShrinkWalOrdering);

        // §4.3 generator: writers (1..6), perWriter (1..30), readers (0..3).
        // readers=0 exercises the "no concurrent readers" edge (invariant
        // holds vacuously); readers=3 exercises maximum read pressure.
        // Same shrinker rationale as WalOrdering above.
        public static Arbitrary<SnapshotWorkload> Snapshot() =>
            Arb.From(
                from w in Gen.Choose(1, 6)
                from e in Gen.Choose(1, 30)
                from r in Gen.Choose(0, 3)
                select new SnapshotWorkload(w, e, r),
                ShrinkSnapshot);

        private static IEnumerable<WalOrderingWorkload> ShrinkWalOrdering(WalOrderingWorkload w)
        {
            if (w.Threads > 1) yield return w with { Threads = w.Threads / 2 };
            if (w.Threads > 1) yield return w with { Threads = w.Threads - 1 };
            if (w.PerThread > 1) yield return w with { PerThread = w.PerThread / 2 };
            if (w.PerThread > 1) yield return w with { PerThread = w.PerThread - 1 };
        }

        private static IEnumerable<SnapshotWorkload> ShrinkSnapshot(SnapshotWorkload w)
        {
            if (w.Writers > 1) yield return w with { Writers = w.Writers / 2 };
            if (w.Writers > 1) yield return w with { Writers = w.Writers - 1 };
            if (w.PerWriter > 1) yield return w with { PerWriter = w.PerWriter / 2 };
            if (w.PerWriter > 1) yield return w with { PerWriter = w.PerWriter - 1 };
            if (w.Readers > 0) yield return w with { Readers = w.Readers - 1 };
        }
    }

    // ---- test doubles (mirrors EventDispatcherFanOutTests) ---------------

    private static WalEvent NewWalEvent() => new SymbolHaltToggledEvent
    {
        Symbol = "PETR4",
        Halted = false,
        ActorUserId = "t",
    };

    private static ExecutionEvent NewExecutionEvent() => new(
        Owner: new EndClientId("alice"),
        ClOrdId: 1UL,
        Symbol: "PETR4",
        Side: OrderSide.Buy,
        Status: OrderStatus.Working,
        Kind: ExecKind.New,
        LeavesQuantity: 100,
        CumulativeQuantity: 0,
        LastQuantity: 0,
        LastPrice: 0m,
        RejectReason: null,
        TimestampUtc: DateTimeOffset.UtcNow);

    private sealed class RecordingSink : IExecutionFanOutSink
    {
        private readonly List<(long Seq, ExecutionEvent Ev)> _items = new();
        public RecordingSink(ExecutionFanOutTargets target) => Target = target;
        public ExecutionFanOutTargets Target { get; }
        public IReadOnlyList<(long Seq, ExecutionEvent Ev)> Items => _items;
        public void Enqueue(long seq, ExecutionEvent ev) => _items.Add((seq, ev));
    }

    private sealed class RecordingStore : IEventStore
    {
        public List<(WalEvent Evt, byte[] Payload)> Appended { get; } = new();
        private long _seq;
        public long CurrentSeq => Interlocked.Read(ref _seq);
        public long Append(WalEvent evt) => Interlocked.Increment(ref _seq);
        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            Appended.Add((evt, preSerialisedPayload.ToArray()));
            return Interlocked.Increment(ref _seq);
        }
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
