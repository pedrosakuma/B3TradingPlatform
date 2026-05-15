using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Trading.Domain;
using B3.Trading.EntryPointListener.Hosting;
using DomainTif = B3.Trading.Domain.TimeInForce;
using SbeTif = B3.Entrypoint.Fixp.Sbe.V6.TimeInForce;

namespace B3.Trading.EntryPointListener.Tests.Hosting;

/// <summary>
/// Q1.1 (#253). Pins the SBE → Domain mapping tables consumed by
/// the FIXP listener. A regression here means the matching pipeline
/// would silently mis-classify Stop / GTD / IOC orders coming from
/// real B3 EntryPoint sessions.
/// </summary>
public class FixpOrderAdapterMappingTests
{
    [Theory]
    [InlineData(OrdType.LIMIT, OrderType.Limit)]
    [InlineData(OrdType.MARKET, OrderType.Market)]
    [InlineData(OrdType.STOP_LOSS, OrderType.StopLoss)]
    [InlineData(OrdType.STOP_LIMIT, OrderType.StopLimit)]
    [InlineData(OrdType.MARKET_WITH_LEFTOVER_AS_LIMIT, OrderType.MarketWithLeftover)]
    public void TryMapOrdType_KnownValues(OrdType raw, OrderType expected)
    {
        Assert.True(FixpOrderAdapter.TryMapOrdType(raw, out var got));
        Assert.Equal(expected, got);
    }

    [Fact]
    public void TryMapOrdType_UnknownValue_Rejected()
    {
        Assert.False(FixpOrderAdapter.TryMapOrdType((OrdType)200, out _));
    }

    [Theory]
    [InlineData(SbeTif.DAY, DomainTif.Day)]
    [InlineData(SbeTif.GOOD_TILL_CANCEL, DomainTif.GTC)]
    [InlineData(SbeTif.IMMEDIATE_OR_CANCEL, DomainTif.IOC)]
    [InlineData(SbeTif.FILL_OR_KILL, DomainTif.FOK)]
    [InlineData(SbeTif.GOOD_TILL_DATE, DomainTif.GTD)]
    [InlineData(SbeTif.AT_THE_CLOSE, DomainTif.AtClose)]
    [InlineData(SbeTif.GOOD_FOR_AUCTION, DomainTif.GoodForAuction)]
    public void TryMapTimeInForce_KnownValues(SbeTif raw, DomainTif expected)
    {
        Assert.True(FixpOrderAdapter.TryMapTimeInForce(raw, out var got));
        Assert.Equal(expected, got);
    }

    [Fact]
    public void TryMapTimeInForce_UnknownValue_Rejected()
    {
        Assert.False(FixpOrderAdapter.TryMapTimeInForce((SbeTif)200, out _));
    }
}
