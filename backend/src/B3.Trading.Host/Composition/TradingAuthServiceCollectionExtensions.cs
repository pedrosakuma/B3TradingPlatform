using B3.Trading.Api.Auth;
using B3.Trading.Api.Auth.Totp;
using B3.Trading.Api.Auth.WebAuthn;
using B3.Trading.Application;
using Fido2NetLib;
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
        services.Configure<WebAuthnOptions>(
            configuration.GetSection(WebAuthnOptions.SectionName));
        services.PostConfigure<WebAuthnOptions>(options =>
        {
            var auth = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>()
                ?? new AuthOptions();
            if (options.Origins.Count == 0)
            {
                if (Uri.TryCreate(auth.Issuer, UriKind.Absolute, out var issuer)
                    && (issuer.Scheme == Uri.UriSchemeHttp || issuer.Scheme == Uri.UriSchemeHttps))
                {
                    options.Origins.Add(issuer.GetLeftPart(UriPartial.Authority));
                }
                else
                {
                    options.Origins.Add("http://localhost:8080");
                }
            }

            if (string.IsNullOrWhiteSpace(options.RelyingPartyId))
            {
                options.RelyingPartyId = Uri.TryCreate(
                    options.Origins[0], UriKind.Absolute, out var origin)
                    ? origin.Host
                    : "localhost";
            }
        });
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
        services.AddSingleton<IValidateOptions<WebAuthnOptions>, WebAuthnOptionsValidator>();
        services.AddSingleton<ILoginAttemptTracker, InMemoryLoginAttemptTracker>();
        services.AddSingleton<ITotpAttemptTracker, InMemoryTotpAttemptTracker>();
        services.AddSingleton<ITotpService, TotpService>();
        services.AddSingleton<ITotpSecretProtector, TotpSecretProtector>();
        services.AddSingleton<IPendingTotpEnrollmentStore, InMemoryPendingTotpEnrollmentStore>();
        services.AddSingleton<ITotpChallengeStore, InMemoryTotpChallengeStore>();
        services.AddSingleton<IWebAuthnCredentialProtector, WebAuthnCredentialProtector>();
        services.AddSingleton<IWebAuthnChallengeStore, InMemoryWebAuthnChallengeStore>();
        services.AddSingleton<IFido2>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<WebAuthnOptions>>().Value;
            return new Fido2(new Fido2Configuration
            {
                ServerDomain = options.RelyingPartyId,
                ServerName = options.RelyingPartyName,
                Origins = options.Origins.ToHashSet(StringComparer.OrdinalIgnoreCase),
                Timeout = options.TimeoutMilliseconds,
                ChallengeSize = 32,
            });
        });
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
        services.AddSingleton<ILegacyUserSnapshotProvider>(sp =>
            (ILegacyUserSnapshotProvider)sp.GetRequiredService<IUserStore>());

        services.AddSingleton<JwtIssuer>();
        services.AddSingleton<ITradingSessionIssuer, TradingSessionIssuer>();
        services.AddSingleton<IExternalIdentityConfigurationProvider, ExternalIdentityConfigurationProvider>();
        services.AddSingleton<IExternalIdentityTokenValidator, ExternalIdentityTokenValidator>();

        // Auth: JWT bearer with explicit claim mapping. We disable the legacy
        // inbound mapping so 'sub' stays 'sub' (not ClaimTypes.NameIdentifier),
        // matching what JwtIssuer emits.
        JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer()
            .AddJwtBearer(ExternalIdentityOptions.DefaultScheme);
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
        services.AddOptions<JwtBearerOptions>(ExternalIdentityOptions.DefaultScheme)
            .Configure<IOptions<AuthOptions>>((options, authHolder) =>
            {
                var external = authHolder.Value.ExternalIdentity;
                options.MapInboundClaims = false;
                if (!string.IsNullOrWhiteSpace(external.Authority))
                    options.Authority = external.Authority;
                if (!string.IsNullOrWhiteSpace(external.MetadataAddress))
                    options.MetadataAddress = external.MetadataAddress;
                options.RequireHttpsMetadata = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidIssuer = external.Issuer,
                    ValidAudience = external.Audience,
                    ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = JwtIssuer.RoleClaim,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });
        services.AddAuthorization(options =>
        {
            options.AddPolicy("admin", policy => policy.RequireRole("admin"));
            // Q4.14 (#314). Admin OR compliance — used by /api/admin/audit so a
            // compliance principal can read the audit log (server-side
            // firm-scoped at the endpoint, never trust query filters).
            // Distinct from the CVM policy below because the policy name
            // pattern is "admin-or-compliance" for surfaces that started as
            // admin-only and were broadened. The CVM policy uses its own
            // canonical name (ComplianceOrAdmin) for endpoints that were
            // compliance-first.
            options.AddPolicy(
                "admin-or-compliance",
                policy => policy.RequireRole(
                    B3.Trading.Api.Auth.Roles.Admin,
                    B3.Trading.Api.Auth.Roles.Compliance));
            // Q4.8 (#308). CVM 35/505 transaction-report export — open
            // to both admin and compliance principals (compliance is
            // the firm-scoped, read-only role added in Q4.6).
            options.AddPolicy(
                B3.Trading.Api.CvmReportEndpoints.PolicyName,
                policy => policy.RequireRole(
                    B3.Trading.Api.Auth.Roles.Admin,
                    B3.Trading.Api.Auth.Roles.Compliance));
        });

        return services;
    }
}
