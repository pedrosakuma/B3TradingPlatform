using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// #380 path B — session-version guard on recovery. Verifies that
/// <see cref="PersistenceRecovery"/> reacts to a firm whose live FIXP
/// SessionVerId has advanced past the verId the snapshot recorded.
///
/// <para>
/// Per <see href="https://github.com/pedrosakuma/B3TradingPlatform/issues/419">#419</see>
/// the reaction is two-tier: <see cref="Order.MarkStale"/> for
/// confirmed Working / PartiallyFilled orders (the venue typically
/// persists the book across FIXP session rolls — keep them visible
/// but gate Cancel/Modify until reconciliation), <see cref="Order.MarkCancelled"/>
/// for never-acked PendingNew orders (no venue record possible under
/// any session version).
/// </para>
/// </summary>
public class PersistenceRecoverySessionVerGuardTests : IDisposable
{
    private readonly string _root;

    public PersistenceRecoverySessionVerGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "b3tp-380-" + Guid.NewGuid().ToString("N"));
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

    private sealed class FakeFirmSessionStatusProvider(IReadOnlyList<FirmSessionStatus> snap)
        : IFirmSessionStatusProvider
    {
        public IReadOnlyList<FirmSessionStatus> Snapshot() => snap;
    }

    [Fact]
    public async Task RolledForward_RetiresAllWorkingOrdersForThatFirm()
    {
        // Phase 1: snapshot 2 firms with verId 5 each, 2 PendingNew orders each.
        // PendingNew = no venue ack ever received → cancel-fallback path.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, _, _, ownership, snapshotter, dispatcher, _, _, _) =
                BuildState(store, new FakeFirmSessionStatusProvider(new[]
                {
                    new FirmSessionStatus("FIRM01", "established", false, 5u),
                    new FirmSessionStatus("FIRM02", "established", false, 5u),
                }));

            DispatchSubmit(dispatcher, book, ownership, 1UL, "alice", "FIRM01", "PETR4");
            DispatchSubmit(dispatcher, book, ownership, 2UL, "alice", "FIRM01", "VALE3");
            DispatchSubmit(dispatcher, book, ownership, 10UL, "bob", "FIRM02", "PETR4");
            DispatchSubmit(dispatcher, book, ownership, 11UL, "bob", "FIRM02", "VALE3");

            var snapStore = new SnapshotStore(_root, "test");
            PlatformSnapshot? snap = null;
            dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
            snapStore.Write(snap!);

            // Sanity: dict captured per-firm verIds.
            Assert.Equal(5u, snap!.FirmSessionVerIds["FIRM01"]);
            Assert.Equal(5u, snap.FirmSessionVerIds["FIRM02"]);

            await store.FlushAsync();
        }

        // Phase 2: cold boot — provider reports FIRM01 advanced to 8,
        // FIRM02 still at 5. FIRM01 PendingNew orders take the
        // cancel-fallback (#419) since they were never acked; FIRM02
        // keeps everything.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var provider = new FakeFirmSessionStatusProvider(new[]
            {
                new FirmSessionStatus("FIRM01", "established", false, 8u),
                new FirmSessionStatus("FIRM02", "established", false, 5u),
            });
            var (book, _, killSwitch, ownership, snapshotter, _, processor, _, algos) =
                BuildState(store, provider);
            var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(),
                new SessionPhaseService(), processor, algos, new ClOrdIdPrefixRegistry(), new AlgoIdRegistry());
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"), NullLogger<PersistenceRecovery>.Instance,
                orders: book, ownership: ownership, firmSessionStatus: provider);

            await recovery.RunAsync();

            // PendingNew + session-roll → cancelled (no possible venue record).
            Assert.True(book.TryGet(1UL, out var o1) && o1!.Status == OrderStatus.Cancelled);
            Assert.False(o1!.IsStale);
            Assert.True(book.TryGet(2UL, out var o2) && o2!.Status == OrderStatus.Cancelled);
            Assert.False(o2!.IsStale);
            Assert.True(book.TryGet(10UL, out var o10) && o10!.Status == OrderStatus.PendingNew);
            Assert.True(book.TryGet(11UL, out var o11) && o11!.Status == OrderStatus.PendingNew);

            // Cancelled orders drop out of the "working" view.
            Assert.Empty(book.EnumerateForFirm("FIRM01"));
            Assert.Equal(2, book.EnumerateForFirm("FIRM02").Count);
        }
    }

    [Fact]
    public async Task RolledForward_ConfirmedOrders_AreStaled_NotCancelled()
    {
        // #419. Confirmed (Working / PartiallyFilled) orders survive a
        // session roll — the venue persists the book — but get the
        // staleness overlay so Cancel/Modify is gated at the API until
        // a real ER lifts the flag. Verifies the order stays visible
        // and keeps its pre-roll status.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, _, _, ownership, snapshotter, dispatcher, _, _, _) =
                BuildState(store, new FakeFirmSessionStatusProvider(new[]
                {
                    new FirmSessionStatus("FIRM01", "established", false, 5u),
                }));
            DispatchSubmit(dispatcher, book, ownership, 1UL, "alice", "FIRM01", "PETR4");
            // Confirm at the venue so the order transitions PendingNew → Working
            // (mirrors what an inbound OrderConfirmed ER would do during normal
            // operation, captured into the snapshot we're about to write).
            dispatcher.WithSnapshotLock(_ =>
            {
                Assert.True(book.TryGet(1UL, out var order));
                order!.MarkWorking();
            });

            var snapStore = new SnapshotStore(_root, "test");
            PlatformSnapshot? snap = null;
            dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
            snapStore.Write(snap!);
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var provider = new FakeFirmSessionStatusProvider(new[]
            {
                new FirmSessionStatus("FIRM01", "established", false, 8u),
            });
            var (book, _, killSwitch, ownership, snapshotter, _, processor, _, algos) =
                BuildState(store, provider);
            var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(),
                new SessionPhaseService(), processor, algos, new ClOrdIdPrefixRegistry(), new AlgoIdRegistry());
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"), NullLogger<PersistenceRecovery>.Instance,
                orders: book, ownership: ownership, firmSessionStatus: provider);

            await recovery.RunAsync();

            Assert.True(book.TryGet(1UL, out var o1));
            Assert.Equal(OrderStatus.Working, o1!.Status);
            Assert.True(o1.IsStale);
            Assert.Contains("session-rolled", o1.StaleReason ?? "");
            Assert.NotNull(o1.StaledAtUtc);

            // Stale orders MUST remain enumerable so positions/cash/blotter
            // reflect the live venue state until reconciliation completes.
            Assert.Single(book.EnumerateForFirm("FIRM01"));
        }
    }

    [Fact]
    public async Task EqualVerId_NoRetirement()
    {
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, _, _, ownership, snapshotter, dispatcher, _, _, _) =
                BuildState(store, new FakeFirmSessionStatusProvider(new[]
                {
                    new FirmSessionStatus("FIRM01", "established", false, 5u),
                }));
            DispatchSubmit(dispatcher, book, ownership, 1UL, "alice", "FIRM01", "PETR4");

            var snapStore = new SnapshotStore(_root, "test");
            PlatformSnapshot? snap = null;
            dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
            snapStore.Write(snap!);
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var provider = new FakeFirmSessionStatusProvider(new[]
            {
                new FirmSessionStatus("FIRM01", "established", false, 5u),
            });
            var (book, _, killSwitch, ownership, snapshotter, _, processor, _, algos) =
                BuildState(store, provider);
            var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(),
                new SessionPhaseService(), processor, algos, new ClOrdIdPrefixRegistry(), new AlgoIdRegistry());
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"), NullLogger<PersistenceRecovery>.Instance,
                orders: book, ownership: ownership, firmSessionStatus: provider);

            await recovery.RunAsync();

            Assert.True(book.TryGet(1UL, out var o1) && o1!.Status == OrderStatus.PendingNew);
        }
    }

    [Fact]
    public async Task NoProvider_NoRetirement()
    {
        // Mock/Stub composition: no IFirmSessionStatusProvider. Guard
        // must silently no-op rather than wipe the book.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, _, _, ownership, snapshotter, dispatcher, _, _, _) = BuildState(store, null);
            DispatchSubmit(dispatcher, book, ownership, 1UL, "alice", "FIRM01", "PETR4");

            var snapStore = new SnapshotStore(_root, "test");
            PlatformSnapshot? snap = null;
            dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
            snapStore.Write(snap!);

            Assert.Empty(snap!.FirmSessionVerIds);
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, _, killSwitch, ownership, snapshotter, _, processor, _, algos) =
                BuildState(store, null);
            var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(),
                new SessionPhaseService(), processor, algos, new ClOrdIdPrefixRegistry(), new AlgoIdRegistry());
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"), NullLogger<PersistenceRecovery>.Instance,
                orders: book, ownership: ownership, firmSessionStatus: null);

            await recovery.RunAsync();

            Assert.True(book.TryGet(1UL, out var o1) && o1!.Status == OrderStatus.PendingNew);
        }
    }

    [Fact]
    public async Task PreFieldSnapshot_NoRetirement()
    {
        // Legacy snapshot has no FirmSessionVerIds entry for the firm
        // (simulated by capturing without a provider). On warm restart
        // with a provider now wired, stored=0 baseline must NOT retire
        // anything — we can't distinguish a fresh session from "first
        // boot since the field was added".
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, _, _, ownership, snapshotter, dispatcher, _, _, _) = BuildState(store, null);
            DispatchSubmit(dispatcher, book, ownership, 1UL, "alice", "FIRM01", "PETR4");

            var snapStore = new SnapshotStore(_root, "test");
            PlatformSnapshot? snap = null;
            dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
            snapStore.Write(snap!);
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var provider = new FakeFirmSessionStatusProvider(new[]
            {
                new FirmSessionStatus("FIRM01", "established", false, 99u),
            });
            var (book, _, killSwitch, ownership, snapshotter, _, processor, _, algos) =
                BuildState(store, provider);
            var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(),
                new SessionPhaseService(), processor, algos, new ClOrdIdPrefixRegistry(), new AlgoIdRegistry());
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"), NullLogger<PersistenceRecovery>.Instance,
                orders: book, ownership: ownership, firmSessionStatus: provider);

            await recovery.RunAsync();

            Assert.True(book.TryGet(1UL, out var o1) && o1!.Status == OrderStatus.PendingNew);
        }
    }

    private sealed class TestSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent evt) { }
    }

    private (
        WorkingOrderBook,
        PositionKeeper,
        KillSwitchService,
        OrderOwnershipMap,
        StateSnapshotter,
        EventDispatcher,
        ExecutionReportProcessor,
        TestSink,
        AlgoBook) BuildState(IEventStore store, IFirmSessionStatusProvider? provider)
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
        var snapshotter = new StateSnapshotter(book, positions, killSwitch, new SymbolHaltService(),
            new SessionPhaseService(), clOrdIds, ownership, algos, new AlgoIdRegistry(), new CashLedger(),
            firmSessionStatus: provider);
        var dispatcher = new EventDispatcher(store);
        return (book, positions, killSwitch, ownership, snapshotter, dispatcher, processor, sink, algos);
    }

    private static void DispatchSubmit(
        EventDispatcher d, WorkingOrderBook book, OrderOwnershipMap ownership,
        ulong clOrdId, string endClient, string firmId, string symbol)
    {
        var owner = new EndClientId(endClient);
        d.Dispatch(
            new OrderSubmittedEvent
            {
                ClOrdId = clOrdId,
                EndClientId = endClient,
                FirmId = firmId,
                Symbol = symbol,
                SecurityId = 4321UL,
                Side = OrderSide.Buy.ToString(),
                Type = "Limit",
                Quantity = 100,
                Price = 30m,
            },
            () =>
            {
                book.TryAdd(new Order(clOrdId, owner, symbol, 4321UL, OrderSide.Buy, OrderType.Limit,
                    100, 30m, firmId: firmId));
                ownership.Register(clOrdId, owner);
            });
    }
}
