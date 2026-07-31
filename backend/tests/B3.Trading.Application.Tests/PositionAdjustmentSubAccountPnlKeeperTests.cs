using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #671/#753 (RFC PR 1, code-review addendum #2). End-to-end coverage
/// proving the admin position-adjustment path (mirroring
/// <c>AdminEndpoints.HandlePositionAdjustment</c>: <see cref="PositionKeeper.SetAbsolute"/>
/// + <see cref="PnlKeeper.SetAbsoluteAvgCost"/> +
/// <see cref="SubAccountPnlKeeper.SetAbsoluteMasterBucketAvgCost"/> in a
/// single dispatched apply) converges correctly with the PRODUCTION
/// <see cref="ExecutionReportProcessor"/> live-fill path, both
/// immediately and after a cold WAL replay. Specifically proves the
/// concrete scenario called out in review: an adjusted long 100@20
/// followed by a real sell fill of 50@25 realizes exactly +250 via
/// <see cref="SubAccountPnlKeeper"/>'s MASTER bucket (not the aggregate
/// <see cref="PnlKeeper"/> alone) and leaves the master bucket at
/// 50@20 — and that a zero adjustment clears the master bucket basis,
/// live and after replay.
/// </summary>
public class PositionAdjustmentSubAccountPnlKeeperTests : IDisposable
{
    private const string Firm = "FIRM01";
    private readonly string _root;

