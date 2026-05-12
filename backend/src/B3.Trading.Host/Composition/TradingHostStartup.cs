using B3.Trading.Api.Auth;
using B3.Trading.Api.Lifecycle;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Domain;
using B3.Trading.EntryPointListener;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Host.Composition;

/// <summary>
/// Post-Build composition steps. Each helper is a self-contained unit
/// extracted verbatim from pre-#187 Program.cs so the boot-time effects
/// (metric source registration, fail-fast guards, recovery + opening
/// seeds) remain order-equivalent.
/// </summary>
internal static class TradingHostStartup
{
    /// <summary>
    /// Hook the slice-7 throttle ledgers into MetricsRegistry so the
    /// observable gauges have a source. Done after Build so the singletons
    /// are resolvable; safe to call multiple times (sources are
    /// last-write-wins).
    /// </summary>
    public static void RegisterMetricsSources(WebApplication app)
    {
        var rolling = app.Services.GetRequiredService<RollingNotionalAccountant>();
        var rate = app.Services.GetRequiredService<OrderRateAccountant>();
        B3.Trading.Application.Observability.MetricsRegistry.RegisterRollingNotionalSources(
            () => rolling.EndClientLedger.ActiveBucketCount,
            () => rolling.FirmLedger.ActiveBucketCount);
        B3.Trading.Application.Observability.MetricsRegistry.RegisterOrderRateSources(
            () => rate.EndClientLedger.ActiveBucketCount,
            () => rate.FirmLedger.ActiveBucketCount);
        var marginProvider = app.Services.GetService<ReserveOnSubmitMarginProvider>();
        if (marginProvider is not null)
        {
            // #153 follow-up: only register when the concrete provider
            // is in the container (Margin.Enabled=true). NoOp mode has
            // no reservations to count.
            B3.Trading.Application.Observability.MetricsRegistry.RegisterMarginReservationCountsSource(
                () => marginProvider.GetReservationCounts());
        }

        // Issue #234 — build-info gauges for the perf-v0 tunables
        // (RUNBOOK §1.3 / §1.4). Sourced from IOptionsMonitor so
        // a config reload (file-watcher or IConfigurationRoot.Reload())
        // is reflected on the next scrape without a host restart.
        var entryPointOpts = app.Services.GetRequiredService<IOptionsMonitor<EntryPointListenerOptions>>();
        B3.Trading.Application.Observability.MetricsRegistry.RegisterOutboundDrainShutdownTimeoutSource(
            () => entryPointOpts.CurrentValue.Buffers.OutboundDrainShutdownTimeout.TotalSeconds);
        var persistenceOpts = app.Services.GetRequiredService<IOptionsMonitor<PersistenceOptions>>();
        B3.Trading.Application.Observability.MetricsRegistry.RegisterGroupCommitMaxRecordsSource(
            () => persistenceOpts.CurrentValue.GroupCommitMaxRecords);
    }

    /// <summary>
    /// Refuse to boot in non-Development environments when:
    ///   * The JWT signing key is the well-known dev-only string (would
    ///     allow trivial token forgery).
    ///   * ER injection is enabled outside the explicit prod opt-out
    ///     (catastrophic blast radius).
    ///   * The FIXP listener config violates Production safety rules.
    /// All three guards also emit a loud warning banner so dashboards can
    /// alert on drift even when the boot succeeds.
    /// </summary>
    public static void ValidateBootGuards(WebApplication app)
    {
        // Fail-fast on weak / missing JWT signing key outside Development.
        var authOpts = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        AuthSigningKeyValidator.Validate(app.Environment.EnvironmentName, authOpts.SigningKey);

        // ER-injection safeguards (formerly Simulator-mode safeguards; #163
        // merged Simulator into Mock + AllowErInjection).
        var exchangeOpts = app.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value;
        if (exchangeOpts.AllowErInjection)
        {
            ErInjectionBootGuard.Validate(app.Environment.EnvironmentName, exchangeOpts.AllowErInjection, exchangeOpts.AllowErInjectionInProduction);
            var warning = ErInjectionBootGuard.BuildWarning(app.Environment.EnvironmentName, exchangeOpts.AllowErInjection, exchangeOpts.AllowErInjectionInProduction);
            if (warning is not null)
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ErInjection").LogWarning("{Warning}", warning);
            B3.Trading.Application.Observability.MetricsRegistry.ErInjectionEnabled.Add(1);
        }

        // FIXP listener boot guard: enforce Production safety rules and emit a
        // warning banner when the listener is active. Mirrors ErInjectionBootGuard.
        var listenerOpts = app.Services.GetRequiredService<IOptions<EntryPointListenerOptions>>().Value;
        EntryPointListenerBootGuard.Validate(app.Environment.EnvironmentName, listenerOpts);
        var listenerWarning = EntryPointListenerBootGuard.BuildWarning(app.Environment.EnvironmentName, listenerOpts);
        if (listenerWarning is not null)
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EntryPointListener").LogWarning("{Warning}", listenerWarning);
    }

    /// <summary>
    /// Synchronous recovery before any traffic is accepted: load latest
    /// snapshot, then replay every WAL event past it. Idempotent — safe to
    /// run on a fresh data dir, on the NullEventStore (no-op), or after a
    /// graceful shutdown that already snapshotted. Position/cash seeds are
    /// applied AFTER recovery so warm restarts always preserve the actual
    /// fills and seeds only fill slots recovery left empty.
    /// </summary>
    public static async Task RunRecoveryAndSeedingAsync(WebApplication app)
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
}
