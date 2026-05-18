using System.Globalization;
using System.Security.Claims;
using B3.Trading.Application.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.RateLimit;

/// <summary>
/// Q4.4 (#304). ASP.NET Core middleware that gates each request through
/// the <see cref="IRateLimiter"/> using a rule resolved by
/// <see cref="RateLimitRuleResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// Mounted AFTER <c>UseAuthentication</c> so <c>User.Identity.Name</c>
/// is populated for already-authenticated requests. Pre-auth requests
/// (login, 2FA challenge/enroll endpoints) have no identity and fall
/// back to the client IP — the per-IP bucket is what stops a single
/// host from credential-stuffing the login endpoint.
/// </para>
/// <para>
/// On rejection the response is <c>429 Too Many Requests</c> with
/// <c>Retry-After: &lt;ceil(seconds-to-next-token)&gt;</c> and a JSON
/// body of <c>{"error":"rate_limited","retryAfterSeconds":N}</c>. The
/// metric <c>trading.ratelimit.rejected_total</c> increments with low-
/// cardinality tags <c>path</c> (matched rule pattern, not raw path)
/// and <c>principal_kind</c> (one of <c>user</c>, <c>ip</c>,
/// <c>anonymous</c>). The throttled identity itself (sub-claim or
/// remote IP) is intentionally NOT a metric tag — under an IP-spray
/// attack that would explode the time series cardinality — but it IS
/// emitted on the rejection log line for forensics.
/// </para>
/// </remarks>
public sealed class TokenBucketRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRateLimiter _limiter;
    private readonly RateLimitRuleResolver _resolver;
    private readonly IOptionsMonitor<TokenBucketRateLimitOptions> _options;
    private readonly ILogger<TokenBucketRateLimitMiddleware> _logger;

    public TokenBucketRateLimitMiddleware(
        RequestDelegate next,
        IRateLimiter limiter,
        RateLimitRuleResolver resolver,
        IOptionsMonitor<TokenBucketRateLimitOptions> options,
        ILogger<TokenBucketRateLimitMiddleware> logger)
    {
        _next = next;
        _limiter = limiter;
        _resolver = resolver;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled)
        {
            await _next(context);
            return;
        }

        // Bypass roles: an admin with the role configured in
        // BypassRoles skips the limiter entirely. Default (empty)
        // means admins are ALSO throttled — operators must opt in.
        if (opts.BypassRoles.Count > 0 && HasBypassRole(context.User, opts.BypassRoles))
        {
            await _next(context);
            return;
        }

        var rule = _resolver.Resolve(context);
        if (rule is null)
        {
            await _next(context);
            return;
        }

        var (userKey, principalKind) = ResolveUserKey(context);
        var endpointKey = rule.PathPattern;

        if (_limiter.TryAcquire(userKey, endpointKey, rule.Burst, rule.RefillPerSecond, out var retryAfterSeconds))
        {
            await _next(context);
            return;
        }

        var retryAfter = Math.Max(1, (int)Math.Ceiling(retryAfterSeconds));

        // Tags are intentionally bounded: `path` is the rule pattern
        // (a short, operator-defined list) and `principal_kind` is one
        // of three string constants. The user/IP identity is captured
        // on the log line below so operators can still attribute a
        // spike to a specific actor.
        MetricsRegistry.RateLimitRejected.Add(1,
            new KeyValuePair<string, object?>("path", endpointKey),
            new KeyValuePair<string, object?>("principal_kind", principalKind));

        _logger.LogInformation(
            "ratelimit.rejected path={Path} principalKind={PrincipalKind} user={User} retryAfterSeconds={RetryAfter}",
            endpointKey, principalKind, userKey, retryAfter);

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            $"{{\"error\":\"rate_limited\",\"retryAfterSeconds\":{retryAfter.ToString(CultureInfo.InvariantCulture)}}}");
    }

    private static bool HasBypassRole(ClaimsPrincipal user, IReadOnlyList<string> bypassRoles)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;
        foreach (var role in bypassRoles)
        {
            if (user.IsInRole(role)) return true;
        }
        return false;
    }

    private static (string Key, string Kind) ResolveUserKey(HttpContext ctx)
    {
        // Prefer the JWT sub-claim (set as User.Identity.Name via
        // NameClaimType = sub in the bearer configuration). Pre-auth
        // requests fall back to the connection peer IP — see class
        // remarks for why X-Forwarded-For is not consulted here. The
        // returned Kind is a low-cardinality category used as a metric
        // tag; the Key remains the bucket identity and is logged but
        // never exported as a tag.
        var name = ctx.User?.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name)) return (name, "user");
        var ip = ctx.Connection.RemoteIpAddress;
        return ip is null ? ("anonymous", "anonymous") : (ip.ToString(), "ip");
    }
}
