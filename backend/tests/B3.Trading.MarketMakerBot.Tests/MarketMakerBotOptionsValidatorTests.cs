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

    [Fact]
    public void Validate_DisabledVolatility_DoesNotValidateInactiveValues()
    {
        var options = Options();
        options.Instruments[0].VolatilitySpread = new VolatilitySpreadConfig
        {
            Enabled = false,
            Window = TimeSpan.Zero,
            MaxSamples = 0,
            MinSamples = -1,
            Multiplier = 0m,
            MaxAdditionalSpreadTicks = -1,
        };

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [MemberData(nameof(InvalidVolatilityConfigurations))]
    public void Validate_EnabledVolatility_RejectsInvalidValues(VolatilitySpreadConfig config)
    {
        var options = Options();
        options.Instruments[0].VolatilitySpread = config;

        Assert.False(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_EnabledVolatility_RejectsCheckedSpreadArithmeticOverflow()
    {
        var options = Options();
        options.Instruments[0].SpreadTicks = int.MaxValue;
        options.Instruments[0].VolatilitySpread = EnabledVolatility(maxAdditionalSpreadTicks: 1);

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("supported price range", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsEnabledVolatility()
    {
        var options = Options();
        options.Instruments[0].VolatilitySpread = EnabledVolatility();

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/ws")]
    public void Validate_PauseAndCancel_RequiresAbsoluteWsUrl(string? wsUrl)
    {
        var options = Options();
        options.MarketData = new MarketDataOptions
        {
            FeedLossPolicy = FeedLossPolicy.PauseAndCancel,
            WsUrl = wsUrl,
            MaxReferenceAge = TimeSpan.FromSeconds(10),
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("WsUrl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_PauseAndCancel_RequiresPositiveMaxReferenceAge(int seconds)
    {
        var options = Options();
        options.MarketData = new MarketDataOptions
        {
            FeedLossPolicy = FeedLossPolicy.PauseAndCancel,
            WsUrl = "wss://marketdata.test/ws",
            MaxReferenceAge = TimeSpan.FromSeconds(seconds),
        };

        Assert.False(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_StaticRefPrice_AllowsNoFeedAndInactiveFreshness()
    {
        var options = Options();
        options.MarketData = new MarketDataOptions
        {
            FeedLossPolicy = FeedLossPolicy.StaticRefPrice,
            WsUrl = null,
            MaxReferenceAge = TimeSpan.Zero,
        };

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_AcceptsPauseAndCancelWithAbsoluteFeedAndPositiveAge()
    {
        var options = Options();
        options.MarketData = new MarketDataOptions
        {
            FeedLossPolicy = FeedLossPolicy.PauseAndCancel,
            WsUrl = "wss://marketdata.test/ws",
            MaxReferenceAge = TimeSpan.FromSeconds(10),
        };

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_RejectsUnknownFeedLossPolicy()
    {
        var options = Options();
        options.MarketData.FeedLossPolicy = (FeedLossPolicy)99;

        Assert.False(_validator.Validate(null, options).Succeeded);
    }

    public static TheoryData<VolatilitySpreadConfig> InvalidVolatilityConfigurations() => new()
    {
        EnabledVolatility(window: TimeSpan.Zero),
        EnabledVolatility(maxSamples: 0),
        EnabledVolatility(minSamples: 0),
        EnabledVolatility(maxSamples: 10, minSamples: 11),
        EnabledVolatility(multiplier: 0m),
        EnabledVolatility(maxAdditionalSpreadTicks: -1),
    };

    private static VolatilitySpreadConfig EnabledVolatility(
        TimeSpan? window = null,
        int maxSamples = 10,
        int minSamples = 2,
        decimal multiplier = 1m,
        int maxAdditionalSpreadTicks = 5) => new()
        {
            Enabled = true,
            Window = window ?? TimeSpan.FromMinutes(1),
            MaxSamples = maxSamples,
            MinSamples = minSamples,
            Multiplier = multiplier,
            MaxAdditionalSpreadTicks = maxAdditionalSpreadTicks,
        };

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
