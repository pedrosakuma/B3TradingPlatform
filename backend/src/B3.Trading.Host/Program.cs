using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using B3.Trading.Api;
using B3.Trading.Api.Auth;
using B3.Trading.Api.Lifecycle;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Application.Risk.Checks;
using B3.Trading.Domain;
using B3.Trading.Host.Observability;
using B3.Trading.Host.MarketData;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ExchangeOptions>()
    .Bind(builder.Configuration.GetSection(ExchangeOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ExchangeOptions>, ExchangeOptionsValidator>();
builder.Services.Configure<AuthOptions>(
    builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<RiskOptions>(
    builder.Configuration.GetSection(RiskOptions.SectionName));
builder.Services.Configure<SymbolDirectoryOptions>(
    builder.Configuration.GetSection(SymbolDirectoryOptions.SectionName));
builder.Services.AddSingleton(sp =>
    new SymbolDirectory(sp.GetRequiredService<IOptions<SymbolDirectoryOptions>>().Value));
builder.Services.Configure<PersistenceOptions>(
    builder.Configuration.GetSection(PersistenceOptions.SectionName));
builder.Services.Configure<PositionSeedOptions>(
    builder.Configuration.GetSection(PositionSeedOptions.SectionName));
builder.Services.Configure<CashSeedOptions>(
    builder.Configuration.GetSection(CashSeedOptions.SectionName));

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
builder.Services.AddSingleton<IUserStore, InMemoryUserStore>();
builder.Services.AddSingleton<EndClientRegistry>();
builder.Services.AddSingleton<ClOrdIdPrefixRegistry>();
builder.Services.AddSingleton<OrderOwnershipMap>();
builder.Services.AddSingleton<WorkingOrderBook>();
builder.Services.AddSingleton<AlgoBook>();
builder.Services.AddSingleton<AlgoIdRegistry>();
builder.Services.AddSingleton<PositionKeeper>();
builder.Services.AddSingleton<CashLedger>();
builder.Services.AddSingleton<SubscriptionManager>();
builder.Services.AddSingleton<IExecutionEventSink, WebSocketExecutionEventSink>();
builder.Services.AddSingleton<IAlgoEventSink, WebSocketAlgoEventSink>();
builder.Services.AddSingleton<ExecutionReportProcessor>();
builder.Services.AddSingleton<OrderSubmissionService>();

// Algo engine signal channel + hosted consumer (RFC algo-orders-v0 §4.3).
// In slice 5a the consumer body was a no-op reactor; slice 5b plugged in the
// Iceberg state machine; slice 6 adds the AlgoScheduler hosted service that
// drives TWAP slice firing on a separate thread (RFC §4.11 commitment 1).
builder.Services.AddSingleton<AlgoSignalQueue>();
builder.Services.AddSingleton<IAlgoSignalQueue>(sp => sp.GetRequiredService<AlgoSignalQueue>());
builder.Services.AddHostedService<AlgoEngine>();
builder.Services.AddHostedService<AlgoScheduler>();
builder.Services.AddSingleton<JwtIssuer>();

// Lifecycle: drain flag flipped on SIGTERM /
// IHostApplicationLifetime.ApplicationStopping. Read by /ready (503 when
// draining) and POST /orders (refuses new orders so in-flight can finish).
builder.Services.AddSingleton<DrainState>();
builder.Services.AddSingleton<B3.Trading.Application.Lifecycle.IDrainGate>(
    sp => sp.GetRequiredService<DrainState>());
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
builder.Services.AddSingleton<SymbolHaltService>();
builder.Services.AddTradingMarketData(builder.Configuration);
builder.Services.AddSingleton<IMarginProvider>(sp =>
{
    var opts = sp.GetRequiredService<IOptionsMonitor<RiskOptions>>().CurrentValue;
    return opts.Margin.Enabled
        ? new ReserveOnSubmitMarginProvider(
            sp.GetRequiredService<IOptionsMonitor<RiskOptions>>(),
            sp.GetRequiredService<ILogger<ReserveOnSubmitMarginProvider>>(),
            sp.GetRequiredService<CashLedger>())
        : new NoOpMarginProvider();
});
builder.Services.AddSingleton<IRiskCheck, KillSwitchCheck>();
builder.Services.AddSingleton<IRiskCheck, SymbolHaltedCheck>();
builder.Services.AddSingleton<IRiskCheck, MinTickSizeCheck>();
builder.Services.AddSingleton<IRiskCheck, MinLotSizeCheck>();
builder.Services.AddSingleton<IRiskCheck, MaxQuantityCheck>();
builder.Services.AddSingleton<IRiskCheck, MaxNotionalCheck>();
builder.Services.AddSingleton<IRiskCheck, MinNotionalCheck>();
builder.Services.AddSingleton<IRiskCheck, PositionLimitCheck>();
builder.Services.AddSingleton<IRiskCheck, RollingNotionalCheck>();
builder.Services.AddSingleton<IRiskCheck, OrderRateLimitCheck>();
builder.Services.AddSingleton<IRiskCheck, MaxOpenOrdersCheck>();
builder.Services.AddSingleton<IRiskCheck, NoNakedShortCheck>();
builder.Services.AddSingleton<IRiskCheck, SelfTradePreventionCheck>();
builder.Services.AddSingleton<IRiskCheck, PriceCollarCheck>();
builder.Services.AddSingleton<RiskPipeline>();

// Throttle accountants (slice 7). TimeProvider is fetched from DI so
// tests can substitute a FakeTimeProvider; production resolves to
// TimeProvider.System via the registration below.
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RollingNotionalAccountant>();
builder.Services.AddSingleton<OrderRateAccountant>();
builder.Services.AddSingleton<IRiskAccountant>(sp => sp.GetRequiredService<RollingNotionalAccountant>());
builder.Services.AddSingleton<IRiskAccountant>(sp => sp.GetRequiredService<OrderRateAccountant>());
builder.Services.AddSingleton<CompositeRiskAccountant>();
builder.Services.AddHostedService<ThrottleLedgerSweeper>();

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
        var persistence = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
        var stateRoot = Path.Combine(persistence.DataDirectory, "entrypoint-state");
        var gateways = opts.Firms.Select(firm =>
        {
            // Shape + uniqueness validation happened at startup via
            // ExchangeOptionsValidator (ValidateOnStart). Endpoint DNS
            // resolution is deferred to here because it requires network.
            var ep = FirmConfigValidation.ParseEndpoint(firm.Endpoint);

            // Wire the SDK's file-backed warm-restart store + resolve the
            // next SessionVerId from the persisted snapshot. Without this,
            // a process restart would replay the configured SessionVerId
            // and the gateway would terminate with InvalidSessionVerId.
            var stateDir = Path.Combine(stateRoot, firm.FirmId);
            Directory.CreateDirectory(stateDir);
            var stateStore = new B3.EntryPoint.Client.State.FileSessionStateStore(stateDir);
            uint? persistedVerId = null;
            try
            {
                var snap = stateStore.LoadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                if (snap is not null) persistedVerId = snap.SessionVerId;
            }
            catch (Exception ex)
            {
                lf.CreateLogger("FirmGatewayConnector").LogWarning(ex,
                    "Failed to load persisted SessionStateStore for firm {Firm}; starting from configured SessionVerId.", firm.FirmId);
            }
            var resolvedVerId = SessionVerIdResolver.Resolve(firm.SessionVerId, persistedVerId);

            var clientOpts = new B3.EntryPoint.Client.EntryPointClientOptions
            {
                Endpoint = ep,
                SessionId = firm.SessionId,
                SessionVerId = resolvedVerId,
                EnteringFirm = firm.EnteringFirm,
                Credentials = B3.EntryPoint.Client.EntryPointClientOptions.AccessKey(firm.AccessKey),
                KeepAliveIntervalMs = firm.KeepAliveIntervalMs,
                SenderLocation = firm.SenderLocation,
                EnteringTrader = firm.EnteringTrader,
                SessionStateStore = stateStore,
                Logger = lf.CreateLogger($"B3.EntryPoint.Client[{firm.FirmId}]"),
            };
            var upstream = new B3.EntryPoint.Client.EntryPointClient(clientOpts);
            var gwLogger = lf.CreateLogger<B3EntryPointClientGateway>();
            return new B3EntryPointClientGateway(upstream, firm.FirmId, resolvedVerId, gwLogger);
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
        ExchangeMode.Simulator => sp.GetRequiredService<EntryPointClientGateway>(),
        _ => sp.GetRequiredService<EntryPointClientGateway>(),
    };
});

builder.Services.AddSingleton<ExchangeStatus>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<ExchangeOptions>>().Value;
    return ExchangeStatus.FromOptions(opts);
});

