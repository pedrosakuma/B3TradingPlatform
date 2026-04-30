using B3.Trading.Domain;

namespace B3.Trading.Domain.Tests;

public class PositionTests
{
    [Fact]
    public void Buys_AverageEntryPriceIsWeighted()
    {
        var p = new Position(new EndClientId("alice"), "PETR4");

        p.ApplyFill(OrderSide.Buy, 100, 30m);
        p.ApplyFill(OrderSide.Buy, 100, 32m);

        Assert.Equal(200, p.NetQuantity);
        Assert.Equal(31m, p.AverageEntryPrice);
    }

    [Fact]
    public void OffsettingFill_ReducesNet_KeepsAveragePrice()
    {
        var p = new Position(new EndClientId("alice"), "PETR4");

        p.ApplyFill(OrderSide.Buy, 100, 30m);
        p.ApplyFill(OrderSide.Sell, 40, 31m);

        Assert.Equal(60, p.NetQuantity);
        Assert.Equal(30m, p.AverageEntryPrice);
    }

    [Fact]
    public void Flatten_ResetsAverage()
    {
        var p = new Position(new EndClientId("alice"), "PETR4");

        p.ApplyFill(OrderSide.Buy, 100, 30m);
        p.ApplyFill(OrderSide.Sell, 100, 31m);

        Assert.Equal(0, p.NetQuantity);
        Assert.Equal(0m, p.AverageEntryPrice);
    }
}
