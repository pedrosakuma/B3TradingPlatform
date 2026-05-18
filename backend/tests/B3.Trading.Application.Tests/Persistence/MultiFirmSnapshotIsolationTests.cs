using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// Q4.2 (#302) — Multi-firm snapshot &amp; restore contract under the
/// real WAL + snapshot + recovery pipeline.
///
/// <para>The platform keeps a SINGLE global snapshot with <c>FirmId</c>
/// carried as a dimension on every owner-keyed structure (Position,
/// PnL avg-cost, Order, Ownership, SubAccount position/PnL …). This
/// suite stands up a real <see cref="FileEventStore"/> +
/// <see cref="EventDispatcher"/> + <see cref="StateSnapshotter"/> +
/// <see cref="PersistenceRecovery"/> harness (mirroring
/// <see cref="TwoPhaseSnapshotCaptureTests"/> /
/// <see cref="RecoveryAndSnapshotTests"/>), drives orders + fills
/// across FIRM01/FIRM02/FIRM03 through the live processor, captures a
/// platform snapshot, appends a few more WAL events (tail), tears the
/// platform down and reconstructs it from snapshot + WAL tail, and
/// asserts that every firm's WorkingOrderBook / PositionKeeper /
/// SubAccountPositionKeeper / PnlKeeper avg-cost / SubAccountPnlKeeper
/// row survives unchanged with ZERO cross-firm bleed-through.</para>
///
/// <para>The narrower per-keeper recovery tests
/// (<c>CashKeeperRecoveryTests</c>, <c>PnlKeeperRecoveryTests</c>, …)
/// already exercise individual WAL-replay legs; this test focuses on
/// the multi-firm composition under the real persistence pipeline.</para>
/// </summary>
public class MultiFirmSnapshotIsolationTests : IDisposable
{
    private readonly string _root;

    public MultiFirmSnapshotIsolationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "b3tp-multi-firm-snap-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// Bare-keeper round-trip (kept from the original test): asserts
    /// that <see cref="PositionKeeper.Snapshot"/>+
    /// <see cref="PositionKeeper.Restore"/> keep <c>(FirmId, owner)</c>
    /// rows distinct. Cheap unit test that guards the snapshot-DTO
    /// shape without spinning up the full pipeline; the headline
    /// assertion is the integration test below.
    /// </summary>
    [Fact]
    public void PositionKeeper_Snapshot_Restore_PreservesPerFirmSlices()
    {
        var alice = new EndClientId("alice");
        var bob = new EndClientId("bob");
        var charlie = new EndClientId("charlie");

        var src = new PositionKeeper();
        src.ApplyFill("FIRM01", alice, "PETR4", OrderSide.Buy, 100, 30m);
        src.ApplyFill("FIRM02", bob, "VALE3", OrderSide.Buy, 200, 60m);
        src.ApplyFill("FIRM03", charlie, "ITUB4", OrderSide.Buy, 300, 25m);

        // Same JWT-sub spanning two firms: distinct (firmId, owner, symbol)
        // keys must remain distinct rows through snapshot/restore.
        src.ApplyFill("FIRM01", bob, "PETR4", OrderSide.Buy, 50, 30m);
        src.ApplyFill("FIRM02", bob, "PETR4", OrderSide.Buy, 70, 31m);

        var snap = src.Snapshot().ToList();
        Assert.Equal(5, snap.Count);
        Assert.All(snap, s => Assert.False(string.IsNullOrEmpty(s.FirmId)));

        var dst = new PositionKeeper();
        dst.Restore(snap);

        Assert.Single(dst.ForEndClientAndFirm("FIRM01", alice));
        Assert.Equal(2, dst.ForEndClientAndFirm("FIRM02", bob).Count);
        Assert.Single(dst.ForEndClientAndFirm("FIRM03", charlie));
        Assert.Empty(dst.ForEndClientAndFirm("FIRM01", charlie));
        Assert.Empty(dst.ForEndClientAndFirm("FIRM03", alice));
        Assert.Empty(dst.ForEndClientAndFirm("FIRM03", bob));

        var bobF1 = dst.ForEndClientAndFirm("FIRM01", bob).Single();
        Assert.Equal("PETR4", bobF1.Symbol);
        Assert.Equal(50, bobF1.NetQuantity);
        var bobF2 = dst.ForEndClientAndFirm("FIRM02", bob).Single(p => p.Symbol == "PETR4");
        Assert.Equal(70, bobF2.NetQuantity);
        Assert.Equal(31m, bobF2.AverageEntryPrice);
    }

