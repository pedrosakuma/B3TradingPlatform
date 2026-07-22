namespace B3.Trading.Api.Auth;

/// <summary>
/// Slice 2 of #97 hardening: anti-abuse rate limits for the public auth
/// endpoints. Bound from <c>Trading:Auth:RateLimit</c> in
/// <c>appsettings.json</c>. Each policy is independently toggleable so
/// operators can ship behind a proxy that already throttles, or test
/// suites can run hot loops without tripping limits.
/// </summary>
/// <remarks>
/// All three policies are wired into a single chained
/// <see cref="System.Threading.RateLimiting.PartitionedRateLimiter"/>
/// installed as <c>GlobalLimiter</c>. The chain order is intentional:
/// per-IP runs first so a single abusive source cannot burn the global
/// signup fuse before being rejected at the IP layer.
/// </remarks>
public sealed class AuthRateLimitOptions
{
    public const string SectionName = "Trading:Auth:RateLimit";

    /// <summary>Per-IP throttle for <c>POST /api/auth/signup</c>.</summary>
    public RateLimitPolicyOptions SignupPerIp { get; set; } = new()
    {
        Enabled = true,
        PermitLimit = 5,
        WindowSeconds = 3600,
    };

    /// <summary>Global fuse for <c>POST /api/auth/signup</c>; second link of the chain.</summary>
    public RateLimitPolicyOptions SignupGlobal { get; set; } = new()
    {
        Enabled = true,
        PermitLimit = 100,
        WindowSeconds = 3600,
    };

    /// <summary>
    /// Per-IP throttle for <c>POST /api/auth/login</c>. Defense-in-depth
    /// against credential stuffing while slice 4 (per-username failed-
    /// login lockout) is not in place. Counts ALL login attempts (incl.
    /// successful) since this is endpoint-level — not a substitute for
    /// proper failed-login tracking.
    /// </summary>
    public RateLimitPolicyOptions LoginPerIp { get; set; } = new()
    {
        Enabled = true,
        PermitLimit = 20,
        WindowSeconds = 300,
    };

    /// <summary>Per-IP throttle for <c>POST /api/auth/exchange</c>.</summary>
    public RateLimitPolicyOptions ExchangePerIp { get; set; } = new()
    {
        Enabled = true,
        PermitLimit = 30,
        WindowSeconds = 300,
    };
}

public sealed class RateLimitPolicyOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Requests allowed per window. Values &lt;= 0 disable the policy.</summary>
    public int PermitLimit { get; set; } = 5;

    /// <summary>Window length in seconds. Values &lt;= 0 disable the policy.</summary>
    public int WindowSeconds { get; set; } = 60;

    public bool IsActive => Enabled && PermitLimit > 0 && WindowSeconds > 0;
    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}
