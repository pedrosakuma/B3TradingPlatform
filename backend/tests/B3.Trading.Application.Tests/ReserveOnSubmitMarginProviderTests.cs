using B3.Trading.Application.Risk;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;

// These tests cover the legacy Margin.Initial fallback path on
// purpose (#107 slice 4 deprecated but did not remove it). Suppress
// the obsolete warning at file scope; when the property is removed
// in a follow-up, this file will be migrated or deleted.
#pragma warning disable CS0618 // Type or member is obsolete

namespace B3.Trading.Application.Tests;

public class ReserveOnSubmitMarginProviderTests
{
    private static (ReserveOnSubmitMarginProvider provider, StaticOptionsMonitor<RiskOptions> monitor) Build(
        decimal initial = 100_000m, string owner = "alice")
    {
        var opts = new RiskOptions();
        opts.Margin.Enabled = true;
        opts.Margin.Initial[owner] = initial;
        var monitor = new StaticOptionsMonitor<RiskOptions>(opts);
        var provider = new ReserveOnSubmitMarginProvider(monitor, NullLogger<ReserveOnSubmitMarginProvider>.Instance);
        return (provider, monitor);
    }

    private static RiskContext Buy(string owner, decimal price, long qty, OrderType type = OrderType.Limit) =>
        new(new EndClientId(owner), "FIRM", "PETR4", OrderSide.Buy, type, qty, type == OrderType.Market ? null : price);

    private static RiskContext Buy(string firmId, string owner, decimal price, long qty, OrderType type = OrderType.Limit) =>
        new(new EndClientId(owner), firmId, "PETR4", OrderSide.Buy, type, qty, type == OrderType.Market ? null : price);

    [Fact]
    public async Task Approves_until_balance_depleted_then_rejects()
    {
        var (p, _) = Build(initial: 1_000m);
        Assert.True((await p.TryReserveAsync(1, Buy("alice", 10m, 50), CancellationToken.None)).Approved);
        Assert.True((await p.TryReserveAsync(2, Buy("alice", 10m, 50), CancellationToken.None)).Approved);
        var d3 = await p.TryReserveAsync(3, Buy("alice", 10m, 1), CancellationToken.None);
        Assert.False(d3.Approved);
        Assert.Contains("insufficient margin", d3.Reason);
    }

