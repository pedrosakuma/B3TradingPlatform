using B3.Trading.Application.Persistence;
using B3.Trading.Application;
using B3.Trading.Application.Outbound;
using B3.Trading.Host.Hosted;
using B3.Trading.Host.Lifecycle;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Host.Composition;

/// <summary>
/// Wires the event-sourced WAL + periodic snapshot pipeline plus the
/// recovery + EOD materialiser surface. Position/cash seed options are
/// bound here too — they are owned by the persistence lifecycle.
/// </summary>
public static class TradingPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddTradingPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<PersistenceOptions>(
            configuration.GetSection(PersistenceOptions.SectionName));
        services.Configure<PositionSeedOptions>(
            configuration.GetSection(PositionSeedOptions.SectionName));
        services.Configure<CashSeedOptions>(
            configuration.GetSection(CashSeedOptions.SectionName));
        services.Configure<SandboxCashOptions>(
            configuration.GetSection(SandboxCashOptions.SectionName));
        services.Configure<OutboundCommandProtectionOptions>(
            configuration.GetSection(OutboundCommandProtectionOptions.SectionName));

        // Persistence: event-sourced WAL + periodic snapshot. The IEventStore
        // implementation is chosen at resolution time from the bound options so
        // test-time config overrides (added via IHostBuilder.ConfigureAppConfiguration
        // after Program.cs finishes registering services) are honoured. When
        // Enabled=false, NullEventStore is wired and SnapshotService self-skips.
        services.AddSingleton<SnapshotStore>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            return new SnapshotStore(o.DataDirectory, o.FirmId);
        });
        services.AddSingleton<IEventStore>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            var fence = sp.GetRequiredService<ActiveHostFence>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            if (!fence.TryAcquire())
            {
                return new FaultedEventStore(
                    fence.Failure ?? new IOException("The active-host fence is unavailable."));
            }
            if (!o.Enabled)
                return new NullEventStore();
            try
            {
                return new FileEventStore(o, sp.GetRequiredService<ILogger<FileEventStore>>());
            }
            catch (Exception ex)
            {
                fence.RecordStorageFailure(ex);
                var diagnostic = PersistenceFaultDiagnostics.Describe(ex, o);
                loggerFactory
                    .CreateLogger("B3.Trading.PersistenceStartup")
                    .LogCritical(
                        ex,
                        diagnostic is null
                            ? "Persistence startup failed; readiness remains closed."
                            : "Persistence startup failed with {FaultCode}; readiness remains closed. Recommended action: {RecommendedAction}",
                        diagnostic?.Code,
                        diagnostic?.RecommendedAction);
                return new FaultedEventStore(ex);
            }
        });
        services.AddSingleton<IEventStoreHealth>(sp =>
            (IEventStoreHealth)sp.GetRequiredService<IEventStore>());
        services.AddSingleton<IReconciliationMarkerStore>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            if (!o.Enabled)
                return new InMemoryReconciliationMarkerStore();
            var fence = sp.GetRequiredService<ActiveHostFence>();
            if (!fence.TryAcquire())
            {
                return new FaultedReconciliationMarkerStore(
                    fence.Failure ?? new IOException("The active-host fence is unavailable."));
            }
            try
            {
                return new FileReconciliationMarkerStore(o);
            }
            catch (Exception ex)
            {
                fence.RecordStorageFailure(ex);
                return new FaultedReconciliationMarkerStore(ex);
            }
        });
        services.AddSingleton<ReconciliationResolutionWriter>();
        services.AddSingleton<IOutboundNonceSource, CryptographicOutboundNonceSource>();
        services.AddSingleton<IOutboundCommandProtector>(sp =>
        {
            var configured = sp.GetRequiredService<IOptions<OutboundCommandProtectionOptions>>().Value;
            if (configured.Keys.Count > 0
                || sp.GetRequiredService<IOptions<PersistenceOptions>>().Value.Enabled)
                return new AeadOutboundCommandProtector(
                    configured,
                    sp.GetRequiredService<IOutboundNonceSource>());

            var ephemeral = new OutboundCommandProtectionOptions
            {
                ActiveKeyId = "ephemeral-no-persistence",
                ActiveKeyVersion = 1,
                StableReferenceKeyId = "ephemeral-no-persistence",
                StableReferenceKeyVersion = 1,
                Keys =
                [
                    new OutboundCommandProtectionKeyOptions
                    {
                        KeyId = "ephemeral-no-persistence",
                        Version = 1,
                        KeyBase64 = Convert.ToBase64String(
                            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                    },
                ],
            };
            return new AeadOutboundCommandProtector(
                ephemeral,
                sp.GetRequiredService<IOutboundNonceSource>());
        });
        services.AddSingleton<OutboundMutationLedger>();
        services.AddSingleton<OutboundReconciliationService>();
        services.AddSingleton(_ => OutboundProcessEpoch.CreateUninitialized());
        services.AddSingleton<OutboundRecoveryState>();
        services.AddSingleton<IOutboundRecoveryGate>(sp =>
            sp.GetRequiredService<OutboundRecoveryState>());
        services.AddSingleton<ActiveHostFence>();
        services.AddSingleton<OutboundColdStartRecoveryCoordinator>();
        services.AddSingleton<RestOrderIdempotencyStore>();
        services.AddSingleton<NewOrderApprovalFactory>();
        services.AddSingleton<CancelReplaceApprovalFactory>();
        services.AddSingleton<ReconciliationMarkerRecovery>();
        services.AddSingleton<ColdStartLifecycleGuard>();
        services.AddSingleton<StateSnapshotter>();
        services.AddSingleton<EventReplayer>();
        services.AddSingleton<PersistenceRecovery>();
        services.AddSingleton<EodMaterialiser>();
        services.AddSingleton<IEodMaterialiser>(sp =>
        {
            // #188: Api consumes IEodMaterialiser only; the Persistence-disabled
            // case is satisfied by DisabledEodMaterialiser whose IsAvailable=false
            // makes the admin endpoint surface 409 cleanly.
            var opts = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            return opts.Enabled
                ? sp.GetRequiredService<EodMaterialiser>()
                : new DisabledEodMaterialiser();
        });
        services.AddSingleton<SnapshotService>();
        // RFC §5.2 (F2). Resolve all registered IExecutionFanOutSink
        // singletons (WS hub channel sink, bot router) and snapshot
        // them into the dispatcher's flat array so the dispatch hot
        // path is allocation-free.
        services.AddSingleton<EventDispatcher>(sp => new EventDispatcher(
            sp.GetRequiredService<IEventStore>(),
            sp.GetServices<IExecutionFanOutSink>()));
        services.AddHostedService<OutboundColdStartRecoveryHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<SnapshotService>());

        // #512 / #380. Runtime session-roll reactor. On a CONFIRMED roll
        // (Establish-reuse rejected → renegotiate, detected at connect or on a
        // live Renegotiated reconnect) it preserves un-acked PendingNew and
        // flags Working/PartiallyFilled stale via OrderStalenessService
        // (operator-clearable; WAL-durable). Session identity alone never
        // terminalises outbound state or releases capacity. EventDispatcher is
        // always registered (NullEventStore when persistence is off);
        // OrderStalenessService is optional in reduced/mock compositions.
        services.AddSingleton<IConnectSessionRollReactor>(sp => new PendingNewReapingConnectRollReactor(
            sp.GetRequiredService<WorkingOrderBook>(),
            sp.GetRequiredService<EventDispatcher>(),
            sp.GetRequiredService<ILogger<PendingNewReapingConnectRollReactor>>(),
            sp.GetService<OrderStalenessService>(),
            sp.GetService<TimeProvider>()));

        return services;
    }
}
