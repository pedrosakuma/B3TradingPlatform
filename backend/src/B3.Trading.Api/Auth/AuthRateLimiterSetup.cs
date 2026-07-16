using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Auth;

/// <summary>
/// Slice 2 of #97 hardening: anti-abuse rate limiting for the public
/// auth endpoints (<c>/auth/signup</c>, <c>/auth/login</c>).
/// </summary>
/// <remarks>
/// <para>
/// All limiters are installed as a single chained
/// <see cref="PartitionedRateLimiter{TResource}"/> on
/// <see cref="RateLimiterOptions.GlobalLimiter"/>. The chain order is
/// per-IP signup → per-IP login → global signup fuse so a single
/// abusive source is rejected before it can advance the global counter.
/// </para>
/// <para>
/// Each partitioner resolves <see cref="IOptionsMonitor{T}"/> from
/// <see cref="HttpContext.RequestServices"/> on every request. This is
/// what makes <c>WebApplicationFactory</c> overrides actually take
/// effect (eager startup capture would freeze defaults).
/// </para>
/// <para>
/// Trust of <c>X-Forwarded-For</c> is intentionally NOT enabled here.
/// Behind a reverse proxy the IP partition collapses to the proxy's IP,
/// effectively converting per-IP into global; operators must opt-in to
/// forwarded headers (with trusted-proxy config) before the per-IP
/// partition becomes meaningful in that topology.
/// </para>
/// </remarks>
internal static class AuthRateLimiterSetup
{
    private const string SignupPath = "/auth/signup";
    private const string LoginPath = "/auth/login";
    private const string ExchangePath = "/auth/exchange";

    public static void AddAuthRateLimiter(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                var loggerFactory = http.RequestServices.GetService<ILoggerFactory>();
                var logger = loggerFactory?.CreateLogger("AuthRateLimiter");

                int retryAfterSeconds = 60;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                }

                http.Response.Headers.RetryAfter =
                    retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                http.Response.ContentType = "application/json";

                logger?.LogWarning(
                    "auth.ratelimit.rejected path={Path} retryAfterSeconds={RetryAfter}",
                    http.Request.Path.Value,
                    retryAfterSeconds);

                var error = http.Request.Path.StartsWithSegments(ExchangePath, StringComparison.OrdinalIgnoreCase)
                    ? "rate_limited"
                    : "too many requests";
                await http.Response.WriteAsync(
                    $"{{\"error\":\"{error}\",\"retryAfterSeconds\":{retryAfterSeconds.ToString(CultureInfo.InvariantCulture)}}}",
                    cancellationToken);
            };

            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                BuildPathPolicy(SignupPath, opts => opts.SignupPerIp, partitionByIp: true),
                BuildPathPolicy(LoginPath, opts => opts.LoginPerIp, partitionByIp: true),
                BuildPathPolicy(ExchangePath, opts => opts.ExchangePerIp, partitionByIp: true),
                BuildPathPolicy(SignupPath, opts => opts.SignupGlobal, partitionByIp: false));
        });
    }

    private static PartitionedRateLimiter<HttpContext> BuildPathPolicy(
        string path,
        Func<AuthRateLimitOptions, RateLimitPolicyOptions> select,
        bool partitionByIp)
    {
        return PartitionedRateLimiter.Create<HttpContext, string>(http =>
        {
            // Path gate: anything outside the auth endpoint is a no-op
            // partition, so the chained global limiter does not affect
            // the rest of the API surface.
            if (!http.Request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase))
                return RateLimitPartition.GetNoLimiter("none");

            var monitor = http.RequestServices.GetRequiredService<IOptionsMonitor<AuthRateLimitOptions>>();
            var policy = select(monitor.CurrentValue);

            if (!policy.IsActive)
                return RateLimitPartition.GetNoLimiter("disabled");

            var key = partitionByIp ? ClientIpKey(http) : "global";

            return RateLimitPartition.GetFixedWindowLimiter(
                $"{path}:{(partitionByIp ? "ip" : "global")}:{key}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = policy.PermitLimit,
                    Window = policy.Window,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });
        });
    }

    private static string ClientIpKey(HttpContext http)
    {
        // Intentionally NOT consulting X-Forwarded-For here — see class
        // remarks for rationale. ConnectionInfo.RemoteIpAddress is the
        // socket peer; behind a proxy that becomes the proxy IP and the
        // per-IP bucket collapses, but that is a configuration concern
        // (UseForwardedHeaders) the operator must opt into.
        var ip = http.Connection.RemoteIpAddress;
        return ip is null ? "unknown" : ip.ToString();
    }
}
