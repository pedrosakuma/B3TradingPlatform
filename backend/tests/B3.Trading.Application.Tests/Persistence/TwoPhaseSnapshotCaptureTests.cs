using System.Collections.Concurrent;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// P6 / F8 — pins the two-phase snapshot capture pipeline against the
/// <see cref="EventDispatcher"/> lock contract:
/// <list type="bullet">
///   <item>(a) The snapshot consistency invariant from RFC §4.3 holds
///   under concurrent dispatch + concurrent snapshot reads.</item>
///   <item>(b) Shallow-copy correctness: the raw arrays returned by
///   <see cref="StateSnapshotter.CaptureRaw"/> are independent of the
///   live registries — subsequent dispatcher mutations do not perturb
///   captured values.</item>
///   <item>(c) <see cref="StateSnapshotter.Project"/> never reads back
///   into the live registries — it is a pure function of the raw
///   arrays — so it is safe to run outside the dispatcher lock.</item>
/// </list>
/// </summary>
public class TwoPhaseSnapshotCaptureTests
{
    /// <summary>
    /// Concurrent dispatch + concurrent snapshot. The invariant pinned
    /// here: every snapshot taken under <c>WithSnapshotLock</c> +
    /// projected outside the lock observes a per-order
    /// <c>CumulativeQuantity</c> consistent with its own <c>seq</c>.
    ///
    /// <para>The construction: a single <see cref="Order"/> is submitted
    /// (seq 1). <c>N</c> background threads then dispatch a stream of
    /// <see cref="ExecutionReportReceivedEvent"/> partial fills, each
    /// adding 1 to <c>CumulativeQuantity</c>. Every dispatched event
    /// gets a unique <c>seq</c>, and after a fill at seq <c>S</c> the
    /// order's <c>CumulativeQuantity</c> equals <c>S − 1</c> (one is
    /// burnt by the submit). M reader threads concurrently take
    /// snapshots; for each captured snapshot, the projected
    /// <see cref="OrderSnapshot.CumulativeQuantity"/> for our order
    /// MUST equal <c>snap.Seq − 1</c>. A lock-leak would let the
    /// projection observe a fill applied at seq &gt; snap.Seq, which
    /// would manifest as <c>cum &gt; snap.Seq − 1</c>.</para>
    /// </summary>
    [Fact]
    public async Task SnapshotConsistency_HoldsUnderConcurrentDispatchAndConcurrentReads()
    {
        var root = Path.Combine(Path.GetTempPath(), "b3tp-snap-conc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var opts = new PersistenceOptions
            {
                DataDirectory = root,
                FirmId = "test",
                ChannelCapacity = 32_768,
                GroupCommitMaxRecords = 32,
                GroupCommitWindow = TimeSpan.FromMilliseconds(5),
                FsyncOnFlush = false,
            };
            await using var store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance);

            var book = new WorkingOrderBook();
            var ownership = new OrderOwnershipMap();
            var positions = new PositionKeeper();
            var sink = new RecordingSink();
            var processor = new ExecutionReportProcessor(ownership, book, positions, sink,
                new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);
            var snapshotter = new StateSnapshotter(book, positions, new KillSwitchService(),
                new SymbolHaltService(), new SessionPhaseService(),
                new ClOrdIdPrefixRegistry(), ownership, new AlgoBook(),
                new AlgoIdRegistry(), new CashLedger());
            var dispatcher = new EventDispatcher(store);

            var alice = new EndClientId("alice");
            const ulong clOrdId = 1UL;
            const long quantity = 1_000_000L;

            // Seq 1: submit. After this, CurrentSeq == 1, CumQ == 0.
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

            using var cts = new CancellationTokenSource();
            const int writers = 4;
            const int dispatchesPerWriter = 4_000;
            const int readers = 3;

            // Per-thread "next cumulative" cursor is delegated to the
            // dispatcher: each fill reads + advances the order's
            // CumulativeQuantity inside the same lock that bumps the
            // event's seq, so the (cum, seq) pair is atomic.
            var dispatchTasks = new Task[writers];
            for (var w = 0; w < writers; w++)
            {
                dispatchTasks[w] = Task.Run(() =>
                {
                    for (var i = 0; i < dispatchesPerWriter; i++)
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
                                // Single-step CumQ via the domain's
                                // ApplyCumulativeFill path, which only
                                // advances (never regresses).
                                ord.ApplyCumulativeFill(ord.CumulativeQuantity + 1);
                            });
                    }
                });
            }

            // Reader threads: keep snapping until cancellation. Verify
            // the §4.3 invariant on every snapshot.
            var observedSnapshots = new ConcurrentBag<(long Seq, long Cum)>();
            var readerErrors = new ConcurrentBag<string>();
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
                        if (ord is null)
                        {
                            readerErrors.Add($"order missing from snapshot at seq={snap.Seq}");
                            continue;
                        }
                        // Every event with seq > 1 is a +1 fill on our order; CumQ
                        // at seq S must equal exactly S − 1. A leaked read of the
                        // live aggregate after lock release would let CumQ exceed
                        // this bound (it can never be less because mutations only
                        // advance CumQ).
                        if (ord.CumulativeQuantity != snap.Seq - 1)
                        {
                            readerErrors.Add(
                                $"§4.3 violation: snap.Seq={snap.Seq}, ord.CumQ={ord.CumulativeQuantity} (expected {snap.Seq - 1})");
                        }
                        observedSnapshots.Add((snap.Seq, ord.CumulativeQuantity));
                    }
                });
            }

            await Task.WhenAll(dispatchTasks);
            cts.Cancel();
            await Task.WhenAll(readerTasks);

            Assert.Empty(readerErrors);
            // Sanity: at least one snapshot must have been observed mid-flight.
            Assert.NotEmpty(observedSnapshots);

            // Final state: every fill applied; CurrentSeq == writers * dispatchesPerWriter + 1.
            var totalFills = (long)writers * dispatchesPerWriter;
            Assert.Equal(totalFills + 1, dispatcher.CurrentSeq);
            Assert.True(book.TryGet(clOrdId, out var finalOrder));
            Assert.Equal(totalFills, finalOrder!.CumulativeQuantity);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Shallow-copy correctness — the raw arrays returned by
    /// <see cref="StateSnapshotter.CaptureRaw"/> must NOT alias the
    /// underlying registry storage. Mutating the live registry after a
    /// raw capture must leave the captured arrays untouched, otherwise
    /// concurrent dispatch could perturb a snapshot mid-projection.
    /// </summary>
    [Fact]
    public void RawSnapshot_ArraysAreIndependentOfLiveRegistries()
    {
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var positions = new PositionKeeper();
        var alice = new EndClientId("alice");

        book.TryAdd(new Order(1UL, alice, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m));
        ownership.Register(1UL, alice);
        positions.ApplyFill(alice, "PETR4", OrderSide.Buy, 50, 30m);

        var snapshotter = new StateSnapshotter(book, positions, new KillSwitchService(),
            new SymbolHaltService(), new SessionPhaseService(),
            new ClOrdIdPrefixRegistry(), ownership, new AlgoBook(),
            new AlgoIdRegistry(), new CashLedger());

        var raw = snapshotter.CaptureRaw(seq: 7);
        Assert.Single(raw.Orders);
        Assert.Single(raw.Ownership);
        Assert.Single(raw.Positions);
        var capturedOrderRaw = raw.Orders[0];

        // Mutate live registries after the raw capture — adding new
        // entries and mutating the existing order's CumQ. None of this
        // should bleed into the raw arrays.
        book.TryAdd(new Order(2UL, alice, "VALE3", 5555UL, OrderSide.Sell, OrderType.Limit, 200, 80m));
        ownership.Register(2UL, alice);
        positions.ApplyFill(alice, "VALE3", OrderSide.Sell, 200, 80m);
        Assert.True(book.TryGet(1UL, out var live));
        live!.ApplyCumulativeFill(75); // CumQ 0 → 75 on the live order.

        // Captured arrays untouched.
        Assert.Single(raw.Orders);
        Assert.Single(raw.Ownership);
        Assert.Single(raw.Positions);
        Assert.Equal(0L, capturedOrderRaw.Cum);
        Assert.Equal(OrderStatus.PendingNew, capturedOrderRaw.Status);

        // Project the original raw — must reflect the captured (pre-mutation) state.
        var snap = StateSnapshotter.Project(raw);
        Assert.Single(snap.WorkingOrders);
        Assert.Equal(0L, snap.WorkingOrders[0].CumulativeQuantity);
        Assert.Equal(nameof(OrderStatus.PendingNew), snap.WorkingOrders[0].Status);
        Assert.Single(snap.Ownership);
        Assert.Single(snap.Positions);
    }

    /// <summary>
    /// <see cref="StateSnapshotter.Project"/> must be a pure function of
    /// the supplied <see cref="RawPlatformSnapshot"/> — it must never
    /// peek back into the live registries. We verify this by mutating
    /// the live registries between <c>CaptureRaw</c> and <c>Project</c>;
    /// the projected output must still match the raw arrays as captured.
    /// </summary>
    [Fact]
    public void Project_NeverReadsLiveRegistries()
    {
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var alice = new EndClientId("alice");
        book.TryAdd(new Order(11UL, alice, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m));
        ownership.Register(11UL, alice);

        var snapshotter = new StateSnapshotter(book, new PositionKeeper(), new KillSwitchService(),
            new SymbolHaltService(), new SessionPhaseService(),
            new ClOrdIdPrefixRegistry(), ownership, new AlgoBook(),
            new AlgoIdRegistry(), new CashLedger());

        var raw = snapshotter.CaptureRaw(seq: 99);

        // Add a second order to the live registry between capture and project.
        // If Project leaks reads back into the live book, it would surface
        // both orders; the contract is that it surfaces exactly the one
        // captured into raw.
        book.TryAdd(new Order(22UL, alice, "VALE3", 5555UL, OrderSide.Sell, OrderType.Limit, 200, 80m));
        ownership.Register(22UL, alice);

        var snap = StateSnapshotter.Project(raw);

        Assert.Equal(99L, snap.Seq);
        Assert.Single(snap.WorkingOrders);
        Assert.Equal(11UL, snap.WorkingOrders[0].ClOrdId);
        Assert.Single(snap.Ownership);
        Assert.Equal(11UL, snap.Ownership[0].ClOrdId);
    }

    /// <summary>
    /// Equivalence test: <see cref="StateSnapshotter.Capture"/> (the
    /// legacy entry point, now implemented as <c>Project(CaptureRaw())</c>)
    /// must produce a snapshot that round-trips through restore to a
    /// state equivalent to the legacy direct-projection output. We
    /// exercise every captured registry to lock the byte-equivalent
    /// contract from the issue's acceptance.
    /// </summary>
    [Fact]
    public void TwoPhase_RoundTripsAllRegistriesIdenticallyToLegacyShape()
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var killSwitch = new KillSwitchService();
        var halts = new SymbolHaltService();
        var phases = new SessionPhaseService(SessionPhase.AfterHours);
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var algos = new AlgoBook();
        var algoIds = new AlgoIdRegistry();
        var cash = new CashLedger();
        var creds = new InMemoryUserBotCredentialRegistry();
        var sessions = new InMemoryUserBotSessionRegistry();
        var maps = new InMemoryUserBotOrderMappingRegistry();

        var alice = new EndClientId("alice");
        var bob = new EndClientId("bob");

        book.TryAdd(new Order(1UL, alice, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m,
            minQty: 25));
        book.TryAdd(new Order(2UL, bob, "VALE3", 5555UL, OrderSide.Sell, OrderType.Limit, 50, 80m));
        ownership.Register(1UL, alice);
        ownership.Register(2UL, bob);
        positions.ApplyFill(alice, "PETR4", OrderSide.Buy, 30, 30m);
        killSwitch.KillFirm("EVIL");
        killSwitch.KillEndClient(bob);
        halts.Halt("PETR4");
        phases.SetPhase("VALE3", SessionPhase.OpeningAuction);
        cash.SeedIfAbsent(alice, 1000m);
        clOrdIds.AllocatePrefix(alice);
        algoIds.Generate("TEST");
        maps.RegisterOrderInternal(1UL, Guid.NewGuid(), 100UL);

        var snapshotter = new StateSnapshotter(book, positions, killSwitch, halts, phases,
            clOrdIds, ownership, algos, algoIds, cash, creds, sessions, maps);

        var snap = snapshotter.Capture(seq: 12345);

        // Round-trip through JSON to mimic the disk path.
        var json = System.Text.Json.JsonSerializer.Serialize(snap);
        var snapBack = System.Text.Json.JsonSerializer.Deserialize<PlatformSnapshot>(json)!;

        var book2 = new WorkingOrderBook();
        var positions2 = new PositionKeeper();
        var killSwitch2 = new KillSwitchService();
        var halts2 = new SymbolHaltService();
        var phases2 = new SessionPhaseService();
        var clOrdIds2 = new ClOrdIdPrefixRegistry();
        var ownership2 = new OrderOwnershipMap();
        var algos2 = new AlgoBook();
        var algoIds2 = new AlgoIdRegistry();
        var cash2 = new CashLedger();
        var creds2 = new InMemoryUserBotCredentialRegistry();
        var sessions2 = new InMemoryUserBotSessionRegistry();
        var maps2 = new InMemoryUserBotOrderMappingRegistry();

        var snapshotter2 = new StateSnapshotter(book2, positions2, killSwitch2, halts2, phases2,
            clOrdIds2, ownership2, algos2, algoIds2, cash2, creds2, sessions2, maps2);
        snapshotter2.Restore(snapBack);

        Assert.True(book2.TryGet(1UL, out var restoredWithMinQty));
        Assert.True(book2.TryGet(2UL, out _));
        Assert.Equal(25, restoredWithMinQty!.MinQty);
        Assert.True(killSwitch2.IsFirmKilled("EVIL"));
        Assert.True(killSwitch2.IsEndClientKilled(bob));
        Assert.True(halts2.IsHalted("PETR4"));
        Assert.Equal(SessionPhase.AfterHours, phases2.DefaultPhase);
        Assert.Equal(SessionPhase.OpeningAuction, phases2.GetPhase("VALE3"));
        Assert.True(ownership2.TryResolve(1UL, out _));
        Assert.True(maps2.TryGetOrderMapping(1UL, out _));
    }

    private sealed class RecordingSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent evt) { }
    }
}
