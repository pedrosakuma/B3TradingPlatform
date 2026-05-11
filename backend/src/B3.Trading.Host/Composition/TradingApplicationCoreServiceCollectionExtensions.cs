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
        services.AddSingleton<IExecutionEventSink, WebSocketExecutionEventSink>();
        services.AddSingleton<IAlgoEventSink, WebSocketAlgoEventSink>();
        services.AddSingleton<ExecutionReportProcessor>();
        services.AddSingleton<OrderSubmissionService>();
        services.AddSingleton<OrderCancelService>();
        services.AddSingleton<OrderModifyService>();

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
