using System.Runtime.CompilerServices;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// #671/#753 (RFC: admin account reset, PR 3). Pins the atomicity +
/// TOCTOU-safety contract for <c>AdminEndpoints.HandleAccountReset</c>'s
/// dispatcher usage: the guard re-check, every keeper mutation, and the
/// WAL append all execute under the SAME <see cref="EventDispatcher"/>
/// critical region. Shaped directly after <c>CashWithdrawalAtomicityTests</c>
/// (the sibling admin-cash atomicity regression), reusing its
/// <c>WedgeableStore</c> pattern to force an <c>Append</c> failure mid-
/// dispatch and assert the rollback delegate restores EXACT pre-reset
/// state (cash, position, avg-cost basis) — never a partially-applied
/// reset silently left in memory with no matching WAL record.
/// </summary>
public class AccountResetAtomicityTests
{
    /// <summary>
    /// Forces the WAL <c>Append</c> for a reset to throw
    /// (<see cref="WalBackpressureException"/>-equivalent failure path)
    /// AFTER <c>preApply</c> has already mutated every in-memory keeper.
    /// The rollback delegate must restore cash/position/avg-cost to the
    /// EXACT pre-reset values — this is the "absolute overwrite, not a
    /// delta" rollback contract documented on
    /// <c>AdminEndpoints.HandleAccountReset</c>.
    /// </summary>
    [Fact]
    public void Reset_AppendFails_RollbackRestoresExactPriorState()
    {
        var store = new ThrowingStore();
        var dispatcher = new EventDispatcher(store);
        var positions = new PositionKeeper();
        var pnl = new PnlKeeper();
        var subPnl = new SubAccountPnlKeeper();
        var cashKeeper = new CashKeeper();
        var cashLedger = new CashLedger();
        var alice = new EndClientId("alice");

        // Pre-reset state.
        cashKeeper.Apply("FIRM01", "Deposit", alice, 1_000m);
        cashLedger.ApplyDeposit("FIRM01", alice, 1_000m);
        positions.SetAbsolute("FIRM01", alice, "PETR4", 200, 25m);
        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 200, 25m);
        subPnl.SetAbsoluteMasterBucketAvgCost("FIRM01", "alice", "PETR4", 200, 25m);

        var beforeCashKeeper = cashKeeper.GetAvailable("FIRM01", alice);
        var beforeCashLedger = cashLedger.GetAvailable("FIRM01", alice);
        var beforePosition = positions.ForEndClientAndFirm("FIRM01", alice).Single(p => p.Symbol == "PETR4");
        var beforeAvgCost = pnl.GetAvgCost("FIRM01", "alice", "PETR4")!;
        var beforeBuckets = subPnl.SnapshotBucketsForAccount("FIRM01", "alice");

        // Let the FIRST Append (the seed deposit, if any) succeed; only
        // wedge the reset's own Append.
        store.ThrowOnNextAppend = true;

