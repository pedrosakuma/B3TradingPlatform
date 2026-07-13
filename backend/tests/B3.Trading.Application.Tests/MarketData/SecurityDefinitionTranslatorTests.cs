using B3.Trading.Application;
using B3.Trading.Application.MarketData;

namespace B3.Trading.Application.Tests.MarketData;

/// <summary>
/// OPT-D (#486). Covers the SDK→OptionMetadata translation that
/// projects raw SBE numerics from <c>SecurityDefinition_12</c> into
/// the typed <see cref="OptionMetadata"/> the application risk
/// pipeline consumes. Translator lives in Application (not Host) so
/// these tests don't carry the SDK package dependency.
/// </summary>
public class SecurityDefinitionTranslatorTests
{
    [Fact]
    public void TryProject_ReturnsNull_WhenMultiplierMissing()
    {
        var opt = SecurityDefinitionRegistry.TryProject(
            contractMultiplier: null, maturityDate: 20271231, putOrCall: 1,
            exerciseStyle: 2, strikePrice: 200000, priceDivisor: 10000, underlyingAsset: "PETR");
        Assert.Null(opt);
    }

    [Fact]
    public void TryProject_ReturnsNull_WhenMaturityMissing()
    {
        var opt = SecurityDefinitionRegistry.TryProject(
            contractMultiplier: 100, maturityDate: null, putOrCall: 1,
            exerciseStyle: 2, strikePrice: 200000, priceDivisor: 10000, underlyingAsset: "PETR");
        Assert.Null(opt);
    }

    [Fact]
    public void TryProject_ReturnsNull_WhenPutOrCallMissing()
    {
        var opt = SecurityDefinitionRegistry.TryProject(
            contractMultiplier: 100, maturityDate: 20271231, putOrCall: null,
            exerciseStyle: 2, strikePrice: 200000, priceDivisor: 10000, underlyingAsset: "PETR");
        Assert.Null(opt);
    }

    [Fact]
    public void TryProject_ReturnsNull_OnUnknownPutOrCall()
    {
        // Defensive: any value outside {0, 1} drops the option block
        // (better no metadata than wrong P/C — a Put mis-projected as
        // Call would mis-classify hedges).
        var opt = SecurityDefinitionRegistry.TryProject(
            contractMultiplier: 100, maturityDate: 20271231, putOrCall: 9,
            exerciseStyle: 2, strikePrice: 200000, priceDivisor: 10000, underlyingAsset: "PETR");
        Assert.Null(opt);
    }

    [Fact]
    public void TryProject_DefaultsExerciseToAmerican_WhenMissing()
    {
        var opt = SecurityDefinitionRegistry.TryProject(
            contractMultiplier: 100, maturityDate: 20271231, putOrCall: 1,
            exerciseStyle: null, strikePrice: 200000, priceDivisor: 10000, underlyingAsset: "PETR");
        Assert.NotNull(opt);
        Assert.Equal(ExerciseStyle.American, opt!.Value.ExerciseStyle);
    }

    [Fact]
    public void TryProject_MapsExerciseStyleCodes()
    {
        var american = SecurityDefinitionRegistry.TryProject(
            100, 20271231, 1, exerciseStyle: 2, 200000, 10000, "PETR");
        var european = SecurityDefinitionRegistry.TryProject(
            100, 20271231, 1, exerciseStyle: 1, 200000, 10000, "PETR");
        Assert.Equal(ExerciseStyle.American, american!.Value.ExerciseStyle);
        Assert.Equal(ExerciseStyle.European, european!.Value.ExerciseStyle);
    }

    [Fact]
    public void TryProject_ReturnsNull_OnUnknownExerciseStyle()
    {
        var opt = SecurityDefinitionRegistry.TryProject(
            100, 20271231, 1, exerciseStyle: 9, 200000, 10000, "PETR");
        Assert.Null(opt);
    }

