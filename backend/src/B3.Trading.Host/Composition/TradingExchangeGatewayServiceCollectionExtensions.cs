using B3.Trading.Application;
using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.SubAccount;
using B3.Trading.Host.Hosted;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Host.Composition;

/// <summary>
/// Wires the wire-side gateway based on <see cref="ExchangeOptions.Mode"/>:
/// <list type="bullet">
///   <item><c>Stub</c>        → no-op <see cref="StubExchangeGateway"/>, no client wired (CI / smoke).</item>
///   <item><c>Mock</c>        → in-process <see cref="MockEntryPointClient"/> + <see cref="EntryPointClientGateway"/> (test seam, dev-loop).</item>
///   <item><c>Real</c>        → one upstream <c>EntryPointClient</c> per FirmConfig + <see cref="MultiFirmExchangeGateway"/>,
///         aggregated through <see cref="FirmGatewayRegistry"/> (which doubles as the single
///         <see cref="IEntryPointClient"/> consumed by <see cref="EntryPointExecutionReportRouter"/>).</item>
///   <item><c>Unavailable</c> → fail-closed <see cref="UnavailableExchangeGateway"/>; submits surface as 502
///         gateway-unavailable. Production-honest no-broker mode.</item>
/// </list>
///
/// <para>
/// <see cref="IExchangeGateway"/> is registered as a factory so the active
/// implementation is chosen at DI resolution time — late enough that
/// <c>WebApplicationFactory</c> test overrides via
/// <c>ConfigureAppConfiguration</c> are visible (the WebApplication
/// minimal-API builder reads pre-Build config eagerly, so we can't switch on
/// the option value at registration time).
/// </para>
///
/// <para>
/// Real-mode hosted services (<see cref="FirmGatewayConnector"/> + ER
/// router) MUST be registered pre-Build, so we still need an early read for
/// that branch only. Tests never exercise Real mode, so the early-vs-late
/// split is invisible to them; the only Mode they switch is
/// Stub/Mock/Unavailable.
/// </para>
/// </summary>
public static class TradingExchangeGatewayServiceCollectionExtensions
{
    public static IServiceCollection AddTradingExchangeGateway(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ExchangeOptions>()
            .Bind(configuration.GetSection(ExchangeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ExchangeOptions>, ExchangeOptionsValidator>();

        var exchangeSection = configuration.GetSection(ExchangeOptions.SectionName);
        var earlyMode = exchangeSection["Mode"];
        var earlyIsReal = string.Equals(earlyMode, nameof(ExchangeMode.Real), StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrEmpty(earlyMode) && exchangeSection.GetValue("UseRealEntryPointClient", false));

        services.AddSingleton<StubExchangeGateway>();
        services.AddSingleton<UnavailableExchangeGateway>();

        if (earlyIsReal)
        {
            services.AddSingleton<FirmGatewayRegistry>(sp =>
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

                    // #126. Materialise the access key via the central
                    // resolver so file-mounted secrets + the legacy flat
                    // AccessKey shape both flow through the same code
                    // path (with file-mode enforcement on Linux).
                    var accessKey = FirmCredentialResolver.ResolveAccessKey(
                        firm, lf.CreateLogger($"FirmCredentialResolver[{firm.FirmId}]"));

                    var clientOpts = new B3.EntryPoint.Client.EntryPointClientOptions
                    {
                        Endpoint = ep,
                        SessionId = firm.SessionId,
                        SessionVerId = resolvedVerId,
                        EnteringFirm = firm.EnteringFirm,
                        Credentials = B3.EntryPoint.Client.EntryPointClientOptions.AccessKey(accessKey),
                        KeepAliveIntervalMs = firm.KeepAliveIntervalMs,
                        SenderLocation = firm.SenderLocation,
                        EnteringTrader = firm.EnteringTrader,
                        SessionStateStore = stateStore,
                        Logger = lf.CreateLogger($"B3.EntryPoint.Client[{firm.FirmId}]"),
                    };
                    var upstream = new B3.EntryPoint.Client.EntryPointClient(clientOpts);
                    var gwLogger = lf.CreateLogger<B3EntryPointClientGateway>();
                    var reactor = sp.GetService<IVenueDisconnectReactor>();
                    var riskOpts = sp.GetService<IOptionsMonitor<RiskOptions>>();
                    var subAccountMapper = sp.GetService<ISubAccountWireIdMapper>();
                    return new B3EntryPointClientGateway(upstream, firm.FirmId, resolvedVerId, gwLogger,
                        venueDisconnectReactor: reactor,
                        riskOptions: riskOpts,
                        subAccountWireIdMapper: subAccountMapper);
                });
                return new FirmGatewayRegistry(gateways);
            });
            services.AddSingleton<IEntryPointClient>(sp => sp.GetRequiredService<FirmGatewayRegistry>());
            services.AddSingleton<IFirmSessionStatusProvider>(sp => sp.GetRequiredService<FirmGatewayRegistry>());
            services.AddSingleton<MultiFirmExchangeGateway>(sp =>
                new MultiFirmExchangeGateway(sp.GetRequiredService<FirmGatewayRegistry>()));
            services.AddSingleton<EntryPointExecutionReportRouter>();
            services.AddHostedService<EntryPointRouterStarter>();
            services.AddHostedService<FirmGatewayConnector>();
        }
        else
        {
            services.AddSingleton<MockEntryPointClient>();
            services.AddSingleton<IEntryPointClient>(sp => sp.GetRequiredService<MockEntryPointClient>());
            services.AddSingleton<EntryPointClientGateway>(sp =>
            {
                var opts = sp.GetRequiredService<IOptions<ExchangeOptions>>().Value;
                var firmId = opts.Firms.FirstOrDefault()?.FirmId ?? "DEFAULT";
                return new EntryPointClientGateway(sp.GetRequiredService<IEntryPointClient>(), firmId);
            });
            services.AddSingleton<EntryPointExecutionReportRouter>();
            services.AddHostedService<EntryPointRouterStarter>();
        }

        services.AddSingleton<IExchangeGateway>(sp =>
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

        services.AddSingleton<ExchangeStatus>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<ExchangeOptions>>().Value;
            return ExchangeStatus.FromOptions(opts);
        });

        // #188: Api consumes IFirmDirectory only. ConfigFirmDirectory always reads
        // per-firm config from ExchangeOptions; FirmGatewayRegistry is optional
        // (only registered in Real mode) and overlays live FIXP session state when
        // present. The endpoint shape is identical regardless of mode.
        services.AddSingleton<IFirmDirectory>(sp =>
            new ConfigFirmDirectory(
                sp.GetRequiredService<IOptions<ExchangeOptions>>(),
                sp.GetService<FirmGatewayRegistry>()));

        return services;
    }
}
