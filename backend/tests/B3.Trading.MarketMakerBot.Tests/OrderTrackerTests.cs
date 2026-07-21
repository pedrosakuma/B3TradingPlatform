using B3.Trading.MarketMakerBot;

namespace B3.Trading.MarketMakerBot.Tests;

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
    public void HasOpenSide_TrueWhileResting_FalseAfterTerminal()
    {
        var t = new OrderTracker();
        Assert.False(t.HasOpenSide("PETR4", isBuy: true));

        t.RegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        Assert.True(t.HasOpenSide("PETR4", isBuy: true));
        Assert.False(t.HasOpenSide("PETR4", isBuy: false));

        t.OnTerminal(1UL);
        Assert.False(t.HasOpenSide("PETR4", isBuy: true));
    }

    [Fact]
    public void HasOpenSide_DistinguishesSymbolAndSide()
    {
        var t = new OrderTracker();
        t.RegisterSubmit(1UL, "PETR4", 30m, 100, isBuy: true);
        t.RegisterSubmit(2UL, "PETR4", 31m, 100, isBuy: false);
        t.RegisterSubmit(3UL, "VALE3", 70m, 100, isBuy: true);

        Assert.True(t.HasOpenSide("PETR4", isBuy: true));
        Assert.True(t.HasOpenSide("PETR4", isBuy: false));
        Assert.True(t.HasOpenSide("VALE3", isBuy: true));
        Assert.False(t.HasOpenSide("VALE3", isBuy: false));
        Assert.False(t.HasOpenSide("MGLU3", isBuy: true));
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
