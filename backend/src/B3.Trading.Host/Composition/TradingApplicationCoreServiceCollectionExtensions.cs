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
        // Q4.7 (#307). Fill projection — keyed by {ClOrdId}:{cumQty}.
        // Singleton so the live ExecutionReportProcessor fold, WAL
        // replay (same processor path), and REST/WS reads all share
        // the same in-memory dictionary. Bounded FIFO eviction via
        // FillProjectionOptions.Capacity keeps memory in check.
        services.Configure<FillProjectionOptions>(
            configuration.GetSection(FillProjectionOptions.SectionName));
        services.AddSingleton<FillProjection>();
        // Q4.1 (#301). Sub-account model — registry + per-sub-account
        // position and P&L keepers. All singletons so live folds, WAL
        // replay, and snapshot capture/restore share the same instance.
        services.AddSingleton<SubAccountsRegistry>();
        services.AddSingleton<SubAccountPositionKeeper>();
        services.AddSingleton<SubAccountPnlKeeper>();
        // Q4.5 (#305). Audit log keeper + dispatcher-backed logger.
        // Both singletons so the live-dispatch path, recovery replay
        // and admin read endpoint all share the same in-memory ring
        // buffer. AuditLogger has a hard dep on EventDispatcher which
        // is registered in AddTradingPersistence; Program.cs orders
        // ApplicationCore → Persistence, so resolution at first use
        // (HTTP handlers) sees both wired.
        services.Configure<B3.Trading.Application.Audit.AuditLogOptions>(
            configuration.GetSection(B3.Trading.Application.Audit.AuditLogOptions.SectionName));
        services.AddSingleton<B3.Trading.Application.Audit.AuditLogKeeper>();
        services.AddSingleton<B3.Trading.Application.Audit.IAuditLogger, B3.Trading.Application.Audit.AuditLogger>();
        // Q4.8 (#308). CVM 35/505 transaction-report export. The
        // source enumerator + writer are stateless singletons — they
        // own no per-request state and the writer's per-firm-day LGPD
        // hash seed comes from CvmReportOptions, so a singleton lets
        // the endpoint resolve them without per-request allocation.
        // Nothing is persisted; both depend only on IEventStore +
        // OrderOwnershipMap which are already wired above.
        services.Configure<B3.Trading.Application.Reports.Cvm.CvmReportOptions>(
            configuration.GetSection(B3.Trading.Application.Reports.Cvm.CvmReportOptions.SectionName));
        services.AddSingleton<B3.Trading.Application.Reports.Cvm.CvmReportSource>();
        services.AddSingleton<B3.Trading.Application.Reports.Cvm.CvmReportWriter>();
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

        // #386. balance.me WS fan-out — bridges CashLedger.BalanceChanged
        // (fills + fees + opening seed) to subscribed clients. Singleton +
        // hosted service so the drain task starts/stops with the host;
        // the event subscription itself attaches in the ctor so deltas
        // queued before StartAsync run are picked up at start.
        services.AddSingleton<WebSocketBalanceFanOut>();
        services.AddHostedService(sp => sp.GetRequiredService<WebSocketBalanceFanOut>());
        // RFC §5.2 (F2). The WS hub sink is channel-backed and runs as
        // a hosted service so its drain task starts/stops with the host.
        // Both the IExecutionEventSink (synthetic publishes from
        // OrderStalenessService etc.) and the IExecutionFanOutSink
        // (dispatcher fan-out under the lock) routes funnel into the
        // same per-sink channel — see the type doc-comment for ordering
        // semantics.
        services.AddSingleton<WebSocketExecutionEventSink>();
        services.AddSingleton<IExecutionFanOutSink>(sp => sp.GetRequiredService<WebSocketExecutionEventSink>());
        services.AddHostedService(sp => sp.GetRequiredService<WebSocketExecutionEventSink>());
        services.AddSingleton<IAlgoEventSink, WebSocketAlgoEventSink>();

        // Q4.6 (#306). Compliance drop-copy fan-out: a parallel WS sink
        // that fans every captured ExecutionEvent out to firm-scoped
        // drop-copy subscribers (orders / fills / cancels). Registered
        // as an IExecutionFanOutSink (dispatcher main path,
        // target=DropCopy); synthetic publishes from
        // OrderStalenessService / WAL-backpressure fallback also reach
        // it via the composite IExecutionEventSink wired below.
        services.AddSingleton<B3.Trading.Api.WebSockets.DropCopy.DropCopyManager>();
        services.AddSingleton<B3.Trading.Api.WebSockets.DropCopy.DropCopyExecutionEventSink>();
        services.AddSingleton<IExecutionFanOutSink>(sp =>
            sp.GetRequiredService<B3.Trading.Api.WebSockets.DropCopy.DropCopyExecutionEventSink>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<B3.Trading.Api.WebSockets.DropCopy.DropCopyExecutionEventSink>());

        // Composite IExecutionEventSink. The single IExecutionEventSink
        // dependency on OrderStalenessService (and the
        // EntryPointExecutionReportRouter WAL-backpressure fallback) is
        // wrapped so synthetic Publish() calls reach BOTH the per-user
        // WS hub AND the drop-copy fan-out — without it, a
        // suspect-stale flag or a WAL-backpressured ER would surface on
        // orders.me but go unseen by compliance, which would defeat
        // the "all traffic" guarantee of the drop-copy feed.
        services.AddSingleton<IExecutionEventSink>(sp =>
            new CompositeExecutionEventSink(
                sp.GetRequiredService<WebSocketExecutionEventSink>(),
                sp.GetRequiredService<B3.Trading.Api.WebSockets.DropCopy.DropCopyExecutionEventSink>()));

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

        // #370 Stage A. Bridges venue-originated trading-status deltas
        // observed by the market-data adapter into SymbolHaltService,
        // audited via SymbolHaltToggledEvent { Origin = Venue }. Lives
        // here (next to the other MD-driven singletons) so wiring stays
        // colocated; the subscriber itself is in B3.Trading.Application
        // because the service it bridges to is.
        services.AddSingleton<B3.Trading.Application.Risk.VenueHaltSubscriber>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<B3.Trading.Application.Risk.VenueHaltSubscriber>());

        services.AddSingleton<WebSocketAuctionEventSink>();
        services.AddHostedService(sp => sp.GetRequiredService<WebSocketAuctionEventSink>());

        // #394. The per-symbol book.${symbol} (L2) and bookmbo.${symbol}
        // (L3) trading-host fan-out channels were deprecated in favour of
        // having the FE consume B3MarketDataPlatform directly — see issue
        // #394 + RFC. The matching IL2BookView infrastructure remains
        // wired above because MboPegBookPump still consumes BookChanged
        // for pegged-algo recalculation.
        services.AddSingleton<IPublicChannelSnapshots>(sp =>
            sp.GetRequiredService<WebSocketAuctionEventSink>());

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

        // #351 — IOC/FOK silent-drop watchdog. Defensive against
        // upstream B3MatchingPlatform#357 (Limit/IOC against empty
        // opposite book silently drops without an ER). Singleton so
        // OrderSubmissionService.Register and
        // ExecutionReportProcessor.OnOrderTerminal both target the
        // same timer registry.
        services.Configure<B3.Trading.Application.Scheduling.IocFokWatchdogOptions>(
            configuration.GetSection(B3.Trading.Application.Scheduling.IocFokWatchdogOptions.SectionName));
        services.AddSingleton<B3.Trading.Application.Scheduling.IocFokWatchdog>();

        // Algo engine signal channel + hosted consumer (RFC algo-orders-v0 §4.3).
        // In slice 5a the consumer body was a no-op reactor; slice 5b plugged in the
        // Iceberg state machine; slice 6 adds the AlgoScheduler hosted service that
        // drives TWAP slice firing on a separate thread (RFC §4.11 commitment 1).
        // Q3.1 (#281) wires the VWAP volume-curve estimator as a singleton.
        // The MarketDataVolumePump hosted service (#294 P1#1A) bridges
        // IMarketDataSubscriber.Trade → VolumeCurveEstimator.RecordTrade so
        // the engine sees the venue's live intraday volume; without the pump
        // the estimator stays empty and the engine falls back to uniform CDF.
        services.AddSingleton<AlgoSignalQueue>();
        services.AddSingleton<IAlgoSignalQueue>(sp => sp.GetRequiredService<AlgoSignalQueue>());
        services.AddSingleton<B3.Trading.Application.MarketData.VolumeCurveEstimator>();
        // Pump is registered as a concrete singleton AND wired into the
        // hosted-service collection through the same instance so AlgoEngine
        // can take an optional ctor dependency on it for the per-VWAP
        // EnsureSubscribedAsync demand-subscribe (#294 pass-2 P1) without
        // splitting startup ordering across two instances.
        services.AddSingleton<B3.Trading.Application.MarketData.MarketDataVolumePump>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<B3.Trading.Application.MarketData.MarketDataVolumePump>());
        // Pass-1 review (#295) P1#1. Per-POV scheduling progress book
        // — restores cumulative-market-volume baseline on restart so
        // POV does not under-slice while VolumeCurveEstimator's in-memory
        // buckets re-warm from post-restart prints.
        services.AddSingleton<PovProgressBook>();
        // Pass-1 review (#296) P1-C. Per-Pegged in-flight repeg-cycle
        // marker book — restores RepegPending + expected-cancel
        // marker on restart so a post-restart cancel-ack ER does not
        // suspend the parent (it routes through SubmitNextSliceAsync
        // and places the replacement child instead).
        services.AddSingleton<PeggedRepegBook>();
        // Q3.3 (#283). Pegged book-top cache + pump follow the
        // MarketDataVolumePump pattern: singleton + hosted service
        // resolving the same instance so the engine takes an optional
        // ctor dep and EnsureSubscribedAsync demand-subscribes per
        // Pegged parent without a second startup race.
        services.AddSingleton<B3.Trading.Application.MarketData.PegBookTopCache>();
        services.AddSingleton<B3.Trading.Application.MarketData.MarketDataPegBookPump>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<B3.Trading.Application.MarketData.MarketDataPegBookPump>());
        // Q3.6 Stage C (#286). Bridges live BBO from IL2BookView into
        // the Pegged book-top cache so PegRef.Mid / PegRef.Best resolve
        // to real best-bid/best-ask. No-op when IL2BookView is wired to
        // the InMemoryL2BookView fallback (MarketData off or
        // EnableBook=false): the legacy v1 last-trade fallback then
        // kicks in unchanged.
        services.AddSingleton<B3.Trading.Application.MarketData.MboPegBookPump>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<B3.Trading.Application.MarketData.MboPegBookPump>());
        services.AddSingleton<AlgoEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<AlgoEngine>());
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
