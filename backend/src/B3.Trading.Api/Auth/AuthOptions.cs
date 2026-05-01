namespace B3.Trading.Api.Auth;

/// <summary>
/// Auth + JWT configuration. Bound from <c>Trading:Auth</c> in
/// <c>appsettings.json</c>. v1 is intentionally local-only: no OIDC, no
/// refresh tokens. Operators must supply a 256-bit signing key via
/// environment / user-secrets in production.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Trading:Auth";

    public string Issuer { get; set; } = "b3-trading";
    public string Audience { get; set; } = "b3-trading-clients";
    public string SigningKey { get; set; } = string.Empty;
    public int TokenLifetimeMinutes { get; set; } = 60;
    public int Pbkdf2Iterations { get; set; } = 600_000;
    public List<UserConfig> Users { get; set; } = new();
}

public sealed class UserConfig
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Salt { get; set; } = string.Empty;
    public int Iterations { get; set; } = 600_000;
    public string Role { get; set; } = "user";
    public string Firm { get; set; } = "default";
}