    /// <summary>
    /// Integration: real WAL + snapshot + recovery across FIRM01/02/03.
    /// Asserts that after a snapshot + tail-append + cold-boot from
    /// snapshot+WAL, every firm's WorkingOrderBook, PositionKeeper,
    /// SubAccountPositionKeeper, PnlKeeper avg-cost, and
    /// SubAccountPnlKeeper basis rows survive intact, with no
    /// cross-firm bleed-through.
    /// </summary>
    [Fact]
    public async Task SnapshotAndRestart_PreservesPerFirmKeepersAcrossWalTail_NoCrossFirmBleed()
    {
        var alice = new EndClientId("alice");
        var bob = new EndClientId("bob");
        var charlie = new EndClientId("charlie");

        // Phase 1: live session — submit + fill orders for each firm,
        // capture a snapshot, then append a tail of more fills.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var h = BuildPlatform(store);

            // ------- Pre-snapshot events (will land in snapshot) -------
            // FIRM01 / alice / PETR4 / Buy 100 @ 30, then full fill.
            SubmitAndFill(h, clOrdId: 1UL, firm: "FIRM01", owner: "alice", symbol: "PETR4",
                side: OrderSide.Buy, qty: 100, px: 30m, lastQty: 100, subAccount: null);

            // FIRM02 / bob / VALE3 / Buy 200 @ 60, full fill.
            SubmitAndFill(h, clOrdId: 2UL, firm: "FIRM02", owner: "bob", symbol: "VALE3",
                side: OrderSide.Buy, qty: 200, px: 60m, lastQty: 200, subAccount: null);

            // FIRM03 / charlie / ITUB4 / Buy 300 @ 25, full fill, sub-account "DESK_A".
            SubmitAndFill(h, clOrdId: 3UL, firm: "FIRM03", owner: "charlie", symbol: "ITUB4",
                side: OrderSide.Buy, qty: 300, px: 25m, lastQty: 300, subAccount: "DESK_A");

            // Same JWT-sub (bob) under FIRM01: distinct firm key, must NOT
            // collide with the FIRM02 bob row in any keeper.
            SubmitAndFill(h, clOrdId: 4UL, firm: "FIRM01", owner: "bob", symbol: "PETR4",
                side: OrderSide.Buy, qty: 50, px: 30m, lastQty: 50, subAccount: null);

            // Capture the snapshot at this point.
            var snapStore = new SnapshotStore(_root, "test");
            PlatformSnapshot? snap = null;
            h.Dispatcher.WithSnapshotLock(seq => snap = h.Snapshotter.Capture(seq));
            Assert.NotNull(snap);
            snapStore.Write(snap!);

            // ------- Tail events (must come back via WAL replay) -------
            // FIRM02 / bob / PETR4 / Buy 70 @ 31 — partial then fill so the
            // tail exercises both ER applies post-snapshot.
            SubmitAndPartialThenFill(h, clOrdId: 5UL, firm: "FIRM02", owner: "bob", symbol: "PETR4",
                side: OrderSide.Buy, qty: 70, px: 31m, firstQty: 30, secondQty: 40, subAccount: null);

            // FIRM03 / charlie / ITUB4 / Sell 100 — closes part of the long.
            // Realises PnL against the existing avg-cost basis. Sub-account
            // tagged so SubAccountPnlKeeper sees the bucket close.
            SubmitAndFill(h, clOrdId: 6UL, firm: "FIRM03", owner: "charlie", symbol: "ITUB4",
                side: OrderSide.Sell, qty: 100, px: 28m, lastQty: 100, subAccount: "DESK_A");

            await store.FlushAsync();
        }

