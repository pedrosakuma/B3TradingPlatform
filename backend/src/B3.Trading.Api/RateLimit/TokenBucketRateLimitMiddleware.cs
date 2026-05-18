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
/// and <c>user</c> (sub-claim or IP).
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

        var userKey = ResolveUserKey(context);
        var endpointKey = rule.PathPattern;

        if (_limiter.TryAcquire(userKey, endpointKey, rule.Burst, rule.RefillPerSecond, out var retryAfterSeconds))
        {
            await _next(context);
            return;
        }

        var retryAfter = Math.Max(1, (int)Math.Ceiling(retryAfterSeconds));

        MetricsRegistry.RateLimitRejected.Add(1,
            new KeyValuePair<string, object?>("path", endpointKey),
            new KeyValuePair<string, object?>("user", userKey));

        _logger.LogWarning(
            "ratelimit.rejected path={Path} user={User} retryAfterSeconds={RetryAfter}",
            endpointKey, userKey, retryAfter);

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

    private static string ResolveUserKey(HttpContext ctx)
    {
        // Prefer the JWT sub-claim (set as User.Identity.Name via
        // NameClaimType = sub in the bearer configuration). Pre-auth
        // requests fall back to the connection peer IP — see class
        // remarks for why X-Forwarded-For is not consulted here.
        var name = ctx.User?.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name)) return name;
        var ip = ctx.Connection.RemoteIpAddress;
        return ip is null ? "anonymous" : ip.ToString();
    }
}
