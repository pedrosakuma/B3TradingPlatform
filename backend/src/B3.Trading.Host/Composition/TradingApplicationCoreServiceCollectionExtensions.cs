using B3.Trading.Api.Lifecycle;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.UserBots;
using B3.Trading.Api.WebSockets;
using B3.Trading.Host.Lifecycle;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace B3.Trading.Host.Composition;

/// <summary>
/// Registers the in-process application core: domain registries, the order
/// books, the execution-report processor + sinks, the algo engine/scheduler
/// signal channel and the drain/lifecycle gate. These are all plain
/// singletons with no external dependencies — kept together so the order of
/// registration is preserved verbatim from pre-#187 Program.cs.
/// </summary>
public static class TradingApplicationCoreServiceCollectionExtensions
{
    public static IServiceCollection AddTradingApplicationCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Application-layer singletons: registries, books, processor, sink.
        services.AddSingleton<EndClientRegistry>();
        services.AddSingleton<ClOrdIdPrefixRegistry>();
        services.AddSingleton<OrderOwnershipMap>();
        services.AddSingleton<WorkingOrderBook>();
        services.AddSingleton<AlgoBook>();
        services.AddSingleton<AlgoIdRegistry>();
        services.AddSingleton<PositionKeeper>();
        services.AddSingleton<CashLedger>();
        services.AddSingleton<CashKeeper>();
        // Q2.3 (#270). Fee calculator + keeper.
        // FeeOptions is bound from Trading:Fees so the calculator's
        // IOptionsMonitor.CurrentValue picks up hot-reload changes per
        // call. Keeper is a singleton so live folds + replay folds +
        // snapshot capture all see the same instance.
        services.Configure<FeeOptions>(configuration.GetSection(FeeOptions.SectionName));
        services.AddSingleton<IFeeCalculator, BpsFeeCalculator>();
        services.AddSingleton<FeeKeeper>();
        services.AddSingleton<PnlKeeper>();
        services.AddSingleton<InMemoryUserBotCredentialRegistry>();
        services.AddSingleton<IUserBotCredentialRegistry>(sp =>
            sp.GetRequiredService<InMemoryUserBotCredentialRegistry>());
        services.AddSingleton<InMemoryUserBotSessionRegistry>();
        services.AddSingleton<IUserBotSessionRegistry>(sp =>
            sp.GetRequiredService<InMemoryUserBotSessionRegistry>());
        // Sub-issue #171 (E): bot order mapping side-registry. Singleton so
        // snapshot capture/restore + WAL replay all share the same instance.
        services.AddSingleton<InMemoryUserBotOrderMappingRegistry>();
        services.AddSingleton<IUserBotOrderMappingRegistry>(sp =>
            sp.GetRequiredService<InMemoryUserBotOrderMappingRegistry>());
        services.AddSingleton<SubscriptionManager>();
        // RFC §5.2 (F2). The WS hub sink is channel-backed and runs as
        // a hosted service so its drain task starts/stops with the host.
        // Both the IExecutionEventSink (synthetic publishes from
        // OrderStalenessService etc.) and the IExecutionFanOutSink
        // (dispatcher fan-out under the lock) routes funnel into the
        // same per-sink channel — see the type doc-comment for ordering
        // semantics.
        services.AddSingleton<WebSocketExecutionEventSink>();
        services.AddSingleton<IExecutionEventSink>(sp => sp.GetRequiredService<WebSocketExecutionEventSink>());
        services.AddSingleton<IExecutionFanOutSink>(sp => sp.GetRequiredService<WebSocketExecutionEventSink>());
        services.AddHostedService(sp => sp.GetRequiredService<WebSocketExecutionEventSink>());
        services.AddSingleton<IAlgoEventSink, WebSocketAlgoEventSink>();

