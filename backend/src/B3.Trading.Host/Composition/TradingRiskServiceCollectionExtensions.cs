using B3.Trading.Application;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Application.Risk.Checks;
using B3.Trading.Domain;
using B3.Trading.Host.MarketData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Host.Composition;

/// <summary>
/// Registers the pre-trade risk surface: kill-switch, halts, session phase,
/// staleness reactor, the full <see cref="IRiskCheck"/> set + pipeline,
/// margin provider + replace coordinator, and the throttle accountants.
/// Also brings up the market-data reference-price subscriber via the
/// pre-existing <see cref="MarketDataRegistration.AddTradingMarketData"/>
/// hook because the risk pipeline (PriceCollar / StaleReferencePrice
/// checks) is the only consumer of <c>IReferencePrice</c>.
/// </summary>
public static class TradingRiskServiceCollectionExtensions
{
    public static IServiceCollection AddTradingRisk(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RiskOptions>(
            configuration.GetSection(RiskOptions.SectionName));
        // Q4.1 (#301). Optional per-(firm, sub-account) cap config.
        // Bound from Trading:Risk:SubAccount; missing section is fine —
        // the resolver returns null and the check becomes a no-op.
        services.Configure<SubAccountRiskOptions>(
            configuration.GetSection(SubAccountRiskOptions.SectionName));
        services.Configure<SymbolDirectoryOptions>(
            configuration.GetSection(SymbolDirectoryOptions.SectionName));
        // OPT-D (#486, refs #454 Fase 2). SecurityDefinitionRegistry
        // is the projection target for SDK 0.5.0's SecurityDefinition
        // channel. Registered as a singleton so the host adapter
        // (writer) and SymbolDirectory (reader) share the same
        // instance; tests that build SymbolDirectory directly without
        // DI still get the v1 (config-only) behaviour because the
        // ctor with no registry argument remains.
        services.AddSingleton<B3.Trading.Application.MarketData.SecurityDefinitionRegistry>();
        services.AddSingleton(sp =>
            new SymbolDirectory(
                sp.GetRequiredService<IOptions<SymbolDirectoryOptions>>().Value,
                sp.GetService<B3.Trading.Application.MarketData.SecurityDefinitionRegistry>()));

        // OPT-E (#487, refs #482 OPT-readiness umbrella).
        // PriceBandRegistry is the projection target for SDK 0.6.0's
        // PriceBand channel (upstream pedrosakuma/B3MarketDataPlatform#56).
        // Registered as a singleton so the host adapter (writer) and
        // PriceBandCheck (reader) share the same instance; the
        // IPriceBandSource seam is also bound to it so tests can
        // substitute a stub without bringing the SDK. When
        // Trading:MarketData:EnablePriceBand=false the adapter skips
        // the subscribe-flag (no SDK callbacks fire), the registry
        // stays empty, the check fails open via PriceBandBypassedNoBand.
        services.AddSingleton<B3.Trading.Application.MarketData.PriceBandRegistry>();
        services.AddSingleton<B3.Trading.Application.MarketData.IPriceBandSource>(
            sp => sp.GetRequiredService<B3.Trading.Application.MarketData.PriceBandRegistry>());

        // #454 Fase 1. Per-symbol tick-size provider seam. Default impl
        // wraps the config-backed SymbolDirectory; Fase 2 will swap (or
        // chain in front of) an SDK-backed impl once upstream
        // pedrosakuma/B3MarketDataPlatform#55 ships SecurityDefinitionEvent.
        services.AddSingleton<B3.Trading.Application.MarketData.ITickSizeProvider,
            B3.Trading.Application.MarketData.SymbolDirectoryTickSizeProvider>();

        // OPT-B (#484). Per-symbol notional calculator seam — applies
        // OptionMetadata.ContractMultiplier so MaxNotional / margin
        // reserve / rolling-notional ledger don't silently
        // under-count option flow by ~100x. Same source of truth as
        // ITickSizeProvider above; Fase 2 swaps both when
        // SecurityDefinitionEvent lands.
        services.AddSingleton<B3.Trading.Application.MarketData.IMarketValueCalculator,
            B3.Trading.Application.MarketData.SymbolDirectoryMarketValueCalculator>();

        // Pre-trade risk: pipeline + checks + kill-switch + reference price +
        // margin provider. Each IRiskCheck registration is auto-discovered by
        // the RiskPipeline through the IEnumerable<IRiskCheck> ctor injection.
        services.AddSingleton<KillSwitchService>();
        services.AddSingleton<SymbolHaltService>();
        services.AddSingleton<SessionPhaseService>(_ =>
        {
            // #108 SessionPhase. Default is Continuous (back-compat); ops can pin
            // production to a stricter posture (e.g. Closed at boot, then flip via
            // the admin endpoint or feed) by setting Trading:SessionPhase:Default.
            var raw = configuration["Trading:SessionPhase:Default"];
            var def = !string.IsNullOrWhiteSpace(raw)
                && Enum.TryParse<SessionPhase>(raw, ignoreCase: true, out var parsed)
                ? parsed : SessionPhase.Continuous;
            return new SessionPhaseService(def);
        });
        services.AddSingleton<OrderStalenessService>();
        // Slice 2 of #132. Reactor reads the flag set off the Trading:AutoStale section.
        services.Configure<AutoStaleOptions>(configuration.GetSection(AutoStaleOptions.SectionName));
        services.AddSingleton<IVenueDisconnectReactor>(sp =>
            new OrderStaleningVenueReactor(
                sp.GetRequiredService<OrderStalenessService>(),
                sp.GetRequiredService<IOptions<AutoStaleOptions>>().Value,
                sp.GetService<TimeProvider>()));
        services.AddTradingMarketData(configuration);
        services.AddSingleton<ReserveOnSubmitMarginProvider>(sp =>
            new ReserveOnSubmitMarginProvider(
                sp.GetRequiredService<IOptionsMonitor<RiskOptions>>(),
                sp.GetRequiredService<ILogger<ReserveOnSubmitMarginProvider>>(),
                sp.GetRequiredService<CashLedger>()));
        services.AddSingleton<IMarginProvider>(sp =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<RiskOptions>>().CurrentValue;
            return opts.Margin.Enabled
                ? sp.GetRequiredService<ReserveOnSubmitMarginProvider>()
                : new NoOpMarginProvider();
        });
        // Slice 2 of #122: the replace coordinator shares the reservation
        // ledger with IMarginProvider, so it always points at the concrete
        // ReserveOnSubmitMarginProvider singleton — even when margin is
        // disabled the coordinator's Commit/Abort are harmless no-ops on an
        // empty ledger.
        services.AddSingleton<PendingReplacementRegistry>();
        services.AddSingleton<IReplaceMarginCoordinator>(sp =>
            sp.GetRequiredService<ReserveOnSubmitMarginProvider>());
        services.AddSingleton<IRiskCheck, KillSwitchCheck>();
        services.AddSingleton<IRiskCheck, SymbolHaltedCheck>();
        services.AddSingleton<IRiskCheck, SessionPhaseCheck>();
        services.AddSingleton<IRiskCheck, OrderTypeAllowedCheck>();
        // #473 (SDK 0.15.0). Pre-trade whitelist gate for the
        // routing instruction stamped on outbound orders. Default-
        // DENY: if a resolver returns a value but the resolved
        // scope has no AllowedRoutingInstructions whitelist, the
        // order is rejected. This is intentionally inverse to
        // OrderTypeAllowedCheck (default-allow) because routing
        // instructions carry fairness / conflict-of-interest
        // implications (e.g. BrokerOnly) — a value flowing into
        // an unconfigured scope is a config smell.
        services.AddSingleton<IRiskCheck, RoutingInstructionAllowedCheck>();
        services.AddSingleton<IRiskCheck, MinTickSizeCheck>();
        services.AddSingleton<IRiskCheck, MinLotSizeCheck>();
        services.AddSingleton<IRiskCheck, MaxQuantityCheck>();
        services.AddSingleton<IRiskCheck, MaxNotionalCheck>();
        services.AddSingleton<IRiskCheck, MinNotionalCheck>();
        services.AddSingleton<IRiskCheck, PositionLimitCheck>();
        services.AddSingleton<IRiskCheck, RollingNotionalCheck>();
        services.AddSingleton<IRiskCheck, OrderRateLimitCheck>();
        services.AddSingleton<IRiskCheck, MaxOpenOrdersCheck>();
        services.AddSingleton<IRiskCheck, SubAccountLimitsCheck>();
        services.AddSingleton<IRiskCheck, NoNakedShortCheck>();
        services.TryAddSingleton<IBeneficialOwnerResolver, OptionsBeneficialOwnerResolver>();
        services.AddSingleton<IRiskCheck, SelfTradePreventionCheck>();
        services.AddSingleton<IRiskCheck, PriceCollarCheck>();
        services.AddSingleton<IRiskCheck, PriceBandCheck>();
        services.AddSingleton<IRiskCheck, StaleReferencePriceCheck>();
        // Q1.2 (#254). Stop-trigger / IOC-FOK-leftover / GFA-phase /
        // GTD-bounds gates for the new Q1.1 order surface.
        // IPhaseProvider defaults to NoPhaseProvider until #257 wires
        // the auction-MD-driven implementation — see IPhaseProvider.cs.
        services.TryAddSingleton<IPhaseProvider, NoPhaseProvider>();
        services.AddSingleton<IRiskCheck, StopTriggerCheck>();
        services.AddSingleton<IRiskCheck, IocFokMarketWithLeftoverCheck>();
        services.AddSingleton<IRiskCheck, GoodForAuctionPhaseCheck>();
        services.AddSingleton<IRiskCheck, GtdBoundsCheck>();
        services.AddSingleton<RiskPipeline>();

        // Throttle accountants (slice 7). TimeProvider is fetched from DI so
        // tests can substitute a FakeTimeProvider; production resolves to
        // TimeProvider.System via the registration below.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RollingNotionalAccountant>();
        services.AddSingleton<OrderRateAccountant>();
        services.AddSingleton<IRiskAccountant>(sp => sp.GetRequiredService<RollingNotionalAccountant>());
        services.AddSingleton<IRiskAccountant>(sp => sp.GetRequiredService<OrderRateAccountant>());
        services.AddSingleton<CompositeRiskAccountant>();
        services.AddHostedService<ThrottleLedgerSweeper>();

        return services;
    }
}
