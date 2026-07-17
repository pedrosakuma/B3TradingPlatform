using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// Q2.2 (#269). End-to-end snapshot + WAL replay coverage for
/// <see cref="CashKeeper"/>. The keeper's projection is fed exclusively
/// by <c>CashLedgerEvent</c> records on the WAL, so the tests are
/// shaped around that single event surface.
/// </summary>
public class CashKeeperRecoveryTests : IDisposable
{
    private readonly string _root;

    public CashKeeperRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "b3tp-cashkeeper-" + Guid.NewGuid().ToString("N"));
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
    public async Task Replay_FromWalAlone_RebuildsBalances()
    {
        // Phase 1: live — append cash ledger events.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var keeper = new CashKeeper();
            var dispatcher = new EventDispatcher(store);
            var alice = new EndClientId("alice");

            DispatchCash(dispatcher, keeper, "alice", "Deposit", 1_000m);
            DispatchCash(dispatcher, keeper, "alice", "Withdrawal", 250m);
            DispatchCash(dispatcher, keeper, "bob", "Deposit", 500m);
            await store.FlushAsync();
        }

        // Phase 2: cold boot — fresh keeper, replay rebuilds state.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var keeper = new CashKeeper();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(keeper);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            Assert.Equal(750m, keeper.GetAvailable(new EndClientId("alice")));
            Assert.Equal(500m, keeper.GetAvailable(new EndClientId("bob")));
        }
    }

    [Fact]
    public async Task SnapshotPlusTail_RestoresFromSnapshot_AndAppliesTailEvents()
    {
        // Phase 1: append events, take a snapshot, append more events.
        long snapSeq;
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var keeper = new CashKeeper();
            var dispatcher = new EventDispatcher(store);
            DispatchCash(dispatcher, keeper, "alice", "Deposit", 1_000m);
            DispatchCash(dispatcher, keeper, "bob", "Deposit", 200m);

            // Capture a snapshot under the dispatcher lock.
            var (snapshotter, _) = BuildSnapshotterAndReplayer(keeper);
            var snapStore = new SnapshotStore(_root, "test");
            PlatformSnapshot? snap = null;
            dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
            snapStore.Write(snap!);
            snapSeq = snap!.Seq;

            // Snapshot dict carries both balances at point-in-time.
            Assert.Equal(
                1_000m,
                snap.CashByEndclient[CashKeeper.FormatSnapshotKey("DEFAULT", "alice")]);
            Assert.Equal(
                200m,
                snap.CashByEndclient[CashKeeper.FormatSnapshotKey("DEFAULT", "bob")]);

            // Tail: more events past the snapshot seq.
            DispatchCash(dispatcher, keeper, "alice", "Withdrawal", 300m);
            DispatchCash(dispatcher, keeper, "carol", "Deposit", 50m);
            await store.FlushAsync();
        }

        // Phase 2: cold boot — recovery loads snapshot then replays tail.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var keeper = new CashKeeper();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(keeper);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            // Snapshot supplied 1000/200/0; tail debited 300 from alice
            // and credited 50 to carol; net matches the direct projection.
            Assert.Equal(700m, keeper.GetAvailable(new EndClientId("alice")));
            Assert.Equal(200m, keeper.GetAvailable(new EndClientId("bob")));
            Assert.Equal(50m, keeper.GetAvailable(new EndClientId("carol")));
            Assert.True(snapSeq > 0);
        }
    }

    [Fact]
    public async Task SnapshotAndReplay_SegregatesSameOwnerAcrossFirms()
    {
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var keeper = new CashKeeper();
            var dispatcher = new EventDispatcher(store);
            DispatchCash(dispatcher, keeper, "alice", "Deposit", 1_000m, "FIRM01");
            DispatchCash(dispatcher, keeper, "alice", "Deposit", 2_000m, "FIRM02");
            var (snapshotter, _) = BuildSnapshotterAndReplayer(keeper);
            PlatformSnapshot? snapshot = null;
            dispatcher.WithSnapshotLock(seq => snapshot = snapshotter.Capture(seq));
            new SnapshotStore(_root, "test").Write(snapshot!);
            DispatchCash(dispatcher, keeper, "alice", "Withdrawal", 250m, "FIRM01");
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var keeper = new CashKeeper();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(keeper);
            var recovery = new PersistenceRecovery(
                store,
                snapshotter,
                replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            var owner = new EndClientId("alice");
            Assert.Equal(750m, keeper.GetAvailable("FIRM01", owner));
            Assert.Equal(2_000m, keeper.GetAvailable("FIRM02", owner));
            Assert.Equal(0m, keeper.GetAvailable("FIRM03", owner));
        }
    }

    private static void DispatchCash(
        EventDispatcher d,
        CashKeeper k,
        string ec,
        string kind,
        decimal amount,
        string firmId = CashLedger.DefaultFirmId)
    {
        var owner = new EndClientId(ec);
        var evt = new CashLedgerEvent
        {
            EndClientId = ec,
            FirmId = firmId,
            Operation = kind,
            Amount = amount,
            Currency = "BRL",
            Reference = "test",
            OperatorId = "test-operator",
        };
        if (kind == "Deposit")
        {
            d.Dispatch(evt, () => k.ApplyDeposit(firmId, owner, amount));
        }
        else
        {
            d.DispatchWithPreApply(
                evt,
                preApply: () => k.TryWithdraw(firmId, owner, amount),
                rollback: () => k.ApplyDeposit(firmId, owner, amount));
        }
    }

    private (StateSnapshotter, EventReplayer) BuildSnapshotterAndReplayer(CashKeeper keeper)
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var killSwitch = new KillSwitchService();
        var ownership = new OrderOwnershipMap();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var algos = new AlgoBook();
        var sink = new NullSink();
        var processor = new ExecutionReportProcessor(ownership, book, positions, sink,
            new B3.Trading.Application.Risk.NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        var snapshotter = new StateSnapshotter(book, positions, killSwitch,
            new SymbolHaltService(), new SessionPhaseService(),
            clOrdIds, ownership, algos, new AlgoIdRegistry(),
            new CashLedger(),
            cashKeeper: keeper);
        var replayer = new EventReplayer(book, ownership, killSwitch,
            new SymbolHaltService(), new SessionPhaseService(),
            processor, algos, clOrdIds, new AlgoIdRegistry(),
            cashKeeper: keeper);
        return (snapshotter, replayer);
    }

    private sealed class NullSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent evt) { }
    }
}
