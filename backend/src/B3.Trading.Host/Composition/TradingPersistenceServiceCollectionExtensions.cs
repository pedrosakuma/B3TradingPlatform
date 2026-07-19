using B3.Trading.Application.Persistence;
using B3.Trading.Application;
using B3.Trading.Application.Outbound;
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
            return o.Enabled
                ? new FileEventStore(o, sp.GetRequiredService<ILogger<FileEventStore>>())
                : new NullEventStore();
        });
        services.AddSingleton<IEventStoreHealth>(sp =>
            (IEventStoreHealth)sp.GetRequiredService<IEventStore>());
        services.AddSingleton<IReconciliationMarkerStore>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            return o.Enabled
                ? new FileReconciliationMarkerStore(o)
                : new InMemoryReconciliationMarkerStore();
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
        services.AddSingleton<OutboundProcessEpoch>();
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
        services.AddHostedService<SnapshotService>();
        // RFC §5.2 (F2). Resolve all registered IExecutionFanOutSink
        // singletons (WS hub channel sink, bot router) and snapshot
        // them into the dispatcher's flat array so the dispatch hot
        // path is allocation-free.
        services.AddSingleton<EventDispatcher>(sp => new EventDispatcher(
            sp.GetRequiredService<IEventStore>(),
            sp.GetServices<IExecutionFanOutSink>()));

        // #512 / #380. Runtime session-roll reactor. On a CONFIRMED roll
        // (Establish-reuse rejected → renegotiate, detected at connect or on a
        // live Renegotiated reconnect) it reaps un-acked PendingNew under the
        // dispatcher lock AND flags surviving Working/PartiallyFilled stale via
        // OrderStalenessService (operator-clearable; WAL-durable). The boot
        // reconcile stays conservative (PendingNew only) because it cannot tell
        // a reuse-reject from a benign verId advance. EventDispatcher is always
        // registered (NullEventStore when persistence is off); OrderStalenessService
        // is optional so reduced/mock compositions degrade to reap-only (the
        // reactor logs a warning).
        services.AddSingleton<IConnectSessionRollReactor>(sp => new PendingNewReapingConnectRollReactor(
            sp.GetRequiredService<WorkingOrderBook>(),
            sp.GetRequiredService<EventDispatcher>(),
            sp.GetRequiredService<ILogger<PendingNewReapingConnectRollReactor>>(),
            sp.GetService<OrderStalenessService>(),
            sp.GetService<TimeProvider>()));

        return services;
    }
}
