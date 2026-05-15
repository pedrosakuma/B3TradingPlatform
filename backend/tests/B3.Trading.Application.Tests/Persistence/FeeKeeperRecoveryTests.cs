using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// Q2.3 (#270). Snapshot + WAL replay end-to-end coverage for
/// <see cref="FeeKeeper"/>. The keeper's projection is fed by
/// <see cref="FeeAccruedEvent"/> records on the WAL, with idempotence
/// keyed on <see cref="FeeAccruedEvent.ExecutionId"/>.
/// </summary>
public class FeeKeeperRecoveryTests : IDisposable
{
    private readonly string _root;

    public FeeKeeperRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "b3tp-feekeeper-" + Guid.NewGuid().ToString("N"));
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
    public async Task Replay_FromWalAlone_RebuildsTotals_DedupesOnExecutionId()
    {
        var t = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var keeper = new FeeKeeper();
            var dispatcher = new EventDispatcher(store);
            DispatchFee(dispatcher, keeper, "alice", "1:10", 5m, t);
            DispatchFee(dispatcher, keeper, "alice", "2:20", 7m, t);
            DispatchFee(dispatcher, keeper, "bob", "3:10", 11m, t);
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var keeper = new FeeKeeper();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(keeper);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            var day = new DateOnly(2025, 1, 15);
            Assert.Equal(12m, keeper.GetDayTotal("alice", day));
            Assert.Equal(11m, keeper.GetDayTotal("bob", day));
        }
    }

    [Fact]
    public async Task SnapshotPlusTail_RestoresFromSnapshot_AndDedupesTail()
    {
        var t = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

        // Phase 1: live — append events, take a snapshot mid-stream,
        // then append more. The snapshot includes both totals and the
        // seen-set, so any tail event whose ExecutionId is already in
        // the snapshot would be a no-op on replay.
        long snapSeq;
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var keeper = new FeeKeeper();
            var dispatcher = new EventDispatcher(store);
            DispatchFee(dispatcher, keeper, "alice", "1:10", 5m, t);
            DispatchFee(dispatcher, keeper, "bob", "2:10", 7m, t);

            var (snapshotter, _) = BuildSnapshotterAndReplayer(keeper);
            var snapStore = new SnapshotStore(_root, "test");
            PlatformSnapshot? snap = null;
            dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
            snapStore.Write(snap!);
            snapSeq = snap!.Seq;

            // Snapshot reflects point-in-time totals + seen-set.
            Assert.Equal(5m, snap.FeesByEndclientDay[FeeKeeper.FormatKey("alice", new DateOnly(2025, 1, 15))]);
            Assert.Equal(7m, snap.FeesByEndclientDay[FeeKeeper.FormatKey("bob", new DateOnly(2025, 1, 15))]);
            Assert.Contains("1:10", snap.FeeSeenExecutionIds);
            Assert.Contains("2:10", snap.FeeSeenExecutionIds);

            // Tail: more fees past the snapshot seq.
            DispatchFee(dispatcher, keeper, "alice", "1:20", 3m, t);
            DispatchFee(dispatcher, keeper, "carol", "4:10", 2m, t);
            await store.FlushAsync();
        }

        // Phase 2: cold boot — recovery loads snapshot then replays
        // tail. Tail has TWO new events (alice:20 +3, carol:10 +2);
        // pre-snapshot events are NOT replayed (snapshot.Seq filters
        // them out via PersistenceRecovery's sinceSeq).
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var keeper = new FeeKeeper();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(keeper);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            var day = new DateOnly(2025, 1, 15);
            Assert.Equal(8m, keeper.GetDayTotal("alice", day));   // 5 (snap) + 3 (tail)
            Assert.Equal(7m, keeper.GetDayTotal("bob", day));
            Assert.Equal(2m, keeper.GetDayTotal("carol", day));
            Assert.True(snapSeq > 0);
        }
    }

    private static void DispatchFee(EventDispatcher d, FeeKeeper k, string ec,
        string executionId, decimal total, DateTimeOffset ts)
    {
        var clOrdId = ulong.Parse(executionId.Split(':')[0]);
        var evt = new FeeAccruedEvent
        {
            ClOrdId = clOrdId,
            ExecutionId = executionId,
            EndClientId = ec,
            Symbol = "PETR4",
            Side = "Buy",
            FillQuantity = 10,
            FillPrice = 30m,
            Notional = 300m,
            Brokerage = total - 1m,
            Emolumentos = 0.5m,
            Liquidacao = 0.5m,
            Total = total,
            TimestampUtc = ts,
        };
        d.Dispatch(evt, () => k.Apply(evt));
    }

    private (StateSnapshotter, EventReplayer) BuildSnapshotterAndReplayer(FeeKeeper keeper)
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
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
            feeKeeper: keeper);
        var replayer = new EventReplayer(book, ownership, killSwitch,
            new SymbolHaltService(), new SessionPhaseService(),
            processor, algos, clOrdIds, new AlgoIdRegistry(),
            feeKeeper: keeper);
        return (snapshotter, replayer);
    }

    private sealed class NullSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent evt) { }
    }
}
