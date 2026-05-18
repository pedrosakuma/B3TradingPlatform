namespace B3.Trading.Api.Auth.Totp;

/// <summary>
/// Configuration for TOTP-based 2FA (#303). Bound from
/// <c>Trading:Auth:Totp</c>.
/// </summary>
public sealed class TotpOptions
{
    public const string SectionName = "Trading:Auth:Totp";

    /// <summary>Issuer label shown in authenticator apps.</summary>
    public string Issuer { get; set; } = "B3";

    /// <summary>How long a pending enrollment stays valid before expiring.</summary>
    public TimeSpan PendingEnrollmentTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a login-flow TOTP challenge token stays valid.</summary>
    public TimeSpan ChallengeTokenTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How many recovery codes are minted at enrollment.</summary>
    public int RecoveryCodeCount { get; set; } = 10;

    /// <summary>
    /// Optional override for the Data Protection key ring directory used
    /// to encrypt TOTP shared secrets at rest. Empty means "reuse the
    /// host's existing key ring under <c>{DataDirectory}/dp-keys</c>",
    /// which is the sensible default. Operators wanting a separate ring
    /// (e.g. <c>/var/lib/b3/data-protection-keys</c>) set this to the
    /// absolute path.
    /// </summary>
    public string KeyRingDirectory { get; set; } = string.Empty;
}

/// <summary>
/// Lockout policy for TOTP verify attempts (#303). Bound from
/// <c>Trading:Auth:TotpLockout</c>. Mirrors <see cref="LoginLockoutOptions"/>
/// but is tracked separately so a TOTP-only flood does not lock the
/// password login channel and vice-versa.
/// </summary>
public sealed class TotpLockoutOptions
{
    public const string SectionName = "Trading:Auth:TotpLockout";

    public bool Enabled { get; set; } = true;
    public int MaxFailedAttempts { get; set; } = 5;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(5);
}
