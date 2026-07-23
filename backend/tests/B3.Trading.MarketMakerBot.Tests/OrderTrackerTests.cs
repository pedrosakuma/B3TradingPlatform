using B3.Trading.MarketMakerBot;

namespace B3.Trading.MarketMakerBot.Tests;

public class OrderTrackerTests
{
    [Fact]
    public void RegisterSubmit_ReflectedInInFlightCount()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.TryRegisterSubmit(2UL, "PETR4", 30.5m, 100, isBuy: false);
        t.TryRegisterSubmit(3UL, "VALE3", 70m, 100, isBuy: false);
        Assert.Equal(2, t.InFlightCount("PETR4"));
        Assert.Equal(1, t.InFlightCount("VALE3"));
        Assert.Equal(0, t.InFlightCount("MGLU3"));
    }

    [Fact]
    public void TryRegisterSubmit_SameSideAlreadyActive_ReturnsFalseAndDoesNotOverwrite()
    {
        var t = new OrderTracker();
        Assert.True(t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true));
        Assert.False(t.TryRegisterSubmit(2UL, "PETR4", 29m, 100, isBuy: true));

        // The second (rejected) registration must not have been recorded.
        Assert.False(t.TryGet(2UL, out _));
        Assert.Equal(1, t.InFlightCount("PETR4"));
    }

    [Fact]
    public void TryRegisterSubmit_AfterSideCloses_CanReserveAgain()
    {
        var t = new OrderTracker();
        Assert.True(t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true));
        t.OnTerminal(1UL);
        Assert.True(t.TryRegisterSubmit(2UL, "PETR4", 29m, 100, isBuy: true));
        Assert.Equal(1, t.InFlightCount("PETR4"));
    }

    [Fact]
    public void OnTrade_LeavesZero_ClosesOrder()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, true);
        t.OnTrade(1UL, leaves: 0);
        Assert.Equal(0, t.InFlightCount("PETR4"));
    }

    [Fact]
    public void OnTrade_PartialFill_StaysOpen()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 200, true);
        t.OnTrade(1UL, leaves: 100);
        Assert.Equal(1, t.InFlightCount("PETR4"));
    }

    [Fact]
    public void OnTerminal_ClosesOrder()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, true);
        t.OnTerminal(1UL);
        Assert.Equal(0, t.InFlightCount("PETR4"));
    }

    [Fact]
    public void OnAccepted_NoFill_StaysOpenWithLeaves()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, true);
        t.OnAccepted(1UL, leaves: 100);
        Assert.Equal(1, t.InFlightCount("PETR4"));
    }

    [Fact]
    public void TryGet_UnknownClOrdId_ReturnsFalse()
    {
        var t = new OrderTracker();
        Assert.False(t.TryGet(999UL, out _));
    }

    [Fact]
    public void HasOpenSide_TrueWhileResting_FalseAfterTerminal()
    {
        var t = new OrderTracker();
        Assert.False(t.HasOpenSide("PETR4", isBuy: true));

        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        Assert.True(t.HasOpenSide("PETR4", isBuy: true));
        Assert.False(t.HasOpenSide("PETR4", isBuy: false));

        t.OnTerminal(1UL);
        Assert.False(t.HasOpenSide("PETR4", isBuy: true));
    }

    [Fact]
    public void HasOpenSide_DistinguishesSymbolAndSide()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.TryRegisterSubmit(2UL, "PETR4", 31m, 100, isBuy: false);
        t.TryRegisterSubmit(3UL, "VALE3", 70m, 100, isBuy: true);

        Assert.True(t.HasOpenSide("PETR4", isBuy: true));
        Assert.True(t.HasOpenSide("PETR4", isBuy: false));
        Assert.True(t.HasOpenSide("VALE3", isBuy: true));
        Assert.False(t.HasOpenSide("VALE3", isBuy: false));
        Assert.False(t.HasOpenSide("MGLU3", isBuy: true));
    }

    [Fact]
    public void OnTerminal_DuplicateForSupersededOrder_DoesNotEvictNewerReservation()
    {
        // Regression test for the OrderTracker.Close() owner-check bug
        // (RFC #703): a stale/duplicate terminal ER for an order that has
        // already been superseded by a newer submit on the same
        // (symbol, side) must not evict the newer order's reservation.
        var t = new OrderTracker();
        Assert.True(t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true));
        t.OnTerminal(1UL); // legitimately closes clOrdId 1, frees the side.
        Assert.True(t.TryRegisterSubmit(2UL, "PETR4", 29m, 100, isBuy: true)); // new owner of the side.

        // A duplicate/racing terminal ER for the OLD order arrives late.
        t.OnTerminal(1UL);

        // The new order's reservation must still be intact.
        Assert.True(t.HasOpenSide("PETR4", isBuy: true));
        Assert.Equal(1, t.InFlightCount("PETR4"));
        Assert.False(t.TryRegisterSubmit(3UL, "PETR4", 28m, 100, isBuy: true));
    }

    [Fact]
    public void OpenCount_ReflectsOpenOrdersAcrossSymbols()
    {
        var t = new OrderTracker();
        Assert.Equal(0, t.OpenCount());
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.TryRegisterSubmit(2UL, "VALE3", 70m, 100, isBuy: false);
        Assert.Equal(2, t.OpenCount());
        t.OnTerminal(1UL);
        Assert.Equal(1, t.OpenCount());
    }

    [Fact]
    public void FindStale_ReturnsOnlyOrdersAtOrOverMaxAge()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var t = new OrderTracker(clock);
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        clock.Advance(TimeSpan.FromMinutes(3));
        t.TryRegisterSubmit(2UL, "PETR4", 31m, 100, isBuy: false);
        clock.Advance(TimeSpan.FromMinutes(2));

        // clOrdId 1 is now 5 minutes old, clOrdId 2 is 2 minutes old.
        var stale = t.FindStale(TimeSpan.FromMinutes(5), t.UtcNow);
        Assert.Single(stale);
        Assert.Equal(1UL, stale[0].ClOrdId);
    }

    [Fact]
    public void FindStale_ClosedOrders_AreExcluded()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var t = new OrderTracker(clock);
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.OnTerminal(1UL);
        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Empty(t.FindStale(TimeSpan.FromMinutes(5), t.UtcNow));
    }

    [Fact]
    public void RegisterCancelAttempt_ResolvesBackToOriginalOrder()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);

        // Simulate the bot sending an explicit cancel with a fresh ClOrdID.
        t.RegisterCancelAttempt(cancelClOrdId: 99UL, origClOrdId: 1UL);

        Assert.True(t.TryResolveCancelAttempt(99UL, out var origId));
        Assert.Equal(1UL, origId);

        // Registering a cancel attempt must NOT create a second entry in
        // the primary order collection — otherwise OpenCount/InFlightCount
        // /FindStale would double-count the same resting order (it's
        // still just ONE order, referenced by two different ClOrdIDs).
        Assert.False(t.TryGet(99UL, out _));
        Assert.Equal(1, t.OpenCount());
        Assert.Equal(1, t.InFlightCount("PETR4"));
    }

    [Fact]
    public void RegisterCancelAttempt_UnresolvedId_TryResolveReturnsFalse()
    {
        var t = new OrderTracker();
        Assert.False(t.TryResolveCancelAttempt(99UL, out _));
    }

    [Fact]
    public void RegisterCancelAttempt_DoesNotDoubleCountInOpenOrInFlightCounts()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.RegisterCancelAttempt(cancelClOrdId: 99UL, origClOrdId: 1UL);

        // Still just ONE resting order, referenced by two ClOrdIDs — must
        // not be double-counted by anything that iterates the order set.
        Assert.Equal(1, t.OpenCount());
        Assert.Equal(1, t.InFlightCount("PETR4"));
    }

    [Fact]
    public void FindStale_SkipsOrdersWithOutstandingCancelAttempt()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var t = new OrderTracker(clock);
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        clock.Advance(TimeSpan.FromMinutes(10));

        // First check: nothing pending yet, order is stale.
        Assert.Single(t.FindStale(TimeSpan.FromMinutes(5), t.UtcNow));

        // Register the cancel the reconcile loop would send for it.
        t.RegisterCancelAttempt(cancelClOrdId: 99UL, origClOrdId: 1UL);

        // A later reconcile tick (before the cancel resolves) must not
        // pick the same order again — one outstanding cancel at a time.
        Assert.Empty(t.FindStale(TimeSpan.FromMinutes(5), t.UtcNow));
    }

    [Fact]
    public void ClearPendingCancel_AllowsFindStaleToPickItUpAgain()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var t = new OrderTracker(clock);
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        clock.Advance(TimeSpan.FromMinutes(10));
        t.RegisterCancelAttempt(cancelClOrdId: 99UL, origClOrdId: 1UL);
        Assert.Empty(t.FindStale(TimeSpan.FromMinutes(5), t.UtcNow));

        // The cancel came back rejected without proving the order is
        // gone — the worker clears the pending marker but leaves the
        // order itself open (see MarketMakerWorker.HandleEventAsync's
        // OrderRejected case).
        t.ClearPendingCancel(1UL);

        Assert.Single(t.FindStale(TimeSpan.FromMinutes(5), t.UtcNow));
    }

    [Fact]
    public void OnTerminal_ClearsPendingCancelMarker()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.RegisterCancelAttempt(cancelClOrdId: 99UL, origClOrdId: 1UL);
        t.OnTerminal(1UL);

        Assert.False(t.HasOpenSide("PETR4", isBuy: true));
        Assert.Equal(0, t.InFlightCount("PETR4"));
    }

    [Fact]
    public void ClearPendingCancelIfMatches_OnlyClearsMatchingAttempt()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.RegisterCancelAttempt(cancelClOrdId: 99UL, origClOrdId: 1UL);

        // A stale (superseded) expected id must not clear a newer pending
        // cancel that has since been registered for the same order.
        t.RegisterCancelAttempt(cancelClOrdId: 100UL, origClOrdId: 1UL);
        t.ClearPendingCancelIfMatches(origClOrdId: 1UL, expectedCancelClOrdId: 99UL);
        Assert.Empty(t.FindStale(TimeSpan.Zero, t.UtcNow)); // still pending (100UL), so not eligible for retry yet

        // Clearing with the CURRENT pending id succeeds.
        t.ClearPendingCancelIfMatches(origClOrdId: 1UL, expectedCancelClOrdId: 100UL);
        Assert.Single(t.FindStale(TimeSpan.Zero, t.UtcNow));
    }

    [Fact]
    public void SetOrderId_MakesIsOwnOrderTrue()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        Assert.False(t.IsOwnOrder(555UL));
        t.SetOrderId(1UL, 555UL);
        Assert.True(t.IsOwnOrder(555UL));
    }

    [Fact]
    public void SetOrderId_UnknownClOrdId_IsANoOp()
    {
        var t = new OrderTracker();
        t.SetOrderId(clOrdId: 999UL, orderId: 555UL);
        Assert.False(t.IsOwnOrder(555UL));
    }

    [Fact]
    public void SetOrderId_ZeroOrderId_IsANoOp()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.SetOrderId(1UL, 0UL);
        Assert.False(t.IsOwnOrder(0UL));
    }

    [Fact]
    public void Close_RemovesOrderIdFromOwnershipSet()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.SetOrderId(1UL, 555UL);
        Assert.True(t.IsOwnOrder(555UL));

        t.OnTerminal(1UL);

        Assert.False(t.IsOwnOrder(555UL));
    }

    [Fact]
    public void TryGetActiveSideOrder_ReturnsCurrentResting()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);

        Assert.True(t.TryGetActiveSideOrder("PETR4", isBuy: true, out var order));
        Assert.Equal(1UL, order.ClOrdId);
        Assert.Equal(30m, order.Price);

        Assert.False(t.TryGetActiveSideOrder("PETR4", isBuy: false, out _));
        Assert.False(t.TryGetActiveSideOrder("VALE3", isBuy: true, out _));
    }

    [Fact]
    public void TryGetActiveSideOrder_AfterSideCloses_ReturnsFalse()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.OnTerminal(1UL);

        Assert.False(t.TryGetActiveSideOrder("PETR4", isBuy: true, out _));
    }

    [Fact]
    public void TryRegisterCancelAttempt_SecondCallForSameOrder_FailsWithoutOverwriting()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);

        // Simulates the staleness guard and the book-driven reactive path
        // racing to cancel the same order concurrently — only the first
        // registration must win.
        Assert.True(t.TryRegisterCancelAttempt(cancelClOrdId: 90UL, origClOrdId: 1UL));
        Assert.False(t.TryRegisterCancelAttempt(cancelClOrdId: 91UL, origClOrdId: 1UL));

        Assert.True(t.TryResolveCancelAttempt(90UL, out var linked));
        Assert.Equal(1UL, linked);
        Assert.False(t.TryResolveCancelAttempt(91UL, out _));
    }

    [Fact]
    public void TryRegisterCancelAttempt_AfterPendingCancelCleared_CanRegisterAgain()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        Assert.True(t.TryRegisterCancelAttempt(cancelClOrdId: 90UL, origClOrdId: 1UL));
        t.ClearPendingCancel(1UL);

        Assert.True(t.TryRegisterCancelAttempt(cancelClOrdId: 91UL, origClOrdId: 1UL));
    }

    [Fact]
    public void TryRegisterCancelAttempt_UnknownOrigClOrdId_ReturnsFalse()
    {
        var t = new OrderTracker();
        Assert.False(t.TryRegisterCancelAttempt(cancelClOrdId: 90UL, origClOrdId: 999UL));
    }

    [Fact]
    public void TryRegisterCancelAttempt_ClosedOrder_ReturnsFalse()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.OnTerminal(1UL);

        // A stale TrackedOrder reference (e.g. from a FindStale/
        // TryGetActiveSideOrder snapshot) must not be able to pick up a
        // fresh cancel attempt after a concurrent fill/cancel has already
        // closed it.
        Assert.False(t.TryRegisterCancelAttempt(cancelClOrdId: 90UL, origClOrdId: 1UL));
        Assert.False(t.TryResolveCancelAttempt(90UL, out _));
    }

    [Fact]
    public void TryRegisterCancelAttempt_WithinMinInterval_ReturnsFalse()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var t = new OrderTracker(clock);
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);

        clock.Advance(TimeSpan.FromSeconds(2)); // clear of the interval since submission too
        Assert.True(t.TryRegisterCancelAttempt(cancelClOrdId: 90UL, origClOrdId: 1UL, TimeSpan.FromSeconds(1)));
        t.ClearPendingCancel(1UL); // e.g. a rejected cancel — free to retry per PendingCancelClOrdId, but not yet per the interval

        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(t.TryRegisterCancelAttempt(cancelClOrdId: 91UL, origClOrdId: 1UL, TimeSpan.FromSeconds(1)));

        clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.True(t.TryRegisterCancelAttempt(cancelClOrdId: 92UL, origClOrdId: 1UL, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ClearPendingCancel_RemovesCancelAttemptCorrelation()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.TryRegisterCancelAttempt(cancelClOrdId: 90UL, origClOrdId: 1UL);

        t.ClearPendingCancel(1UL);

        // The order is still open (cancel was merely rejected, not
        // proven terminal), but the abandoned correlation row for the
        // rejected cancel request itself must not linger forever.
        Assert.True(t.TryGet(1UL, out var order));
        Assert.True(order.IsOpen);
        Assert.False(t.TryResolveCancelAttempt(90UL, out _));
    }

    [Fact]
    public void ClearPendingCancelIfMatches_RemovesCancelAttemptCorrelation()
    {
        var t = new OrderTracker();
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.TryRegisterCancelAttempt(cancelClOrdId: 90UL, origClOrdId: 1UL);

        t.ClearPendingCancelIfMatches(origClOrdId: 1UL, expectedCancelClOrdId: 90UL);

        Assert.False(t.TryResolveCancelAttempt(90UL, out _));
    }

    [Fact]
    public void PruneClosed_RemovesOldClosedOrdersAndTheirCancelAttempts()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var t = new OrderTracker(clock);
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.TryRegisterCancelAttempt(cancelClOrdId: 90UL, origClOrdId: 1UL);
        t.OnTerminal(1UL); // Close() clears PendingCancelClOrdId as part of closing.

        clock.Advance(TimeSpan.FromMinutes(10));
        t.PruneClosed(TimeSpan.FromMinutes(5), t.UtcNow);

        Assert.False(t.TryGet(1UL, out _));
        Assert.False(t.TryResolveCancelAttempt(90UL, out _));
    }

    [Fact]
    public void PruneClosed_SkipsOpenOrder()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var t = new OrderTracker(clock);
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);

        clock.Advance(TimeSpan.FromMinutes(10));
        t.PruneClosed(TimeSpan.FromMinutes(5), t.UtcNow);

        Assert.True(t.TryGet(1UL, out _));
    }

    [Fact]
    public void PruneClosed_SkipsClosedOrderYoungerThanRetention()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var t = new OrderTracker(clock);
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.OnTerminal(1UL);

        clock.Advance(TimeSpan.FromMinutes(1));
        t.PruneClosed(TimeSpan.FromMinutes(5), t.UtcNow);

        Assert.True(t.TryGet(1UL, out _));
    }

    [Fact]
    public void RegisterCancelAttempt_SetsLastCancelAttemptAtUtc()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var t = new OrderTracker(clock);
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);

        clock.Advance(TimeSpan.FromMinutes(1));
        t.RegisterCancelAttempt(cancelClOrdId: 90UL, origClOrdId: 1UL);

        Assert.True(t.TryGet(1UL, out var order));
        Assert.Equal(clock.GetUtcNow(), order.LastCancelAttemptAtUtc);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
