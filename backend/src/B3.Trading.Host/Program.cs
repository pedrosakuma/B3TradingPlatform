using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.Extensions.Options;
using B3.Trading.Api;
using B3.Trading.Api.Auth;
using B3.Trading.Api.Lifecycle;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Checks;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ExchangeOptions>(
    builder.Configuration.GetSection(ExchangeOptions.SectionName));
builder.Services.Configure<AuthOptions>(
    builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<RiskOptions>(
    builder.Configuration.GetSection(RiskOptions.SectionName));
builder.Services.Configure<PersistenceOptions>(
    builder.Configuration.GetSection(PersistenceOptions.SectionName));

// CORS: opt-in allowlist for the dev/prod frontend origins. Empty list
// disables CORS entirely (server-only deploys, integration tests).
const string CorsPolicy = "trading-frontend";
var corsOrigins = builder.Configuration.GetSection("Trading:Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));
}

// Application-layer singletons: registries, books, processor, sink.
builder.Services.AddSingleton<EndClientRegistry>();
builder.Services.AddSingleton<ClOrdIdPrefixRegistry>();
builder.Services.AddSingleton<OrderOwnershipMap>();
builder.Services.AddSingleton<WorkingOrderBook>();
builder.Services.AddSingleton<PositionKeeper>();
builder.Services.AddSingleton<SubscriptionManager>();
builder.Services.AddSingleton<IExecutionEventSink, WebSocketExecutionEventSink>();
builder.Services.AddSingleton<ExecutionReportProcessor>();
builder.Services.AddSingleton<JwtIssuer>();

// Lifecycle: drain flag flipped on SIGTERM /
// IHostApplicationLifetime.ApplicationStopping. Read by /ready (503 when
// draining) and POST /orders (refuses new orders so in-flight can finish).
builder.Services.AddSingleton<DrainState>();
builder.Services.AddHostedService<DrainHostedService>();

// Persistence: event-sourced WAL + periodic snapshot. The IEventStore
// implementation is chosen at resolution time from the bound options so
// test-time config overrides (added via IHostBuilder.ConfigureAppConfiguration
// after Program.cs finishes registering services) are honoured. When
// Enabled=false, NullEventStore is wired and SnapshotService self-skips.
builder.Services.AddSingleton<SnapshotStore>(sp =>
{
    var o = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
    return new SnapshotStore(o.DataDirectory, o.FirmId);
});
builder.Services.AddSingleton<IEventStore>(sp =>
{
    var o = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
    return o.Enabled
        ? new FileEventStore(o, sp.GetRequiredService<ILogger<FileEventStore>>())
        : new NullEventStore();
});
builder.Services.AddSingleton<StateSnapshotter>();
builder.Services.AddSingleton<EventReplayer>();
builder.Services.AddSingleton<PersistenceRecovery>();
builder.Services.AddSingleton<EodMaterialiser>();
builder.Services.AddHostedService<SnapshotService>();
builder.Services.AddSingleton<EventDispatcher>();

// Pre-trade risk: pipeline + checks + kill-switch + reference price +
// margin provider. Each IRiskCheck registration is auto-discovered by
// the RiskPipeline through the IEnumerable<IRiskCheck> ctor injection.
builder.Services.AddSingleton<KillSwitchService>();
builder.Services.AddSingleton<IReferencePrice, ConfigReferencePrice>();
builder.Services.AddSingleton<IMarginProvider, NoOpMarginProvider>();
builder.Services.AddSingleton<IRiskCheck, KillSwitchCheck>();
builder.Services.AddSingleton<IRiskCheck, MaxQuantityCheck>();
builder.Services.AddSingleton<IRiskCheck, MaxNotionalCheck>();
builder.Services.AddSingleton<IRiskCheck, PositionLimitCheck>();
builder.Services.AddSingleton<IRiskCheck, PriceCollarCheck>();
builder.Services.AddSingleton<RiskPipeline>();

// Auth: JWT bearer with explicit claim mapping. We disable the legacy
// inbound mapping so 'sub' stays 'sub' (not ClaimTypes.NameIdentifier),
// matching what JwtIssuer emits.
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
// Configure JwtBearerOptions through the options pipeline so test-time
// AuthOptions overrides (in-memory config) propagate end-to-end.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
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
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("admin", policy => policy.RequireRole("admin"));
});

