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

    /// <summary>
    /// TOTP 2FA configuration (issue #303). Null when the user has not
    /// enrolled. <see cref="UserTotpConfig.SharedSecret"/> is the
    /// Data-Protection-encrypted base32 secret — never plaintext on
    /// disk.
    /// </summary>
    public UserTotpConfig? Totp { get; set; }

    /// <summary>
    /// When true, the user MUST enroll a TOTP factor; login returns
    /// <c>requires2faEnrollment=true</c> with a short-lived enrollment
    /// token instead of a JWT. Defaults to false.
    /// </summary>
    public bool Require2FA { get; set; }
}

/// <summary>
/// Per-user TOTP state (#303). Persisted as part of <see cref="UserConfig"/>.
/// </summary>
public sealed class UserTotpConfig
{
    /// <summary>
    /// Base32 TOTP shared secret, encrypted at rest via ASP.NET Core
    /// Data Protection (<see cref="Auth.Totp.ITotpSecretProtector"/>).
    /// The wire format is the protector's opaque ciphertext; do NOT
    /// assume it is base32 itself.
    /// </summary>
    public string SharedSecret { get; set; } = string.Empty;

    /// <summary>When the user completed enrollment (verify step).</summary>
    public DateTimeOffset? EnrolledAt { get; set; }

    /// <summary>
    /// SHA-256 hashes of unused recovery codes. Each entry is consumed
    /// on use (removed from the list).
    /// </summary>
    public List<string> RecoveryCodes { get; set; } = new();
}
