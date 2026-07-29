using B3.Trading.SampleBot;

namespace B3.Trading.SampleBot.Tests;

public sealed class SampleBotOptionsValidatorTests
{
    [Fact]
    public void Validate_RejectsMissingLocalPasswordSecret()
    {
        var validator = new SampleBotOptionsValidator();
        var result = validator.Validate(null, new SampleBotOptions
        {
            BaseUrl = "https://trading.local",
            Auth = new SampleBotAuthOptions
            {
                Mode = SampleBotAuthMode.LocalPassword,
                Username = "alice",
            },
        });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("Password", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsMatchingPlatformBaseUrl()
    {
        var validator = new SampleBotOptionsValidator();
        var result = validator.Validate(null, new SampleBotOptions
        {
            BaseUrl = "https://matching-platform:9876",
            Auth = new SampleBotAuthOptions
            {
                Mode = SampleBotAuthMode.InternalToken,
                InternalTradingToken = "jwt",
            },
        });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("matching-platform", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_EnabledStrategyRequiresMarketDataEndpointAndBounds()
    {
        var validator = new SampleBotOptionsValidator();
        var result = validator.Validate(null, new SampleBotOptions
        {
            BaseUrl = "https://trading.local",
            Auth = new SampleBotAuthOptions
            {
                Mode = SampleBotAuthMode.InternalToken,
                InternalTradingToken = "jwt",
            },
            DemoOrder = new DemoOrderOptions
            {
                Enabled = true,
                Symbol = "PETR4",
                Side = "Buy",
                Quantity = 100,
                TickSize = 0.01m,
                PriceOffsetTicks = 0,
                MaxNotional = 0m,
                OrderTimeout = TimeSpan.Zero,
            },
        });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("MarketData:WsUrl", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("PriceOffsetTicks", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("MaxNotional", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("OrderTimeout", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsMatchingPlatformMarketDataUrl()
    {
        var validator = new SampleBotOptionsValidator();
        var result = validator.Validate(null, new SampleBotOptions
        {
            BaseUrl = "https://trading.local",
            Auth = new SampleBotAuthOptions
            {
                Mode = SampleBotAuthMode.InternalToken,
                InternalTradingToken = "jwt",
            },
            MarketData = new SampleBotMarketDataOptions
            {
                WsUrl = "wss://matching-platform/ws",
            },
            DemoOrder = new DemoOrderOptions
            {
                Enabled = true,
                Symbol = "PETR4",
                Side = "Buy",
                Quantity = 100,
                TickSize = 0.01m,
                PriceOffsetTicks = 1,
                MaxNotional = 1000m,
                OrderTimeout = TimeSpan.FromSeconds(5),
            },
        });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("B3MarketDataPlatform", StringComparison.Ordinal));
    }
}
