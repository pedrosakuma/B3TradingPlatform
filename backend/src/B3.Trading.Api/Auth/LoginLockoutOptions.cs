namespace B3.Trading.Api.Auth;

/// <summary>
/// Per-username login lockout. Slice 4 of issue #97. Complements
/// <see cref="AuthRateLimitOptions"/> (per-IP / global rate limit) by
/// throttling guesses against a single account from any IP. Bound from
/// <c>Trading:Auth:LoginLockout</c>.
/// </summary>
public sealed class LoginLockoutOptions
{
    public const string SectionName = "Trading:Auth:LoginLockout";

    /// <summary>Disable for tests (default in <c>TestAppFactory</c>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Failed attempts that flip a username into lockout. Counted only
    /// inside the rolling <see cref="Window"/>; once <see cref="LockoutDuration"/>
    /// elapses, the slate is wiped and the next failure starts a fresh window.
    /// </summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>Rolling window across which failures accumulate.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a locked username stays locked.</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);
}
