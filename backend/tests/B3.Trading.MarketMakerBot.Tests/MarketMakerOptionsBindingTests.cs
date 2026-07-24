using B3.Trading.MarketMakerBot;
using Microsoft.Extensions.Configuration;

namespace B3.Trading.MarketMakerBot.Tests;

public class MarketMakerOptionsBindingTests
{
    [Fact]
    public void LegacyInstrumentConfiguration_KeepsVolatilityDisabledDefaults()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["MarketMaker:Instruments:0:Symbol"] = "PETR4",
            ["MarketMaker:Instruments:0:SecurityId"] = "1",
            ["MarketMaker:Instruments:0:RefPrice"] = "30.00",
            ["MarketMaker:Instruments:0:TickSize"] = "0.01",
            ["MarketMaker:Instruments:0:SpreadTicks"] = "5",
        });

        var volatility = Assert.Single(options.Instruments).VolatilitySpread;
        Assert.False(volatility.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(1), volatility.Window);
        Assert.Equal(120, volatility.MaxSamples);
        Assert.Equal(10, volatility.MinSamples);
        Assert.Equal(1m, volatility.Multiplier);
        Assert.Equal(20, volatility.MaxAdditionalSpreadTicks);
    }

    [Fact]
    public void PartialVolatilityOverride_BindsEnabledAndKeepsOtherDefaults()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["MarketMaker:Instruments:0:Symbol"] = "PETR4",
            ["MarketMaker:Instruments:0:VolatilitySpread:Enabled"] = "true",
        });

        var volatility = Assert.Single(options.Instruments).VolatilitySpread;
        Assert.True(volatility.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(1), volatility.Window);
        Assert.Equal(120, volatility.MaxSamples);
        Assert.Equal(10, volatility.MinSamples);
        Assert.Equal(1m, volatility.Multiplier);
        Assert.Equal(20, volatility.MaxAdditionalSpreadTicks);
    }

    private static MarketMakerBotOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var options = new MarketMakerBotOptions();
        configuration.GetSection(MarketMakerBotOptions.SectionName).Bind(options);
        return options;
    }
}
