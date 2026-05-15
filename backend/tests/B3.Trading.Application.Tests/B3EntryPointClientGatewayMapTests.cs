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
}
