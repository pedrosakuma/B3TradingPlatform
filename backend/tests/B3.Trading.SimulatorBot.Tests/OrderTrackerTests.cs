using B3.Trading.SimulatorBot;

namespace B3.Trading.SimulatorBot.Tests;

public class OrderTrackerTests
{
    [Fact]
    public void RegisterSubmit_ReflectedInInFlightCount()
    {
        var t = new OrderTracker();
        t.RegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.RegisterSubmit(2UL, "PETR4", 30m, 100, isBuy: true);
        t.RegisterSubmit(3UL, "VALE3", 70m, 100, isBuy: false);
        Assert.Equal(2, t.InFlightCount("PETR4"));
        Assert.Equal(1, t.InFlightCount("VALE3"));
        Assert.Equal(0, t.InFlightCount("MGLU3"));
    }

    [Fact]
    public void OnTrade_LeavesZero_ClosesOrder()
    {
        var t = new OrderTracker();
        t.RegisterSubmit(1UL, "PETR4", 30m, 100, true);
        t.OnTrade(1UL, leaves: 0);
        Assert.Equal(0, t.InFlightCount("PETR4"));
    }

    [Fact]
    public void OnTrade_PartialFill_StaysOpen()
    {
        var t = new OrderTracker();
        t.RegisterSubmit(1UL, "PETR4", 30m, 200, true);
        t.OnTrade(1UL, leaves: 100);
        Assert.Equal(1, t.InFlightCount("PETR4"));
    }

    [Fact]
    public void OnTerminal_ClosesOrder()
    {
        var t = new OrderTracker();
        t.RegisterSubmit(1UL, "PETR4", 30m, 100, true);
        t.OnTerminal(1UL);
        Assert.Equal(0, t.InFlightCount("PETR4"));
    }

    [Fact]
    public void OnAccepted_NoFill_StaysOpenWithLeaves()
    {
        var t = new OrderTracker();
        t.RegisterSubmit(1UL, "PETR4", 30m, 100, true);
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
    public void SnapshotStaleOpen_OnlyReturnsOldEnoughOpenOrders()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var t = new OrderTracker(clock);
        t.RegisterSubmit(1UL, "PETR4", 30m, 100, true);  // submitted now
        clock.Advance(TimeSpan.FromSeconds(40));
        t.RegisterSubmit(2UL, "PETR4", 30m, 100, true);  // submitted at +40s
        clock.Advance(TimeSpan.FromSeconds(10));         // now at +50s

        // Threshold = 30s ⇒ only order #1 (submitted 50s ago) qualifies.
        var stale = t.SnapshotStaleOpen(TimeSpan.FromSeconds(30));
        Assert.Single(stale);
        Assert.Equal(1UL, stale[0].ClOrdId);
    }

    [Fact]
    public void SnapshotStaleOpen_SkipsClosedOrders()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var t = new OrderTracker(clock);
        t.RegisterSubmit(1UL, "PETR4", 30m, 100, true);
        t.OnTerminal(1UL);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(t.SnapshotStaleOpen(TimeSpan.FromSeconds(30)));
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
