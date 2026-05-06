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

    /// <summary>
    /// Password policy applied at signup only. Login intentionally does not
    /// re-check the policy so env-seeded operator accounts are not retroactively
    /// locked out when defaults tighten. See issue #97 (slice 1).
    /// </summary>
    public PasswordPolicyOptions PasswordPolicy { get; set; } = new();

    /// <summary>
    /// Exact-match (case-insensitive) reserved usernames blocked at signup.
    /// Defaults cover well-known admin handles. Operators may override via
    /// <c>Trading:Auth:ReservedUsernames</c>; the configured list replaces
    /// defaults (standard .NET options array semantics).
    /// </summary>
    public List<string> ReservedUsernames { get; set; } = new()
    {
        "admin", "administrator", "root", "system",
    };

    /// <summary>
    /// Prefix-match (case-insensitive) reserved username patterns. Same
    /// override semantics as <see cref="ReservedUsernames"/>. Defaults
    /// reserve the demo/bot namespaces used by docker-compose.demo.yml so a
    /// drive-by signup cannot impersonate a bot identity.
    /// </summary>
    public List<string> ReservedUsernamePrefixes { get; set; } = new()
    {
        "bot-", "bot_", "demo-", "demo_",
    };
}

public sealed class PasswordPolicyOptions
{
    /// <summary>Minimum length. Values &lt;= 0 are clamped to <see cref="DefaultMinLength"/>.</summary>
    public int MinLength { get; set; } = DefaultMinLength;
    public bool RequireDigit { get; set; } = true;
    public bool RequireLetter { get; set; } = true;

    public const int DefaultMinLength = 8;

    public int EffectiveMinLength => MinLength <= 0 ? DefaultMinLength : MinLength;
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
