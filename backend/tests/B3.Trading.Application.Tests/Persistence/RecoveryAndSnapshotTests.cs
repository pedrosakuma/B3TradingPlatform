using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// End-to-end recovery tests: simulate a crash by disposing the store
/// after some events, reopening with fresh state objects, and asserting
/// the in-memory world matches what was logged.
/// </summary>
public class RecoveryAndSnapshotTests : IDisposable
{
    private readonly string _root;

    public RecoveryAndSnapshotTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "b3tp-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private PersistenceOptions Opts() => new()
    {
        DataDirectory = _root,
        FirmId = "test",
        ChannelCapacity = 1024,
        GroupCommitMaxRecords = 8,
        GroupCommitWindow = TimeSpan.FromMilliseconds(5),
        FsyncOnFlush = false,
    };

    [Fact]
    public async Task Recovery_FromWalAlone_ReproducesOrdersOwnershipPositionsAndKillSwitch()
    {
        // Phase 1: live session — append events through the dispatcher, mutate state.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, positions, killSwitch, ownership, _, dispatcher, processor, sink, _) = BuildState(store);

            // Submit two orders.
            DispatchSubmit(dispatcher, book, ownership, 1UL, "alice", "PETR4", OrderSide.Buy, 100, 30m);
            DispatchSubmit(dispatcher, book, ownership, 2UL, "alice", "PETR4", OrderSide.Buy, 50, 31m);

            // Fill the first one, partial-fill the second.
            DispatchEr(dispatcher, processor, 1UL, ExecKind.Fill, leaves: 0, cum: 100, last: 100, lastPx: 30m);
            DispatchEr(dispatcher, processor, 2UL, ExecKind.PartialFill, leaves: 20, cum: 30, last: 30, lastPx: 31m);

            // Toggle the kill switch on a firm.
            dispatcher.Dispatch(
                new KillSwitchToggledEvent { Scope = "firm", Target = "TEST", Killed = true },
                () => killSwitch.KillFirm("TEST"));

            await store.FlushAsync();
        }

