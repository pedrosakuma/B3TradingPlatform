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

    [Fact]
    public async Task SnapshotPlusTail_CrashAfterErAppendBeforeFeeAppend_RecoversFeeFromReplaySynth()
    {
        // P1 regression for #277 pass-2: simulate the crash window.
        // Live phase: append an ER fill but DO NOT append the matching
        // FeeAccruedEvent (the WAL writer crashed mid-window). Cold
        // boot: recovery replays the ER with isReplay=true and the
        // ExecutionReportProcessor synthesises the fee directly into
        // FeeKeeper, so the keeper recovers despite the missing audit
        // event.
        var t = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            // Seed the order via OrderSubmittedEvent so the replayer's
            // book has it before the ER replays.
            store.Append(new OrderSubmittedEvent
            {
                ClOrdId = 100UL,
                EndClientId = "alice",
                FirmId = "TEST",
                Symbol = "PETR4",
                SecurityId = 1UL,
                Quantity = 100,
                Price = 1_000m,
                Side = "Buy",
                Type = "Limit",
                TimestampUtc = t,
            });
            // ER New (so order is Working) then ER Fill — but NO matching
            // FeeAccruedEvent (simulated crash in window between ER Fill
            // append and Fee append).
            store.Append(new ExecutionReportReceivedEvent
            {
                ClOrdId = 100UL,
                ExecKind = nameof(ExecKind.New),
                LeavesQuantity = 100,
                CumulativeQuantity = 0,
                LastQuantity = 0,
                LastPrice = 0m,
                RejectReason = null,
                Synthetic = false,
                OrigClOrdId = 0,
                TimestampUtc = t,
            });
            store.Append(new ExecutionReportReceivedEvent
            {
                ClOrdId = 100UL,
                ExecKind = nameof(ExecKind.Fill),
                LeavesQuantity = 0,
                CumulativeQuantity = 100,
                LastQuantity = 100,
                LastPrice = 1_000m,
                RejectReason = null,
                Synthetic = false,
                OrigClOrdId = 0,
                TimestampUtc = t,
            });
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var keeper = new FeeKeeper();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(keeper, withFees: true);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            var day = new DateOnly(2025, 1, 15);
            // 100k notional → brokerage 50 + emol 32.50 + liq 27.50 = 110.
            // Synthesised on replay even though FeeAccruedEvent is missing.
            Assert.Equal(110m, keeper.GetDayTotal("alice", day));
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

    private (StateSnapshotter, EventReplayer) BuildSnapshotterAndReplayer(FeeKeeper keeper, bool withFees = false)
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var killSwitch = new KillSwitchService();
        var ownership = new OrderOwnershipMap();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var algos = new AlgoBook();
        var sink = new NullSink();
        IFeeCalculator? calc = null;
        if (withFees)
        {
            var optsMonitor = new TestOptionsMonitor<FeeOptions>(new FeeOptions
            {
                BrokerageBps = 5m,
                BrokerageMin = 0m,
                EmolumentosBps = 3.25m,
                LiquidacaoBps = 2.75m,
            });
            calc = new BpsFeeCalculator(optsMonitor);
        }
        var processor = new ExecutionReportProcessor(ownership, book, positions, sink,
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            feeCalculator: calc,
            feeKeeper: withFees ? keeper : null);
        var snapshotter = new StateSnapshotter(book, positions, killSwitch,
            new SymbolHaltService(), new SessionPhaseService(),
            clOrdIds, ownership, algos, new AlgoIdRegistry(),
            new CashLedger(),
            feeKeeper: keeper);
        var replayer = new EventReplayer(book, ownership, killSwitch,
            new SymbolHaltService(), new SessionPhaseService(),
            processor, algos, clOrdIds, new AlgoIdRegistry(),
            feeKeeper: keeper,
            feeCalculator: calc);
        return (snapshotter, replayer);
    }

    private sealed class TestOptionsMonitor<T> : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        private readonly T _value;
        public TestOptionsMonitor(T value) => _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class NullSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent evt) { }
    }
}
