using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// #671/#753 (RFC: admin account reset, PR 3). End-to-end
/// snapshot + WAL replay coverage for <see cref="AccountResetEvent"/>.
/// Proves the RFC's "cold-start acceptance" bar: events before reset,
/// the reset itself, later fills/fees, and snapshots taken at
/// arbitrary boundaries all converge on the identical final state
/// live vs. replayed — and that replay NEVER re-resolves
/// <see cref="CashSeedOptions"/> / <see cref="PositionSeedOptions"/>
/// (the resolved payload persisted on the event is authoritative).
/// Shaped after <c>PositionAdjustmentRecoveryTests</c> /
/// <c>CashWithdrawalAtomicityTests</c> (the sibling admin-driven
/// projection replay suites).
/// </summary>
public class AccountResetRecoveryTests : IDisposable
{
    private readonly string _root;

    public AccountResetRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "b3tp-acctreset-" + Guid.NewGuid().ToString("N"));
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
    /// Full lifecycle: fills establish pre-reset state, an
    /// AccountResetEvent zeros/flattens it, then a later fill lands
    /// post-reset. Cold WAL-only replay must reproduce the exact same
    /// final (cash, position, avg-cost, master-bucket-basis) state the
    /// live path produced — never re-accumulating pre-reset state and
    /// never dropping the post-reset fill.
    /// </summary>
    [Fact]
    public async Task Replay_FromWalAlone_EventsBeforeResetThenReset_ThenLaterFill_ConvergesToLiveState()
    {
        var alice = new EndClientId("alice");
        decimal liveCash;
        long livePosition;
        decimal liveAvgCost;

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var cashKeeper = new CashKeeper();
            var cashLedger = new CashLedger();
            var dispatcher = new EventDispatcher(store);

            // Events BEFORE reset: cash deposit + a fill establishing a
            // non-flat PETR4 position.
            DispatchDeposit(dispatcher, cashKeeper, cashLedger, "FIRM01", alice, 5_000m);
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "FIRM01", "alice", "PETR4", 500, 28m);

            // The reset itself: absolute cash + flatten PETR4.
            DispatchReset(
                dispatcher, positions, pnl, subPnl, cashKeeper, cashLedger,
                "FIRM01", "alice", cashAvailable: 10_000m,
                positionEntries: new[] { new AccountResetPositionEntry("PETR4", 0, 0m) });

            // Later fill AFTER reset — must land on top of the
            // post-reset baseline, not the pre-reset one.
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "FIRM01", "alice", "PETR4", 100, 30m);
            DispatchDeposit(dispatcher, cashKeeper, cashLedger, "FIRM01", alice, 500m);

            liveCash = cashLedger.GetAvailable("FIRM01", alice);
            livePosition = Assert.Single(positions.ForEndClientAndFirm("FIRM01", alice)).NetQuantity;
            liveAvgCost = pnl.GetAvgCost("FIRM01", "alice", "PETR4")!.AvgPrice;

            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var cashKeeper = new CashKeeper();
            var cashLedger = new CashLedger();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(positions, pnl, subPnl, cashKeeper, cashLedger);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            Assert.Equal(liveCash, cashLedger.GetAvailable("FIRM01", alice));
            Assert.Equal(10_500m, cashLedger.GetAvailable("FIRM01", alice));

            var replayedPosition = Assert.Single(positions.ForEndClientAndFirm("FIRM01", alice));
            Assert.Equal(livePosition, replayedPosition.NetQuantity);
            Assert.Equal(100, replayedPosition.NetQuantity);
            Assert.Equal(30m, replayedPosition.AverageEntryPrice);

            var basis = pnl.GetAvgCost("FIRM01", "alice", "PETR4");
            Assert.NotNull(basis);
            Assert.Equal(liveAvgCost, basis!.AvgPrice);

            var masterBasis = subPnl.GetBucketAvgCost("FIRM01", "alice", subAccount: null, "PETR4");
            Assert.NotNull(masterBasis);
            Assert.Equal(100, masterBasis!.NetQuantity);
            Assert.Equal(30m, masterBasis.AvgPrice);
        }
    }

    /// <summary>
    /// Snapshot taken exactly between the reset and a later fill: the
    /// snapshot must already reflect the reset (not the pre-reset
    /// state) and the tail replay of the later fill must land on top
    /// of it.
    /// </summary>
    [Fact]
    public async Task SnapshotPlusTail_BoundaryImmediatelyAfterReset_ConvergesToLiveState()
    {
        long snapSeq;
        var alice = new EndClientId("alice");

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var cashKeeper = new CashKeeper();
            var cashLedger = new CashLedger();
            var dispatcher = new EventDispatcher(store);

            DispatchDeposit(dispatcher, cashKeeper, cashLedger, "FIRM01", alice, 1_000m);
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "FIRM01", "alice", "PETR4", 200, 25m);
            DispatchReset(
                dispatcher, positions, pnl, subPnl, cashKeeper, cashLedger,
                "FIRM01", "alice", cashAvailable: 50_000m,
                positionEntries: new[]
                {
                    new AccountResetPositionEntry("PETR4", 0, 0m),
                    new AccountResetPositionEntry("VALE3", 300, 60m),
                });

            var (snapshotter, _) = BuildSnapshotterAndReplayer(positions, pnl, subPnl, cashKeeper, cashLedger);
            PlatformSnapshot? snap = null;
            dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
            new SnapshotStore(_root, "test").Write(snap!);
            snapSeq = snap!.Seq;

            // The snapshot itself must already reflect the reset, not
            // the pre-reset PETR4 position or the pre-reset cash.
            Assert.Equal(50_000m, snap.CashBalances.Single(c => c.EndClientId == "alice").Available);
            Assert.DoesNotContain(snap.Positions, p => p.EndClientId == "alice" && p.Symbol == "PETR4" && p.NetQuantity != 0);
            Assert.Contains(snap.Positions, p => p.EndClientId == "alice" && p.Symbol == "VALE3" && p.NetQuantity == 300);

            // Tail: one more fill, past the snapshot boundary.
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "FIRM01", "alice", "VALE3", 350, 61m);
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var cashKeeper = new CashKeeper();
            var cashLedger = new CashLedger();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(positions, pnl, subPnl, cashKeeper, cashLedger);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            Assert.Equal(50_000m, cashLedger.GetAvailable("FIRM01", alice));
            var all = positions.ForEndClientAndFirm("FIRM01", alice);
            // The flattened PETR4 row is legitimately absent here: flat
            // positions are never persisted in a snapshot (PositionKeeper.
            // RawSnapshot's pre-existing "skip flat" convention) and the
            // last WAL event touching PETR4 (the reset) is BEFORE the
            // snapshot boundary, so it isn't in the tail either. Either
            // way, "no row" and "a flat row" are behaviourally identical
            // — PositionKeeper re-materialises a flat position on demand.
            Assert.DoesNotContain(all, p => p.Symbol == "PETR4" && p.NetQuantity != 0);
            var vale = Assert.Single(all, p => p.Symbol == "VALE3");
            Assert.Equal(350, vale.NetQuantity);
            Assert.Equal(61m, vale.AverageEntryPrice);
            Assert.True(snapSeq > 0);
        }
    }

    /// <summary>
    /// Snapshot taken BEFORE the reset (reset + later fill live only
    /// in the tail): recovery must apply the reset from the WAL tail,
    /// not miss it because the snapshot pre-dates it.
    /// </summary>
    [Fact]
    public async Task SnapshotPlusTail_BoundaryBeforeReset_TailAppliesResetAndLaterFill()
    {
        var alice = new EndClientId("alice");

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var cashKeeper = new CashKeeper();
            var cashLedger = new CashLedger();
            var dispatcher = new EventDispatcher(store);

            DispatchDeposit(dispatcher, cashKeeper, cashLedger, "FIRM01", alice, 1_000m);
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "FIRM01", "alice", "PETR4", 200, 25m);

            var (snapshotter, _) = BuildSnapshotterAndReplayer(positions, pnl, subPnl, cashKeeper, cashLedger);
            PlatformSnapshot? snap = null;
            dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
            new SnapshotStore(_root, "test").Write(snap!);

            // Reset AND a later fill both live only in the tail.
            DispatchReset(
                dispatcher, positions, pnl, subPnl, cashKeeper, cashLedger,
                "FIRM01", "alice", cashAvailable: 0m,
                positionEntries: new[] { new AccountResetPositionEntry("PETR4", 0, 0m) });
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "FIRM01", "alice", "ITUB4", 40, 12m);
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var cashKeeper = new CashKeeper();
            var cashLedger = new CashLedger();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(positions, pnl, subPnl, cashKeeper, cashLedger);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            Assert.Equal(0m, cashLedger.GetAvailable("FIRM01", alice));
            var all = positions.ForEndClientAndFirm("FIRM01", alice);
            var itub = Assert.Single(all, p => p.Symbol == "ITUB4");
            Assert.Equal(40, itub.NetQuantity);
            var petr4 = Assert.Single(all, p => p.Symbol == "PETR4");
            Assert.Equal(0, petr4.NetQuantity);
        }
    }

    /// <summary>
    /// A named sub-account bucket seeded before the reset must be gone
    /// after cold replay — the reset payload carries no bucket data
    /// (buckets are only ever cleared, never re-seeded), and this must
    /// hold true across replay exactly as it does live.
    /// </summary>
    [Fact]
    public async Task Replay_NamedSubAccountBucket_IsClearedAndNotFabricated()
    {
        var alice = new EndClientId("alice");

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var cashKeeper = new CashKeeper();
            var cashLedger = new CashLedger();
            var dispatcher = new EventDispatcher(store);

            // Seed a named sub-account bucket directly (mirrors
            // ExecutionReportProcessor's fill fan-out).
            subPnl.ApplyBucketFill("FIRM01", "alice", new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "FIRM01", "alice", "PETR4", 100, 28m);

            DispatchReset(
                dispatcher, positions, pnl, subPnl, cashKeeper, cashLedger,
                "FIRM01", "alice", cashAvailable: 0m,
                positionEntries: new[] { new AccountResetPositionEntry("PETR4", 0, 0m) });
            await store.FlushAsync();
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var cashKeeper = new CashKeeper();
            var cashLedger = new CashLedger();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(positions, pnl, subPnl, cashKeeper, cashLedger);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            Assert.Null(subPnl.GetBucketAvgCost("FIRM01", "alice", new SubAccountId("SUB1"), "PETR4"));
            Assert.Null(subPnl.GetBucketAvgCost("FIRM01", "alice", subAccount: null, "PETR4"));
        }
    }

    /// <summary>
    /// #671/#753 code-review addendum #2. Whole-account reset must
    /// clear <see cref="SubAccountPositionKeeper"/> ROWS (not just the
    /// PnL buckets covered by <see cref="Replay_NamedSubAccountBucket_IsClearedAndNotFabricated"/>)
    /// both live and, here, during cold-start <see cref="AccountResetEvent"/>
    /// replay. A named sub-account position row referencing a
    /// pre-reset (NetQuantity, AverageEntryPrice) that survives reset
    /// would be risk-visible stale state — exactly the class of bug
    /// the addendum called out.
    /// </summary>
    [Fact]
    public async Task Replay_NamedSubAccountPositionRow_IsClearedAndNotFabricated()
    {
        var alice = new EndClientId("alice");

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var subPositions = new SubAccountPositionKeeper();
            var cashKeeper = new CashKeeper();
            var cashLedger = new CashLedger();
            var dispatcher = new EventDispatcher(store);

            // Seed a named sub-account position row directly (mirrors
            // ExecutionReportProcessor's fill fan-out).
            subPositions.ApplyFill("FIRM01", alice, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);
            DispatchAdjustment(dispatcher, positions, pnl, subPnl, "FIRM01", "alice", "PETR4", 100, 28m);

            DispatchReset(
                dispatcher, positions, pnl, subPnl, cashKeeper, cashLedger,
                "FIRM01", "alice", cashAvailable: 0m,
                positionEntries: new[] { new AccountResetPositionEntry("PETR4", 0, 0m) },
                subPositions: subPositions);
            await store.FlushAsync();

            // Live path: the row must already be gone before any replay.
            Assert.All(
                subPositions.ForSubAccount("FIRM01", alice, new SubAccountId("SUB1")),
                p => Assert.Equal(0, p.NetQuantity));
        }

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var subPositions = new SubAccountPositionKeeper();
            var cashKeeper = new CashKeeper();
            var cashLedger = new CashLedger();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(
                positions, pnl, subPnl, cashKeeper, cashLedger, subPositions: subPositions);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            // Cold-start replay: the row must not be fabricated back;
            // reset must have cleared it deterministically.
            Assert.Empty(subPositions.ForSubAccount("FIRM01", alice, new SubAccountId("SUB1")));
            Assert.Empty(subPositions.EnumerateForOwner("FIRM01", alice));
        }
    }

    /// <summary>
    /// Replay must call <c>IMarginProvider.ReleaseAllReservationsForAccount</c>
    /// for the reset event, exactly as the live path does — closes any
    /// stale reservation left over from a pre-crash margin hold that
    /// (for whatever reason) was never released before the crash.
    /// </summary>
    [Fact]
    public async Task Replay_CallsReleaseAllReservationsForAccount()
    {
        var alice = new EndClientId("alice");

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var cashKeeper = new CashKeeper();
            var cashLedger = new CashLedger();
            var dispatcher = new EventDispatcher(store);

            DispatchReset(
                dispatcher, positions, pnl, subPnl, cashKeeper, cashLedger,
                "FIRM01", "alice", cashAvailable: 0m,
                positionEntries: Array.Empty<AccountResetPositionEntry>());
            await store.FlushAsync();
        }

        var spy = new SpyMarginProvider();
        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var cashKeeper = new CashKeeper();
            var cashLedger = new CashLedger();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(
                positions, pnl, subPnl, cashKeeper, cashLedger, marginProvider: spy);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();
        }

        Assert.Contains(("FIRM01", alice), spy.ReleasedAccounts);
    }

    /// <summary>
    /// Determinism-under-changed-configuration: <see cref="EventReplayer.Apply(WalEvent)"/>'s
    /// <see cref="AccountResetEvent"/> case has NO dependency on
    /// <see cref="CashSeedOptions"/> / <see cref="PositionSeedOptions"/>
    /// at all — it only reads the payload persisted on the event
    /// itself. This test drives that structurally: the cold-start
    /// components used for replay are built with zero knowledge of any
    /// seed configuration (no <see cref="AccountResetPayloadResolver"/>
    /// call anywhere in the recovery path), and replay still reproduces
    /// a payload resolved from a DIFFERENT (now-superseded) seed
    /// configuration at live-request time — proving a later operator
    /// edit to seed config cannot change historical replay outcomes.
    /// </summary>
    [Fact]
    public async Task Replay_IsDeterministic_EvenWhenSeedConfigChangesAfterLiveReset()
    {
        var alice = new EndClientId("alice");

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var cashKeeper = new CashKeeper();
            var cashLedger = new CashLedger();
            var dispatcher = new EventDispatcher(store);

            // Live reset resolved against "old" config: seed = 10,000.
            var oldSeeds = new CashSeedOptions
            {
                Seeds = { new CashSeed { FirmId = "FIRM01", EndClientId = "alice", InitialAvailable = 10_000m } },
            };
            var payload = AccountResetPayloadResolver.Resolve(
                "FIRM01", alice, Array.Empty<Domain.Position>(), oldSeeds, new PositionSeedOptions());

            DispatchReset(
                dispatcher, positions, pnl, subPnl, cashKeeper, cashLedger,
                "FIRM01", "alice", payload.CashAvailable, payload.Positions);
            await store.FlushAsync();
        }

        // "Operator edits config" between live reset and replay: this
        // new value is never consulted by EventReplayer, only by a
        // FUTURE live reset request.
        var newSeeds = new CashSeedOptions
        {
            Seeds = { new CashSeed { FirmId = "FIRM01", EndClientId = "alice", InitialAvailable = 999_999m } },
        };
        _ = newSeeds; // never passed to the replay path — that's the point.

        await using (var store = new FileEventStore(Opts(), NullLogger<FileEventStore>.Instance))
        {
            var positions = new PositionKeeper();
            var pnl = new PnlKeeper();
            var subPnl = new SubAccountPnlKeeper();
            var cashKeeper = new CashKeeper();
            var cashLedger = new CashLedger();
            var (snapshotter, replayer) = BuildSnapshotterAndReplayer(positions, pnl, subPnl, cashKeeper, cashLedger);
            var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                new SnapshotStore(_root, "test"),
                NullLogger<PersistenceRecovery>.Instance);
            await recovery.RunAsync();

            // The replayed balance is the ORIGINAL resolved value
            // (10,000), never the "changed" 999,999 — because replay
            // has no code path that ever reads CashSeedOptions.
            Assert.Equal(10_000m, cashLedger.GetAvailable("FIRM01", alice));
        }
    }

    private static void DispatchDeposit(
        EventDispatcher d, CashKeeper cashKeeper, CashLedger cashLedger,
        string firmId, EndClientId owner, decimal amount)
    {
        var evt = new CashLedgerEvent
        {
            FirmId = firmId,
            EndClientId = owner.Value,
            Operation = "Deposit",
            Amount = amount,
            Currency = "BRL",
            Reference = "test",
            OperatorId = "test-operator",
        };
        d.Dispatch(evt, () =>
        {
            cashKeeper.Apply(firmId, "Deposit", owner, amount);
            cashLedger.ApplyDeposit(firmId, owner, amount);
        });
    }

    private static void DispatchAdjustment(
        EventDispatcher d,
        PositionKeeper positions,
        PnlKeeper pnl,
        SubAccountPnlKeeper subPnl,
        string firmId,
        string ec,
        string symbol,
        long netQuantity,
        decimal averageEntryPrice)
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

    private static void DispatchReset(
        EventDispatcher d,
        PositionKeeper positions,
        PnlKeeper pnl,
        SubAccountPnlKeeper subPnl,
        CashKeeper cashKeeper,
        CashLedger cashLedger,
        string firmId,
        string ec,
        decimal cashAvailable,
        IReadOnlyList<AccountResetPositionEntry> positionEntries,
        SubAccountPositionKeeper? subPositions = null)
    {
        var owner = new EndClientId(ec);
        var evt = new AccountResetEvent
        {
            EndClientId = ec,
            FirmId = firmId,
            CashAvailable = cashAvailable,
            Positions = positionEntries,
            OperatorId = "test-operator",
        };
        d.Dispatch(evt, () =>
        {
            subPnl.ClearAllBucketsForAccount(firmId, ec);
            subPositions?.ClearAllForAccount(firmId, owner);
            foreach (var entry in positionEntries)
            {
                positions.SetAbsolute(firmId, owner, entry.Symbol, entry.NetQuantity, entry.AverageEntryPrice);
                pnl.SetAbsoluteAvgCost(firmId, ec, entry.Symbol, entry.NetQuantity, entry.AverageEntryPrice);
                subPnl.SetAbsoluteMasterBucketAvgCost(firmId, ec, entry.Symbol, entry.NetQuantity, entry.AverageEntryPrice);
            }
            cashKeeper.SetAbsolute(firmId, owner, cashAvailable);
            cashLedger.SetAbsolute(firmId, owner, cashAvailable);
        });
    }

    private (StateSnapshotter, EventReplayer) BuildSnapshotterAndReplayer(
        PositionKeeper positions,
        PnlKeeper pnl,
        SubAccountPnlKeeper subPnl,
        CashKeeper cashKeeper,
        CashLedger cashLedger,
        IMarginProvider? marginProvider = null,
        SubAccountPositionKeeper? subPositions = null)
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
            cashLedger,
            cashKeeper: cashKeeper,
            pnlKeeper: pnl,
            subAccountPositions: subPositions,
            subAccountPnl: subPnl);
        var replayer = new EventReplayer(book, ownership, killSwitch,
            new SymbolHaltService(), new SessionPhaseService(),
            processor, algos, clOrdIds, new AlgoIdRegistry(),
            cashKeeper: cashKeeper,
            pnlKeeper: pnl,
            positions: positions,
            subAccountPositions: subPositions,
            subAccountPnl: subPnl,
            cash: cashLedger,
            marginProvider: marginProvider);
        return (snapshotter, replayer);
    }

    private sealed class NullSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent evt) { }
    }

    private sealed class SpyMarginProvider : IMarginProvider
    {
        public List<(string FirmId, EndClientId Owner)> ReleasedAccounts { get; } = new();

        public Task<RiskDecision> TryReserveAsync(ulong clOrdId, RiskContext ctx, CancellationToken ct) =>
            Task.FromResult(RiskDecision.Approve);

        public void ReleaseAllReservationsForAccount(string firmId, EndClientId owner) =>
            ReleasedAccounts.Add((firmId, owner));
    }
}
