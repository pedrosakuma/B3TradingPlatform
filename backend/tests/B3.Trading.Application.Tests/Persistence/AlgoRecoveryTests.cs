using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// Slice 2: parent algo aggregates persist via three new WAL events
/// (<see cref="AlgoCreatedEvent"/>, <see cref="AlgoCancelRequestedEvent"/>,
/// <see cref="AlgoTerminalStateRecordedEvent"/>) plus a new
/// <see cref="AlgoSnapshot"/> array. These tests exercise the full
/// recovery shape: WAL-only replay and snapshot+tail must both produce
/// the same in-memory <see cref="AlgoBook"/>.
/// </summary>
public class AlgoRecoveryTests : IDisposable
{
    private readonly string _root;

    public AlgoRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "b3tp-algo-recovery-" + Guid.NewGuid().ToString("N"));
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
    public async Task AlgoEvents_ReplayedFromWal_ReproduceParentAggregate()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var terminalAt = createdAt.AddSeconds(5);

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, ownership, killSwitch, processor, algos, dispatcher) = Build(store);

            // Iceberg created.
            dispatcher.Dispatch(
                new AlgoCreatedEvent
                {
                    AlgoId = 100UL,
                    EndClientId = "alice",
                    FirmId = "TEST",
                    Symbol = "PETR4",
                    SecurityId = 4321UL,
                    Side = "Buy",
                    Type = "Iceberg",
                    TotalQuantity = 1000,
                    CreatedAtUtc = createdAt,
                    IcebergDisplayQuantity = 100,
                    IcebergLimitPrice = 30m,
                },
                () => algos.TryAdd(new Algo(100UL, new EndClientId("alice"), "TEST", "PETR4", 4321UL,
                    OrderSide.Buy, AlgoType.Iceberg, 1000,
                    new IcebergParameters(100, 30m), createdAt)));

            // First child child submitted (manual orders pipeline reused).
            dispatcher.Dispatch(
                new OrderSubmittedEvent
                {
                    ClOrdId = 1UL,
                    EndClientId = "alice",
                    FirmId = "TEST",
                    Symbol = "PETR4",
                    SecurityId = 4321UL,
                    Side = "Buy",
                    Type = "Limit",
                    Quantity = 100,
                    Price = 30m,
                    ParentAlgoId = 100UL,
                    AlgoSliceSeq = 0,
                },
                () =>
                {
                    book.TryAdd(new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL,
                        OrderSide.Buy, OrderType.Limit, 100, 30m, "TEST", 100UL, 0));
                    ownership.Register(1UL, new EndClientId("alice"));
                });

            // Operator cancels.
            dispatcher.Dispatch(
                new AlgoCancelRequestedEvent { AlgoId = 100UL, FirmId = "TEST", ActorUserId = "carol" },
                () => algos.TryGet("TEST", 100UL, out var a).Then(() => a!.RequestCancel()));

            // Engine records terminal.
            dispatcher.Dispatch(
                new AlgoTerminalStateRecordedEvent
                {
                    AlgoId = 100UL,
                    FirmId = "TEST",
                    Status = "Cancelled",
                    Reason = "UserCancelled",
                    AtUtc = terminalAt,
                },
                () => algos.TryGet("TEST", 100UL, out var a).Then(() =>
                    a!.RecordTerminal(AlgoStatus.Cancelled, AlgoTerminalReason.UserCancelled, terminalAt)));
        }

        // Cold boot — replay WAL only, no snapshot taken.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, ownership, killSwitch, processor, algos, _) = Build(store);
            var algoIds = new AlgoIdRegistry();
            var snapshotter = new StateSnapshotter(book, new PositionKeeper(), killSwitch, new SymbolHaltService(), new SessionPhaseService(), new ClOrdIdPrefixRegistry(), ownership, algos, algoIds, new CashLedger());
            var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(), new SessionPhaseService(), processor, algos, new ClOrdIdPrefixRegistry(), new AlgoIdRegistry());
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"), NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            Assert.True(algos.TryGet("TEST", 100UL, out var algo) && algo is not null);
            Assert.Equal(AlgoStatus.Cancelled, algo!.Status);
            Assert.Equal(AlgoTerminalReason.UserCancelled, algo.TerminalReason);
            Assert.Equal(terminalAt, algo.TerminalAtUtc);
            Assert.Equal(AlgoType.Iceberg, algo.Type);
            var ip = Assert.IsType<IcebergParameters>(algo.Parameters);
            Assert.Equal(100, ip.DisplayQuantity);
            Assert.Equal(30m, ip.LimitPrice);

            // Child carries the algo linkage on the Order.
            Assert.True(book.TryGet(1UL, out var child) && child is not null);
            Assert.Equal(100UL, child!.ParentAlgoId);
            Assert.Equal(0, child.AlgoSliceSeq);
        }
    }

    [Fact]
    public async Task TwapAlgo_RoundtripsThroughSnapshot()
    {
        var createdAt = new DateTimeOffset(2026, 5, 4, 13, 0, 0, TimeSpan.Zero);
        var twapStart = createdAt;
        var twapEnd = createdAt.AddMinutes(10);

        PlatformSnapshot? snap;
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, ownership, killSwitch, _, algos, dispatcher) = Build(store);
            var algoIds = new AlgoIdRegistry();
            var snapshotter = new StateSnapshotter(book, new PositionKeeper(), killSwitch, new SymbolHaltService(), new SessionPhaseService(), new ClOrdIdPrefixRegistry(), ownership, algos, algoIds, new CashLedger());

            dispatcher.Dispatch(
                new AlgoCreatedEvent
                {
                    AlgoId = 200UL,
                    EndClientId = "bob",
                    FirmId = "TEST",
                    Symbol = "VALE3",
                    SecurityId = 1234UL,
                    Side = "Sell",
                    Type = "Twap",
                    TotalQuantity = 5000,
                    CreatedAtUtc = createdAt,
                    TwapStartUtc = twapStart,
                    TwapEndUtc = twapEnd,
                    TwapSliceCount = 5,
                    TwapChildOrderType = "Market",
                    TwapChildPrice = null,
                },
                () => algos.TryAdd(new Algo(200UL, new EndClientId("bob"), "TEST", "VALE3", 1234UL,
                    OrderSide.Sell, AlgoType.Twap, 5000,
                    new TwapParameters(twapStart, twapEnd, 5, OrderType.Market, null), createdAt)));

            // Two slice fills of 1000 each.
            algos.TryGet("TEST", 200UL, out var live);
            live!.MarkWorking();
            live.RecordFill(1000);
            live.RecordFill(1000);

            snap = null;
            dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
        }

        Assert.NotNull(snap);
        Assert.Single(snap!.Algos);

        // Restore into a fresh book and verify shape.
        var restored = new AlgoBook();
        restored.Restore(snap.Algos);
        Assert.True(restored.TryGet("TEST", 200UL, out var rehydrated) && rehydrated is not null);
        Assert.Equal(2000, rehydrated!.FilledQuantity);
        Assert.Equal(3000, rehydrated.RemainingQuantity);
        Assert.Equal(AlgoStatus.Working, rehydrated.Status);
        var tp = Assert.IsType<TwapParameters>(rehydrated.Parameters);
        Assert.Equal(twapStart, tp.StartUtc);
        Assert.Equal(twapEnd, tp.EndUtc);
        Assert.Equal(5, tp.SliceCount);
        Assert.Equal(OrderType.Market, tp.ChildOrderType);
        Assert.Null(tp.ChildPrice);
    }

    [Fact]
    public async Task VwapAlgo_ReplayedFromWal_ReproduceParentAggregate()
    {
        // Pass-1 review (#294) P2. Mirrors the Iceberg WAL-replay test
        // above for VWAP: the AlgoCreatedEvent VWAP block + the parent
        // cancel + terminal events round-trip through a cold-boot WAL
        // replay back into an AlgoBook with the same shape.
        var createdAt = new DateTimeOffset(2026, 5, 4, 13, 0, 0, TimeSpan.Zero);
        var vwapStart = createdAt;
        var vwapEnd = createdAt.AddMinutes(30);
        var terminalAt = createdAt.AddSeconds(5);
        var tick = TimeSpan.FromSeconds(30);

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, ownership, killSwitch, processor, algos, dispatcher) = Build(store);

            dispatcher.Dispatch(
                new AlgoCreatedEvent
                {
                    AlgoId = 300UL,
                    EndClientId = "carol",
                    FirmId = "TEST",
                    Symbol = "PETR4",
                    SecurityId = 4321UL,
                    Side = "Buy",
                    Type = "Vwap",
                    TotalQuantity = 5000,
                    CreatedAtUtc = createdAt,
                    VwapStartUtc = vwapStart,
                    VwapEndUtc = vwapEnd,
                    VwapChildOrderType = "Limit",
                    VwapChildPrice = 30m,
                    VwapTickIntervalTicks = tick.Ticks,
                    VwapSliceMaxPct = 0.20m,
                    VwapPriceLimit = 31m,
                    VwapParticipationCap = 0.10m,
                },
                () => algos.TryAdd(new Algo(300UL, new EndClientId("carol"), "TEST", "PETR4", 4321UL,
                    OrderSide.Buy, AlgoType.Vwap, 5000,
                    new VwapParameters(vwapStart, vwapEnd, OrderType.Limit, 30m, tick, 0.20m, 31m, 0.10m), createdAt)));

            dispatcher.Dispatch(
                new AlgoVwapSlicedEvent
                {
                    AlgoId = 300UL,
                    FirmId = "TEST",
                    SliceSeq = 0,
                    TargetCumQty = 100,
                    ExecutedCum = 0,
                    SliceQty = 100,
                    PlannedAtUtc = vwapStart,
                },
                () => { /* observability-only: no aggregate mutation */ });

            dispatcher.Dispatch(
                new AlgoCancelRequestedEvent { AlgoId = 300UL, FirmId = "TEST", ActorUserId = "ops" },
                () => algos.TryGet("TEST", 300UL, out var a).Then(() => a!.RequestCancel()));

            dispatcher.Dispatch(
                new AlgoTerminalStateRecordedEvent
                {
                    AlgoId = 300UL,
                    FirmId = "TEST",
                    Status = "Cancelled",
                    Reason = "UserCancelled",
                    AtUtc = terminalAt,
                },
                () => algos.TryGet("TEST", 300UL, out var a).Then(() =>
                    a!.RecordTerminal(AlgoStatus.Cancelled, AlgoTerminalReason.UserCancelled, terminalAt)));
        }

        // Cold-boot replay.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, ownership, killSwitch, processor, algos, _) = Build(store);
            var algoIds = new AlgoIdRegistry();
            var snapshotter = new StateSnapshotter(book, new PositionKeeper(), killSwitch, new SymbolHaltService(), new SessionPhaseService(), new ClOrdIdPrefixRegistry(), ownership, algos, algoIds, new CashLedger());
            var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(), new SessionPhaseService(), processor, algos, new ClOrdIdPrefixRegistry(), new AlgoIdRegistry());
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"), NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            Assert.True(algos.TryGet("TEST", 300UL, out var algo) && algo is not null);
            Assert.Equal(AlgoStatus.Cancelled, algo!.Status);
            Assert.Equal(AlgoTerminalReason.UserCancelled, algo.TerminalReason);
            Assert.Equal(terminalAt, algo.TerminalAtUtc);
            Assert.Equal(AlgoType.Vwap, algo.Type);
            var vp = Assert.IsType<VwapParameters>(algo.Parameters);
            Assert.Equal(vwapStart, vp.StartUtc);
            Assert.Equal(vwapEnd, vp.EndUtc);
            Assert.Equal(OrderType.Limit, vp.ChildOrderType);
            Assert.Equal(30m, vp.ChildPrice);
            Assert.Equal(tick, vp.TickInterval);
            Assert.Equal(0.20m, vp.SliceMaxPct);
            Assert.Equal(31m, vp.PriceLimit);
            Assert.Equal(0.10m, vp.ParticipationCap);
        }
    }

    [Fact]
    public void AlgoBook_EnumerateForOwner_ExcludesTerminalByDefault()
    {
        var algos = new AlgoBook();
        var alice = new EndClientId("alice");
        var live = new Algo(1UL, alice, "TEST", "PETR4", 4321UL, OrderSide.Buy, AlgoType.Iceberg,
            100, new IcebergParameters(10, 30m), DateTimeOffset.UtcNow);
        var done = new Algo(2UL, alice, "TEST", "PETR4", 4321UL, OrderSide.Buy, AlgoType.Iceberg,
            100, new IcebergParameters(10, 30m), DateTimeOffset.UtcNow);
        done.RecordTerminal(AlgoStatus.Completed, AlgoTerminalReason.None, DateTimeOffset.UtcNow);
        algos.TryAdd(live);
        algos.TryAdd(done);

        Assert.Single(algos.EnumerateForOwner("TEST", alice));
        Assert.Equal(2, algos.EnumerateForOwner("TEST", alice, includeTerminal: true).Count);
    }

    private static (WorkingOrderBook, OrderOwnershipMap, KillSwitchService, ExecutionReportProcessor, AlgoBook, EventDispatcher) Build(IEventStore store)
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var killSwitch = new KillSwitchService();
        var ownership = new OrderOwnershipMap();
        var algos = new AlgoBook();
        var sink = new TestSink();
        var processor = new ExecutionReportProcessor(ownership, book, positions, sink,
            new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);
        var dispatcher = new EventDispatcher(store);
        return (book, ownership, killSwitch, processor, algos, dispatcher);
    }

    private sealed class TestSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent evt) { }
    }
}

internal static class TupleExt
{
    // Tiny helper so the dispatcher action stays inline-readable when we
    // need to do "TryGet then call a method on the result" without a temp.
    public static void Then(this bool found, Action a)
    {
        if (found) a();
    }
}
