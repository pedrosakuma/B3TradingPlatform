using B3.Trading.MarketMakerBot;

namespace B3.Trading.MarketMakerBot.Tests;

public class InventorySkewCalculatorTests
{
    [Fact]
    public void Calculate_Disabled_IgnoresInactiveValuesAndReturnsNoShift()
    {
        var config = new InventorySkewConfig
        {
            Enabled = false,
            FullSkewAtLots = 0,
            MaxSkewTicks = -1m,
        };

        Assert.Equal(default, InventorySkewCalculator.Calculate(config, long.MaxValue, 0, 0m));
    }

    [Fact]
    public void Calculate_FlatInventory_ReturnsNoShift()
    {
        var result = InventorySkewCalculator.Calculate(Enabled(), 0, lotSize: 100, tickSize: 0.01m);

        Assert.Equal(0m, result.NormalizedInventory);
        Assert.Equal(0m, result.SkewTicks);
        Assert.Equal(0m, result.MidShift);
    }

    [Theory]
    [InlineData(500, 0.5, 2.5, -0.025)]
    [InlineData(-500, -0.5, -2.5, 0.025)]
    [InlineData(2000, 1, 5, -0.05)]
    [InlineData(-2000, -1, -5, 0.05)]
    public void Calculate_AppliesFractionalSignedSkewAndSaturates(
        long netQuantity,
        double expectedNormalized,
        double expectedTicks,
        double expectedShift)
    {
        var result = InventorySkewCalculator.Calculate(Enabled(), netQuantity, 100, 0.01m);

        Assert.Equal((decimal)expectedNormalized, result.NormalizedInventory);
        Assert.Equal((decimal)expectedTicks, result.SkewTicks);
        Assert.Equal((decimal)expectedShift, result.MidShift);
    }

    [Fact]
    public void Decide_RoundsOnlyAfterCombiningFractionalShiftAndSpread()
    {
        var skew = InventorySkewCalculator.Calculate(Enabled(), 500, 100, 0.01m);

        var bid = QuoteCalculator.Decide(new QuoteInputs(
            true,
            30.001m,
            QuoteReferenceSource.Explicit,
            skew.MidShift,
            skew.SkewTicks,
            ConfiguredHalfSpread: 0.05m,
            EffectiveHalfSpread: 0.05m,
            AdditionalHalfSpreadTicks: 0,
            TickSize: 0.01m));
        var ask = QuoteCalculator.Decide(new QuoteInputs(
            false,
            30.001m,
            QuoteReferenceSource.Explicit,
            skew.MidShift,
            skew.SkewTicks,
            ConfiguredHalfSpread: 0.05m,
            EffectiveHalfSpread: 0.05m,
            AdditionalHalfSpreadTicks: 0,
            TickSize: 0.01m));

        Assert.Equal(29.93m, bid.Price);
        Assert.Equal(30.03m, ask.Price);
        Assert.Equal(-0.025m, bid.InventoryMidShift);
        Assert.Equal(2.5m, bid.InventorySkewTicks);
    }

    [Fact]
    public void Decide_RoundsOnceAfterCombiningInventoryShiftAndAdaptiveSpread()
    {
        var skew = InventorySkewCalculator.Calculate(Enabled(), 500, 100, 0.01m);

        var bid = QuoteCalculator.Decide(new QuoteInputs(
            true,
            30.001m,
            QuoteReferenceSource.Explicit,
            skew.MidShift,
            skew.SkewTicks,
            ConfiguredHalfSpread: 0.05m,
            EffectiveHalfSpread: 0.07m,
            AdditionalHalfSpreadTicks: 2,
            TickSize: 0.01m));
        var ask = QuoteCalculator.Decide(new QuoteInputs(
            false,
            30.001m,
            QuoteReferenceSource.Explicit,
            skew.MidShift,
            skew.SkewTicks,
            ConfiguredHalfSpread: 0.05m,
            EffectiveHalfSpread: 0.07m,
            AdditionalHalfSpreadTicks: 2,
            TickSize: 0.01m));

        Assert.Equal(29.91m, bid.Price);
        Assert.Equal(30.05m, ask.Price);
    }

    [Fact]
    public void Calculate_CheckedLotsToQuantityConversion_ThrowsOnOverflow()
    {
        var config = Enabled();
        config.FullSkewAtLots = long.MaxValue;

        Assert.Throws<OverflowException>(() =>
            InventorySkewCalculator.Calculate(config, 1, lotSize: 2, tickSize: 0.01m));
    }

    private static InventorySkewConfig Enabled() => new()
    {
        Enabled = true,
        FullSkewAtLots = 10,
        MaxSkewTicks = 5m,
    };
}