    public PositionAdjustmentSubAccountPnlKeeperTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "b3tp-positionadj-subpnl-" + Guid.NewGuid().ToString("N"));
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

    private sealed class NullSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent ev) { }
    }

    private sealed class Bench
    {
        public ExecutionReportProcessor Proc { get; init; } = null!;
        public EventDispatcher Dispatcher { get; init; } = null!;
        public PositionKeeper Positions { get; init; } = null!;
        public PnlKeeper Pnl { get; init; } = null!;
        public SubAccountPnlKeeper SubPnl { get; init; } = null!;
        public OrderOwnershipMap Ownership { get; init; } = null!;
        public WorkingOrderBook Book { get; init; } = null!;
        private ulong _nextClOrdId = 1;

        public ulong AddOrder(EndClientId owner, OrderSide side, long qty, decimal px)
        {
            var id = _nextClOrdId++;
            Book.TryAdd(new Order(id, owner, "PETR4", 1UL, side, OrderType.Limit, qty, px, firmId: Firm));
            Ownership.Register(id, owner);
            return id;
        }

        public void Fill(ulong clOrdId, long qty, decimal px)
        {
            Dispatcher.Dispatch(
                new ExecutionReportReceivedEvent
                {
                    ClOrdId = clOrdId,
                    ExecKind = nameof(ExecKind.Fill),
                    LeavesQuantity = 0,
                    CumulativeQuantity = qty,
                    LastQuantity = qty,
                    LastPrice = px,
                    Synthetic = false,
                    OrigClOrdId = 0,
                },
                fanOut => Proc.Apply(clOrdId, ExecKind.Fill, 0, qty, qty, px, null, 0, fanOut));
        }

        /// <summary>
        /// Mimics <c>AdminEndpoints.HandlePositionAdjustment</c>'s single
        /// dispatched apply delegate exactly: all three keepers updated
        /// atomically in one dispatcher-serialised apply.
        /// </summary>
        public void AdjustPosition(EndClientId owner, string symbol, long netQuantity, decimal averageEntryPrice)
        {
            Dispatcher.Dispatch(
                new PositionAdjustmentEvent
                {
                    EndClientId = owner.Value,
                    FirmId = Firm,
                    Symbol = symbol,
                    NetQuantity = netQuantity,
                    AverageEntryPrice = averageEntryPrice,
                    Reference = "test",
                    OperatorId = "test-operator",
                },
                () =>
                {
                    Positions.SetAbsolute(Firm, owner, symbol, netQuantity, averageEntryPrice);
                    Pnl.SetAbsoluteAvgCost(Firm, owner.Value, symbol, netQuantity, averageEntryPrice);
                    SubPnl.SetAbsoluteMasterBucketAvgCost(Firm, owner.Value, symbol, netQuantity, averageEntryPrice);
                });
        }
    }

    private static List<RealizedPnlEvent> RealizedEvents(IEventStore store) =>
        store is RecordingEventStore rec
            ? rec.Recorded.Select(r => r.Event).OfType<RealizedPnlEvent>().ToList()
            : throw new InvalidOperationException("expected RecordingEventStore");

    private sealed class RecordingEventStore : IEventStore
    {
        public ConcurrentQueue<(long Seq, WalEvent Event)> Recorded { get; } = new();
        private long _seq;
        public long CurrentSeq => Interlocked.Read(ref _seq);
        public long Append(WalEvent evt)
        {
            var s = Interlocked.Increment(ref _seq);
            Recorded.Enqueue((s, evt));
            return s;
        }
        public long Append(WalEvent evt, ReadOnlyMemory<byte> _) => Append(evt);
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (Bench Bench, RecordingEventStore Store) Build()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var pnl = new PnlKeeper();
        var subPnl = new SubAccountPnlKeeper();
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var proc = new ExecutionReportProcessor(
            ownership, book, positions, new NullSink(), new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            algoSignals: null,
            cash: null,
            feeCalculator: null,
            feeKeeper: null,
            dispatcher: dispatcher,
            pnlKeeper: pnl,
            subAccountPositions: null,
            subAccountPnl: subPnl);
        var bench = new Bench
        {
            Proc = proc,
            Dispatcher = dispatcher,
            Positions = positions,
            Pnl = pnl,
            SubPnl = subPnl,
            Ownership = ownership,
            Book = book,
        };
        return (bench, store);
    }

    /// <summary>
    /// Core review scenario: admin adjusts alice's PETR4 MASTER
    /// position to an absolute long 100@20 (mirroring the admin
    /// endpoint's dispatched apply), then a real sell fill of 50@25
    /// flows through the PRODUCTION ExecutionReportProcessor live
    /// path. Must realize exactly +250 via SubAccountPnlKeeper's
    /// master bucket, and the master bucket must be left at 50@20 —
    /// not the aggregate PnlKeeper checked in isolation.
    /// </summary>
    [Fact]
    public void AdjustLongThenSell_RealizesCorrectPnl_ViaProductionExecutionReportProcessor()
    {
        var (b, store) = Build();
        var owner = new EndClientId("alice");

        b.AdjustPosition(owner, "PETR4", 100, 20m);

        var clOrdId = b.AddOrder(owner, OrderSide.Sell, 50, 25m);
        b.Fill(clOrdId, 50, 25m);

        var events = RealizedEvents(store);
        var masterEvent = Assert.Single(events, e => e.SubAccountId is null);
        Assert.Equal(250m, masterEvent.DeltaRealized); // (25 - 20) * 50

        var masterBasis = b.SubPnl.GetBucketAvgCost(Firm, "alice", subAccount: null, "PETR4");
        Assert.NotNull(masterBasis);
        Assert.Equal(50, masterBasis!.NetQuantity);
        Assert.Equal(20m, masterBasis.AvgPrice);

        // Aggregate PnlKeeper must also converge to the same remaining
        // basis (the two keepers must never disagree).
        var aggregateBasis = b.Pnl.GetAvgCost(Firm, "alice", "PETR4");
        Assert.NotNull(aggregateBasis);
        Assert.Equal(50, aggregateBasis!.NetQuantity);
        Assert.Equal(20m, aggregateBasis.AvgPrice);
    }

    /// <summary>
    /// Zero adjustment clears the master bucket basis LIVE (no fill
    /// involved) — proves the admin flatten path reaches
    /// SubAccountPnlKeeper's master bucket exactly like PnlKeeper's
    /// aggregate basis.
    /// </summary>
    [Fact]
    public void ZeroAdjustment_ClearsMasterBucketBasis_Live()
    {
        var (b, _) = Build();
        var owner = new EndClientId("alice");

        b.AdjustPosition(owner, "PETR4", 100, 20m);
        Assert.NotNull(b.SubPnl.GetBucketAvgCost(Firm, "alice", subAccount: null, "PETR4"));

        b.AdjustPosition(owner, "PETR4", 0, 0m);

        Assert.Null(b.SubPnl.GetBucketAvgCost(Firm, "alice", subAccount: null, "PETR4"));
        Assert.Null(b.Pnl.GetAvgCost(Firm, "alice", "PETR4"));
    }

    /// <summary>
    /// Same core scenario as
    /// <see cref="AdjustLongThenSell_RealizesCorrectPnl_ViaProductionExecutionReportProcessor"/>
    /// but via a full cold WAL replay: the admin adjustment is
    /// dispatched to a real <see cref="FileEventStore"/>, the process
    /// "restarts" with fresh keepers, replay rebuilds the master
    /// bucket basis, and only THEN does the sell fill happen against
    /// the freshly-replayed <see cref="ExecutionReportProcessor"/>
    /// instance. Proves the replayed master-bucket basis is usable by
    /// the live production fill path exactly as if it had never been
    /// through a restart.
    /// </summary>
    [Fact]
    public async Task AdjustLongThenSell_AfterColdReplay_RealizesCorrectPnlViaMasterBucket()
    {
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var dispatcher = new EventDispatcher(store);
            dispatcher.Dispatch(
                new PositionAdjustmentEvent
                {
                    EndClientId = "alice",
                    FirmId = Firm,
                    Symbol = "PETR4",
                    NetQuantity = 100,
                    AverageEntryPrice = 20m,
                    Reference = "test",
                    OperatorId = "test-operator",
                },
                () =>
                {
                    positions.SetAbsolute(Firm, new EndClientId("alice"), "PETR4", 100, 20m);
                    pnl.SetAbsoluteAvgCost(Firm, "alice", "PETR4", 100, 20m);
                    subPnl.SetAbsoluteMasterBucketAvgCost(Firm, "alice", "PETR4", 100, 20m);
                });
            await store.FlushAsync();
        }

        // Cold boot: fresh keepers + a fresh, production-shaped
        // ExecutionReportProcessor wired for both replay AND
        // subsequent live fills.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var ownership = new OrderOwnershipMap();
            var book = new WorkingOrderBook();
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var killSwitch = new KillSwitchService();
            var clOrdIds = new ClOrdIdPrefixRegistry();
            var algos = new AlgoBook();
            var recordingStore = new RecordingEventStore();
            var liveDispatcher = new EventDispatcher(recordingStore);
            var proc = new ExecutionReportProcessor(
                ownership, book, positions, new NullSink(), new NoOpMarginProvider(),
                NullLogger<ExecutionReportProcessor>.Instance,
                algoSignals: null,
                cash: null,
                feeCalculator: null,
                feeKeeper: null,
                dispatcher: liveDispatcher,
                pnlKeeper: pnl,
                subAccountPositions: null,
                subAccountPnl: subPnl);

            var snapshotter = new StateSnapshotter(book, positions, killSwitch,
                new SymbolHaltService(), new SessionPhaseService(),
                clOrdIds, ownership, algos, new AlgoIdRegistry(),
                new CashLedger(),
                pnlKeeper: pnl,
                subAccountPnl: subPnl);
            var replayer = new EventReplayer(book, ownership, killSwitch,
                new SymbolHaltService(), new SessionPhaseService(),
                proc, algos, clOrdIds, new AlgoIdRegistry(),
                pnlKeeper: pnl,
                positions: positions,
                subAccountPnl: subPnl);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            // Post-replay: master bucket basis is 100@20, exactly as
            // the live path would have left it.
            var replayedBasis = subPnl.GetBucketAvgCost(Firm, "alice", subAccount: null, "PETR4");
            Assert.NotNull(replayedBasis);
            Assert.Equal(100, replayedBasis!.NetQuantity);
            Assert.Equal(20m, replayedBasis.AvgPrice);

            // Now a real sell fill flows through the freshly-replayed
            // production ExecutionReportProcessor.
            ulong nextClOrdId = 1;
            var owner = new EndClientId("alice");
            book.TryAdd(new Order(nextClOrdId, owner, "PETR4", 1UL, OrderSide.Sell, OrderType.Limit, 50, 25m, firmId: Firm));
            ownership.Register(nextClOrdId, owner);
            liveDispatcher.Dispatch(
                new ExecutionReportReceivedEvent
                {
                    ClOrdId = nextClOrdId,
                    ExecKind = nameof(ExecKind.Fill),
                    LeavesQuantity = 0,
                    CumulativeQuantity = 50,
                    LastQuantity = 50,
                    LastPrice = 25m,
                    Synthetic = false,
                    OrigClOrdId = 0,
                },
                fanOut => proc.Apply(nextClOrdId, ExecKind.Fill, 0, 50, 50, 25m, null, 0, fanOut));

            var events = RealizedEvents(recordingStore);
            var masterEvent = Assert.Single(events, e => e.SubAccountId is null);
            Assert.Equal(250m, masterEvent.DeltaRealized); // (25 - 20) * 50

            var finalBasis = subPnl.GetBucketAvgCost(Firm, "alice", subAccount: null, "PETR4");
            Assert.NotNull(finalBasis);
            Assert.Equal(50, finalBasis!.NetQuantity);
            Assert.Equal(20m, finalBasis.AvgPrice);
        }
    }

    /// <summary>
    /// Zero adjustment clears the master bucket basis AFTER a cold WAL
    /// replay (snapshot-less, WAL-only recovery of two adjustment
    /// events: build then flatten).
    /// </summary>
    [Fact]
    public async Task ZeroAdjustment_ClearsMasterBucketBasis_AfterReplay()
    {
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var dispatcher = new EventDispatcher(store);

            void Adjust(long netQuantity, decimal averageEntryPrice) => dispatcher.Dispatch(
                new PositionAdjustmentEvent
                {
                    EndClientId = "alice",
                    FirmId = Firm,
                    Symbol = "PETR4",
                    NetQuantity = netQuantity,
                    AverageEntryPrice = averageEntryPrice,
                    Reference = "test",
                    OperatorId = "test-operator",
                },
                () =>
                {
                    positions.SetAbsolute(Firm, new EndClientId("alice"), "PETR4", netQuantity, averageEntryPrice);
                    pnl.SetAbsoluteAvgCost(Firm, "alice", "PETR4", netQuantity, averageEntryPrice);
                    subPnl.SetAbsoluteMasterBucketAvgCost(Firm, "alice", "PETR4", netQuantity, averageEntryPrice);
                });

            Adjust(100, 20m);
            Adjust(0, 0m); // flatten
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var ownership = new OrderOwnershipMap();
            var book = new WorkingOrderBook();
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var killSwitch = new KillSwitchService();
            var clOrdIds = new ClOrdIdPrefixRegistry();
            var algos = new AlgoBook();
            var proc = new ExecutionReportProcessor(ownership, book, positions, new NullSink(),
                new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);
            var snapshotter = new StateSnapshotter(book, positions, killSwitch,
                new SymbolHaltService(), new SessionPhaseService(),
                clOrdIds, ownership, algos, new AlgoIdRegistry(),
                new CashLedger(),
                pnlKeeper: pnl,
                subAccountPnl: subPnl);
            var replayer = new EventReplayer(book, ownership, killSwitch,
                new SymbolHaltService(), new SessionPhaseService(),
                proc, algos, clOrdIds, new AlgoIdRegistry(),
                pnlKeeper: pnl,
                positions: positions,
                subAccountPnl: subPnl);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            Assert.Null(subPnl.GetBucketAvgCost(Firm, "alice", subAccount: null, "PETR4"));
            Assert.Null(pnl.GetAvgCost(Firm, "alice", "PETR4"));
            var alicePos = Assert.Single(positions.ForEndClientAndFirm(Firm, new EndClientId("alice")));
            Assert.Equal(0, alicePos.NetQuantity);
            Assert.Equal(0m, alicePos.AverageEntryPrice);
        }
    }
}
