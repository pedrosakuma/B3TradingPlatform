using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using Up = B3.EntryPoint.Client.Models;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Q1.1 (#253). Pins the Domain → SDK enum tables exposed by
/// <see cref="B3EntryPointClientGateway"/>. The mapping is the entire
/// outbound contract for the new order surface — a regression here
/// would silently mis-route Stop / GTD / IOC orders to the venue.
/// </summary>
public class B3EntryPointClientGatewayMapTests
{
    [Theory]
    [InlineData(OrderType.Limit, Up.OrderType.Limit)]
    [InlineData(OrderType.Market, Up.OrderType.Market)]
    [InlineData(OrderType.StopLoss, Up.OrderType.StopLoss)]
    [InlineData(OrderType.StopLimit, Up.OrderType.StopLimit)]
    [InlineData(OrderType.MarketWithLeftover, Up.OrderType.MarketWithLeftoverAsLimit)]
    public void MapOrderType_CoversAllDomainValues(OrderType domain, Up.OrderType sdk)
    {
        Assert.Equal(sdk, B3EntryPointClientGateway.MapOrderType(domain));
    }

    [Theory]
    [InlineData(TimeInForce.Day, Up.TimeInForce.Day)]
    [InlineData(TimeInForce.IOC, Up.TimeInForce.ImmediateOrCancel)]
    [InlineData(TimeInForce.FOK, Up.TimeInForce.FillOrKill)]
    [InlineData(TimeInForce.GTC, Up.TimeInForce.GoodTillCancel)]
    [InlineData(TimeInForce.GTD, Up.TimeInForce.GoodTillDate)]
    [InlineData(TimeInForce.AtClose, Up.TimeInForce.AtTheClose)]
    [InlineData(TimeInForce.GoodForAuction, Up.TimeInForce.GoodForAuction)]
    public void MapTimeInForce_CoversAllDomainValues(TimeInForce domain, Up.TimeInForce sdk)
    {
        Assert.Equal(sdk, B3EntryPointClientGateway.MapTimeInForce(domain));
    }

    [Fact]
    public void MapOrderType_UnmappedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => B3EntryPointClientGateway.MapOrderType((OrderType)999));
    }

    [Fact]
    public void MapTimeInForce_UnmappedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => B3EntryPointClientGateway.MapTimeInForce((TimeInForce)999));
    }

    [Fact]
    public void BuildNewOrderRequest_PlainOrder_LeavesMaxFloorNull()
    {
        // Q3.4 (#284). Full-disclosure (no reserve) orders must not
        // accidentally populate MaxFloor — a non-null value would
        // cause the venue to expose only a slice of the order on the
        // public book.
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM-A");

        var req = B3EntryPointClientGateway.BuildNewOrderRequest(order);

        Assert.Null(req.MaxFloor);
        Assert.Equal(100UL, req.OrderQty);
    }

    [Theory]
    [InlineData(10L, 100L)]
    [InlineData(1L, 1L)]
    [InlineData(50L, 50L)]
    public void BuildNewOrderRequest_Iceberg_MapsDisplayQtyToMaxFloor(long displayQty, long quantity)
    {
        // Q3.4 (#284). Native iceberg path: DisplayQty → MaxFloor on
        // the SDK request. Pin both bounds (min=1, equal-to-qty) to
        // detect any future ulong-cast regression.
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            quantity, 30m, "FIRM-A",
            displayQty: displayQty, displayResetPolicy: DisplayResetPolicy.Always);

        var req = B3EntryPointClientGateway.BuildNewOrderRequest(order);

        Assert.Equal((ulong)displayQty, req.MaxFloor);
        Assert.Equal((ulong)quantity, req.OrderQty);
    }
}
