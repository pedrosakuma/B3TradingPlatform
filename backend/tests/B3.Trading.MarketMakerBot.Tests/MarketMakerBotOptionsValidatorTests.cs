using B3.Trading.MarketMakerBot;

namespace B3.Trading.MarketMakerBot.Tests;

public class MarketMakerBotOptionsValidatorTests
{
    private readonly MarketMakerBotOptionsValidator _validator = new();

    [Fact]
    public void Validate_DisabledSkew_DoesNotValidateInactiveSkewValues()
    {
        var options = Options();
        options.Instruments[0].InventorySkew = new InventorySkewConfig
        {
            Enabled = false,
            FullSkewAtLots = 0,
            MaxSkewTicks = -1m,
        };

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-1, 5)]
    [InlineData(10, -1)]
    public void Validate_EnabledSkew_RejectsInvalidBandOrMaximum(long fullSkewAtLots, double maxSkewTicks)
    {
        var options = Options();
        options.Instruments[0].InventorySkew = new InventorySkewConfig
        {
            Enabled = true,
            FullSkewAtLots = fullSkewAtLots,
            MaxSkewTicks = (decimal)maxSkewTicks,
        };

        Assert.False(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_EnabledSkew_RejectsLotsToQuantityOverflow()
    {
        var options = Options();
        options.Instruments[0].LotSize = 2;
        options.Instruments[0].InventorySkew = new InventorySkewConfig
        {
            Enabled = true,
            FullSkewAtLots = long.MaxValue,
            MaxSkewTicks = 5m,
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("supported quantity range", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsDuplicateSymbolsUsedAsStateKeys()
    {
        var options = Options();
        options.Instruments.Add(Instrument("PETR4"));

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("duplicated", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsEnabledBoundaryValues()
    {
        var options = Options();
        options.Instruments[0].InventorySkew = new InventorySkewConfig
        {
            Enabled = true,
            FullSkewAtLots = 1,
            MaxSkewTicks = 0m,
        };

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    private static MarketMakerBotOptions Options() => new()
    {
        Instruments = [Instrument("PETR4")],
    };

    private static InstrumentConfig Instrument(string symbol) => new()
    {
        Symbol = symbol,
        SecurityId = 1,
        RefPrice = 30m,
        TickSize = 0.01m,
        LotSize = 100,
        QuoteLots = 1,
        SpreadTicks = 5,
    };
}
