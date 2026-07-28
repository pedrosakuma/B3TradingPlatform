using Microsoft.Extensions.Options;

namespace B3.Trading.SampleBot;

public sealed class SampleBotOptionsValidator : IValidateOptions<SampleBotOptions>
{
    public ValidateOptionsResult Validate(string? name, SampleBotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("SampleBot:BaseUrl must be an absolute http:// or https:// URI.");
        }
        else if (string.Equals(baseUri.Host, "matching-platform", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("SampleBot:BaseUrl must target B3TradingPlatform, never matching-platform directly.");
        }

        if (options.ReconnectDelay < TimeSpan.Zero)
            failures.Add("SampleBot:ReconnectDelay must be zero or positive.");
        if (options.InitialSnapshotTimeout <= TimeSpan.Zero)
            failures.Add("SampleBot:InitialSnapshotTimeout must be positive.");

        if (!string.IsNullOrWhiteSpace(options.SubAccountId) && options.SubAccountId != options.SubAccountId.Trim())
            failures.Add("SampleBot:SubAccountId must not contain leading or trailing whitespace.");

        ValidateAuth(options.Auth, failures);
        ValidateDemoOrder(options.DemoOrder, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateAuth(SampleBotAuthOptions auth, List<string> failures)
    {
        switch (auth.Mode)
        {
            case SampleBotAuthMode.LocalPassword:
                if (string.IsNullOrWhiteSpace(auth.Username))
                    failures.Add("SampleBot:Auth:Username is required for LocalPassword mode.");
                if (string.IsNullOrWhiteSpace(auth.Password))
                    failures.Add("SampleBot:Auth:Password is required for LocalPassword mode.");
                break;
            case SampleBotAuthMode.ExternalExchange:
                if (string.IsNullOrWhiteSpace(auth.ExternalAccessToken))
                    failures.Add("SampleBot:Auth:ExternalAccessToken is required for ExternalExchange mode.");
                break;
            case SampleBotAuthMode.InternalToken:
                if (string.IsNullOrWhiteSpace(auth.InternalTradingToken))
                    failures.Add("SampleBot:Auth:InternalTradingToken is required for InternalToken mode.");
                break;
            default:
                failures.Add("SampleBot:Auth:Mode is not supported.");
                break;
        }
    }

    private static void ValidateDemoOrder(DemoOrderOptions demoOrder, List<string> failures)
    {
        if (!demoOrder.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(demoOrder.Symbol))
            failures.Add("SampleBot:DemoOrder:Symbol is required when the demo order is enabled.");
        if (!string.Equals(demoOrder.Side, "Buy", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(demoOrder.Side, "Sell", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("SampleBot:DemoOrder:Side must be Buy or Sell.");
        }
        if (demoOrder.Quantity <= 0)
            failures.Add("SampleBot:DemoOrder:Quantity must be positive.");
        if (demoOrder.Price <= 0)
            failures.Add("SampleBot:DemoOrder:Price must be positive.");
        if (demoOrder.CancelDelay < TimeSpan.Zero)
            failures.Add("SampleBot:DemoOrder:CancelDelay must be zero or positive.");
        if (demoOrder.PostWorkflowWait < TimeSpan.Zero)
            failures.Add("SampleBot:DemoOrder:PostWorkflowWait must be zero or positive.");
        if (string.IsNullOrWhiteSpace(demoOrder.IdempotencyKeyPrefix))
            failures.Add("SampleBot:DemoOrder:IdempotencyKeyPrefix must be nonblank.");
    }
}