// Wire-side: pick the gateway based on ExchangeOptions.Mode (with legacy
// flag fallback for backward compatibility — see ExchangeOptions.ResolveMode).
//   Stub        → no-op StubExchangeGateway, no client wired (CI / smoke).
//   Mock        → in-process MockEntryPointClient + EntryPointClientGateway (test seam, dev-loop).
//   Real        → one upstream EntryPointClient per FirmConfig + MultiFirmExchangeGateway,
//                 aggregated through FirmGatewayRegistry (which doubles as the single
//                 IEntryPointClient consumed by EntryPointExecutionReportRouter).
//   Unavailable → fail-closed UnavailableExchangeGateway; submits surface as 502
//                 gateway-unavailable. Production-honest no-broker mode.
//
// IExchangeGateway is registered as a factory so the active implementation is
// chosen at DI resolution time — late enough that WebApplicationFactory test
// overrides via ConfigureAppConfiguration are visible (the WebApplication
// minimal-API builder reads pre-Build config eagerly, so we can't switch on
// the option value at registration time).
//
// Real-mode hosted services (FirmGatewayConnector + ER router) MUST be
// registered pre-Build, so we still need an early read for that branch only.
// Tests never exercise Real mode, so the early-vs-late split is invisible
// to them; the only Mode they switch is Stub/Mock/Unavailable.
var exchangeSection = builder.Configuration.GetSection(ExchangeOptions.SectionName);
var earlyMode = exchangeSection["Mode"];
var earlyIsReal = string.Equals(earlyMode, nameof(ExchangeMode.Real), StringComparison.OrdinalIgnoreCase)
    || (string.IsNullOrEmpty(earlyMode) && exchangeSection.GetValue("UseRealEntryPointClient", false));

builder.Services.AddSingleton<StubExchangeGateway>();
builder.Services.AddSingleton<UnavailableExchangeGateway>();

if (earlyIsReal)
{
    builder.Services.AddSingleton<FirmGatewayRegistry>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<ExchangeOptions>>().Value;
        if (opts.Firms.Count == 0)
            throw new InvalidOperationException("Trading:Exchange:Mode is Real but no Firms[] configured. Set Mode=Unavailable for an honest no-broker host.");
        var lf = sp.GetRequiredService<ILoggerFactory>();
        var gateways = opts.Firms.Select(firm =>
        {
            FirmConfigValidation.ValidateFirm(firm);
            var ep = FirmConfigValidation.ParseEndpoint(firm.Endpoint);
            var clientOpts = new B3.EntryPoint.Client.EntryPointClientOptions
            {
                Endpoint = ep,
                SessionId = firm.SessionId,
                SessionVerId = firm.SessionVerId,
                EnteringFirm = firm.EnteringFirm,
                Credentials = B3.EntryPoint.Client.EntryPointClientOptions.AccessKey(firm.AccessKey),
                KeepAliveIntervalMs = firm.KeepAliveIntervalMs,
                SenderLocation = firm.SenderLocation,
                EnteringTrader = firm.EnteringTrader,
                Logger = lf.CreateLogger($"B3.EntryPoint.Client[{firm.FirmId}]"),
            };
            var upstream = new B3.EntryPoint.Client.EntryPointClient(clientOpts);
            var gwLogger = lf.CreateLogger<B3EntryPointClientGateway>();
            return new B3EntryPointClientGateway(upstream, firm.FirmId, gwLogger);
        });
        return new FirmGatewayRegistry(gateways);
    });
    builder.Services.AddSingleton<IEntryPointClient>(sp => sp.GetRequiredService<FirmGatewayRegistry>());
    builder.Services.AddSingleton<MultiFirmExchangeGateway>(sp =>
        new MultiFirmExchangeGateway(sp.GetRequiredService<FirmGatewayRegistry>()));
    builder.Services.AddSingleton<EntryPointExecutionReportRouter>();
    builder.Services.AddHostedService<EntryPointRouterStarter>();
    builder.Services.AddHostedService<FirmGatewayConnector>();
}
else
{
    builder.Services.AddSingleton<MockEntryPointClient>();
    builder.Services.AddSingleton<IEntryPointClient>(sp => sp.GetRequiredService<MockEntryPointClient>());
    builder.Services.AddSingleton<EntryPointClientGateway>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<ExchangeOptions>>().Value;
        var firmId = opts.Firms.FirstOrDefault()?.FirmId ?? "DEFAULT";
        return new EntryPointClientGateway(sp.GetRequiredService<IEntryPointClient>(), firmId);
    });
    builder.Services.AddSingleton<EntryPointExecutionReportRouter>();
    builder.Services.AddHostedService<EntryPointRouterStarter>();
}

builder.Services.AddSingleton<IExchangeGateway>(sp =>
{
    var mode = sp.GetRequiredService<IOptions<ExchangeOptions>>().Value.ResolveMode();
    return mode switch
    {
        ExchangeMode.Stub => sp.GetRequiredService<StubExchangeGateway>(),
        ExchangeMode.Unavailable => sp.GetRequiredService<UnavailableExchangeGateway>(),
        ExchangeMode.Real when earlyIsReal => sp.GetRequiredService<MultiFirmExchangeGateway>(),
        ExchangeMode.Real => throw new InvalidOperationException(
            "Trading:Exchange:Mode=Real requires the early-read flag too: set Trading:Exchange:UseRealEntryPointClient=true in env/appsettings (Real-mode hosted services must be wired pre-Build)."),
        ExchangeMode.Mock => sp.GetRequiredService<EntryPointClientGateway>(),
        _ => sp.GetRequiredService<EntryPointClientGateway>(),
    };
});

