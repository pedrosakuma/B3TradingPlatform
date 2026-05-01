namespace B3.Trading.Api.Auth;

/// <summary>
/// Refuses to boot the host when the JWT signing key is missing, too
/// weak, or the well-known dev-only default leaked into a production-
/// shaped environment. Run after <c>builder.Build()</c> so options
/// have already bound from every configuration provider (env, files,
/// user-secrets) the host pipeline composed.
/// </summary>
public static class AuthSigningKeyValidator
{
    /// <summary>
    /// The literal string committed in <c>appsettings.json</c> for the
    /// dev loop. Anything matching this in non-Development environments
    /// is a deployment-config bug and we fail fast.
    /// </summary>
    public const string DevOnlyKey = "DEV-ONLY-32-byte-signing-key__replace_in_production!!";

    /// <summary>HS256 needs ≥256 bits = 32 bytes UTF-8.</summary>
    public const int MinKeyBytes = 32;

    public static void Validate(string environmentName, string signingKey)
    {
        var isDev = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(signingKey))
            throw new InvalidOperationException(
                "Trading:Auth:SigningKey is empty. Set it via Trading__Auth__SigningKey env var (≥32 bytes UTF-8).");

        if (System.Text.Encoding.UTF8.GetByteCount(signingKey) < MinKeyBytes)
            throw new InvalidOperationException(
                $"Trading:Auth:SigningKey must be ≥{MinKeyBytes} bytes UTF-8 for HS256.");

        if (!isDev && signingKey == DevOnlyKey)
            throw new InvalidOperationException(
                $"Trading:Auth:SigningKey is the committed dev-only default in environment '{environmentName}'. " +
                "Set Trading__Auth__SigningKey to a unique secret (≥32 bytes).");
    }
}
