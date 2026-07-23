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
    public void FindStale_DoesNotDoubleCount_AfterCancelAttemptRegistered()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var t = new OrderTracker(clock);
        t.TryRegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.RegisterCancelAttempt(cancelClOrdId: 99UL, origClOrdId: 1UL);
        clock.Advance(TimeSpan.FromMinutes(10));

        var stale = t.FindStale(TimeSpan.FromMinutes(5), t.UtcNow);
        Assert.Single(stale);
        Assert.Equal(1UL, stale[0].ClOrdId);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
