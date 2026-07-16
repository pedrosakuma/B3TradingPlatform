namespace B3.Trading.Api.Auth;

/// <summary>
/// Auth + JWT configuration. Bound from <c>Trading:Auth</c> in
/// <c>appsettings.json</c>. Local remains the compatibility default; Hybrid
/// and Entra add a dedicated external-token exchange without changing the
/// internal JWT bearer scheme.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Trading:Auth";

    public string Issuer { get; set; } = "b3-trading";
    public string Audience { get; set; } = "b3-trading-clients";
    public string SigningKey { get; set; } = string.Empty;
    public int TokenLifetimeMinutes { get; set; } = 60;
    public int Pbkdf2Iterations { get; set; } = 600_000;
    public string Mode { get; set; } = AuthModes.Local;
    public bool? LocalLoginEnabled { get; set; }
    public bool? SignupEnabled { get; set; }
    public bool? TotpEnabled { get; set; }
    public bool? ExchangeEnabled { get; set; }
    public List<UserConfig> Users { get; set; } = new();
    public ExternalIdentityOptions ExternalIdentity { get; set; } = new();

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

    public AuthModeKind ResolveMode() =>
        AuthModes.Parse(Mode);

    public bool IsLocalLoginEnabled() => ResolveMode() switch
    {
        AuthModeKind.Local => LocalLoginEnabled ?? true,
        AuthModeKind.Hybrid => LocalLoginEnabled ?? true,
        AuthModeKind.Entra => LocalLoginEnabled ?? false,
        _ => false,
    };

    public bool IsSignupEnabled() => ResolveMode() switch
    {
        AuthModeKind.Local => SignupEnabled ?? true,
        AuthModeKind.Hybrid => SignupEnabled ?? false,
        AuthModeKind.Entra => SignupEnabled ?? false,
        _ => false,
    };

    public bool IsTotpEnabled() => ResolveMode() switch
    {
        AuthModeKind.Local => TotpEnabled ?? true,
        AuthModeKind.Hybrid => TotpEnabled ?? true,
        AuthModeKind.Entra => TotpEnabled ?? false,
        _ => false,
    };

    public bool IsExchangeEnabled() => ResolveMode() switch
    {
        AuthModeKind.Local => ExchangeEnabled ?? false,
        AuthModeKind.Hybrid => ExchangeEnabled ?? true,
        AuthModeKind.Entra => ExchangeEnabled ?? true,
        _ => false,
    };
}

public enum AuthModeKind
{
    Local,
    Hybrid,
    Entra,
}

public static class AuthModes
{
    public const string Local = "Local";
    public const string Hybrid = "Hybrid";
    public const string Entra = "Entra";

    public static AuthModeKind Parse(string? value)
    {
        if (string.Equals(value, Local, StringComparison.OrdinalIgnoreCase))
            return AuthModeKind.Local;
        if (string.Equals(value, Hybrid, StringComparison.OrdinalIgnoreCase))
            return AuthModeKind.Hybrid;
        if (string.Equals(value, Entra, StringComparison.OrdinalIgnoreCase))
            return AuthModeKind.Entra;
        throw new InvalidOperationException($"{AuthOptions.SectionName}:Mode must be Local, Hybrid or Entra.");
    }
}

public sealed class ExternalIdentityOptions
{
    public const string DefaultScheme = "EntraExternal";
    public const int DefaultInternalTokenLifetimeMinutes = 10;

    public string Scheme { get; set; } = DefaultScheme;
    public string IssuerAlias { get; set; } = "entra";
    public string Authority { get; set; } = string.Empty;
    public string? MetadataAddress { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public bool RequireTenantId { get; set; } = true;
    public string Audience { get; set; } = string.Empty;
    public string RequiredScope { get; set; } = string.Empty;
    public List<string> AllowedClientApplicationIds { get; set; } = new();
    public int InternalTokenLifetimeMinutes { get; set; } = DefaultInternalTokenLifetimeMinutes;

    public TimeSpan InternalTokenLifetime =>
        TimeSpan.FromMinutes(InternalTokenLifetimeMinutes > 0
            ? InternalTokenLifetimeMinutes
            : DefaultInternalTokenLifetimeMinutes);

    public string EffectiveMetadataAddress =>
        !string.IsNullOrWhiteSpace(MetadataAddress)
            ? MetadataAddress
            : $"{Authority.TrimEnd('/')}/.well-known/openid-configuration";
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

    /// <summary>
    /// FIFO-bounded ring of recently-consumed recovery-code hashes.
    /// Used to distinguish a wrong-code attempt from a previously-valid
    /// code (concurrent race loser or replay-after-success) so the TOTP
    /// lockout counter is not incremented in the latter case. Capped at
    /// <see cref="ConsumedRecoveryCodesCap"/> entries with FIFO
    /// eviction — generous enough to cover realistic re-enrollment
    /// churn (defaults: 10 codes per enrollment) while keeping the
    /// persisted footprint small.
    /// <para>
    /// Trade-off: an attacker who learns a hash that has already been
    /// consumed can hit /auth/2fa/verify forever without tripping
    /// lockout. That is acceptable — knowing a consumed code is no
    /// stronger than knowing the JWT the code originally produced, and
    /// the alternative (counting consumed-list hits as failures) would
    /// let any attacker brute-force-lock the legitimate user.
    /// </para>
    /// </summary>
    public List<string> ConsumedRecoveryCodes { get; set; } = new();

    /// <summary>
    /// Maximum size of <see cref="ConsumedRecoveryCodes"/>. Set well
    /// above a single enrollment's <see cref="Totp.TotpOptions.RecoveryCodeCount"/>
    /// (default 10) so multiple re-enrollments fit before FIFO
    /// eviction; small enough to keep the persisted user record bounded.
    /// </summary>
    public const int ConsumedRecoveryCodesCap = 64;

    /// <summary>
    /// Most recent successfully-consumed TOTP time step (RFC 6238 T,
    /// i.e. seconds-since-epoch / period). Used to block replay of a
    /// valid code within the same 30s window via a fresh challenge
    /// token. Nullable so users persisted before this field existed
    /// (legacy / on-disk envelopes) default cleanly to "no prior step
    /// recorded" — the first verify after restart simply seeds it.
    /// </summary>
    public long? LastUsedTimeStep { get; set; }
}
