using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// #671/#753 (RFC: admin account reset + runtime position adjustment,
/// PR 1). End-to-end snapshot + WAL replay coverage for
/// <see cref="PositionAdjustmentEvent"/> / <see cref="PositionKeeper.SetAbsolute"/>,
/// <see cref="PnlKeeper.SetAbsoluteAvgCost"/> (code-review addendum #1),
/// AND <see cref="SubAccountPnlKeeper.SetAbsoluteMasterBucketAvgCost"/>
/// (code-review addendum #2) — proving all three keepers' state is
/// replayed in lockstep on cold replay and snapshot+tail recovery,
/// never left one step behind. Shaped after <c>CashKeeperRecoveryTests</c>
/// (the sibling admin-cash WAL-replay suite) since all three keepers'
/// admin-driven projections share the same "single event kind, no
/// accumulation" replay contract.
/// </summary>
public class PositionAdjustmentRecoveryTests : IDisposable
{
    private readonly string _root;

    public PositionAdjustmentRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "b3tp-positionadj-" + Guid.NewGuid().ToString("N"));
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
    public async Task Replay_FromWalAlone_RebuildsAbsolutePosition()
    {
        // Phase 1: live — append two adjustment events for the same
        // (firm, owner, symbol): the second must overwrite, not
        // accumulate onto, the first.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var dispatcher = new EventDispatcher(store);

            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "alice", "PETR4", 100, 20m);
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "alice", "PETR4", -30, 25m);
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "bob", "VALE3", 50, 60m);
            await store.FlushAsync();
        }

        // Phase 2: cold boot — fresh keepers, replay rebuilds state.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(positions, pnl, subPnl);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            var alice = positions.ForEndClientAndFirm(PositionKeeper.DefaultFirmId, new EndClientId("alice"));
            var alicePos = Assert.Single(alice);
            Assert.Equal("PETR4", alicePos.Symbol);
            // Absolute overwrite: -30/25, NOT 100 + (-30) = 70 a
            // delta-fold would have produced.
            Assert.Equal(-30, alicePos.NetQuantity);
            Assert.Equal(25m, alicePos.AverageEntryPrice);

            var bob = positions.ForEndClientAndFirm(PositionKeeper.DefaultFirmId, new EndClientId("bob"));
            var bobPos = Assert.Single(bob);
            Assert.Equal(50, bobPos.NetQuantity);
            Assert.Equal(60m, bobPos.AverageEntryPrice);

            // PnlKeeper's avg-cost basis must have replayed in lockstep
            // with PositionKeeper — same overwrite, not accumulation.
            var aliceBasis = pnl.GetAvgCost(PnlKeeper.DefaultFirmId, "alice", "PETR4");
            Assert.NotNull(aliceBasis);
            Assert.Equal(-30, aliceBasis!.NetQuantity);
            Assert.Equal(25m, aliceBasis.AvgPrice);

            var bobBasis = pnl.GetAvgCost(PnlKeeper.DefaultFirmId, "bob", "VALE3");
            Assert.NotNull(bobBasis);
            Assert.Equal(50, bobBasis!.NetQuantity);
            Assert.Equal(60m, bobBasis.AvgPrice);

            // SubAccountPnlKeeper's MASTER bucket basis must also have
            // replayed in lockstep — code-review addendum #2.
            var aliceMasterBasis = subPnl.GetBucketAvgCost(PnlKeeper.DefaultFirmId, "alice", subAccount: null, "PETR4");
            Assert.NotNull(aliceMasterBasis);
            Assert.Equal(-30, aliceMasterBasis!.NetQuantity);
            Assert.Equal(25m, aliceMasterBasis.AvgPrice);

            var bobMasterBasis = subPnl.GetBucketAvgCost(PnlKeeper.DefaultFirmId, "bob", subAccount: null, "VALE3");
            Assert.NotNull(bobMasterBasis);
            Assert.Equal(50, bobMasterBasis!.NetQuantity);
            Assert.Equal(60m, bobMasterBasis.AvgPrice);
        }
    }


    [Fact]
    public async Task SnapshotPlusTail_RestoresFromSnapshot_AndAppliesTailAdjustment()
    {
        long snapSeq;
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var dispatcher = new EventDispatcher(store);
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "alice", "PETR4", 100, 20m);
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "bob", "VALE3", 200, 55m);

            // Capture a snapshot under the dispatcher lock.
            var (snapshotter, _) = BuildSnapshotterAndReplayer(positions, pnl, subPnl);
            var snapStore = new SnapshotStore(_root, "test");
            PlatformSnapshot? snap = null;
            dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
            snapStore.Write(snap!);
            snapSeq = snap!.Seq;

            Assert.Contains(snap.Positions, p => p.EndClientId == "alice" && p.NetQuantity == 100);
            Assert.Contains(snap.PnlAvgCost, a => a.EndClientId == "alice" && a.NetQuantity == 100 && a.AvgPrice == 20m);

            // Tail: one more adjustment past the snapshot seq — overwrites
            // alice's PETR4 row; carol is a brand-new post-snapshot row.
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "alice", "PETR4", 400, 21m);
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "carol", "ITUB4", 10, 30m);
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(positions, pnl, subPnl);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            var alice = Assert.Single(
                positions.ForEndClientAndFirm(PositionKeeper.DefaultFirmId, new EndClientId("alice")));
            // Snapshot supplied 100/20; tail overwrote to 400/21 — the
            // tail event must win, and NOT accumulate on the snapshot value.
            Assert.Equal(400, alice.NetQuantity);
            Assert.Equal(21m, alice.AverageEntryPrice);

            var bob = Assert.Single(
                positions.ForEndClientAndFirm(PositionKeeper.DefaultFirmId, new EndClientId("bob")));
            Assert.Equal(200, bob.NetQuantity);

            var carol = Assert.Single(
                positions.ForEndClientAndFirm(PositionKeeper.DefaultFirmId, new EndClientId("carol")));
            Assert.Equal(10, carol.NetQuantity);
            Assert.True(snapSeq > 0);

            // Avg-cost basis: snapshot supplied 100/20, tail overwrote to
            // 400/21 — must match the position exactly, not the snapshot
            // value and not an accumulation of the two.
            var aliceBasis = pnl.GetAvgCost(PnlKeeper.DefaultFirmId, "alice", "PETR4");
            Assert.NotNull(aliceBasis);
            Assert.Equal(400, aliceBasis!.NetQuantity);
            Assert.Equal(21m, aliceBasis.AvgPrice);

            var carolBasis = pnl.GetAvgCost(PnlKeeper.DefaultFirmId, "carol", "ITUB4");
            Assert.NotNull(carolBasis);
            Assert.Equal(10, carolBasis!.NetQuantity);
            Assert.Equal(30m, carolBasis.AvgPrice);

            // Same convergence check for the SubAccountPnlKeeper MASTER
            // bucket (code-review addendum #2).
            var aliceMasterBasis = subPnl.GetBucketAvgCost(PnlKeeper.DefaultFirmId, "alice", subAccount: null, "PETR4");
            Assert.NotNull(aliceMasterBasis);
            Assert.Equal(400, aliceMasterBasis!.NetQuantity);
            Assert.Equal(21m, aliceMasterBasis.AvgPrice);

            var carolMasterBasis = subPnl.GetBucketAvgCost(PnlKeeper.DefaultFirmId, "carol", subAccount: null, "ITUB4");
            Assert.NotNull(carolMasterBasis);
            Assert.Equal(10, carolMasterBasis!.NetQuantity);
            Assert.Equal(30m, carolMasterBasis.AvgPrice);
        }
    }


    [Fact]
    public async Task SnapshotAndReplay_SegregatesSameOwnerAcrossFirms()
    {
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var dispatcher = new EventDispatcher(store);
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "alice", "PETR4", 100, 20m, "FIRM01");
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "alice", "PETR4", 300, 22m, "FIRM02");

            var (snapshotter, _) = BuildSnapshotterAndReplayer(positions, pnl, subPnl);
            PlatformSnapshot? snapshot = null;
            dispatcher.WithSnapshotLock(seq => snapshot = snapshotter.Capture(seq));
            new SnapshotStore(_root, "test").Write(snapshot!);

            // Tail overwrite scoped to FIRM01 only.
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "alice", "PETR4", -20, 18m, "FIRM01");
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(positions, pnl, subPnl);
            var recovery = new PersistenceRecovery(
                store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            var owner = new EndClientId("alice");
            var firm1 = Assert.Single(positions.ForEndClientAndFirm("FIRM01", owner));
            Assert.Equal(-20, firm1.NetQuantity);
            Assert.Equal(18m, firm1.AverageEntryPrice);

            var firm2 = Assert.Single(positions.ForEndClientAndFirm("FIRM02", owner));
            Assert.Equal(300, firm2.NetQuantity);
            Assert.Equal(22m, firm2.AverageEntryPrice);

            Assert.Empty(positions.ForEndClientAndFirm("FIRM03", owner));

            // Avg-cost basis segregation must match the position
            // segregation exactly across firms.
            var firm1Basis = pnl.GetAvgCost("FIRM01", "alice", "PETR4");
            Assert.NotNull(firm1Basis);
            Assert.Equal(-20, firm1Basis!.NetQuantity);
            Assert.Equal(18m, firm1Basis.AvgPrice);

            var firm2Basis = pnl.GetAvgCost("FIRM02", "alice", "PETR4");
            Assert.NotNull(firm2Basis);
            Assert.Equal(300, firm2Basis!.NetQuantity);
            Assert.Equal(22m, firm2Basis.AvgPrice);

            Assert.Null(pnl.GetAvgCost("FIRM03", "alice", "PETR4"));

            // SubAccountPnlKeeper MASTER bucket segregation (code-review
            // addendum #2) must match the same firm segregation.
            var firm1MasterBasis = subPnl.GetBucketAvgCost("FIRM01", "alice", subAccount: null, "PETR4");
            Assert.NotNull(firm1MasterBasis);
            Assert.Equal(-20, firm1MasterBasis!.NetQuantity);
            Assert.Equal(18m, firm1MasterBasis.AvgPrice);

            var firm2MasterBasis = subPnl.GetBucketAvgCost("FIRM02", "alice", subAccount: null, "PETR4");
            Assert.NotNull(firm2MasterBasis);
            Assert.Equal(300, firm2MasterBasis!.NetQuantity);
            Assert.Equal(22m, firm2MasterBasis.AvgPrice);

            Assert.Null(subPnl.GetBucketAvgCost("FIRM03", "alice", subAccount: null, "PETR4"));
        }
    }


    [Fact]
    public async Task Replay_ZeroQuantityAdjustment_ClearsAvgCostBasisOnReplay()
    {
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var dispatcher = new EventDispatcher(store);
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "alice", "PETR4", 100, 20m);
            // Flatten to zero — must clear the basis, not leave (0, 0m).
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "alice", "PETR4", 0, 0m);
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(positions, pnl, subPnl);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            var alice = Assert.Single(
                positions.ForEndClientAndFirm(PositionKeeper.DefaultFirmId, new EndClientId("alice")));
            Assert.Equal(0, alice.NetQuantity);
            Assert.Equal(0m, alice.AverageEntryPrice);

            Assert.Null(pnl.GetAvgCost(PnlKeeper.DefaultFirmId, "alice", "PETR4"));

            // SubAccountPnlKeeper MASTER bucket basis must also be
            // cleared on replay — code-review addendum #2.
            Assert.Null(subPnl.GetBucketAvgCost(PnlKeeper.DefaultFirmId, "alice", subAccount: null, "PETR4"));
        }
    }


    private static void DispatchAdjustment(
        EventDispatcher d,
        PositionKeeper positions,
        PnlKeeper pnl,
        SubAccountPnlKeeper subPnl,
        string ec,
        string symbol,
        long netQuantity,
        decimal averageEntryPrice,
        string firmId = PositionKeeper.DefaultFirmId)
    {
        var owner = new EndClientId(ec);
        var evt = new PositionAdjustmentEvent
        {
            EndClientId = ec,
            FirmId = firmId,
            Symbol = symbol,
            NetQuantity = netQuantity,
            AverageEntryPrice = averageEntryPrice,
            Reference = "test",
            OperatorId = "test-operator",
        };
        d.Dispatch(evt, () =>
        {
            positions.SetAbsolute(firmId, owner, symbol, netQuantity, averageEntryPrice);
            pnl.SetAbsoluteAvgCost(firmId, ec, symbol, netQuantity, averageEntryPrice);
            subPnl.SetAbsoluteMasterBucketAvgCost(firmId, ec, symbol, netQuantity, averageEntryPrice);
        });
    }

    private (StateSnapshotter, EventReplayer) BuildSnapshotterAndReplayer(PositionKeeper positions, PnlKeeper pnl, SubAccountPnlKeeper subPnl)
    {
        var book = new WorkingOrderBook();
        var killSwitch = new KillSwitchService();
        var ownership = new OrderOwnershipMap();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var algos = new AlgoBook();
        var sink = new NullSink();
        var processor = new ExecutionReportProcessor(ownership, book, positions, sink,
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        var snapshotter = new StateSnapshotter(book, positions, killSwitch,
            new SymbolHaltService(), new SessionPhaseService(),
            clOrdIds, ownership, algos, new AlgoIdRegistry(),
            new CashLedger(),
            pnlKeeper: pnl,
            subAccountPnl: subPnl);
        var replayer = new EventReplayer(book, ownership, killSwitch,
            new SymbolHaltService(), new SessionPhaseService(),
            processor, algos, clOrdIds, new AlgoIdRegistry(),
            pnlKeeper: pnl,
            positions: positions,
            subAccountPnl: subPnl);
        return (snapshotter, replayer);
    }


    private sealed class NullSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent evt) { }
    }
}