builder.Services.AddSingleton<ExchangeStatus>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<ExchangeOptions>>().Value;
    return new ExchangeStatus(opts.ResolveMode(), opts.Firms.Count);
});

var app = builder.Build();

// Fail-fast on weak / missing JWT signing key outside Development. The
// default in appsettings.json is a known dev-only string; if it leaks
// into a production-shaped deployment (Docker / Production / Staging),
// every token signed with it would be trivially forgeable. We refuse to
// boot rather than serve insecure tokens.
{
    var authOpts = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
    AuthSigningKeyValidator.Validate(app.Environment.EnvironmentName, authOpts.SigningKey);
}

// Synchronous recovery before any traffic is accepted: load latest
// snapshot, then replay every WAL event past it. Idempotent — safe to
// run on a fresh data dir, on the NullEventStore (no-op), or after a
// graceful shutdown that already snapshotted.
{
    using var scope = app.Services.CreateScope();
    var opts = scope.ServiceProvider.GetRequiredService<IOptions<PersistenceOptions>>().Value;
    if (opts.Enabled)
    {
        var recovery = scope.ServiceProvider.GetRequiredService<PersistenceRecovery>();
        await recovery.RunAsync();
    }
}

if (corsOrigins.Length > 0)
    app.UseCors(CorsPolicy);

app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { service = "B3TradingPlatform", status = "bootstrap" }));
app.MapHealth();

app.MapAuth();
app.MapOrders();
app.MapPositions();
app.MapAdmin();
app.MapWebSocketHub();

app.Run();

/// <summary>
/// Forces construction of <see cref="EntryPointExecutionReportRouter"/> at
/// app start (DI is otherwise lazy). The router subscribes to ER events
/// in its constructor and unsubscribes on dispose.
/// </summary>
internal sealed class EntryPointRouterStarter : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly EntryPointExecutionReportRouter _router;
    public EntryPointRouterStarter(EntryPointExecutionReportRouter router) => _router = router;
    public Task StartAsync(CancellationToken cancellationToken) { _ = _router; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) { _router.Dispose(); return Task.CompletedTask; }
}

/// <summary>
/// Connects every per-firm <see cref="B3EntryPointClientGateway"/> at
/// startup and tears them down on shutdown. Connection errors are logged
/// but do not abort host start — failed firms surface via the
/// <c>trading.entrypoint.connected</c> gauge and via gateway-unavailable
/// rejections at submit time. Phase-2 follow-up (issue #7): readiness gate
/// + automated reconnect with bumped SessionVerId.
/// </summary>
internal sealed class FirmGatewayConnector : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly FirmGatewayRegistry _registry;
    private readonly ILogger<FirmGatewayConnector> _logger;
    public FirmGatewayConnector(FirmGatewayRegistry registry, ILogger<FirmGatewayConnector> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (firmId, gw) in _registry.Gateways)
        {
            try
            {
                await gw.ConnectAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("EntryPoint session connected for firm {Firm}.", firmId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EntryPoint connect failed for firm {Firm}; submits will surface as gateway-unavailable until recovered.", firmId);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => _registry.DisposeAsync().AsTask();
}

internal static class FirmConfigValidation
{
    public static void ValidateFirm(FirmConfig f)
    {
        if (string.IsNullOrWhiteSpace(f.FirmId)) throw new InvalidOperationException("FirmConfig.FirmId required.");
        if (string.IsNullOrWhiteSpace(f.Endpoint)) throw new InvalidOperationException($"FirmConfig.Endpoint required for firm '{f.FirmId}'.");
        if (string.IsNullOrEmpty(f.AccessKey)) throw new InvalidOperationException($"FirmConfig.AccessKey required for firm '{f.FirmId}'.");
        if (f.SenderLocation.Length is 0 or > 10) throw new InvalidOperationException($"FirmConfig.SenderLocation must be 1..10 chars for firm '{f.FirmId}'.");
        if (f.EnteringTrader.Length is 0 or > 5) throw new InvalidOperationException($"FirmConfig.EnteringTrader must be 1..5 chars for firm '{f.FirmId}'.");
    }

    public static System.Net.IPEndPoint ParseEndpoint(string endpoint)
    {
        var parts = endpoint.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
            throw new FormatException($"FirmConfig.Endpoint must be 'host:port', got '{endpoint}'.");
        var addrs = System.Net.Dns.GetHostAddresses(parts[0]);
        if (addrs.Length == 0)
            throw new FormatException($"Could not resolve '{parts[0]}'.");
        return new System.Net.IPEndPoint(addrs[0], port);
    }
}

// Exposed so WebApplicationFactory<Program>-style tests can spin the host up.
public partial class Program;

