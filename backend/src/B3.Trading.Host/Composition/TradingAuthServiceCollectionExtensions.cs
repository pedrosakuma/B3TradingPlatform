using B3.Trading.Api.Auth;
using B3.Trading.Api.Auth.Totp;
using B3.Trading.Application;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace B3.Trading.Host.Composition;

/// <summary>
/// Wires JWT bearer authentication, authorization policies, the user store,
/// login attempt tracking and the auth-side rate limiter. Mirrors the
/// pre-#187 inline layout one-for-one so registration order semantics are
/// preserved.
/// </summary>
public static class TradingAuthServiceCollectionExtensions
{
    public static IServiceCollection AddTradingAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AuthOptions>(
            configuration.GetSection(AuthOptions.SectionName));
        services.Configure<AuthRateLimitOptions>(
            configuration.GetSection(AuthRateLimitOptions.SectionName));
        services.Configure<UserStoreOptions>(
            configuration.GetSection(UserStoreOptions.SectionName));
        services.Configure<LoginLockoutOptions>(
            configuration.GetSection(LoginLockoutOptions.SectionName));
        services.Configure<TotpOptions>(
            configuration.GetSection(TotpOptions.SectionName));
        services.Configure<TotpLockoutOptions>(
            configuration.GetSection(TotpLockoutOptions.SectionName));
        services.AddSingleton<ILoginAttemptTracker, InMemoryLoginAttemptTracker>();
        services.AddSingleton<ITotpAttemptTracker, InMemoryTotpAttemptTracker>();
        services.AddSingleton<ITotpService, TotpService>();
        services.AddSingleton<ITotpSecretProtector, TotpSecretProtector>();
        services.AddSingleton<IPendingTotpEnrollmentStore, InMemoryPendingTotpEnrollmentStore>();
        services.AddSingleton<ITotpChallengeStore, InMemoryTotpChallengeStore>();
        services.AddAuthRateLimiter();

        // Slice 3 of #97: when Trading:Auth:UserStore:Enabled is true (the
        // production default), runtime self-service signups persist to disk so
        // they survive restarts. Tests and ephemeral demos can opt out via
        // Enabled=false to keep the legacy in-memory behavior.
        //
        // FilePath is resolved via PostConfigure so tests can override it
        // before construction; the IUserStore registration is a factory
        // function so Enabled is read AFTER all configuration sources
        // (including WebApplicationFactory overrides) have been merged.
        services.PostConfigure<UserStoreOptions>(opts =>
        {
            if (!opts.Enabled) return;
            if (!string.IsNullOrWhiteSpace(opts.FilePath)) return;
            var persistDir = configuration
                .GetSection(B3.Trading.Infrastructure.Persistence.PersistenceOptions.SectionName)
                .Get<B3.Trading.Infrastructure.Persistence.PersistenceOptions>()?.DataDirectory
                ?? "data";
            opts.FilePath = Path.Combine(persistDir, "users.json");
        });
        services.AddSingleton<IUserStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<UserStoreOptions>>().Value;
            return opts.Enabled
                ? ActivatorUtilities.CreateInstance<FileBackedUserStore>(sp)
                : ActivatorUtilities.CreateInstance<InMemoryUserStore>(sp);
        });

        services.AddSingleton<JwtIssuer>();

        // Auth: JWT bearer with explicit claim mapping. We disable the legacy
        // inbound mapping so 'sub' stays 'sub' (not ClaimTypes.NameIdentifier),
        // matching what JwtIssuer emits.
        JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();
        // Configure JwtBearerOptions through the options pipeline so test-time
        // AuthOptions overrides (in-memory config) propagate end-to-end.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((options, authHolder) =>
            {
                var authOpts = authHolder.Value;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = authOpts.Issuer,
                    ValidAudience = authOpts.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOpts.SigningKey)),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = JwtIssuer.RoleClaim,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                // Browsers can't easily set Authorization on a WS handshake;
                // accept ?access_token= for /ws only.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        if (string.IsNullOrEmpty(ctx.Token) &&
                            ctx.Request.Path.StartsWithSegments("/ws") &&
                            ctx.Request.Query.TryGetValue("access_token", out var accessToken))
                        {
                            ctx.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    },
                };
            });
        services.AddAuthorization(options =>
        {
            options.AddPolicy("admin", policy => policy.RequireRole("admin"));
        });

        return services;
    }
}
