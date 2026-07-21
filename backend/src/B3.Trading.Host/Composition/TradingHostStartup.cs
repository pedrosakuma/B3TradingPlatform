using B3.Trading.Api.Auth;
using B3.Trading.Api.Lifecycle;
using B3.Trading.Application;
using B3.Trading.Application.Identity;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Domain;
using B3.Trading.EntryPointListener;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Identity;
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
        var outboundLedger = app.Services.GetRequiredService<
            B3.Trading.Application.Outbound.OutboundMutationLedger>();
        B3.Trading.Application.Observability.MetricsRegistry.RegisterOutboundReconciliationSource(
            () => outboundLedger.GetReconciliationMetrics(DateTimeOffset.UtcNow));
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

    public static async Task RunIdentityDirectoryStartupAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<ITradingUserDirectory>();
        await directory.InitializeAsync();

        var opts = scope.ServiceProvider.GetRequiredService<IOptions<IdentityDirectoryOptions>>().Value;
        if (!opts.ImportLegacyUsersOnStartup)
        {
            await ValidateEntraModeHasLinkedAdminAsync(app, directory);
            return;
        }

        var legacy = scope.ServiceProvider.GetRequiredService<ILegacyUserSnapshotProvider>();
        var imports = legacy.SnapshotUsers()
            .Select(u => new LegacyTradingUserImport(
                u.Username,
                u.Username,
                u.Firm,
                u.Role))
            .ToArray();
        if (imports.Length == 0)
        {
            await ValidateEntraModeHasLinkedAdminAsync(app, directory);
            return;
        }

        var inserted = await directory.ImportLegacyUsersAsync(imports);
        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("IdentityDirectory")
            .LogInformation(
                "Identity directory legacy import completed: {Inserted} inserted from {Seen} legacy user(s).",
                inserted,
                imports.Length);
        await ValidateEntraModeHasLinkedAdminAsync(app, directory);
    }

    private static async Task ValidateEntraModeHasLinkedAdminAsync(WebApplication app, ITradingUserDirectory directory)
    {
        var auth = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (auth.ResolveMode() != AuthModeKind.Entra)
            return;

        if (!await directory.HasActiveExternallyLinkedAdminAsync())
        {
            throw new InvalidOperationException(
                "Trading:Auth:Mode=Entra requires at least one active admin with an explicit external identity binding. " +
                "Use the documented Hybrid bootstrap or offline recovery procedure before switching to Entra.");
        }
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
            // Pass-1 review fix (#259, P1#5): when ER injection is on,
            // refuse to boot if any seeded non-user role is still using
            // the committed dev-default password material — that would
            // make POST /admin/simulator/er trivially exploitable for
            // anyone with a copy of this repo.
            AdminCredentialDefaultGuard.Validate(
                exchangeOpts.AllowErInjection,
                authOpts.Users.Select(u => (u.Role, u.PasswordHash, u.Salt)));
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

        // #679. Self-service cash deposit boot guard: enforce Production
        // safety rules and emit a warning banner when enabled. Mirrors
        // ErInjectionBootGuard — letting any authenticated end-client mint
        // their own buying power is a real-money risk outside a sandbox.
        var sandboxCashOpts = app.Services.GetRequiredService<IOptions<SandboxCashOptions>>().Value;
        SandboxCashDepositBootGuard.Validate(app.Environment.EnvironmentName, sandboxCashOpts.AllowSelfCashDeposit, sandboxCashOpts.AllowSelfCashDepositInProduction);
        var sandboxCashWarning = SandboxCashDepositBootGuard.BuildWarning(app.Environment.EnvironmentName, sandboxCashOpts.AllowSelfCashDeposit, sandboxCashOpts.AllowSelfCashDepositInProduction);
        if (sandboxCashWarning is not null)
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SandboxCashDeposit").LogWarning("{Warning}", sandboxCashWarning);


        // Pass-1 review (#325) P1. CVM 35/505 LGPD opacification salt is
        // required everywhere; the TestOnly sentinel is only accepted in
        // Development (mirrors AuthSigningKeyValidator). Fail fast so an
        // unconfigured production host never ships effectively-unsalted
        // owner hashes in the regulator-facing XML.
        var cvmOpts = app.Services.GetRequiredService<IOptions<B3.Trading.Application.Reports.Cvm.CvmReportOptions>>().Value;
        cvmOpts.Validate(app.Environment.EnvironmentName);

        // #416. The factory default for Trading:Risk:Margin:Enabled is
        // now `true` so an operator who forgets to opt in does NOT get a
        // silently overspending account (CashLedger.ApplyFill is
        // non-blocking by design; the pre-trade guard lives in the
        // margin provider). When an operator explicitly opts out outside
        // Development, emit a loud warning so the drift is visible on
        // dashboards — mirrors ErInjectionBootGuard's warning posture
        // for "unsafe-but-allowed" configurations.
        var riskBootOpts = app.Services.GetRequiredService<IOptions<RiskOptions>>().Value;
        if (!riskBootOpts.Margin.Enabled
            && !string.Equals(app.Environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("MarginDisabled")
                .LogWarning(
                    "Trading:Risk:Margin:Enabled=false in environment '{Environment}'. "
                    + "Pre-trade cash reservation is OFF (NoOpMarginProvider) — buy orders "
                    + "can drive end-client cash ledgers negative without any guard. "
                    + "This is permitted but explicitly unsafe; flip to true (#416) unless "
                    + "this composition genuinely runs without ledger-backed accounts.",
                    app.Environment.EnvironmentName);
        }
    }

    /// <summary>
    /// Restores the latest snapshot, then replays every WAL event past it.
    /// The cold-start recovery hosted service invokes this while liveness is
    /// available and business ingress remains gated. Idempotent — safe on a
    /// fresh data dir, the NullEventStore, or after a graceful snapshot.
    /// Cash seeds are applied after snapshot restore but before WAL replay;
    /// position seeds are applied after recovery.
    /// </summary>
    public static Task RunRecoveryAndSeedingAsync(WebApplication app) =>
        RunRecoveryAndSeedingAsync(app.Services, CancellationToken.None);

    internal static async Task RunRecoveryAndSeedingAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        void ApplyCashSeeds()
        {
            var cashOpts = scope.ServiceProvider.GetRequiredService<IOptions<CashSeedOptions>>().Value;
            var ledger = scope.ServiceProvider.GetRequiredService<CashLedger>();
            var cashLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("CashSeeder");
            var authOpts = scope.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;

            foreach (var user in authOpts.Users)
            {
                if (string.IsNullOrWhiteSpace(user.Username)
                    || string.IsNullOrWhiteSpace(user.Firm))
                {
                    continue;
                }
                ledger.ResolveLegacyBalances(new Dictionary<string, string>
                {
                    [user.Username] = user.Firm,
                });
            }
            foreach (var seed in cashOpts.Seeds)
            {
                if (string.IsNullOrWhiteSpace(seed.EndClientId)
                    || string.IsNullOrWhiteSpace(seed.FirmId))
                {
                    continue;
                }
                ledger.ResolveLegacyBalances(new Dictionary<string, string>
                {
                    [seed.EndClientId] = seed.FirmId,
                });
            }
            ledger.EnsureNoUnmappedLegacyBalances();

            if (cashOpts.Seeds.Count == 0)
                return;

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
                if (string.IsNullOrWhiteSpace(seed.FirmId))
                {
                    cashLogger.LogWarning(
                        "Skipping malformed CashSeed for {Owner} (empty FirmId).",
                        seed.EndClientId);
                    continue;
                }
                var owner = new EndClientId(seed.EndClientId);
                if (ledger.SeedIfAbsent(seed.FirmId, owner, seed.InitialAvailable))
                {
                    applied++;
                    cashLogger.LogInformation(
                        "Seeded opening cash {Firm}/{Owner} = {Balance}.",
                        seed.FirmId, seed.EndClientId, seed.InitialAvailable);
                }
                else
                {
                    skipped++;
                    cashLogger.LogInformation(
                        "Skipped cash seed for {Firm}/{Owner}: balance already present from recovery.",
                        seed.FirmId, seed.EndClientId);
                }
            }
            cashLogger.LogInformation("CashSeeder finished: {Applied} applied, {Skipped} skipped.", applied, skipped);
        }

        var opts = scope.ServiceProvider.GetRequiredService<IOptions<PersistenceOptions>>().Value;
        if (opts.Enabled)
        {
            var recovery = scope.ServiceProvider.GetRequiredService<PersistenceRecovery>();
            await recovery.RunAsync(ApplyCashSeeds, cancellationToken);
        }
        else
        {
            ApplyCashSeeds();
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
            // PR #316 P1.1. Mirror the seed into the bucket-aware
            // realised-PnL store so the master bucket's avg-cost basis
            // is anchored to the seed BEFORE the first fill mutates
            // the aggregate keeper. Without this, a sub-account fill
            // would silently pollute the master statement-row avg
            // (the only other source of master avg was
            // PositionKeeper.AverageEntryPrice, which mixes master +
            // sub fills) and a master close after a seed would skip
            // RealizedPnlEvent emission entirely (bucket basis
            // empty → ApplyBucketFill returns 0).
            var subAccountPnl = scope.ServiceProvider.GetService<SubAccountPnlKeeper>();
            var seedLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("PositionSeeder");

            // PR #316 P2: real-mode users are namespaced under FIRM01 /
            // FIRM02 etc., but legacy PositionSeed entries land in
            // PositionKeeper.DefaultFirmId (a sentinel no real user is
            // ever authenticated under). When the operator has wired
            // more than one firm AND any seed still lacks an explicit
            // Firm value, log a loud one-shot warning so the silent
            // "naked-short rejects on first Sell" symptom does not
            // recur. The warning is informational; the seed is still
            // applied to whatever firm the seed itself names (explicit
            // > default).
            var authOpts = scope.ServiceProvider.GetRequiredService<IOptions<B3.Trading.Api.Auth.AuthOptions>>().Value;
            var configuredFirms = authOpts.Users
                .Select(u => string.IsNullOrWhiteSpace(u.Firm) ? "default" : u.Firm)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var unfirmedSeed = seedOpts.Seeds.Any(s => string.IsNullOrWhiteSpace(s.Firm));
            if (unfirmedSeed && configuredFirms.Length > 1)
            {
                seedLogger.LogWarning(
                    "PositionSeeder: {Count} seed(s) without an explicit Firm will land in "
                    + "PositionKeeper.DefaultFirmId, but {FirmCount} firms are configured under "
                    + "Trading:Auth:Users ([{Firms}]). Real-mode users authenticated under a firm "
                    + "will NOT see those positions — set Trading:Positions:Seeds[N]:Firm to the "
                    + "user's firm (typically FIRM01) so the naked-short gate does not block their "
                    + "first Sell.",
                    seedOpts.Seeds.Count(s => string.IsNullOrWhiteSpace(s.Firm)),
                    configuredFirms.Length,
                    string.Join(", ", configuredFirms));
            }

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
                var firm = string.IsNullOrWhiteSpace(seed.Firm) ? PositionKeeper.DefaultFirmId : seed.Firm;
                if (keeper.SeedIfAbsent(firm, owner, seed.Symbol, seed.Quantity, seed.AverageEntryPrice))
                {
                    applied++;
                    seedLogger.LogInformation(
                        "Seeded opening position {Firm}/{Owner}/{Symbol} = {Qty} @ {AvgPx}.",
                        firm, seed.EndClientId, seed.Symbol, seed.Quantity, seed.AverageEntryPrice);
                    subAccountPnl?.SeedMasterBucketBasisIfAbsent(
                        firm, owner.Value, seed.Symbol, seed.Quantity, seed.AverageEntryPrice);
                }
                else
                {
                    skipped++;
                    seedLogger.LogInformation(
                        "Skipped seed for {Firm}/{Owner}/{Symbol}: position already present from recovery.",
                        firm, seed.EndClientId, seed.Symbol);
                }
            }
            seedLogger.LogInformation("PositionSeeder finished: {Applied} applied, {Skipped} skipped.", applied, skipped);
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
