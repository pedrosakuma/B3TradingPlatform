using B3.Trading.Application.Persistence;
using B3.Trading.Application;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Host.Composition;

/// <summary>
/// Wires the event-sourced WAL + periodic snapshot pipeline plus the
/// recovery + EOD materialiser surface. Position/cash seed options are
/// bound here too — they are owned by the persistence lifecycle (applied
/// AFTER recovery in <see cref="TradingHostStartup.RunRecoveryAndSeedingAsync"/>).
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

        return services;
    }
}
