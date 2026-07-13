using B3.Trading.Application.MarketData;

namespace B3.Trading.Application.Tests.MarketData;

/// <summary>
/// OPT-E (#487). Translator unit tests for <see cref="PriceBandRegistry.TryProject"/>.
/// SDK-agnostic on purpose — exercises every PriceLimitType branch
/// plus the malformed-frame guards so a degraded venue feed can't
/// poison the registry.
/// </summary>
public sealed class PriceBandTranslatorTests
{
    [Fact]
    public void PriceUnit_AbsoluteBand_HappyPath_Projected()
    {
        var ok = PriceBandRegistry.TryProject(
            lowerBand: 24.50m, upperBand: 26.75m, priceLimitType: 1,
            out var lower, out var upper);

        Assert.True(ok);
        Assert.Equal(24.50m, lower);
        Assert.Equal(26.75m, upper);
    }

    [Fact]
    public void NullPriceLimitType_TreatedAsPriceUnit_Projected()
    {
        // B3 cash-market dumps omit PriceLimitType — bound is implicitly
        // absolute. Dropping these frames would leave equities unguarded.
        var ok = PriceBandRegistry.TryProject(
            lowerBand: 10m, upperBand: 12m, priceLimitType: null,
            out var lower, out var upper);

        Assert.True(ok);
        Assert.Equal(10m, lower);
        Assert.Equal(12m, upper);
    }

    [Theory]
    [InlineData(2)] // TICKS — requires tick-provider context to resolve
    [InlineData(3)] // PERCENTAGE — requires reference price to resolve
    [InlineData(99)] // unknown
    public void RelativePriceLimitTypes_Dropped(long priceLimitType)
    {
        var ok = PriceBandRegistry.TryProject(
            lowerBand: 10m, upperBand: 12m, priceLimitType: priceLimitType,
            out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void MissingLowerBand_Dropped()
    {
        var ok = PriceBandRegistry.TryProject(
            lowerBand: null, upperBand: 12m, priceLimitType: 1,
            out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void MissingUpperBand_Dropped()
    {
        var ok = PriceBandRegistry.TryProject(
            lowerBand: 10m, upperBand: null, priceLimitType: 1,
            out _, out _);

        Assert.False(ok);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public void NonPositiveBound_Dropped(decimal lb, decimal ub)
    {
        var ok = PriceBandRegistry.TryProject(
            lowerBand: lb, upperBand: ub, priceLimitType: 1,
            out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void InvertedBand_LowerAboveUpper_Dropped()
    {
        // Malformed venue frame — must not poison the registry.
        var ok = PriceBandRegistry.TryProject(
            lowerBand: 12m, upperBand: 10m, priceLimitType: 1,
            out _, out _);

        Assert.False(ok);
    }
}
