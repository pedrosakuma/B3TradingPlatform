using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.RateLimit;

/// <summary>
/// Q4.4 (#304). DI wiring for the per-user × endpoint token-bucket
/// rate limiter.
/// </summary>
public static class TokenBucketRateLimitServiceCollectionExtensions
{
    public static IServiceCollection AddTradingRateLimit(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TokenBucketRateLimitOptions>(
            configuration.GetSection(TokenBucketRateLimitOptions.SectionName));

        services.AddSingleton<IRateLimiter, TokenBucketRateLimiter>();

        // Resolver is a snapshot of the merged rule set at startup.
        // Mid-flight config reloads (operator changes a Burst in
        // appsettings) are NOT picked up — that's a deliberate
        // simplification for a non-hot-reloadable subsystem; a host
        // restart re-reads the rules.
        services.AddSingleton<RateLimitRuleResolver>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<TokenBucketRateLimitOptions>>().Value;
            return new RateLimitRuleResolver(opts);
        });

        return services;
    }

    public static IApplicationBuilder UseTradingRateLimit(this IApplicationBuilder app)
        => app.UseMiddleware<TokenBucketRateLimitMiddleware>();
}
