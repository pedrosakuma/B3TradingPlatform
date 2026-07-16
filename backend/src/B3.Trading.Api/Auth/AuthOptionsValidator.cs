using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Auth;

internal sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var failures = new List<string>();
        AuthModeKind mode;
        try
        {
            mode = options.ResolveMode();
        }
        catch (InvalidOperationException ex)
        {
            failures.Add(ex.Message);
            mode = AuthModeKind.Local;
        }

        if (mode == AuthModeKind.Local && options.IsExchangeEnabled())
            failures.Add("Trading:Auth:ExchangeEnabled cannot be true in Local mode.");

        if (mode == AuthModeKind.Entra)
        {
            if (options.IsLocalLoginEnabled())
                failures.Add("Trading:Auth:LocalLoginEnabled cannot be true in Entra mode.");
            if (options.IsSignupEnabled())
                failures.Add("Trading:Auth:SignupEnabled cannot be true in Entra mode.");
            if (options.IsTotpEnabled())
                failures.Add("Trading:Auth:TotpEnabled cannot be true in Entra mode.");
            if (!options.IsExchangeEnabled())
                failures.Add("Trading:Auth:ExchangeEnabled cannot be false in Entra mode.");
        }
        else if (mode == AuthModeKind.Hybrid
            && options.LocalLoginEnabled == false
            && options.TotpEnabled == true)
        {
            failures.Add("Trading:Auth:TotpEnabled cannot be true when Trading:Auth:LocalLoginEnabled is false in Hybrid mode.");
        }

        if (!string.Equals(options.ExternalIdentity.Scheme, ExternalIdentityOptions.DefaultScheme, StringComparison.Ordinal))
            failures.Add($"Trading:Auth:ExternalIdentity:Scheme must be exactly '{ExternalIdentityOptions.DefaultScheme}'.");

        if (mode != AuthModeKind.Local || options.IsExchangeEnabled())
            ValidateExternalIdentity(options.ExternalIdentity, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateExternalIdentity(ExternalIdentityOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.Authority) && string.IsNullOrWhiteSpace(options.MetadataAddress))
            failures.Add("Trading:Auth:ExternalIdentity:Authority or MetadataAddress is required outside Local mode.");
        if (string.IsNullOrWhiteSpace(options.Issuer))
            failures.Add("Trading:Auth:ExternalIdentity:Issuer is required outside Local mode.");
        if (options.RequireTenantId && string.IsNullOrWhiteSpace(options.TenantId))
            failures.Add("Trading:Auth:ExternalIdentity:TenantId is required outside Local mode.");
        if (string.IsNullOrWhiteSpace(options.Audience))
            failures.Add("Trading:Auth:ExternalIdentity:Audience is required outside Local mode.");
        if (string.IsNullOrWhiteSpace(options.RequiredScope))
            failures.Add("Trading:Auth:ExternalIdentity:RequiredScope is required outside Local mode.");
        if (options.AllowedClientApplicationIds.Count == 0
            || options.AllowedClientApplicationIds.Any(string.IsNullOrWhiteSpace))
            failures.Add("Trading:Auth:ExternalIdentity:AllowedClientApplicationIds must contain at least one non-empty SPA client id outside Local mode.");
        if (options.InternalTokenLifetimeMinutes <= 0)
            failures.Add("Trading:Auth:ExternalIdentity:InternalTokenLifetimeMinutes must be > 0.");
    }
}
