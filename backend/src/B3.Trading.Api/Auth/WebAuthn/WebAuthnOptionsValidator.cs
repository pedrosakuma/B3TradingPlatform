using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Auth.WebAuthn;

internal sealed class WebAuthnOptionsValidator : IValidateOptions<WebAuthnOptions>
{
    public ValidateOptionsResult Validate(string? name, WebAuthnOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RelyingPartyId))
            return ValidateOptionsResult.Fail("WebAuthn relying-party ID is required.");
        if (!string.Equals(options.RelyingPartyId, "localhost", StringComparison.OrdinalIgnoreCase)
            && Uri.CheckHostName(options.RelyingPartyId) != UriHostNameType.Dns)
            return ValidateOptionsResult.Fail("WebAuthn relying-party ID must be a DNS name.");
        if (options.ChallengeTtl <= TimeSpan.Zero)
            return ValidateOptionsResult.Fail("WebAuthn challenge TTL must be positive.");
        if (options.TimeoutMilliseconds == 0)
            return ValidateOptionsResult.Fail("WebAuthn browser timeout must be positive.");
        if (options.Origins.Count == 0)
            return ValidateOptionsResult.Fail("At least one WebAuthn origin is required.");

        foreach (var configuredOrigin in options.Origins)
        {
            if (!Uri.TryCreate(configuredOrigin, UriKind.Absolute, out var origin)
                || (origin.Scheme != Uri.UriSchemeHttps
                    && !(origin.Scheme == Uri.UriSchemeHttp
                        && string.Equals(origin.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
                || origin.AbsolutePath != "/"
                || !string.IsNullOrEmpty(origin.Query)
                || !string.IsNullOrEmpty(origin.Fragment)
                || !string.IsNullOrEmpty(origin.UserInfo))
            {
                return ValidateOptionsResult.Fail(
                    $"WebAuthn origin '{configuredOrigin}' must be HTTPS (HTTP is allowed only for localhost).");
            }

            if (!string.Equals(origin.Host, options.RelyingPartyId, StringComparison.OrdinalIgnoreCase)
                && !origin.Host.EndsWith(
                    "." + options.RelyingPartyId, StringComparison.OrdinalIgnoreCase))
            {
                return ValidateOptionsResult.Fail(
                    $"WebAuthn origin host '{origin.Host}' is outside relying-party ID '{options.RelyingPartyId}'.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
