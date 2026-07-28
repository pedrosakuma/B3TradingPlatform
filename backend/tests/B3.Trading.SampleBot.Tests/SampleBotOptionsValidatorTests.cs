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
}