// Persist DataProtection keys onto the data volume. JWT auth uses HMAC and
// doesn't depend on DataProtection, but ASP.NET still spins it up for
// cookie/antiforgery defaults. Without persistence it logs a warning every
// boot ("No XML encryptor configured. Key ... may be persisted ... in
// unencrypted form.") and regenerates keys on every container restart.
{
    var persistOpts = builder.Configuration
        .GetSection(PersistenceOptions.SectionName)
        .Get<PersistenceOptions>() ?? new PersistenceOptions();
    var keysDir = Path.Combine(persistOpts.DataDirectory, "dp-keys");
    Directory.CreateDirectory(keysDir);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
        .SetApplicationName("b3-trading-host");
}

// OpenTelemetry: opt-in via OTEL_EXPORTER_OTLP_ENDPOINT. No-op when unset,
// so this is safe to leave registered for tests, dev loops, and the
// no-broker compose default. PR 7-2c flips it on inside the obs profile.
builder.Services.AddTradingObservability(builder.Configuration);

var app = builder.Build();

// Hook the slice-7 throttle ledgers into MetricsRegistry so the
// observable gauges have a source. Done after Build so the singletons
// are resolvable; safe to call multiple times (sources are last-write-wins).
{
    var rolling = app.Services.GetRequiredService<RollingNotionalAccountant>();
    var rate = app.Services.GetRequiredService<OrderRateAccountant>();
    B3.Trading.Application.Observability.MetricsRegistry.RegisterRollingNotionalSources(
        () => rolling.EndClientLedger.ActiveBucketCount,
        () => rolling.FirmLedger.ActiveBucketCount);
    B3.Trading.Application.Observability.MetricsRegistry.RegisterOrderRateSources(
        () => rate.EndClientLedger.ActiveBucketCount,
        () => rate.FirmLedger.ActiveBucketCount);
}

