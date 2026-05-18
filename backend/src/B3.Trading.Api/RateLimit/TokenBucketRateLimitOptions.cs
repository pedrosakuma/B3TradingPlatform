namespace B3.Trading.Api.RateLimit;

/// <summary>
/// Q4.4 (#304). Per-user × endpoint token-bucket rate-limit options
/// bound from <c>Trading:RateLimit</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two semantically separate sources of rules contribute to the final
/// resolution table the limiter consults at request time:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="Defaults"/> — code-shipped rules that document the
///     out-of-the-box policy (orders write, auth, algo write, generic
///     read/write). Always merged in so operators only have to override
///     the buckets they care about.
///   </description></item>
///   <item><description>
///     <see cref="Rules"/> — operator-supplied rules (config). A rule
///     whose <see cref="TokenBucketRule.PathPattern"/> matches a default
///     replaces it; new patterns are appended.
///   </description></item>
/// </list>
/// <para>
/// Pre-auth requests (login, 2FA verify/enroll) are bucketed by client
/// IP (<c>HttpContext.Connection.RemoteIpAddress</c>) since there is no
/// authenticated identity yet. The user key resolution is
/// <c>User.Identity.Name ?? RemoteIp ?? "anonymous"</c>.
/// </para>
/// <para>
/// Multi-firm note: the user key is the JWT <c>sub</c> claim only, NOT
/// <c>(sub, firm)</c>. Therefore the same login active in two firms
/// shares a single bucket across both firms. This is intentional — an
/// abusive script that floods POST /orders should be throttled
/// regardless of which firm context the calls target.
/// </para>
/// </remarks>
public sealed class TokenBucketRateLimitOptions
{
    public const string SectionName = "Trading:RateLimit";

    /// <summary>
    /// Master kill-switch. When <c>false</c> the middleware is a no-op
    /// (no buckets, no metrics, no 429s). Default is <c>true</c> for
    /// production hosts; the test factory flips this to <c>false</c>
    /// so the broad test suite does not have to reason about buckets.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Operator-supplied rules. Each rule with a path pattern that
    /// matches one of <see cref="Defaults"/> overrides that default;
    /// new patterns are appended. Order in config is preserved for
    /// stable tie-breaking but the final match priority is by
    /// descending pattern length (longest-prefix-wins).
    /// </summary>
    public List<TokenBucketRule> Rules { get; set; } = new();

    /// <summary>
    /// Role names whose bearers bypass the limiter entirely. Default
    /// is empty so admins are throttled along with everybody else —
    /// operators must opt-in explicitly (e.g. <c>["admin"]</c>) to
    /// give an emergency console headroom.
    /// </summary>
    public List<string> BypassRoles { get; set; } = new();

    /// <summary>
    /// Code-shipped defaults. Exposed as a static method (not a
    /// constant collection) so each <see cref="TokenBucketRateLimitOptions"/>
    /// instance gets a fresh list — otherwise concurrent option binds
    /// would mutate a shared default.
    /// </summary>
    public static List<TokenBucketRule> Defaults() => new()
    {
        // Order write: high-value endpoint, modest burst to absorb
        // batched flat-out events without letting a runaway script
        // saturate the gateway.
        new TokenBucketRule
        {
            PathPattern = "/orders",
            Methods = new() { "POST", "PUT", "DELETE", "PATCH" },
            Burst = 5,
            RefillPerSecond = 5,
        },
        // Auth endpoints: tight bucket so brute-force/credential-
        // stuffing attempts trip well before the password-hashing
        // pipeline gets backed up. Methods unrestricted (the routes
        // only accept POST anyway).
        new TokenBucketRule
        {
            PathPattern = "/auth/login",
            Burst = 3,
            RefillPerSecond = 1,
        },
        new TokenBucketRule
        {
            PathPattern = "/auth/2fa/verify",
            Burst = 3,
            RefillPerSecond = 1,
        },
        new TokenBucketRule
        {
            PathPattern = "/auth/2fa/enroll",
            Burst = 3,
            RefillPerSecond = 1,
        },
        // Algo writes: same family as /orders but algos can legitimately
        // produce many child operations (slice cancel/replace) so the
        // burst is larger and the refill leaves headroom.
        new TokenBucketRule
        {
            PathPattern = "/algo/",
            Methods = new() { "POST", "PUT", "DELETE", "PATCH" },
            Burst = 10,
            RefillPerSecond = 5,
        },
        // Generic write/read fall-through. Catch-all patterns kick in
        // when no explicit rule matched the request. Read defaults
        // are deliberately generous since they are typically polling
        // queries from dashboards.
        new TokenBucketRule
        {
            PathPattern = "/",
            Methods = new() { "POST", "PUT", "DELETE", "PATCH" },
            Burst = 20,
            RefillPerSecond = 20,
            IsGenericFallback = true,
        },
        new TokenBucketRule
        {
            PathPattern = "/",
            Methods = new() { "GET", "HEAD", "OPTIONS" },
            Burst = 100,
            RefillPerSecond = 100,
            IsGenericFallback = true,
        },
    };
}

/// <summary>
/// A single token-bucket rule. The <see cref="PathPattern"/> matches an
/// incoming request path by prefix (case-insensitive). When
/// <see cref="Methods"/> is non-empty the rule only applies to those
/// HTTP methods; an empty list matches any method.
/// </summary>
public sealed class TokenBucketRule
{
    public string PathPattern { get; set; } = "/";

    /// <summary>
    /// HTTP methods this rule applies to (upper-case). Empty list
    /// means "any method".
    /// </summary>
    public List<string> Methods { get; set; } = new();

    public int Burst { get; set; } = 20;
    public double RefillPerSecond { get; set; } = 20;

    /// <summary>
    /// Internal flag — <c>true</c> for the generic read/write
    /// fall-through entries so the resolver can deprioritise them
    /// behind any explicit pattern of identical depth.
    /// </summary>
    public bool IsGenericFallback { get; set; }
}