        // Phase 2: cold boot — fresh state objects, recovery replays the WAL.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, positions, killSwitch, ownership, snapshotter, _, processor, _, algos) = BuildState(store);
            var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(), processor, algos);
            var recovery = new PersistenceRecovery(store,
                snapshotter,
                replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            // Working orders restored with execution state intact.
            Assert.True(book.TryGet(1UL, out var o1) && o1!.Status == OrderStatus.Filled);
            Assert.True(book.TryGet(2UL, out var o2) && o2!.Status == OrderStatus.PartiallyFilled);
            Assert.Equal(100, o1!.CumulativeQuantity);
            Assert.Equal(30, o2!.CumulativeQuantity);

            // Position rebuilt from fills.
            var pos = positions.ForEndClient(new EndClientId("alice")).Single();
            Assert.Equal("PETR4", pos.Symbol);
            Assert.Equal(130, pos.NetQuantity);

            // Kill-switch state restored.
            Assert.True(killSwitch.IsFirmKilled("TEST"));

            // Ownership restored — needed by the next ER that arrives for either order.
            Assert.True(ownership.TryResolve(1UL, out _));
        }
    }

    [Fact]
    public async Task Recovery_FromSnapshotPlusTail_SkipsEventsAlreadyInSnapshot()
    {
        // Phase 1: append 3 orders, snapshot, then append 2 more.
        long snapSeq;
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, _, _, ownership, snapshotter, dispatcher, _, _, _) = BuildState(store);
            for (var i = 1UL; i <= 3UL; i++)
                DispatchSubmit(dispatcher, book, ownership, i, "alice", "PETR4", OrderSide.Buy, 10, 30m);

            var snapStore = new SnapshotStore(_root, "test");
            PlatformSnapshot? snap = null;
            dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
            snapStore.Write(snap!);
            snapSeq = snap!.Seq;
            Assert.Equal(3, snapSeq);

            for (var i = 4UL; i <= 5UL; i++)
                DispatchSubmit(dispatcher, book, ownership, i, "alice", "PETR4", OrderSide.Buy, 10, 30m);
            await store.FlushAsync();
        }

        // Phase 2: cold boot from snapshot+tail.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, _, killSwitch, ownership, snapshotter, _, processor, _, algos) = BuildState(store);
            var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(), processor, algos);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"), NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            // All 5 orders should be present (3 from snapshot, 2 from tail replay).
            for (var i = 1UL; i <= 5UL; i++)
                Assert.True(book.TryGet(i, out _), $"ORD-{i} missing after snapshot+tail recovery.");
        }
    }

    [Fact]
    public async Task Snapshot_DoesNotIncludeFlatPositions()
    {
        await using var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance);
        var (book, positions, killSwitch, ownership, snapshotter, dispatcher, processor, _, _) = BuildState(store);
        DispatchSubmit(dispatcher, book, ownership, 1UL, "alice", "PETR4", OrderSide.Buy, 100, 30m);
        DispatchEr(dispatcher, processor, 1UL, ExecKind.Fill, leaves: 0, cum: 100, last: 100, lastPx: 30m);
        DispatchSubmit(dispatcher, book, ownership, 2UL, "alice", "PETR4", OrderSide.Sell, 100, 30m);
        DispatchEr(dispatcher, processor, 2UL, ExecKind.Fill, leaves: 0, cum: 100, last: 100, lastPx: 30m);

        PlatformSnapshot? snap = null;
        dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
        Assert.Empty(snap!.Positions);
    }

    private static (
        WorkingOrderBook,
        PositionKeeper,
        KillSwitchService,
        OrderOwnershipMap,
        StateSnapshotter,
        EventDispatcher,
        ExecutionReportProcessor,
        TestSink,
        AlgoBook) BuildState(IEventStore store)
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var killSwitch = new KillSwitchService();
        var ownership = new OrderOwnershipMap();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var algos = new AlgoBook();
        var sink = new TestSink();
        var processor = new ExecutionReportProcessor(ownership, book, positions, sink,
            new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);
        var snapshotter = new StateSnapshotter(book, positions, killSwitch, new SymbolHaltService(), clOrdIds, ownership, algos, new AlgoIdRegistry(), new CashLedger());
        var dispatcher = new EventDispatcher(store);
        return (book, positions, killSwitch, ownership, snapshotter, dispatcher, processor, sink, algos);
    }

    private static void DispatchSubmit(
        EventDispatcher d, WorkingOrderBook book, OrderOwnershipMap ownership,
        ulong clOrdId, string ec, string symbol, OrderSide side, long qty, decimal price)
    {
        var owner = new EndClientId(ec);
        d.Dispatch(
            new OrderSubmittedEvent
            {
                ClOrdId = clOrdId,
                EndClientId = ec,
                FirmId = "TEST",
                Symbol = symbol,
                SecurityId = 4321UL,
                Side = side.ToString(),
                Type = "Limit",
                Quantity = qty,
                Price = price,
            },
            () =>
            {
                book.TryAdd(new Order(clOrdId, owner, symbol, 4321UL, side, OrderType.Limit, qty, price));
                ownership.Register(clOrdId, owner);
            });
    }

    private static void DispatchEr(
        EventDispatcher d, ExecutionReportProcessor proc,
        ulong clOrdId, ExecKind kind, long leaves, long cum, long last, decimal lastPx)
    {
        d.Dispatch(
            new ExecutionReportReceivedEvent
            {
                ClOrdId = clOrdId,
                ExecKind = kind.ToString(),
                LeavesQuantity = leaves,
                CumulativeQuantity = cum,
                LastQuantity = last,
                LastPrice = lastPx,
                Synthetic = false,
            },
            () => proc.Apply(clOrdId, kind, leaves, cum, last, lastPx, null));
    }

    private sealed class TestSink : IExecutionEventSink
    {
        public List<ExecutionEvent> Events { get; } = new();
        public void Publish(ExecutionEvent evt) => Events.Add(evt);
    }
}
