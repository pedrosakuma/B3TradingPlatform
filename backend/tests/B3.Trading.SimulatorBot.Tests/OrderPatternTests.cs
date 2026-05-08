using B3.Trading.SimulatorBot;

namespace B3.Trading.SimulatorBot.Tests;

public class OrderPatternTests
{
    private static InstrumentConfig PETR4 => new()
    {
        Symbol = "PETR4",
        SecurityId = 4321UL,
        RefPrice = 30m,
        TickSize = 0.01m,
        LotSize = 100,
        MinLots = 1,
        MaxLots = 5,
    };

    [Fact]
    public void Next_RespectsInFlightCap()
    {
        var rng = new Random(42);
        Assert.Null(OrderPattern.Next(rng, PETR4, 0.25, inFlight: 5, cap: 5));
        Assert.Null(OrderPattern.Next(rng, PETR4, 0.25, inFlight: 6, cap: 5));
    }

    [Fact]
    public void Next_QuantityIsLotMultipleWithinRange()
    {
        var rng = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            var d = OrderPattern.Next(rng, PETR4, 0.25, inFlight: 0, cap: 10);
            Assert.NotNull(d);
            Assert.Equal(0, d!.Value.Quantity % PETR4.LotSize);
            var lots = d.Value.Quantity / PETR4.LotSize;
            Assert.InRange(lots, PETR4.MinLots, PETR4.MaxLots);
        }
    }

    [Fact]
    public void Next_PriceIsTickAlignedAndPositive()
    {
        var rng = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            var d = OrderPattern.Next(rng, PETR4, 0.25, inFlight: 0, cap: 10);
            Assert.NotNull(d);
            Assert.True(d!.Value.Price > 0m);
            // Tick alignment: price/tickSize must be (close to) integral.
            var ticks = d.Value.Price / PETR4.TickSize;
            Assert.Equal(0m, ticks - Math.Round(ticks));
        }
    }

    [Fact]
    public void Next_CrossProbabilityZero_NeverCrossesMid()
    {
        // With crossProbability=0 every order is passive (own side of mid).
        var rng = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            var d = OrderPattern.Next(rng, PETR4, crossProbability: 0.0,
                inFlight: 0, cap: 10);
            Assert.NotNull(d);
            if (d!.Value.IsBuy) Assert.True(d.Value.Price <= PETR4.RefPrice);
            else Assert.True(d.Value.Price >= PETR4.RefPrice);
        }
    }

    [Fact]
    public void Next_CrossProbabilityOne_AlwaysCrossesMid()
    {
        var rng = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            var d = OrderPattern.Next(rng, PETR4, crossProbability: 1.0,
                inFlight: 0, cap: 10);
            Assert.NotNull(d);
            if (d!.Value.IsBuy) Assert.True(d.Value.Price > PETR4.RefPrice);
            else Assert.True(d.Value.Price < PETR4.RefPrice);
        }
    }

    [Fact]
    public void Next_DeterministicForSeededRandom()
    {
        var a = new Random(123);
        var b = new Random(123);
        for (var i = 0; i < 50; i++)
        {
            var da = OrderPattern.Next(a, PETR4, 0.3, 0, 10);
            var db = OrderPattern.Next(b, PETR4, 0.3, 0, 10);
            Assert.Equal(da, db);
        }
    }

    [Fact]
    public void RoundToTick_RoundsHalfAwayFromZero()
    {
        Assert.Equal(0.05m, OrderPattern.RoundToTick(0.04999m, 0.01m));
        Assert.Equal(0.05m, OrderPattern.RoundToTick(0.05m, 0.01m));
        Assert.Equal(30.13m, OrderPattern.RoundToTick(30.131m, 0.01m));
    }

    [Fact]
    public void RoundToTick_ZeroTick_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderPattern.RoundToTick(1m, 0m));
    }
}