    [Fact]
    public async Task Cancel_releases_reservation()
    {
        var (p, _) = Build(initial: 1_000m);
        Assert.True((await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None)).Approved);
        Assert.False((await p.TryReserveAsync(2, Buy("alice", 10m, 1), CancellationToken.None)).Approved);
        p.OnExecution(1, ExecKind.Canceled, 0);
        Assert.True((await p.TryReserveAsync(3, Buy("alice", 10m, 100), CancellationToken.None)).Approved);
    }

    [Fact]
    public async Task Reject_releases_reservation()
    {
        var (p, _) = Build(initial: 1_000m);
        Assert.True((await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None)).Approved);
        p.OnExecution(1, ExecKind.Rejected, 0);
        Assert.Equal(1_000m, p.AvailableForTesting("alice"));
    }

    [Fact]
    public async Task PartialFill_releases_proportionally()
    {
        var (p, _) = Build(initial: 1_000m);
        Assert.True((await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None)).Approved);
        Assert.Equal(0m, p.AvailableForTesting("alice"));
        p.OnExecution(1, ExecKind.PartialFill, 30);
        Assert.Equal(300m, p.AvailableForTesting("alice"));
        p.OnExecution(1, ExecKind.PartialFill, 20);
        Assert.Equal(500m, p.AvailableForTesting("alice"));
    }

    [Fact]
    public void RecoveryState_RestoresLeavesAndOnlyTheReplaceUpsizeDelta()
    {
        var (p, _) = Build(initial: 1_000m);
        var order = new OrderSnapshot(
            1,
            "alice",
            "PETR4",
            1234,
            OrderSide.Buy.ToString(),
            OrderType.Limit.ToString(),
            100,
            10m,
            60,
            40,
            OrderStatus.PartiallyFilled.ToString(),
            "FIRM");
        var intent = new OrderReplacementIntent(
            OriginalClOrdId: 1,
            NewClOrdId: 2,
            Owner: new EndClientId("alice"),
            Symbol: "PETR4",
            SecurityId: 1234,
            Side: OrderSide.Buy,
            Type: OrderType.Limit,
            NewQuantity: 80,
            NewPrice: 10m,
            FirmId: "FIRM",
            ParentAlgoId: null,
            AlgoSliceSeq: null);
        var pending = new PendingReplacementEntrySnapshot(
            intent,
            DateTimeOffset.UtcNow,
            AmbiguousMarginHeld: true,
            AmbiguousAt: DateTimeOffset.UtcNow,
            NewRemainingNotional: 800m);

        var restored = p.RestoreRecoveryState([order], [pending]);

        Assert.Equal((1, 1), restored);
        Assert.Equal(800m, p.ReservedForTesting("alice"));
        Assert.Equal(200m, p.AvailableForTesting("alice"));
    }

    [Fact]
    public async Task Terminal_fill_releases_remaining()
    {
        var (p, _) = Build(initial: 1_000m);
        Assert.True((await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None)).Approved);
        p.OnExecution(1, ExecKind.PartialFill, 30);
        p.OnExecution(1, ExecKind.Fill, 70);
        Assert.Equal(1_000m, p.AvailableForTesting("alice"));
    }

    [Fact]
    public async Task Sells_skip_reservation()
    {
        var (p, _) = Build(initial: 100m);
        var ctx = new RiskContext(new EndClientId("alice"), "F", "PETR4", OrderSide.Sell, OrderType.Limit, 10_000, 99m);
        var d = await p.TryReserveAsync(1, ctx, CancellationToken.None);
        Assert.True(d.Approved);
        Assert.Equal(0m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task Markets_skip_reservation()
    {
        var (p, _) = Build(initial: 100m);
        var d = await p.TryReserveAsync(1, Buy("alice", 0m, 10_000, OrderType.Market), CancellationToken.None);
        Assert.True(d.Approved);
        Assert.Equal(0m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task BuyMarket_withStrayPrice_skipsReservation()
    {
        // Pass-3 alignment (#253): the OrderType gate must apply even
        // when a buy Market carries a non-null Price (e.g. caller
        // populated it for telemetry). The replace pipeline gates on
        // IsMarginBearing() — submit must agree, otherwise we'd reserve
        // here and silently release on the next cancel-replace.
        var (p, _) = Build(initial: 100_000m);
        var ctx = new RiskContext(
            new EndClientId("alice"), "FIRM", "PETR4",
            OrderSide.Buy, OrderType.Market, 100, 50m);
        var d = await p.TryReserveAsync(1, ctx, CancellationToken.None);
        Assert.True(d.Approved);
        Assert.Equal(0m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task BuyStopLoss_withStrayPrice_skipsReservation()
    {
        // Same alignment as BuyMarket: StopLoss is a stop-into-Market;
        // it has no resting limit price to reserve against, regardless
        // of any Price set on the context.
        var (p, _) = Build(initial: 100_000m);
        var ctx = new RiskContext(
            new EndClientId("alice"), "FIRM", "PETR4",
            OrderSide.Buy, OrderType.StopLoss, 100, 50m);
        var d = await p.TryReserveAsync(1, ctx, CancellationToken.None);
        Assert.True(d.Approved);
        Assert.Equal(0m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task BuyMarginBearingTypes_reserveAsBefore()
    {
        // Regression guard: the three OrderType.IsMarginBearing() types
        // (Limit / StopLimit / MarketWithLeftover) must continue to
        // reserve cash on a Buy with a positive notional.
        var (p, _) = Build(initial: 10_000m);
        Assert.True((await p.TryReserveAsync(1, Buy("alice", 10m, 100, OrderType.Limit), CancellationToken.None)).Approved);
        Assert.True((await p.TryReserveAsync(2, Buy("alice", 10m, 100, OrderType.StopLimit), CancellationToken.None)).Approved);
        Assert.True((await p.TryReserveAsync(3, Buy("alice", 10m, 100, OrderType.MarketWithLeftover), CancellationToken.None)).Approved);
        Assert.Equal(3_000m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task Sell_ofAnyType_skipsReservation()
    {
        // Regression guard: the Side gate runs first, so even
        // margin-bearing types with a price never reserve on Sell.
        var (p, _) = Build(initial: 10_000m);
        foreach (var t in new[] { OrderType.Limit, OrderType.StopLimit, OrderType.MarketWithLeftover, OrderType.Market, OrderType.StopLoss })
        {
            var ctx = new RiskContext(
                new EndClientId("alice"), "FIRM", "PETR4",
                OrderSide.Sell, t, 100, 50m);
            var d = await p.TryReserveAsync((ulong)t + 100, ctx, CancellationToken.None);
            Assert.True(d.Approved);
        }
        Assert.Equal(0m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task Unknown_owner_has_zero_balance_and_is_rejected()
    {
        var (p, _) = Build(initial: 1_000m, owner: "alice");
        var d = await p.TryReserveAsync(1, Buy("bob", 1m, 1), CancellationToken.None);
        Assert.False(d.Approved);
    }

    [Fact]
    public async Task ReleaseReservation_is_idempotent()
    {
        var (p, _) = Build(initial: 1_000m);
        await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None);
        p.ReleaseReservation(1);
        p.ReleaseReservation(1);
        Assert.Equal(1_000m, p.AvailableForTesting("alice"));
    }

    [Fact]
    public void OnExecution_for_unknown_clordid_is_noop()
    {
        var (p, _) = Build();
        p.OnExecution(999, ExecKind.Fill, 100);
        Assert.Equal(100_000m, p.AvailableForTesting("alice"));
    }

    [Fact]
    public async Task Hot_reload_of_initial_balance_is_observed()
    {
        var (p, monitor) = Build(initial: 100m);
        Assert.False((await p.TryReserveAsync(1, Buy("alice", 10m, 50), CancellationToken.None)).Approved);
        var newOpts = new RiskOptions();
        newOpts.Margin.Enabled = true;
        newOpts.Margin.Initial["alice"] = 1_000m;
        monitor.Set(newOpts);
        Assert.True((await p.TryReserveAsync(2, Buy("alice", 10m, 50), CancellationToken.None)).Approved);
    }

    [Fact]
    public async Task Suspended_releasesCashHold_butKeepsTrackingEntry()
    {
        // #153. The whole point of a stale flip: the order ghost must
        // stop blocking new trading. ReservedForTesting drops; available
        // returns to base. The tracking entry stays so a Restored can
        // re-acquire later.
        var (p, _) = Build(initial: 1_000m);
        Assert.True((await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None)).Approved);
        Assert.Equal(0m, p.AvailableForTesting("alice"));

        p.OnExecution(1, ExecKind.Suspended, 0);

        Assert.Equal(1_000m, p.AvailableForTesting("alice"));
        Assert.Equal(0m, p.ReservedForTesting("alice"));
        // Reservation entry is still tracked (we can verify by Restored re-acquiring exactly).
        p.OnExecution(1, ExecKind.Restored, 0);
        Assert.Equal(0m, p.AvailableForTesting("alice"));
    }

    [Fact]
    public async Task Suspended_isIdempotent_doesNotDoubleRelease()
    {
        // Race guard: if Suspended fires twice (e.g. retry on a sink
        // failure), the second call must be a no-op. Without the
        // IsSuspended flag, the second decrement would clamp to zero
        // and silently release another order's hold.
        var (p, _) = Build(initial: 2_000m);
        await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None);
        await p.TryReserveAsync(2, Buy("alice", 10m, 50), CancellationToken.None);
        Assert.Equal(500m, p.AvailableForTesting("alice"));

        p.OnExecution(1, ExecKind.Suspended, 0);
        p.OnExecution(1, ExecKind.Suspended, 0);

        // Order 2's hold is intact: 2000 - 500 (still held by #2) = 1500.
        Assert.Equal(1_500m, p.AvailableForTesting("alice"));
        Assert.Equal(500m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task Suspended_doesNotCorrupt_otherOrderForSameOwner()
    {
        // Critical: the corruption guard. Two orders, same owner —
        // suspending one must not leak into the other's reservation.
        var (p, _) = Build(initial: 2_000m);
        await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None); // 1000 held
        await p.TryReserveAsync(2, Buy("alice", 10m, 50), CancellationToken.None);  // 500 held

        p.OnExecution(1, ExecKind.Suspended, 0);

        Assert.Equal(500m, p.ReservedForTesting("alice"));
        // Order 2 still terminalizes correctly.
        p.OnExecution(2, ExecKind.Canceled, 0);
        Assert.Equal(0m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task PartialFillWhileSuspended_doesNotDecrementReserved_butReducesRemaining()
    {
        // Stale order partial-fills while suspended (rare but possible
        // if the venue ER lands during the staleness window). Cash was
        // already released by Suspended; the partial must NOT decrement
        // again, but it must reduce RemainingNotional so a later
        // Restored re-acquires only the post-fill leaves.
        var (p, _) = Build(initial: 1_000m);
        await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None);
        p.OnExecution(1, ExecKind.Suspended, 0);
        Assert.Equal(0m, p.ReservedForTesting("alice"));

        p.OnExecution(1, ExecKind.PartialFill, 30);

        // No double decrement.
        Assert.Equal(0m, p.ReservedForTesting("alice"));
        // Restored should re-acquire only 700 (10 * 70 leaves).
        p.OnExecution(1, ExecKind.Restored, 0);
        Assert.Equal(700m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task SuspendedThenFilled_noDoubleDecrement()
    {
        // Auto-clear path in ExecutionReportProcessor: a stale order
        // fills before admin clears it. ER processor calls OnExecution
        // BEFORE ClearStale (no Restored fires). Cash already released
        // by Suspended; Fill must not double-decrement.
        var (p, _) = Build(initial: 1_000m);
        await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None);
        p.OnExecution(1, ExecKind.Suspended, 0);

        p.OnExecution(1, ExecKind.Fill, 100);

        Assert.Equal(0m, p.ReservedForTesting("alice"));
        Assert.Equal(1_000m, p.AvailableForTesting("alice"));
        // Tracking entry removed.
        p.OnExecution(1, ExecKind.Restored, 0);
        Assert.Equal(1_000m, p.AvailableForTesting("alice"));
    }

    [Fact]
    public async Task CancelWhileSuspended_removesEntry_withoutTouchingOtherReservations()
    {
        var (p, _) = Build(initial: 2_000m);
        await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None);
        await p.TryReserveAsync(2, Buy("alice", 10m, 50), CancellationToken.None);
        p.OnExecution(1, ExecKind.Suspended, 0);

        p.OnExecution(1, ExecKind.Canceled, 0);

        // Order 2 still held; alice's reserved exactly = 500.
        Assert.Equal(500m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task RestoredAfterTerminalRemoval_isNoOp()
    {
        // If the entry was already removed (auto-clear path consumed
        // it via terminal ER), a late Restored must be safe.
        var (p, _) = Build(initial: 1_000m);
        await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None);
        p.OnExecution(1, ExecKind.Suspended, 0);
        p.OnExecution(1, ExecKind.Fill, 100);

        p.OnExecution(1, ExecKind.Restored, 0);

        Assert.Equal(1_000m, p.AvailableForTesting("alice"));
    }

    [Fact]
    public async Task Restored_overcommitsSilently_butStillRestoresLedger()
    {
        // #153. The WAL event is already committed when Restored
        // fires; refusing to track the cash here would leave the
        // ledger inconsistent with the WAL. We restore even when it
        // pushes past base capacity (operator must reconcile by
        // cancelling other holds; the metric flags the situation).
        var (p, _) = Build(initial: 1_000m);
        await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None); // 1000 held, all cash
        p.OnExecution(1, ExecKind.Suspended, 0); // released
        // Operator (or a racing flow) consumes the freed cash with another order.
        Assert.True((await p.TryReserveAsync(2, Buy("alice", 10m, 100), CancellationToken.None)).Approved);
        Assert.Equal(1_000m, p.ReservedForTesting("alice"));

        p.OnExecution(1, ExecKind.Restored, 0);

        // Reserved now exceeds base — overcommit.
        Assert.Equal(2_000m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task PrepareReplace_originalSuspended_treatsOldHeldAsZero()
    {
        // #153. PrepareReplaceAsync must NOT credit the suspended
        // original's tracked remaining, because that cash is no
        // longer held in _reserved. Treating it as old-held would
        // approve a replace that, at commit, restores the missing
        // notional and pushes past cap.
        var (p, _) = Build(initial: 1_000m);
        await p.TryReserveAsync(1, Buy("alice", 10m, 50), CancellationToken.None); // 500 held
        p.OnExecution(1, ExecKind.Suspended, 0); // released
        // Use the freed cash on another order.
        await p.TryReserveAsync(2, Buy("alice", 10m, 100), CancellationToken.None); // 1000 held, 0 available
        Assert.Equal(0m, p.AvailableForTesting("alice"));

        // Replace #1 with full new notional: must be rejected because
        // delta = 500 - 0 = 500 > 0 available.
        var d = await p.PrepareReplaceAsync(1, 99, new EndClientId("alice"), 500m, CancellationToken.None);

        Assert.False(d.Approved);
        Assert.Contains("delta", d.Reason);
    }

    [Fact]
    public async Task GetReservationCounts_TracksActiveAndSuspendedSeparately()
    {
        // #153 follow-up. Memory-growth gauge: operators need to see
        // suspended entries accumulating so a venue that never recovers
        // doesn't quietly leak the reservation dictionary.
        var (p, _) = Build(initial: 10_000m);
        Assert.Equal((0, 0), p.GetReservationCounts());

        await p.TryReserveAsync(1, Buy("alice", 10m, 50), CancellationToken.None);
        await p.TryReserveAsync(2, Buy("alice", 10m, 50), CancellationToken.None);
        Assert.Equal((2, 0), p.GetReservationCounts());

        p.OnExecution(1, ExecKind.Suspended, 0);
        Assert.Equal((1, 1), p.GetReservationCounts());

        // Restored flips back to active without changing total.
        p.OnExecution(1, ExecKind.Restored, 0);
        Assert.Equal((2, 0), p.GetReservationCounts());

        // Terminal removes the entry entirely.
        p.OnExecution(2, ExecKind.Canceled, 0);
        Assert.Equal((1, 0), p.GetReservationCounts());
    }

    [Fact]
    public async Task GetReservationCounts_SuspendedThenFilled_DoesNotLeak()
    {
        // The auto-clear path (terminal ER on a suspended order) must
        // remove the entry, otherwise the gauge would grow without
        // bound on every venue restart.
        var (p, _) = Build(initial: 1_000m);
        await p.TryReserveAsync(1, Buy("alice", 10m, 100), CancellationToken.None);
        p.OnExecution(1, ExecKind.Suspended, 0);
        Assert.Equal((0, 1), p.GetReservationCounts());

        p.OnExecution(1, ExecKind.Fill, 100);

        Assert.Equal((0, 0), p.GetReservationCounts());
    }

    [Fact]
    public async Task CommitReplace_originalSuspended_doesNotDoubleCredit()
    {
        // CommitReplace's adjustment math: subtracting the suspended
        // original's tracked remaining would over-release reserved
        // (the cash was never there). Adjustment must be computed
        // against an effective-old of zero.
        var (p, _) = Build(initial: 2_000m);
        await p.TryReserveAsync(1, Buy("alice", 10m, 50), CancellationToken.None); // 500 held
        p.OnExecution(1, ExecKind.Suspended, 0); // released, 2000 available
        // PrepareReplace on the suspended original to a same-size order.
        var prep = await p.PrepareReplaceAsync(1, 99, new EndClientId("alice"), 500m, CancellationToken.None);
        Assert.True(prep.Approved);
        // Delta path was 500 - 0 = 500: alice has 500 reserved (the new transient).
        Assert.Equal(500m, p.ReservedForTesting("alice"));

        p.CommitReplace(originalClOrdId: 1, newClOrdId: 99, confirmedRemainingNotional: 500m);

        // Reserved must be exactly 500 (the new order's held notional).
        // If we had credited oldRemaining=500 in CommitReplace, the
        // adjustment would be 500-500-500 = -500 → reserved = 0,
        // which is wrong (the order is live).
        Assert.Equal(500m, p.ReservedForTesting("alice"));
    }

    // ----- ReleaseAllReservationsForAccount (#671 / RFC #753 PR2) -----

    [Fact]
    public async Task ReleaseAllReservationsForAccount_OnlyReleasesTargetFirm()
    {
        // Firm isolation: the same owner name under two different firms
        // must be tracked (and released) independently — releasing
        // FIRM-A's reservations must not touch FIRM-B's.
        var (p, _) = Build(initial: 1_000m, owner: "alice");
        Assert.True((await p.TryReserveAsync(1, Buy("FIRM-A", "alice", 10m, 50), CancellationToken.None)).Approved); // 500 held @ FIRM-A
        Assert.True((await p.TryReserveAsync(2, Buy("FIRM-B", "alice", 10m, 30), CancellationToken.None)).Approved); // 300 held @ FIRM-B

        p.ReleaseAllReservationsForAccount("FIRM-A", new EndClientId("alice"));

        Assert.Equal(0m, p.ReservedForTesting("FIRM-A", "alice"));
        Assert.Equal(300m, p.ReservedForTesting("FIRM-B", "alice"));
        Assert.Equal((1, 0), p.GetReservationCounts());
    }

    [Fact]
    public async Task ReleaseAllReservationsForAccount_OnlyReleasesTargetEndClient()
    {
        // End-client isolation within the same firm: releasing alice's
        // account must not touch bob's reservations at the same firm.
        var (p, monitor) = Build(initial: 1_000m, owner: "alice");
        monitor.CurrentValue.Margin.Initial["bob"] = 1_000m;
        Assert.True((await p.TryReserveAsync(1, Buy("FIRM", "alice", 10m, 50), CancellationToken.None)).Approved); // 500 held
        Assert.True((await p.TryReserveAsync(2, Buy("FIRM", "bob", 10m, 40), CancellationToken.None)).Approved); // 400 held

        p.ReleaseAllReservationsForAccount("FIRM", new EndClientId("alice"));

        Assert.Equal(0m, p.ReservedForTesting("FIRM", "alice"));
        Assert.Equal(400m, p.ReservedForTesting("FIRM", "bob"));
        Assert.Equal((1, 0), p.GetReservationCounts());
    }

    [Fact]
    public async Task ReleaseAllReservationsForAccount_RemovesMultipleReservationsAndRestoresFullCapacity()
    {
        // Multiple working orders for the same account: all per-ClOrdID
        // entries must be removed and the aggregate reserved figure must
        // drop to zero, restoring the full base capacity.
        var (p, _) = Build(initial: 1_000m, owner: "alice");
        Assert.True((await p.TryReserveAsync(1, Buy("FIRM", "alice", 10m, 30), CancellationToken.None)).Approved); // 300 held
        Assert.True((await p.TryReserveAsync(2, Buy("FIRM", "alice", 10m, 20), CancellationToken.None)).Approved); // 200 held
        Assert.True((await p.TryReserveAsync(3, Buy("FIRM", "alice", 10m, 10), CancellationToken.None)).Approved); // 100 held
        Assert.Equal(400m, p.AvailableForTesting("FIRM", "alice"));

        p.ReleaseAllReservationsForAccount("FIRM", new EndClientId("alice"));

        Assert.Equal(0m, p.ReservedForTesting("FIRM", "alice"));
        Assert.Equal(1_000m, p.AvailableForTesting("FIRM", "alice"));
        Assert.Equal((0, 0), p.GetReservationCounts());

        // Full capacity is usable again — a new order for the whole
        // base amount is approved.
        Assert.True((await p.TryReserveAsync(4, Buy("FIRM", "alice", 10m, 100), CancellationToken.None)).Approved);
    }

    [Fact]
    public async Task ReleaseAllReservationsForAccount_RemovesSuspendedReservationsToo()
    {
        // #153 interplay: a Suspended entry already released its cash
        // from _reserved, but the ReservationEntry itself must still be
        // removed by an account reset — otherwise a later admin
        // Restored on a reset account would re-acquire a stale hold.
        var (p, _) = Build(initial: 1_000m, owner: "alice");
        Assert.True((await p.TryReserveAsync(1, Buy("FIRM", "alice", 10m, 50), CancellationToken.None)).Approved); // 500 held
        p.OnExecution(1, ExecKind.Suspended, 0); // cash released, entry stays flagged suspended
        Assert.Equal((0, 1), p.GetReservationCounts());
        Assert.Equal(1_000m, p.AvailableForTesting("FIRM", "alice"));

        p.ReleaseAllReservationsForAccount("FIRM", new EndClientId("alice"));

        // The tracking entry is gone, so a later Restored on ClOrdID 1
        // is a silent no-op instead of re-acquiring a hold.
        Assert.Equal((0, 0), p.GetReservationCounts());
        p.OnExecution(1, ExecKind.Restored, 0);
        Assert.Equal(0m, p.ReservedForTesting("FIRM", "alice"));
        Assert.Equal(1_000m, p.AvailableForTesting("FIRM", "alice"));
    }

    [Fact]
    public async Task ReleaseAllReservationsForAccount_IsIdempotent()
    {
        // Calling release twice (e.g. a retried admin reset) — and
        // calling it on an account that never reserved anything — must
        // both be harmless no-ops.
        var (p, _) = Build(initial: 1_000m, owner: "alice");
        Assert.True((await p.TryReserveAsync(1, Buy("FIRM", "alice", 10m, 50), CancellationToken.None)).Approved);

        p.ReleaseAllReservationsForAccount("FIRM", new EndClientId("alice"));
        p.ReleaseAllReservationsForAccount("FIRM", new EndClientId("alice"));

        Assert.Equal(0m, p.ReservedForTesting("FIRM", "alice"));
        Assert.Equal(1_000m, p.AvailableForTesting("FIRM", "alice"));

        // Never-reserved account: no-op, does not throw.
        p.ReleaseAllReservationsForAccount("FIRM", new EndClientId("never-traded"));
        Assert.Equal(0m, p.ReservedForTesting("FIRM", "never-traded"));
    }
}