        // DispatchWithPreApply's contract (mirrored by
        // AdminEndpoints.HandleAccountReset's own try/catch): on an
        // Append failure it invokes rollback() and then RE-THROWS —
        // it does not swallow the exception into Applied=false.
        var ex = Assert.Throws<WalBackpressureException>(() =>
            dispatcher.DispatchWithPreApply(
                new AccountResetEvent
                {
                    EndClientId = "alice",
                    FirmId = "FIRM01",
                    CashAvailable = 10_000m,
                    Positions = new[] { new AccountResetPositionEntry("PETR4", 0, 0m) },
                    OperatorId = "op",
                },
                preApply: () =>
                {
                    subPnl.ClearAllBucketsForAccount("FIRM01", "alice");
                    positions.SetAbsolute("FIRM01", alice, "PETR4", 0, 0m);
                    pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 0, 0m);
                    subPnl.SetAbsoluteMasterBucketAvgCost("FIRM01", "alice", "PETR4", 0, 0m);
                    cashKeeper.SetAbsolute("FIRM01", alice, 10_000m);
                    cashLedger.SetAbsolute("FIRM01", alice, 10_000m);
                    return true;
                },
                rollback: () =>
                {
                    positions.SetAbsolute("FIRM01", alice, "PETR4", beforePosition.NetQuantity, beforePosition.AverageEntryPrice);
                    pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", beforeAvgCost.NetQuantity, beforeAvgCost.AvgPrice);
                    subPnl.RestoreBucketsForAccount("FIRM01", "alice", beforeBuckets);
                    cashKeeper.SetAbsolute("FIRM01", alice, beforeCashKeeper);
                    cashLedger.SetAbsolute("FIRM01", alice, beforeCashLedger);
                }));

        Assert.Equal("forced test failure", ex.Message);

        // Every keeper must be back to EXACTLY the pre-reset values —
        // not (0, 0m), not some in-between partial-apply state.
        Assert.Equal(beforeCashKeeper, cashKeeper.GetAvailable("FIRM01", alice));
        Assert.Equal(beforeCashLedger, cashLedger.GetAvailable("FIRM01", alice));
        var afterPosition = positions.ForEndClientAndFirm("FIRM01", alice).Single(p => p.Symbol == "PETR4");
        Assert.Equal(beforePosition.NetQuantity, afterPosition.NetQuantity);
        Assert.Equal(beforePosition.AverageEntryPrice, afterPosition.AverageEntryPrice);
        var afterAvgCost = pnl.GetAvgCost("FIRM01", "alice", "PETR4");
        Assert.NotNull(afterAvgCost);
        Assert.Equal(beforeAvgCost.NetQuantity, afterAvgCost!.NetQuantity);
        Assert.Equal(beforeAvgCost.AvgPrice, afterAvgCost.AvgPrice);
        var afterMasterBucket = subPnl.GetBucketAvgCost("FIRM01", "alice", subAccount: null, "PETR4");
        Assert.NotNull(afterMasterBucket);
        Assert.Equal(200, afterMasterBucket!.NetQuantity);
        Assert.Equal(25m, afterMasterBucket.AvgPrice);
    }

    /// <summary>
    /// Review finding #3 (final round): the GENERIC
    /// <c>DispatchWithPreApply&lt;TEvent&gt;</c> overload's
    /// <c>resolveAndPreApply</c> factory is side-effect-free by
    /// contract — all mutation is deferred to the returned
    /// <c>Apply</c> delegate, which only runs AFTER a successful
    /// Append. An Append failure (WAL backpressure) must therefore
    /// invoke NO rollback at all: nothing was mutated, so there is
    /// nothing to undo, and calling the old-style
    /// rollback-on-Append-failure would have actively fabricated state
    /// (e.g. a flat <c>PositionKeeper</c> row for a symbol the
    /// end-client never held). Proves byte-for-byte/logically
    /// unchanged projections: no flat position row for a previously
    /// absent seed symbol, no <c>CashLedger.BalanceChanged</c> side
    /// effect, no sub-account bucket/position change, no margin
    /// release, and the supplied <c>rollbackOnApplyFailure</c>
    /// delegate is never invoked.
    /// </summary>
    [Fact]
    public void Reset_Generic_AppendFails_NoRollbackInvoked_ProjectionsAndSideEffectsUnchanged()
    {
        var store = new ThrowingStore { ThrowOnNextAppend = true };
        var dispatcher = new EventDispatcher(store);
        var positions = new PositionKeeper();
        var subAccountPositions = new SubAccountPositionKeeper();
        var subPnl = new SubAccountPnlKeeper();
        var cashLedger = new CashLedger();
        var alice = new EndClientId("alice");

        // Pre-reset state: PETR4 is held; VALE3 has never been traded
        // (no row exists for it at all) but is included in the reset
        // payload as a configured seed symbol.
        positions.SetAbsolute("FIRM01", alice, "PETR4", 200, 25m);
        cashLedger.ApplyDeposit("FIRM01", alice, 1_000m);
        subAccountPositions.ApplyFill("FIRM01", alice, new SubAccountId("sub1"), "PETR4", OrderSide.Buy, 50, 20m);
        subPnl.RestoreBucketsForAccount("FIRM01", "alice",
            new List<SubAccountPnlBucketEntry> { new("sub1", "PETR4", 50, 20m) });

        var beforeCash = cashLedger.GetAvailable("FIRM01", alice);
        var balanceChangedFired = false;
        cashLedger.BalanceChanged += (_, _, _) => balanceChangedFired = true;
        var rollbackInvoked = false;
        var applyInvoked = false;
        var marginReleaseInvoked = false;

        var ex = Assert.Throws<WalBackpressureException>(() =>
            dispatcher.DispatchWithPreApply<AccountResetEvent>(
                resolveAndPreApply: () =>
                {
                    // Pure resolve: reset PETR4 to flat and "seed" the
                    // never-held VALE3 at a nonzero configured amount —
                    // exactly the scenario that must NOT leak into the
                    // live keeper when Append fails.
                    var evt = new AccountResetEvent
                    {
                        EndClientId = "alice",
                        FirmId = "FIRM01",
                        CashAvailable = 500m,
                        Positions = new List<AccountResetPositionEntry>
                        {
                            new("PETR4", 0, 0m),
                            new("VALE3", 10, 15m),
                        },
                        OperatorId = "op",
                    };
                    void Apply()
                    {
                        applyInvoked = true;
                        marginReleaseInvoked = true;
                        positions.SetAbsolute("FIRM01", alice, "PETR4", 0, 0m);
                        positions.SetAbsolute("FIRM01", alice, "VALE3", 10, 15m);
                        subAccountPositions.ClearAllForAccount("FIRM01", alice);
                        subPnl.ClearAllBucketsForAccount("FIRM01", "alice");
                        cashLedger.SetAbsolute("FIRM01", alice, 500m);
                    }
                    return (evt, (Action)Apply);
                },
                rollbackOnApplyFailure: () => rollbackInvoked = true));

        Assert.Equal("forced test failure", ex.Message);

        // Apply() must never have run — Append failed before it could.
        Assert.False(applyInvoked);
        Assert.False(marginReleaseInvoked);
        // No rollback invoked either: there was nothing to undo.
        Assert.False(rollbackInvoked);

        // PETR4 is untouched.
        var petr4 = positions.ForEndClientAndFirm("FIRM01", alice).Single(p => p.Symbol == "PETR4");
        Assert.Equal(200, petr4.NetQuantity);
        Assert.Equal(25m, petr4.AverageEntryPrice);
        // VALE3 was never held and must stay entirely absent — not a
        // spurious flat (or seeded) row.
        Assert.DoesNotContain(positions.ForEndClientAndFirm("FIRM01", alice), p => p.Symbol == "VALE3");

        // No cash side effect and no observable BalanceChanged event.
        Assert.Equal(beforeCash, cashLedger.GetAvailable("FIRM01", alice));
        Assert.False(balanceChangedFired);

        // No sub-account changes.
        var subPositions = subAccountPositions.SnapshotForAccount("FIRM01", alice);
        Assert.Contains(subPositions, e => e.SubAccount == "sub1" && e.Symbol == "PETR4" && e.NetQuantity == 50);
        Assert.NotNull(subPnl.GetBucketAvgCost("FIRM01", "alice", new SubAccountId("sub1"), "PETR4"));
    }

    /// <summary>
    /// Review finding #3 (final round) + code-review addendum #4: if
    /// <c>Apply()</c> itself throws AFTER a successful, durable Append
    /// (a theoretically-unreachable defense-in-depth path — every
    /// mutation the reset performs is an in-memory dictionary write
    /// with no I/O), the supplied <c>rollbackOnApplyFailure</c>
    /// delegate must restore EXACT prior state across all THREE
    /// possibilities a symbol can be in, for both
    /// <see cref="PositionKeeper"/> and <see cref="PnlKeeper"/>:
    /// (1) a symbol with a known avg-cost basis restores to those
    /// exact values; (2) a symbol with no row at all (never traded)
    /// must end up genuinely absent again (via
    /// <see cref="PositionKeeper.TryRemove"/> /
    /// <see cref="PnlKeeper.RestoreSymbolBasis"/>), not left as a flat
    /// <c>(0, 0m)</c> row — which would still be silently visible via
    /// the legacy <c>GET /api/positions</c> view; (3) a symbol with a
    /// legacy UNKNOWN-basis quantity
    /// (<see cref="PnlKeeper.GetUnknownBasisQty"/>) must restore that
    /// exact unknown quantity rather than being silently collapsed to
    /// absence or fabricated into a KNOWN basis —
    /// <c>SetAbsoluteAvgCost</c> cannot do this (it always clears the
    /// unknown-basis leg), which is exactly why
    /// <see cref="PnlKeeper.CaptureSymbolBasis"/> /
    /// <see cref="PnlKeeper.RestoreSymbolBasis"/> exist.
    /// </summary>
    [Fact]
    public void Reset_Generic_ApplyFails_RollbackRestoresPresenceAndAbsenceExactly()
    {
        var store = new RecordingStore();
        var dispatcher = new EventDispatcher(store);
        var positions = new PositionKeeper();
        var pnl = new PnlKeeper();
        var alice = new EndClientId("alice");

        // PETR4: known avg-cost basis. VALE3: has never been traded
        // (true absence in both keepers). ITUB4: a legacy position
        // whose basis was never established (pre-#271 snapshot
        // format) — tracked as an UNKNOWN-basis quantity in PnlKeeper,
        // while PositionKeeper still carries a normal (nonzero
        // average-price) row for it (the two keepers are not required
        // to agree on "basis known"; only PnlKeeper distinguishes it).
        positions.SetAbsolute("FIRM01", alice, "PETR4", 200, 25m);
        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 200, 25m);
        positions.SetAbsolute("FIRM01", alice, "ITUB4", 40, 30m);
        pnl.SeedAvgCostFromLegacyPositions(new[]
        {
            new PositionSnapshot("alice", "ITUB4", 40, 0m, "FIRM01"),
        });
        Assert.Equal(40, pnl.GetUnknownBasisQty("FIRM01", "alice", "ITUB4"));
        Assert.Null(pnl.GetAvgCost("FIRM01", "alice", "ITUB4"));

        var beforePetr4 = positions.ForEndClientAndFirm("FIRM01", alice).Single(p => p.Symbol == "PETR4");
        var beforePetr4Basis = pnl.CaptureSymbolBasis("FIRM01", "alice", "PETR4");
        var beforeItub4Basis = pnl.CaptureSymbolBasis("FIRM01", "alice", "ITUB4");
        var beforeVale3Basis = pnl.CaptureSymbolBasis("FIRM01", "alice", "VALE3");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            dispatcher.DispatchWithPreApply<AccountResetEvent>(
                resolveAndPreApply: () =>
                {
                    var evt = new AccountResetEvent
                    {
                        EndClientId = "alice",
                        FirmId = "FIRM01",
                        CashAvailable = 500m,
                        Positions = new List<AccountResetPositionEntry>
                        {
                            new("PETR4", 0, 0m),
                            new("ITUB4", 0, 0m),
                            new("VALE3", 10, 15m),
                        },
                        OperatorId = "op",
                    };
                    void Apply()
                    {
                        // Partially mutate every symbol, then fail
                        // before the mutation sequence completes —
                        // rollback must still restore ALL of them
                        // correctly regardless of exactly where the
                        // failure hit.
                        positions.SetAbsolute("FIRM01", alice, "PETR4", 0, 0m);
                        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 0, 0m);
                        positions.SetAbsolute("FIRM01", alice, "ITUB4", 0, 0m);
                        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "ITUB4", 0, 0m);
                        positions.SetAbsolute("FIRM01", alice, "VALE3", 10, 15m);
                        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "VALE3", 10, 15m);
                        throw new InvalidOperationException("simulated post-append apply failure");
                    }
                    return (evt, (Action)Apply);
                },
                rollbackOnApplyFailure: () =>
                {
                    // PETR4 was present before: restore its exact
                    // values. VALE3 was absent before: remove it
                    // entirely rather than leaving a flat row. ITUB4
                    // had an UNKNOWN pnl basis before: RestoreSymbolBasis
                    // (never SetAbsoluteAvgCost) puts it back as
                    // unknown, not known-zero/absent.
                    positions.SetAbsolute(
                        "FIRM01", alice, "PETR4",
                        beforePetr4.NetQuantity, beforePetr4.AverageEntryPrice);
                    positions.SetAbsolute("FIRM01", alice, "ITUB4", 40, 30m);
                    positions.TryRemove("FIRM01", alice, "VALE3");
                    pnl.RestoreSymbolBasis("FIRM01", "alice", "PETR4", beforePetr4Basis);
                    pnl.RestoreSymbolBasis("FIRM01", "alice", "ITUB4", beforeItub4Basis);
                    pnl.RestoreSymbolBasis("FIRM01", "alice", "VALE3", beforeVale3Basis);
                }));

        Assert.Equal("simulated post-append apply failure", ex.Message);

        // The event WAS durably appended (Append succeeded) — only the
        // in-memory apply partially failed.
        Assert.NotNull(store.LastAppended);
        Assert.IsType<AccountResetEvent>(store.LastAppended);
        // AdvanceApplied must NOT have run: the live projection does
        // not yet fully reflect the persisted event.
        Assert.Equal(0, dispatcher.LastAppliedSeq);

        var afterPetr4 = positions.ForEndClientAndFirm("FIRM01", alice).Single(p => p.Symbol == "PETR4");
        Assert.Equal(beforePetr4.NetQuantity, afterPetr4.NetQuantity);
        Assert.Equal(beforePetr4.AverageEntryPrice, afterPetr4.AverageEntryPrice);
        var afterPetr4Basis = pnl.GetAvgCost("FIRM01", "alice", "PETR4");
        Assert.NotNull(afterPetr4Basis);
        Assert.Equal(200, afterPetr4Basis!.NetQuantity);
        Assert.Equal(25m, afterPetr4Basis.AvgPrice);

        // VALE3 must be genuinely absent again — not a flat (0, 0m) row.
        Assert.DoesNotContain(positions.ForEndClientAndFirm("FIRM01", alice), p => p.Symbol == "VALE3");
        Assert.Null(pnl.GetAvgCost("FIRM01", "alice", "VALE3"));
        Assert.Equal(0, pnl.GetUnknownBasisQty("FIRM01", "alice", "VALE3"));

        // ITUB4's UNKNOWN basis must be restored EXACTLY — not
        // collapsed to a known basis, and not wiped to absence.
        Assert.Equal(40, pnl.GetUnknownBasisQty("FIRM01", "alice", "ITUB4"));
        Assert.Null(pnl.GetAvgCost("FIRM01", "alice", "ITUB4"));
    }

    /// <summary>
    /// TOCTOU regression: a concurrent order submission that lands
    /// INSIDE the same dispatcher-serialised critical region the reset
    /// uses must be visible to the reset's authoritative re-check, even
    /// though a cheap pre-check (run outside the lock, before dispatch)
    /// saw no open order. Models
    /// <c>AdminEndpoints.HandleAccountReset</c>'s two-phase guard: a
    /// pre-check outside the lock, then an authoritative re-check
    /// inside <c>preApply</c>.
    /// </summary>
    [Fact]
    public void Reset_ConcurrentOrderSubmitInsideCriticalRegion_BlocksAuthoritativeGuard()
    {
        var dispatcher = new EventDispatcher(new NullEventStore());
        var orders = new WorkingOrderBook();
        var alice = new EndClientId("alice");

        // Pre-check (outside the lock) sees no open order.
        Assert.Equal(0, orders.CountOpenForOwnerAndFirm("FIRM01", alice));

        // Simulate a racing order submission landing INSIDE the same
        // dispatcher-serialised region the reset's preApply runs in —
        // e.g. a submit whose own DispatchWithPreApply interleaved
        // between the reset's pre-check and its authoritative re-check.
        orders.TryAdd(new Order(
            clOrdId: 1, owner: alice, symbol: "PETR4", securityId: 1,
            side: OrderSide.Buy, type: OrderType.Limit, quantity: 100, price: 20m,
            firmId: "FIRM01"));

        var outcome = dispatcher.DispatchWithPreApply(
            new AccountResetEvent
            {
                EndClientId = "alice",
                FirmId = "FIRM01",
                CashAvailable = 0m,
                Positions = Array.Empty<AccountResetPositionEntry>(),
                OperatorId = "op",
            },
            preApply: () =>
            {
                // Authoritative re-check, inside the critical region —
                // must now observe the racing order and refuse.
                if (orders.CountOpenForOwnerAndFirm("FIRM01", alice) > 0) return false;
                return true;
            },
            rollback: () => { });

        Assert.False(outcome.Applied);
    }

    /// <summary>
    /// #671/#753 code-review addendum #3. The generic
    /// <see cref="EventDispatcher.DispatchWithPreApply{TEvent}(Func{ValueTuple{TEvent,Action}}, Action)"/>
    /// overload must resolve the <see cref="AccountResetEvent"/> payload
    /// from a FRESH, live read taken INSIDE the factory (i.e. under the
    /// dispatcher lock) — never from a snapshot a caller might have
    /// cached before acquiring the lock. This test injects an
    /// intervening position mutation (a brand-new symbol) AFTER a
    /// simulated "stale pre-lock snapshot" is captured but BEFORE the
    /// dispatcher call, and proves the persisted event's
    /// <c>Positions</c> covers the intervening symbol — i.e. the
    /// factory did its own live <see cref="PositionKeeper.ForEndClientAndFirm"/>
    /// read rather than reusing any pre-lock capture.
    /// </summary>
    [Fact]
    public void Reset_GenericDispatchWithPreApply_ResolvesPayloadLiveAtLockInstant()
    {
        var store = new RecordingStore();
        var dispatcher = new EventDispatcher(store);
        var positions = new PositionKeeper();
        var alice = new EndClientId("alice");

        positions.SetAbsolute("FIRM01", alice, "PETR4", 100, 25m);

        // A naive caller's pre-lock snapshot — captured BEFORE the
        // intervening mutation below and BEFORE the dispatcher call.
        // The factory below must NOT use this; it must re-read live.
        var stalePreLockSnapshot = positions.ForEndClientAndFirm("FIRM01", alice).ToList();
        Assert.DoesNotContain(stalePreLockSnapshot, p => p.Symbol == "VALE3");

        // Intervening mutation: a fill/adjustment lands on a brand-new
        // symbol strictly between the stale snapshot above and the
        // dispatcher call — modelling a race that must not escape reset.
        positions.SetAbsolute("FIRM01", alice, "VALE3", 50, 60m);

        var outcome = dispatcher.DispatchWithPreApply<AccountResetEvent>(
            resolveAndPreApply: () =>
            {
                // Authoritative, IN-LOCK live read — this is the fix:
                // resolve from the keeper now, not from any pre-lock
                // capture such as `stalePreLockSnapshot` above.
                var livePositions = positions.ForEndClientAndFirm("FIRM01", alice);
                var payload = AccountResetPayloadResolver.Resolve(
                    "FIRM01", alice, livePositions, new CashSeedOptions(), new PositionSeedOptions());
                var evt = new AccountResetEvent
                {
                    EndClientId = "alice",
                    FirmId = "FIRM01",
                    CashAvailable = payload.CashAvailable,
                    Positions = payload.Positions,
                    OperatorId = "op",
                };
                void Apply()
                {
                    foreach (var entry in payload.Positions)
                        positions.SetAbsolute("FIRM01", alice, entry.Symbol, entry.NetQuantity, entry.AverageEntryPrice);
                }
                return (evt, (Action)Apply);
            },
            rollbackOnApplyFailure: () => { });

        Assert.True(outcome.Applied);
        Assert.NotNull(store.LastAppended);
        var persisted = Assert.IsType<AccountResetEvent>(store.LastAppended);

        // The persisted event must cover BOTH symbols — including the
        // one that only existed because of the intervening mutation.
        // A caller that (incorrectly) used `stalePreLockSnapshot` would
        // only have persisted PETR4, silently leaving VALE3's 50@60
        // position un-reset.
        Assert.Equal(2, persisted.Positions.Count);
        var vale3 = persisted.Positions.Single(p => p.Symbol == "VALE3");
        Assert.Equal(0, vale3.NetQuantity);
        Assert.Equal(0m, vale3.AverageEntryPrice);
        var petr4 = persisted.Positions.Single(p => p.Symbol == "PETR4");
        Assert.Equal(0, petr4.NetQuantity);

        // And the live apply must have actually zeroed VALE3 too.
        Assert.All(positions.ForEndClientAndFirm("FIRM01", alice), p => Assert.Equal(0, p.NetQuantity));
    }

    /// <summary>
    /// End-to-end equivalence mirroring
    /// <c>CashWithdrawalAtomicityTests.Snapshot_DuringWithdrawals_ReplayMatchesDirectProjection</c>:
    /// a snapshot taken immediately after a successful reset, combined
    /// with the WAL tail, must replay to the same state as a straight
    /// WAL-only replay of the same store.
    /// </summary>
    [Fact]
    public async Task Snapshot_AfterReset_ReplayMatchesDirectProjection()
    {
        var root = Path.Combine(Path.GetTempPath(), "b3tp-acctreset-atom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var opts = new PersistenceOptions
            {
                DataDirectory = root,
                FirmId = "test",
                ChannelCapacity = 1024,
                GroupCommitMaxRecords = 8,
                GroupCommitWindow = TimeSpan.FromMilliseconds(5),
                FsyncOnFlush = false,
            };
            var alice = new EndClientId("alice");

            await using (var store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance))
            {
                var positions = new PositionKeeper();
                var pnl = new PnlKeeper();
                var subPnl = new SubAccountPnlKeeper();
                var cashKeeper = new CashKeeper();
                var cashLedger = new CashLedger();
                var dispatcher = new EventDispatcher(store);

                cashKeeper.Apply("FIRM01", "Deposit", alice, 500m);
                cashLedger.ApplyDeposit("FIRM01", alice, 500m);
                positions.SetAbsolute("FIRM01", alice, "PETR4", 300, 22m);

                dispatcher.DispatchWithPreApply(
                    new AccountResetEvent
                    {
                        EndClientId = "alice",
                        FirmId = "FIRM01",
                        CashAvailable = 7_000m,
                        Positions = new[] { new AccountResetPositionEntry("PETR4", 0, 0m) },
                        OperatorId = "op",
                    },
                    preApply: () =>
                    {
                        subPnl.ClearAllBucketsForAccount("FIRM01", "alice");
                        positions.SetAbsolute("FIRM01", alice, "PETR4", 0, 0m);
                        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 0, 0m);
                        subPnl.SetAbsoluteMasterBucketAvgCost("FIRM01", "alice", "PETR4", 0, 0m);
                        cashKeeper.SetAbsolute("FIRM01", alice, 7_000m);
                        cashLedger.SetAbsolute("FIRM01", alice, 7_000m);
                        return true;
                    },
                    rollback: () => { });

                var book = new WorkingOrderBook();
                var killSwitch = new KillSwitchService();
                var ownership = new OrderOwnershipMap();
                var clOrdIds = new ClOrdIdPrefixRegistry();
                var algos = new AlgoBook();
                var snapshotter = new StateSnapshotter(book, positions, killSwitch,
                    new SymbolHaltService(), new SessionPhaseService(),
                    clOrdIds, ownership, algos, new AlgoIdRegistry(),
                    cashLedger,
                    cashKeeper: cashKeeper,
                    pnlKeeper: pnl,
                    subAccountPnl: subPnl);

                PlatformSnapshot? snap = null;
                dispatcher.WithSnapshotLock(seq => snap = snapshotter.Capture(seq));
                new SnapshotStore(root, "test").Write(snap!);

                await store.FlushAsync();
            }

            decimal snapPlusTailCash;
            await using (var store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance))
            {
                var (snapshotter, replayer, cashLedger) = BuildFor(root);
                var recovery = new PersistenceRecovery(store, snapshotter, replayer,
                    new SnapshotStore(root, "test"),
                    NullLogger<PersistenceRecovery>.Instance);
                await recovery.RunAsync();
                snapPlusTailCash = cashLedger.GetAvailable("FIRM01", alice);
            }

            decimal walOnlyCash;
            await using (var store = new FileEventStore(opts, NullLogger<FileEventStore>.Instance))
            {
                var (_, replayer, cashLedger) = BuildFor(root);
                await foreach (var (_, evt) in store.ReadFromAsync(0))
                    replayer.Apply(evt);
                walOnlyCash = cashLedger.GetAvailable("FIRM01", alice);
            }

            Assert.Equal(walOnlyCash, snapPlusTailCash);
            Assert.Equal(7_000m, snapPlusTailCash);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static (StateSnapshotter, EventReplayer, CashLedger) BuildFor(string root)
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var killSwitch = new KillSwitchService();
        var ownership = new OrderOwnershipMap();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var algos = new AlgoBook();
        var sink = new NullSink();
        var cashKeeper = new CashKeeper();
        var cashLedger = new CashLedger();
        var pnl = new PnlKeeper();
        var subPnl = new SubAccountPnlKeeper();
        var processor = new ExecutionReportProcessor(ownership, book, positions, sink,
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        var snapshotter = new StateSnapshotter(book, positions, killSwitch,
            new SymbolHaltService(), new SessionPhaseService(),
            clOrdIds, ownership, algos, new AlgoIdRegistry(),
            cashLedger,
            cashKeeper: cashKeeper,
            pnlKeeper: pnl,
            subAccountPnl: subPnl);
        var replayer = new EventReplayer(book, ownership, killSwitch,
            new SymbolHaltService(), new SessionPhaseService(),
            processor, algos, clOrdIds, new AlgoIdRegistry(),
            cashKeeper: cashKeeper,
            pnlKeeper: pnl,
            positions: positions,
            subAccountPnl: subPnl,
            cash: cashLedger);
        return (snapshotter, replayer, cashLedger);
    }

    private sealed class NullSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent evt) { }
    }

    /// <summary>
    /// IEventStore whose Append throws once <see cref="ThrowOnNextAppend"/>
    /// is armed — models an Append-time failure (e.g. WAL backpressure)
    /// occurring AFTER preApply has already mutated every keeper, so the
    /// test exercises the rollback delegate exactly as
    /// <c>DispatchWithPreApply</c> invokes it.
    /// </summary>
    private sealed class ThrowingStore : IEventStore
    {
        public bool ThrowOnNextAppend;
        private long _seq;
        public long CurrentSeq => Interlocked.Read(ref _seq);

        public long Append(WalEvent evt) => Append(evt, ReadOnlyMemory<byte>.Empty);

        public long Append(WalEvent evt, ReadOnlyMemory<byte> _)
        {
            if (ThrowOnNextAppend)
            {
                ThrowOnNextAppend = false;
                throw new WalBackpressureException("forced test failure");
            }
            return Interlocked.Increment(ref _seq);
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive, CancellationToken ct = default) =>
            EmptyReadFromAsync(ct);

        private static async IAsyncEnumerable<(long, WalEvent)> EmptyReadFromAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// IEventStore that always succeeds and records the last appended
    /// <see cref="WalEvent"/> — used by the finding-#3 concurrency test
    /// to inspect exactly what payload the dispatcher's generic
    /// <c>DispatchWithPreApply&lt;TEvent&gt;</c> overload persisted.
    /// </summary>
    private sealed class RecordingStore : IEventStore
    {
        public WalEvent? LastAppended { get; private set; }
        private long _seq;
        public long CurrentSeq => Interlocked.Read(ref _seq);

        public long Append(WalEvent evt) => Append(evt, ReadOnlyMemory<byte>.Empty);

        public long Append(WalEvent evt, ReadOnlyMemory<byte> _)
        {
            LastAppended = evt;
            return Interlocked.Increment(ref _seq);
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive, CancellationToken ct = default) =>
            EmptyReadFromAsync(ct);

        private static async IAsyncEnumerable<(long, WalEvent)> EmptyReadFromAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