    [Fact]
    public void TryProject_ScalesStrikeByPriceDivisor()
    {
        var opt = SecurityDefinitionRegistry.TryProject(
            100, 20271231, 1, 2, strikePrice: 200000, priceDivisor: 10000, "PETR");
        Assert.NotNull(opt);
        Assert.Equal(20m, opt!.Value.StrikePrice);
    }

    [Fact]
    public void TryProject_DefaultsPriceDivisor_To10000_WhenMissing()
    {
        // B3 default 4-decimal-place grid — venue routinely omits the
        // PriceDivisor field on stable instruments.
        var opt = SecurityDefinitionRegistry.TryProject(
            100, 20271231, 1, 2, strikePrice: 350000, priceDivisor: null, "PETR");
        Assert.NotNull(opt);
        Assert.Equal(35m, opt!.Value.StrikePrice);
    }

    [Fact]
    public void TryProject_StrikeIsZero_WhenStrikeMissing()
    {
        var opt = SecurityDefinitionRegistry.TryProject(
            100, 20271231, 0, 2, strikePrice: null, priceDivisor: 10000, "PETR");
        Assert.NotNull(opt);
        Assert.Equal(0m, opt!.Value.StrikePrice);
    }

    [Fact]
    public void TryProject_ParsesMaturityYyyyMmDd_To_DateOnly()
    {
        var opt = SecurityDefinitionRegistry.TryProject(
            100, maturityDate: 20271231, 1, 2, 200000, 10000, "PETR");
        Assert.NotNull(opt);
        Assert.Equal(new DateOnly(2027, 12, 31), opt!.Value.ExpirationDate);
    }

    [Theory]
    [InlineData(20270000L)] // month 0
    [InlineData(20271300L)] // month 13
    [InlineData(20270132L)] // day 32
    [InlineData(19691231L)] // year < 1970
    [InlineData(21010101L)] // year > 2100
    [InlineData(20270230L)] // Feb 30 (calendar invalid)
    public void TryProject_ReturnsNull_OnMalformedMaturity(long bad)
    {
        var opt = SecurityDefinitionRegistry.TryProject(
            100, maturityDate: bad, 1, 2, 200000, 10000, "PETR");
        Assert.Null(opt);
    }

    [Fact]
    public void TryProject_HappyPath_PETRL200_Call_European()
    {
        var opt = SecurityDefinitionRegistry.TryProject(
            contractMultiplier: 100,
            maturityDate: 20271015,
            putOrCall: 1,
            exerciseStyle: 1,
            strikePrice: 200000,
            priceDivisor: 10000,
            underlyingAsset: "PETR");

        Assert.NotNull(opt);
        var o = opt!.Value;
        Assert.Equal(100m, o.ContractMultiplier);
        Assert.Equal(20m, o.StrikePrice);
        Assert.Equal(PutOrCall.Call, o.PutOrCall);
        Assert.Equal(ExerciseStyle.European, o.ExerciseStyle);
        Assert.Equal(new DateOnly(2027, 10, 15), o.ExpirationDate);
        Assert.Equal("PETR", o.UnderlyingSymbol);
        Assert.Equal(OptPayoutType.Vanilla, o.OptPayoutType);
    }

    [Fact]
    public void TryProject_HappyPath_PutLeg()
    {
        var opt = SecurityDefinitionRegistry.TryProject(
            100, 20281001, putOrCall: 0, exerciseStyle: 2, 250000, 10000, "VALE");
        Assert.NotNull(opt);
        Assert.Equal(PutOrCall.Put, opt!.Value.PutOrCall);
        Assert.Equal("VALE", opt.Value.UnderlyingSymbol);
    }

    [Fact]
    public void TryProject_NullUnderlying_DegradesToEmptyString()
    {
        var opt = SecurityDefinitionRegistry.TryProject(
            100, 20271231, 1, 2, 200000, 10000, underlyingAsset: null);
        Assert.NotNull(opt);
        Assert.Equal(string.Empty, opt!.Value.UnderlyingSymbol);
    }
}
