using B3.Trading.Domain;

namespace B3.Trading.Domain.Tests;

/// <summary>
/// Q1.1 (#253). Domain-level invariants for the expanded
/// <see cref="OrderType"/> / <see cref="TimeInForce"/> surface.
///
/// The constructor enforces cross-field rules so a malformed order cannot
/// reach the matching pipeline regardless of which adapter built it
/// (REST, FIXP listener, replay).
/// </summary>
public class OrderQ1ValidationTests
{
    private static readonly EndClientId Owner = new("alice");

    [Fact]
    public void Limit_DefaultsToDay_WithNoStopOrExpiry()
    {
        var o = new Order(1UL, Owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);

        Assert.Equal(TimeInForce.Day, o.TimeInForce);
        Assert.Null(o.StopPrice);
        Assert.Null(o.GoodTillDate);
    }

    [Theory]
    [InlineData(OrderType.StopLoss)]
    [InlineData(OrderType.StopLimit)]
    public void StopVariant_RequiresStopPrice(OrderType type)
    {
        Assert.Throws<ArgumentException>(() =>
            new Order(1UL, Owner, "PETR4", 4321UL, OrderSide.Buy, type, 100, 30m, timeInForce: TimeInForce.Day, stopPrice: null));
    }

    [Theory]
    [InlineData(OrderType.Limit)]
    [InlineData(OrderType.Market)]
    [InlineData(OrderType.MarketWithLeftover)]
    public void NonStopVariant_RejectsStopPrice(OrderType type)
    {
        Assert.Throws<ArgumentException>(() =>
            new Order(1UL, Owner, "PETR4", 4321UL, OrderSide.Buy, type, 100, 30m, timeInForce: TimeInForce.Day, stopPrice: 29m));
    }

    [Fact]
    public void Gtd_RequiresGoodTillDate()
    {
        Assert.Throws<ArgumentException>(() =>
            new Order(1UL, Owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, timeInForce: TimeInForce.GTD, goodTillDate: null));
    }

    [Theory]
    [InlineData(TimeInForce.Day)]
    [InlineData(TimeInForce.IOC)]
    [InlineData(TimeInForce.FOK)]
    [InlineData(TimeInForce.GTC)]
    [InlineData(TimeInForce.AtClose)]
    [InlineData(TimeInForce.GoodForAuction)]
    public void NonGtd_RejectsGoodTillDate(TimeInForce tif)
    {
        Assert.Throws<ArgumentException>(() =>
            new Order(1UL, Owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, timeInForce: tif, goodTillDate: DateTimeOffset.UtcNow.AddDays(1)));
    }

    [Fact]
    public void StopLimit_WithStopAndPrice_Constructs()
    {
        var o = new Order(1UL, Owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.StopLimit, 100, 30m, timeInForce: TimeInForce.Day, stopPrice: 29m);

        Assert.Equal(OrderType.StopLimit, o.Type);
        Assert.Equal(29m, o.StopPrice);
    }

    [Fact]
    public void Gtd_WithDate_Constructs()
    {
        var when = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var o = new Order(1UL, Owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, timeInForce: TimeInForce.GTD, goodTillDate: when);

        Assert.Equal(TimeInForce.GTD, o.TimeInForce);
        Assert.Equal(when, o.GoodTillDate);
    }
}