        // Phase 2: cold boot — fresh state, recovery loads snapshot +
        // replays WAL tail. Every per-firm assertion below verifies
        // either snapshot-side or tail-side state, plus cross-firm
        // isolation.
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var h = BuildPlatform(store);
            var replayer = new EventReplayer(
                h.Book, h.Ownership, h.KillSwitch, new SymbolHaltService(), new SessionPhaseService(),
                h.Processor, new AlgoBook(), new ClOrdIdPrefixRegistry(), new AlgoIdRegistry(),
                pnlKeeper: h.Pnl, subAccountPositions: h.SubPositions, subAccountPnl: h.SubPnl);
            var recovery = new PersistenceRecovery(store, h.Snapshotter, replayer,
                new SnapshotStore(_root, "test"), NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            // ---------------- WorkingOrderBook ----------------
            Assert.True(h.Book.TryGet(1UL, out var o1));
            Assert.Equal("FIRM01", o1!.FirmId);
            Assert.Equal(OrderStatus.Filled, o1.Status);

            Assert.True(h.Book.TryGet(2UL, out var o2));
            Assert.Equal("FIRM02", o2!.FirmId);
            Assert.Equal(OrderStatus.Filled, o2.Status);

            Assert.True(h.Book.TryGet(3UL, out var o3));
            Assert.Equal("FIRM03", o3!.FirmId);
            Assert.Equal(OrderStatus.Filled, o3.Status);

            Assert.True(h.Book.TryGet(4UL, out var o4));
            Assert.Equal("FIRM01", o4!.FirmId);
            Assert.Equal(OrderStatus.Filled, o4.Status);

            // Tail orders survive WAL replay past the snapshot.
            Assert.True(h.Book.TryGet(5UL, out var o5));
            Assert.Equal("FIRM02", o5!.FirmId);
            Assert.Equal(OrderStatus.Filled, o5.Status);
            Assert.Equal(70, o5.CumulativeQuantity);

            Assert.True(h.Book.TryGet(6UL, out var o6));
            Assert.Equal("FIRM03", o6!.FirmId);
            Assert.Equal(OrderStatus.Filled, o6.Status);

            // ---------------- PositionKeeper (master) ----------------
            // FIRM01 alice PETR4 100@30 (snapshot)
            var f1Alice = h.Positions.ForEndClientAndFirm("FIRM01", alice).Single();
            Assert.Equal("PETR4", f1Alice.Symbol);
            Assert.Equal(100, f1Alice.NetQuantity);
            // FIRM01 bob PETR4 50@30 (snapshot)
            var f1Bob = h.Positions.ForEndClientAndFirm("FIRM01", bob).Single();
            Assert.Equal("PETR4", f1Bob.Symbol);
            Assert.Equal(50, f1Bob.NetQuantity);
            // FIRM02 bob has TWO rows: VALE3 200 (snapshot) + PETR4 70 (tail)
            var f2BobRows = h.Positions.ForEndClientAndFirm("FIRM02", bob);
            Assert.Equal(2, f2BobRows.Count);
            Assert.Equal(200, f2BobRows.Single(p => p.Symbol == "VALE3").NetQuantity);
            Assert.Equal(70, f2BobRows.Single(p => p.Symbol == "PETR4").NetQuantity);
            // FIRM03 charlie ITUB4: opened 300 (snapshot) then sold 100 (tail) → 200 net
            var f3Charlie = h.Positions.ForEndClientAndFirm("FIRM03", charlie).Single();
            Assert.Equal("ITUB4", f3Charlie.Symbol);
            Assert.Equal(200, f3Charlie.NetQuantity);

            // Cross-firm isolation: no row leaks across firms.
            Assert.Empty(h.Positions.ForEndClientAndFirm("FIRM02", alice));
            Assert.Empty(h.Positions.ForEndClientAndFirm("FIRM03", alice));
            Assert.Empty(h.Positions.ForEndClientAndFirm("FIRM01", charlie));
            Assert.Empty(h.Positions.ForEndClientAndFirm("FIRM03", bob));

            // ---------------- SubAccountPositionKeeper ----------------
            // Only the charlie / DESK_A entries are sub-account-tagged.
            // Opened 300 (snapshot, sub-account fill into the keeper) +
            // closed 100 (tail) → 200 net in the DESK_A bucket of FIRM03.
            var charlieDesk = h.SubPositions
                .ForSubAccount("FIRM03", charlie, new SubAccountId("DESK_A"))
                .Single();
            Assert.Equal("ITUB4", charlieDesk.Symbol);
            Assert.Equal(200, charlieDesk.NetQuantity);
            // No cross-firm/cross-account bleed.
            Assert.Empty(h.SubPositions.ForSubAccount("FIRM01", charlie, new SubAccountId("DESK_A")));
            Assert.Empty(h.SubPositions.ForSubAccount("FIRM03", alice, new SubAccountId("DESK_A")));
            Assert.Empty(h.SubPositions.ForSubAccount("FIRM03", charlie, new SubAccountId("DESK_B")));

            // ---------------- PnlKeeper avg-cost (master) ----------------
            // Master basis rows survive snapshot + tail per (firm, owner, symbol).
            Assert.Equal(30m, h.Pnl.GetAvgCost("FIRM01", "alice", "PETR4")!.AvgPrice);
            Assert.Equal(100, h.Pnl.GetAvgCost("FIRM01", "alice", "PETR4")!.NetQuantity);
            Assert.Equal(60m, h.Pnl.GetAvgCost("FIRM02", "bob", "VALE3")!.AvgPrice);
            // FIRM02 bob PETR4 basis is the tail-side 31@70.
            Assert.Equal(31m, h.Pnl.GetAvgCost("FIRM02", "bob", "PETR4")!.AvgPrice);
            Assert.Equal(70, h.Pnl.GetAvgCost("FIRM02", "bob", "PETR4")!.NetQuantity);
            // FIRM03 charlie ITUB4 basis after open 300@25 + sell 100@28
            // remains 25 on the residual 200 (close doesn't move avg).
            Assert.Equal(25m, h.Pnl.GetAvgCost("FIRM03", "charlie", "ITUB4")!.AvgPrice);
            Assert.Equal(200, h.Pnl.GetAvgCost("FIRM03", "charlie", "ITUB4")!.NetQuantity);

            // Cross-firm avg-cost isolation: no row leaks.
            Assert.Null(h.Pnl.GetAvgCost("FIRM02", "alice", "PETR4"));
            Assert.Null(h.Pnl.GetAvgCost("FIRM01", "charlie", "ITUB4"));
            Assert.Null(h.Pnl.GetAvgCost("FIRM03", "bob", "VALE3"));

            // ---------------- SubAccountPnlKeeper bucket basis ----------
            // DESK_A bucket basis matches the master after the partial close.
            var deskBasis = h.SubPnl.GetBucketAvgCost("FIRM03", "charlie",
                new SubAccountId("DESK_A"), "ITUB4");
            Assert.NotNull(deskBasis);
            Assert.Equal(25m, deskBasis!.AvgPrice);
            Assert.Equal(200, deskBasis.NetQuantity);
            // No bleed into another firm with the same sub-account name.
            Assert.Null(h.SubPnl.GetBucketAvgCost("FIRM01", "charlie",
                new SubAccountId("DESK_A"), "ITUB4"));
            Assert.Null(h.SubPnl.GetBucketAvgCost("FIRM02", "charlie",
                new SubAccountId("DESK_A"), "ITUB4"));
        }
    }

    // ----- Test harness -----

    private sealed class Platform
    {
        public required WorkingOrderBook Book { get; init; }
        public required OrderOwnershipMap Ownership { get; init; }
        public required PositionKeeper Positions { get; init; }
        public required SubAccountPositionKeeper SubPositions { get; init; }
        public required PnlKeeper Pnl { get; init; }
        public required SubAccountPnlKeeper SubPnl { get; init; }
        public required KillSwitchService KillSwitch { get; init; }
        public required EventDispatcher Dispatcher { get; init; }
        public required StateSnapshotter Snapshotter { get; init; }
        public required ExecutionReportProcessor Processor { get; init; }
        public required TestSink Sink { get; init; }
    }

    private sealed class TestSink : IExecutionEventSink
    {
        public List<ExecutionEvent> Events { get; } = new();
        public void Publish(ExecutionEvent evt) => Events.Add(evt);
    }

    private static Platform BuildPlatform(IEventStore store)
    {
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var positions = new PositionKeeper();
        var subPositions = new SubAccountPositionKeeper();
        var pnl = new PnlKeeper();
        var subPnl = new SubAccountPnlKeeper();
        var killSwitch = new KillSwitchService();
        var sink = new TestSink();
        var dispatcher = new EventDispatcher(store);
        var processor = new ExecutionReportProcessor(
            ownership, book, positions, sink,
            new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance,
            dispatcher: dispatcher,
            pnlKeeper: pnl,
            subAccountPositions: subPositions,
            subAccountPnl: subPnl);
        var snapshotter = new StateSnapshotter(
            book, positions, killSwitch, new SymbolHaltService(), new SessionPhaseService(),
            new ClOrdIdPrefixRegistry(), ownership, new AlgoBook(), new AlgoIdRegistry(), new CashLedger(),
            pnlKeeper: pnl,
            subAccountPositions: subPositions,
            subAccountPnl: subPnl);
        return new Platform
        {
            Book = book,
            Ownership = ownership,
            Positions = positions,
            SubPositions = subPositions,
            Pnl = pnl,
            SubPnl = subPnl,
            KillSwitch = killSwitch,
            Dispatcher = dispatcher,
            Snapshotter = snapshotter,
            Processor = processor,
            Sink = sink,
        };
    }

    private static void DispatchSubmit(
        Platform h, ulong clOrdId, string firm, string owner, string symbol,
        OrderSide side, long qty, decimal px, string? subAccount)
    {
        var ownerId = new EndClientId(owner);
        var sub = SubAccountId.FromNullableString(subAccount);
        h.Dispatcher.Dispatch(
            new OrderSubmittedEvent
            {
                ClOrdId = clOrdId,
                EndClientId = owner,
                FirmId = firm,
                Symbol = symbol,
                SecurityId = 4321UL,
                Side = side.ToString(),
                Type = "Limit",
                Quantity = qty,
                Price = px,
                SubAccountId = subAccount,
            },
            () =>
            {
                h.Book.TryAdd(new Order(clOrdId, ownerId, symbol, 4321UL, side, OrderType.Limit,
                    qty, px, firmId: firm, subAccountId: sub));
                h.Ownership.Register(clOrdId, ownerId);
            });
    }

    private static void DispatchEr(
        Platform h, ulong clOrdId, ExecKind kind, long leaves, long cum, long lastQty, decimal lastPx, string firm)
    {
        h.Dispatcher.Dispatch(
            new ExecutionReportReceivedEvent
            {
                ClOrdId = clOrdId,
                ExecKind = kind.ToString(),
                LeavesQuantity = leaves,
                CumulativeQuantity = cum,
                LastQuantity = lastQty,
                LastPrice = lastPx,
                Synthetic = false,
                FirmId = firm,
            },
            fanOut => h.Processor.Apply(clOrdId, kind, leaves, cum, lastQty, lastPx,
                rejectReason: null, origClOrdId: 0, fanOut: fanOut, envelopeFirmId: firm));
    }

    private static void SubmitAndFill(
        Platform h, ulong clOrdId, string firm, string owner, string symbol,
        OrderSide side, long qty, decimal px, long lastQty, string? subAccount)
    {
        DispatchSubmit(h, clOrdId, firm, owner, symbol, side, qty, px, subAccount);
        DispatchEr(h, clOrdId, ExecKind.New, leaves: qty, cum: 0, lastQty: 0, lastPx: 0m, firm);
        DispatchEr(h, clOrdId, ExecKind.Fill, leaves: qty - lastQty, cum: lastQty,
            lastQty: lastQty, lastPx: px, firm);
    }

    private static void SubmitAndPartialThenFill(
        Platform h, ulong clOrdId, string firm, string owner, string symbol,
        OrderSide side, long qty, decimal px, long firstQty, long secondQty, string? subAccount)
    {
        DispatchSubmit(h, clOrdId, firm, owner, symbol, side, qty, px, subAccount);
        DispatchEr(h, clOrdId, ExecKind.New, leaves: qty, cum: 0, lastQty: 0, lastPx: 0m, firm);
        DispatchEr(h, clOrdId, ExecKind.PartialFill, leaves: qty - firstQty, cum: firstQty,
            lastQty: firstQty, lastPx: px, firm);
        DispatchEr(h, clOrdId, ExecKind.Fill, leaves: qty - firstQty - secondQty,
            cum: firstQty + secondQty, lastQty: secondQty, lastPx: px, firm);
    }
}