        // Q1.5 (#257). Auction state-store + public WS channels
        // (phases.${symbol} / auction.${symbol}). The store is wired
        // unconditionally (cheap; idle when the SDK never raises an
        // auction event) so IPhaseProvider is always available to the
        // risk pipeline. The auction sink doubles as the snapshot
        // resolver for the WS hub via IPublicChannelSnapshots — same
        // singleton routes both reads and writes.
        //
        // Coordination with #261: the canonical IPhaseProvider lives
        // in B3.Trading.Application.Risk and AddTradingRisk(...) below
        // performs a TryAddSingleton<NoPhaseProvider> stub. Because we
        // register the live AuctionStateStore-backed provider here
        // FIRST (Program.cs calls AddTradingApplicationCore before
        // AddTradingRisk), TryAdd in the risk module is a no-op and
        // GoodForAuctionPhaseCheck resolves the live provider.
        services.AddSingleton<B3.Trading.Application.MarketData.AuctionStateStore>();
        services.AddSingleton<B3.Trading.Application.Risk.IPhaseProvider>(sp =>
            sp.GetRequiredService<B3.Trading.Application.MarketData.AuctionStateStore>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<B3.Trading.Application.MarketData.AuctionStateStore>());
        services.AddSingleton<WebSocketAuctionEventSink>();
        services.AddSingleton<IPublicChannelSnapshots>(sp =>
            sp.GetRequiredService<WebSocketAuctionEventSink>());
        services.AddHostedService(sp => sp.GetRequiredService<WebSocketAuctionEventSink>());

        services.AddSingleton<ExecutionReportProcessor>();
        services.AddSingleton<OrderSubmissionService>();
        services.AddSingleton<OrderCancelService>();
        services.AddSingleton<OrderModifyService>();

        // Q1.3 (#255). GTD expiration scheduler. Registered as both a
        // singleton (so OrderSubmissionService + ExecutionReportProcessor
        // can take an optional ctor dependency on it) AND as a hosted
        // service (so its StartAsync runs after WAL recovery has
        // populated WorkingOrderBook — RunRecoveryAndSeedingAsync
        // awaits before app.Run, IHostedService.StartAsync runs at
        // app.Run).
        services.AddSingleton<B3.Trading.Application.Scheduling.GtdExpirationScheduler>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<B3.Trading.Application.Scheduling.GtdExpirationScheduler>());

        // Algo engine signal channel + hosted consumer (RFC algo-orders-v0 §4.3).
        // In slice 5a the consumer body was a no-op reactor; slice 5b plugged in the
        // Iceberg state machine; slice 6 adds the AlgoScheduler hosted service that
        // drives TWAP slice firing on a separate thread (RFC §4.11 commitment 1).
        services.AddSingleton<AlgoSignalQueue>();
        services.AddSingleton<IAlgoSignalQueue>(sp => sp.GetRequiredService<AlgoSignalQueue>());
        services.AddHostedService<AlgoEngine>();
        services.AddHostedService<AlgoScheduler>();

        // Lifecycle: drain flag flipped on SIGTERM /
        // IHostApplicationLifetime.ApplicationStopping. Read by /ready (503 when
        // draining) and POST /orders (refuses new orders so in-flight can finish).
        services.AddSingleton<DrainState>();
        services.AddSingleton<B3.Trading.Application.Lifecycle.IDrainGate>(
            sp => sp.GetRequiredService<DrainState>());
        services.AddHostedService<DrainHostedService>();

        return services;
    }

    /// <summary>
    /// Persist DataProtection keys onto the data volume. JWT auth uses HMAC and
    /// doesn't depend on DataProtection, but ASP.NET still spins it up for
    /// cookie/antiforgery defaults. Without persistence it logs a warning every
    /// boot ("No XML encryptor configured. Key ... may be persisted ... in
    /// unencrypted form.") and regenerates keys on every container restart.
    /// </summary>
    public static IServiceCollection AddTradingDataProtection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var persistOpts = configuration
            .GetSection(PersistenceOptions.SectionName)
            .Get<PersistenceOptions>() ?? new PersistenceOptions();
        var keysDir = Path.Combine(persistOpts.DataDirectory, "dp-keys");
        Directory.CreateDirectory(keysDir);
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
            .SetApplicationName("b3-trading-host");
        return services;
    }
}