// Fail-fast on weak / missing JWT signing key outside Development. The
// default in appsettings.json is a known dev-only string; if it leaks
// into a production-shaped deployment (Docker / Production / Staging),
// every token signed with it would be trivially forgeable. We refuse to
// boot rather than serve insecure tokens.
{
    var authOpts = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
    AuthSigningKeyValidator.Validate(app.Environment.EnvironmentName, authOpts.SigningKey);
}

// Simulator-mode safeguards (RFC algo-orders-v0 §4.10/§7-B3). Synthetic
// ER injection is a powerful test feature with catastrophic blast radius
// if it leaks into production. Four barriers: (1) loud boot-time warning,
// (2) refuse-to-boot in Production unless an explicit opt-out is set,
// (3) trading.simulator.mode_active gauge so dashboards/alerts can spot
// drift, (4) /health body already exposes the mode via ExchangeStatus.
{
    var exchange = app.Services.GetRequiredService<ExchangeStatus>();
    if (exchange.Mode == ExchangeMode.Simulator)
    {
        var exchangeOpts = app.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value;
        SimulatorBootGuard.Validate(app.Environment.EnvironmentName, exchange.Mode, exchangeOpts.AllowSimulatorInProduction);
        var warning = SimulatorBootGuard.BuildWarning(app.Environment.EnvironmentName, exchange.Mode, exchangeOpts.AllowSimulatorInProduction);
        if (warning is not null)
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Simulator").LogWarning("{Warning}", warning);
        B3.Trading.Application.Observability.MetricsRegistry.SimulatorModeActive.Add(1);
    }
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

    // Apply optional opening-position seeds AFTER recovery, so warm
    // restarts always preserve the actual fills and the seed is only
    // ever applied to slots that recovery left empty. Intended for
    // dogfood / dev environments where the naked-short gate would
    // otherwise block any first Sell from a fresh account.
    var seedOpts = scope.ServiceProvider.GetRequiredService<IOptions<PositionSeedOptions>>().Value;
    if (seedOpts.Seeds.Count > 0)
    {
        var keeper = scope.ServiceProvider.GetRequiredService<PositionKeeper>();
        var seedLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("PositionSeeder");
        var applied = 0;
        var skipped = 0;
        foreach (var seed in seedOpts.Seeds)
        {
            if (string.IsNullOrWhiteSpace(seed.EndClientId) || string.IsNullOrWhiteSpace(seed.Symbol))
            {
                seedLogger.LogWarning("Skipping malformed PositionSeed (EndClientId='{Owner}', Symbol='{Symbol}').",
                    seed.EndClientId, seed.Symbol);
                continue;
            }
            var owner = new EndClientId(seed.EndClientId);
            if (keeper.SeedIfAbsent(owner, seed.Symbol, seed.Quantity, seed.AverageEntryPrice))
            {
                applied++;
                seedLogger.LogInformation(
                    "Seeded opening position {Owner}/{Symbol} = {Qty} @ {AvgPx}.",
                    seed.EndClientId, seed.Symbol, seed.Quantity, seed.AverageEntryPrice);
            }
            else
            {
                skipped++;
                seedLogger.LogInformation(
                    "Skipped seed for {Owner}/{Symbol}: position already present from recovery.",
                    seed.EndClientId, seed.Symbol);
            }
        }
        seedLogger.LogInformation("PositionSeeder finished: {Applied} applied, {Skipped} skipped.", applied, skipped);
    }

    // Cash balance seeds (#107 slice 1) — same lifecycle as position
    // seeds: applied AFTER recovery so warm restarts preserve the
    // settled-cash ledger and the seed only fills slots recovery left
    // empty. Negative balances are accepted by the ledger but logged
    // here as a warning so a config typo doesn't silently put a fresh
    // dogfood account in the red.
    var cashOpts = scope.ServiceProvider.GetRequiredService<IOptions<CashSeedOptions>>().Value;
    if (cashOpts.Seeds.Count > 0)
    {
        var ledger = scope.ServiceProvider.GetRequiredService<CashLedger>();
        var cashLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("CashSeeder");
        var applied = 0;
        var skipped = 0;
        foreach (var seed in cashOpts.Seeds)
        {
            if (string.IsNullOrWhiteSpace(seed.EndClientId))
            {
                cashLogger.LogWarning("Skipping malformed CashSeed (empty EndClientId).");
                continue;
            }
            if (seed.InitialAvailable < 0m)
            {
                cashLogger.LogWarning(
                    "CashSeed for {Owner} has negative InitialAvailable={Balance} — applying anyway, but this is almost certainly a typo.",
                    seed.EndClientId, seed.InitialAvailable);
            }
            var owner = new EndClientId(seed.EndClientId);
            if (ledger.SeedIfAbsent(owner, seed.InitialAvailable))
            {
                applied++;
                cashLogger.LogInformation(
                    "Seeded opening cash {Owner} = {Balance}.",
                    seed.EndClientId, seed.InitialAvailable);
            }
            else
            {
                skipped++;
                cashLogger.LogInformation(
                    "Skipped cash seed for {Owner}: balance already present from recovery.",
                    seed.EndClientId);
            }
        }
        cashLogger.LogInformation("CashSeeder finished: {Applied} applied, {Skipped} skipped.", applied, skipped);
    }

    // Deprecation warning (#107 slice 4): Margin.Initial is the
    // legacy per-end-client opening-balance config. It still works as
    // a transition fallback inside ReserveOnSubmitMarginProvider, but
    // every populated key here means the operator is on the legacy
    // path and should migrate to Trading:Cash:Seeds[].
    var riskOpts = scope.ServiceProvider.GetRequiredService<IOptions<RiskOptions>>().Value;
#pragma warning disable CS0618 // Type or member is obsolete
    if (riskOpts.Margin.Initial.Count > 0)
    {
        var deprecationLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MarginInitialDeprecation");
        deprecationLogger.LogWarning(
            "Trading:Risk:Margin:Initial is DEPRECATED (#107 slice 4) and will be removed in a follow-up. "
            + "{Count} owner entry(ies) populated: [{Owners}]. "
            + "Migrate to Trading:Cash:Seeds[] for static opening balances and "
            + "Trading:Cash:SignupInitialBalance for self-service signup defaults.",
            riskOpts.Margin.Initial.Count,
            string.Join(", ", riskOpts.Margin.Initial.Keys));
    }
#pragma warning restore CS0618
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
app.MapAlgo();
app.MapPositions();
app.MapBalance();
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
/// rejections at submit time. Subsequent peer-initiated terminations
/// drive the gateway's own auto-reconnect loop (Phase 3/1b); this hosted
/// service only owns the cold-start connect.
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
    /// <summary>
    /// DNS-resolves <paramref name="endpoint"/> in <c>host:port</c> form into
    /// an <see cref="System.Net.IPEndPoint"/>. Shape validation lives in
    /// <see cref="ExchangeOptionsValidator"/>; this helper is invoked at first
    /// DI resolution by the Real-mode factory because it needs network access
    /// and shouldn't block <c>ValidateOnStart</c>.
    /// </summary>
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

