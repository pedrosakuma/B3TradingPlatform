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
            var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(), new SessionPhaseService(), processor, algos, new ClOrdIdPrefixRegistry(), new AlgoIdRegistry());
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
            var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(), new SessionPhaseService(), processor, algos, new ClOrdIdPrefixRegistry(), new AlgoIdRegistry());
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

    [Fact]
    public async Task Recovery_OrderReplaceRequested_RestoresIntentAndOwnershipLink()
    {
        // Phase 1: submit an order, then dispatch a replace-requested event.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, _, _, ownership, _, dispatcher, _, _, _) = BuildState(store);
            DispatchSubmit(dispatcher, book, ownership, 1UL, "alice", "PETR4", OrderSide.Buy, 100, 30m);
            dispatcher.Dispatch(
                new OrderReplaceRequestedEvent
                {
                    OriginalClOrdId = 1UL,
                    NewClOrdId = 2UL,
                    EndClientId = "alice",
                    FirmId = "TEST",
                    Symbol = "PETR4",
                    SecurityId = 4321UL,
                    Side = "Buy",
                    Type = "Limit",
                    NewQuantity = 200,
                    NewPrice = 31m,
                },
                () => { /* live wiring done by OrderModifyService; replay re-applies it */ });
            await store.FlushAsync();
        }

        // Phase 2: cold boot — replayer with PendingReplacementRegistry must
        // re-register the intent AND the new→orig ownership link so a
        // subsequent Replaced/Rejected ER under newClOrdId resolves.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, _, killSwitch, ownership, snapshotter, _, processor, _, algos) = BuildState(store);
            var replacements = new PendingReplacementRegistry();
            var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(), new SessionPhaseService(), processor, algos, new ClOrdIdPrefixRegistry(), new AlgoIdRegistry(), replacements);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"), NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            Assert.True(replacements.IsOriginalInFlight(1UL));
            Assert.True(replacements.TryGet(2UL, out var intent));
            Assert.NotNull(intent);
            Assert.Equal(1UL, intent!.OriginalClOrdId);
            Assert.Equal(200, intent.NewQuantity);
            Assert.Equal(31m, intent.NewPrice);
            // Owner of newClOrdId resolves through the replace link.
            Assert.True(ownership.TryResolve(2UL, out var newOwner));
            Assert.Equal(new EndClientId("alice"), newOwner);
        }
    }

    [Fact]
    public async Task Recovery_OrderReplaceRequested_NoReplacementsRegistry_IsNoOp()
    {
        // Backward-compat: existing constructors without the optional
        // PendingReplacementRegistry must still tolerate the new event.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, _, _, ownership, _, dispatcher, _, _, _) = BuildState(store);
            DispatchSubmit(dispatcher, book, ownership, 1UL, "alice", "PETR4", OrderSide.Buy, 100, 30m);
            dispatcher.Dispatch(
                new OrderReplaceRequestedEvent
                {
                    OriginalClOrdId = 1UL,
                    NewClOrdId = 2UL,
                    EndClientId = "alice",
                    FirmId = "TEST",
                    Symbol = "PETR4",
                    SecurityId = 4321UL,
                    Side = "Buy",
                    Type = "Limit",
                    NewQuantity = 200,
                    NewPrice = 31m,
                },
                () => { });
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, _, killSwitch, ownership, snapshotter, _, processor, _, algos) = BuildState(store);
            var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(), new SessionPhaseService(), processor, algos, new ClOrdIdPrefixRegistry(), new AlgoIdRegistry());
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"), NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            Assert.True(book.TryGet(1UL, out _));
        }
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
        var snapshotter = new StateSnapshotter(book, positions, killSwitch, new SymbolHaltService(), new SessionPhaseService(), clOrdIds, ownership, algos, new AlgoIdRegistry(), new CashLedger());
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

    [Fact]
    public void EventReplayer_AppliesSessionPhaseChangedEvents()
    {
        // #108 — verify replayer rebuilds default + per-symbol phase state
        // from the WAL stream alone (no snapshot).
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var killSwitch = new KillSwitchService();
        var phases = new SessionPhaseService(SessionPhase.Continuous);
        var algos = new AlgoBook();
        var processor = new ExecutionReportProcessor(ownership, book, new PositionKeeper(),
            new TestSink(), new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);
        var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(),
            phases, processor, algos, new ClOrdIdPrefixRegistry(), new AlgoIdRegistry());

        replayer.Apply(new SessionPhaseChangedEvent { Symbol = null, Phase = "Closed" });
        Assert.Equal(SessionPhase.Closed, phases.DefaultPhase);

        replayer.Apply(new SessionPhaseChangedEvent { Symbol = "PETR4", Phase = "OpeningAuction" });
        Assert.Equal(SessionPhase.OpeningAuction, phases.GetPhase("PETR4"));
        Assert.Equal(SessionPhase.Closed, phases.GetPhase("VALE3")); // no override → default

        replayer.Apply(new SessionPhaseChangedEvent { Symbol = "PETR4", Phase = "Continuous", Cleared = true });
        Assert.Equal(SessionPhase.Closed, phases.GetPhase("PETR4")); // override removed → default
    }

    [Fact]
    public void StateSnapshotter_RoundTripsSessionPhase()
    {
        // #108 — the snapshot must carry the default + per-symbol overrides,
        // otherwise the WAL+snapshot recovery path drops phase state every
        // time the snapshot rotates.
        var phases1 = new SessionPhaseService(SessionPhase.Continuous);
        phases1.SetDefaultPhase(SessionPhase.AfterHours);
        phases1.SetPhase("PETR4", SessionPhase.OpeningAuction);
        phases1.SetPhase("VALE3", SessionPhase.Closed);

        var snapshotter1 = new StateSnapshotter(
            new WorkingOrderBook(), new PositionKeeper(), new KillSwitchService(),
            new SymbolHaltService(), phases1,
            new ClOrdIdPrefixRegistry(), new OrderOwnershipMap(), new AlgoBook(),
            new AlgoIdRegistry(), new CashLedger());
        var snap = snapshotter1.Capture(seq: 42);

        // Round-trip through JSON to mimic file-store materialisation.
        var json = System.Text.Json.JsonSerializer.Serialize(snap);
        var snapBack = System.Text.Json.JsonSerializer.Deserialize<PlatformSnapshot>(json)!;

        var phases2 = new SessionPhaseService(SessionPhase.Continuous);
        var snapshotter2 = new StateSnapshotter(
            new WorkingOrderBook(), new PositionKeeper(), new KillSwitchService(),
            new SymbolHaltService(), phases2,
            new ClOrdIdPrefixRegistry(), new OrderOwnershipMap(), new AlgoBook(),
            new AlgoIdRegistry(), new CashLedger());
        snapshotter2.Restore(snapBack);

        Assert.Equal(SessionPhase.AfterHours, phases2.DefaultPhase);
        Assert.Equal(SessionPhase.OpeningAuction, phases2.GetPhase("PETR4"));
        Assert.Equal(SessionPhase.Closed, phases2.GetPhase("VALE3"));
    }

    [Fact]
    public void StateSnapshotter_Restore_SeedsLegacyZeroBasis_FromPass2ShapedSnapshot()
    {
        // Pass-4 (#278) P1#1 — a pass-2-shaped snapshot has
        // PnlAvgCost populated (the non-zero-basis rows seeded by
        // pass-1's SeedAvgCostFromLegacyPositions) but no
        // PnlUnknownBasis block (the field didn't exist yet). The
        // previous gate (PnlAvgCost.Count == 0 && PnlUnknownBasis.Count == 0)
        // refused to seed in this case, leaving zero-basis Position
        // rows as phantom-P&L sources. Seeding must run whenever
        // PnlUnknownBasis is empty; SeedAvgCostFromLegacyPositions
        // is idempotent on existing _avgCost keys, so we only add
        // _unknownBasisQty for the zero-basis rows that need it.
        var snap = new PlatformSnapshot
        {
            Seq = 1,
            Positions =
            {
                new PositionSnapshot("alice", "PETR4", 100, 25m), // already in PnlAvgCost
                new PositionSnapshot("alice", "VALE3", 50, 0m),   // legacy zero-basis row
            },
            PnlAvgCost = { new PnlAvgCostSnapshot("alice", "PETR4", 100, 25m) },
            // PnlUnknownBasis intentionally empty — pass-2 snapshot.
        };

        var pnl = new PnlKeeper();
        var snapshotter = new StateSnapshotter(
            new WorkingOrderBook(), new PositionKeeper(), new KillSwitchService(),
            new SymbolHaltService(), new SessionPhaseService(),
            new ClOrdIdPrefixRegistry(), new OrderOwnershipMap(), new AlgoBook(),
            new AlgoIdRegistry(), new CashLedger(),
            pnlKeeper: pnl);
        snapshotter.Restore(snap);

        // Existing PnlAvgCost row preserved, zero-basis row tracked
        // as unknown.
        var known = pnl.GetAvgCost("alice", "PETR4")!;
        Assert.Equal(100, known.NetQuantity);
        Assert.Equal(25m, known.AvgPrice);
        Assert.Equal(50, pnl.GetUnknownBasisQty("alice", "VALE3"));

        // Next sell on the unknown leg realises 0 (not phantom).
        Assert.Equal(0m, pnl.ApplyFillToAvgCost("alice", "VALE3", OrderSide.Sell, 50, 33m));
    }

    [Fact]
    public void EventReplayer_AdvancesClOrdIdWatermark_FromOrderSubmittedAndReplaceAndEr()
    {
        // #157 — full coverage: OrderSubmittedEvent, OrderReplaceRequestedEvent
        // (new ID), and ExecutionReportReceivedEvent (cancel-side ID resolved
        // via OrigClOrdId) must all advance the registry watermark so the
        // next live Generate cannot regress.
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var killSwitch = new KillSwitchService();
        var phases = new SessionPhaseService();
        var algos = new AlgoBook();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var processor = new ExecutionReportProcessor(ownership, book, new PositionKeeper(),
            new TestSink(), new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);
        var replacements = new PendingReplacementRegistry();
        var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(),
            phases, processor, algos, clOrdIds, new AlgoIdRegistry(), replacements);

        var alice = new EndClientId("alice");
        // Synthesise the kind of IDs the live registry would have produced
        // for alice after a snapshot at watermark 0 (prefix=0, counter=N).
        const ulong prefix = 3UL;
        ulong Pack(ulong counter) => (prefix << ClOrdIdPrefixRegistry.CounterBits) | counter;
        var orig = Pack(10UL);
        var newId = Pack(11UL);
        var cancelId = Pack(12UL);

        replayer.Apply(new OrderSubmittedEvent
        {
            ClOrdId = orig,
            EndClientId = "alice",
            FirmId = "TEST",
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
        });

        replayer.Apply(new OrderReplaceRequestedEvent
        {
            OriginalClOrdId = orig,
            NewClOrdId = newId,
            EndClientId = "alice",
            FirmId = "TEST",
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            NewQuantity = 100,
            NewPrice = 31m,
        });

        // Cancel-side ID: only the ER carries it; OrigClOrdId resolves owner.
        replayer.Apply(new ExecutionReportReceivedEvent
        {
            ClOrdId = cancelId,
            OrigClOrdId = newId,
            ExecKind = ExecKind.Canceled.ToString(),
            LeavesQuantity = 0,
            CumulativeQuantity = 0,
            LastQuantity = 0,
            LastPrice = 0m,
            Synthetic = false,
        });

        // Next Generate(alice) must skip past 12 — not regress to 1.
        var next = clOrdIds.Generate(alice);
        Assert.Equal(prefix, next >> ClOrdIdPrefixRegistry.CounterBits);
        Assert.Equal(13UL, next & ClOrdIdPrefixRegistry.CounterMask);
    }

    [Fact]
    public void EventReplayer_AdvancesAlgoIdWatermark_FromAlgoCreatedEvent()
    {
        // #160 — replay of AlgoCreatedEvent must advance AlgoIdRegistry's
        // per-firm counter so the next live Generate doesn't regress and
        // re-issue an AlgoId already owned by a restored algo.
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var killSwitch = new KillSwitchService();
        var phases = new SessionPhaseService();
        var algos = new AlgoBook();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var algoIds = new AlgoIdRegistry();
        var processor = new ExecutionReportProcessor(ownership, book, new PositionKeeper(),
            new TestSink(), new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);
        var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(),
            phases, processor, algos, clOrdIds, algoIds);

        replayer.Apply(new AlgoCreatedEvent
        {
            AlgoId = 42UL,
            EndClientId = "alice",
            FirmId = "TEST",
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Iceberg",
            TotalQuantity = 1000,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IcebergDisplayQuantity = 100,
            IcebergLimitPrice = 30m,
        });

        Assert.Equal(43UL, algoIds.Generate("TEST"));
        // Other firms unaffected.
        Assert.Equal(1UL, algoIds.Generate("OTHER"));
    }

    /// <summary>
    /// Q4.15 (#315). The chaos drill's <c>host-kill</c> scenario asserts
    /// operational symptoms (the host reboots, /ready comes back, WAL
    /// seq is monotonic). This test asserts the underlying invariant in
    /// pure .NET, so the contract is enforceable without a docker stack:
    ///
    /// <list type="bullet">
    ///   <item>Write N events, flush a prefix.</item>
    ///   <item>Append a torn record at the tail (length header + partial
    ///   payload) — exactly the shape an ungraceful <c>kill -9</c> mid-
    ///   group-commit leaves on disk.</item>
    ///   <item>Re-open and run <see cref="PersistenceRecovery"/>.</item>
    ///   <item>Assert the in-memory state matches the WAL's last-flushed
    ///   seq EXACTLY — no torn-write false positives (good records
    ///   skipped), no false negatives (torn record applied).</item>
    /// </list>
    ///
    /// Companion to <c>scripts/chaos/run-chaos-drill.sh</c> and
    /// <c>docs/operations/runbook-failover-recovery.md</c> §2.4.
    /// </summary>
    [Fact]
    public async Task UngracefulStop_NoFlush_RecoversToLastFlushedSeq_NoTornWriteFalsePositives()
    {
        const int flushedCount = 5;

        // Phase 1: write `flushedCount` orders and flush. This is the
        // "durable prefix" the recovery must reproduce exactly.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var (book, _, _, ownership, _, dispatcher, _, _, _) = BuildState(store);
            for (var i = 1UL; i <= (ulong)flushedCount; i++)
                DispatchSubmit(dispatcher, book, ownership, i, "alice", "PETR4", OrderSide.Buy, 100, 30m);
            await store.FlushAsync();
        }

        // Phase 2: simulate an ungraceful kill mid-write — append a
        // record header that says "the next 999 bytes are the payload"
        // but write only 3 bytes before "dying". SegmentReader must
        // detect this as torn and stop at the last good record.
        var segLog = Directory.EnumerateFiles(Path.Combine(_root, "test", "wal"), "*.log",
            SearchOption.AllDirectories).Single();
        var preTornLength = new FileInfo(segLog).Length;
        await using (var fs = new FileStream(segLog, FileMode.Append, FileAccess.Write))
        {
            // [u32 length=999][u32 crc=0xDEADBEEF][3 bytes of "payload"]
            var header = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 999);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 0xDEADBEEFu);
            await fs.WriteAsync(header);
            await fs.WriteAsync(new byte[] { 0x7B, 0x22, 0x6B }); // 3 bytes (not 999)
        }
        Assert.True(new FileInfo(segLog).Length > preTornLength,
            "test setup invariant: torn tail must have been appended");

        // Phase 3: cold boot. PersistenceRecovery must reproduce
        // exactly the flushed prefix — no torn-write false positives
        // (every good record present), no false negatives (the torn
        // record must NOT materialise as an order in the book).
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            Assert.Equal(flushedCount, store.CurrentSeq);

            var (book, _, killSwitch, ownership, snapshotter, _, processor, _, algos) = BuildState(store);
            var replayer = new EventReplayer(book, ownership, killSwitch, new SymbolHaltService(),
                new SessionPhaseService(), processor, algos, new ClOrdIdPrefixRegistry(), new AlgoIdRegistry());
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"), NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            for (var i = 1UL; i <= (ulong)flushedCount; i++)
                Assert.True(book.TryGet(i, out _), $"missing flushed order ORD-{i} (torn-write false positive)");
            Assert.False(book.TryGet((ulong)flushedCount + 1, out _),
                "torn record materialised as an order — torn-write false negative");
        }

        // Phase 4: clean re-open of the SAME directory (still has the
        // torn tail) must still report the same last-good seq, i.e. a
        // repeat boot is idempotent — operators can restart any number
        // of times without losing the durable prefix.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            Assert.Equal(flushedCount, store.CurrentSeq);
        }
    }

    private sealed class TestSink : IExecutionEventSink
    {
        public List<ExecutionEvent> Events { get; } = new();
        public void Publish(ExecutionEvent evt) => Events.Add(evt);
    }
}
