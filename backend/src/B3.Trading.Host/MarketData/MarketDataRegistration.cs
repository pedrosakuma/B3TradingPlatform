using B3.MarketData.WebSocketClient;
using B3.Trading.Application.MarketData;
using B3.Trading.Application.Risk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace B3.Trading.Host.MarketData;

/// <summary>
/// Wires the live <see cref="IReferencePrice"/> path on top of the
/// B3MarketDataPlatform WebSocket SDK.
///
/// <para>
/// Activation is gated on <c>Trading:MarketData:WsUrl</c> so the dev
/// loop / no-op deployments pay zero cost — when WsUrl is unset we
/// fall back to <see cref="ConfigReferencePrice"/> exactly like before.
/// When it is set, we build:
/// </para>
/// <list type="number">
///   <item><see cref="MarketDataClient"/> via the SDK's DI extension
///         (transparent reconnect, auto-resubscribe, bounded
///         back-pressure with drop-oldest).</item>
///   <item>An adapter (<see cref="SdkMarketDataSubscriber"/>) translating
///         SDK event types into application-owned records.</item>
///   <item><see cref="MarketDataReferencePrice"/> as a singleton
///         resolved BOTH as <see cref="IReferencePrice"/> AND as
///         <see cref="IHostedService"/> (same instance) so DI is
///         forced to construct it before the hosted-service loop
///         attaches event handlers.</item>
/// </list>
///
/// <para>
/// Options are bound directly from <see cref="IConfiguration"/> at
/// registration time (not via <c>IOptionsSnapshot</c>) because the gate
/// must be evaluated before <c>builder.Build()</c> — the DataProtection
/// + auth gates do the same upstream.
/// </para>
/// </summary>
public static class MarketDataRegistration
{
    public static IServiceCollection AddTradingMarketData(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(MarketDataOptions.SectionName);
        var opts = section.Get<MarketDataOptions>() ?? new MarketDataOptions();

        services.Configure<MarketDataOptions>(section);

        // ConfigReferencePrice is always registered as a concrete
        // singleton — used either as the IReferencePrice (when MD is
        // off) or as the fallback wrapped by MarketDataReferencePrice.
        services.TryAddSingleton<ConfigReferencePrice>();
        services.TryAddSingleton(TimeProvider.System);

        // IL2BookView is registered unconditionally so MboPegBookPump
        // resolves in DI. The live wire-path adapter (SdkBookFeedAdapter,
        // backed by SDK 0.4.0 IBookFeed) replaces this singleton further
        // down when WsUrl + EnableBook are both on.
        services.TryAddSingleton<InMemoryL2BookView>();
        services.TryAddSingleton<IL2BookView>(sp => sp.GetRequiredService<InMemoryL2BookView>());

        // #394. IMboBookEventSource was the seam feeding the deprecated
        // bookmbo.${symbol} WS sink — removed entirely. The L2 view
        // (IL2BookView / SdkBookFeedAdapter / InMemoryL2BookView) stays
        // because MboPegBookPump consumes BookChanged for algo recalc.

        if (string.IsNullOrWhiteSpace(opts.WsUrl))
        {
            services.AddSingleton<IReferencePrice>(sp =>
                sp.GetRequiredService<ConfigReferencePrice>());

            // Q1.5 (#257). AuctionStateStore needs an IMarketDataSubscriber
            // unconditionally so the risk pipeline (IPhaseProvider) and
            // the public phases.* / auction.* WS channels resolve in DI.
            // When the live feed is off we wire a no-op subscriber that
            // never raises events; the store stays empty, GetPhase
            // returns Unknown, snapshots are empty.
            services.AddSingleton<IMarketDataSubscriber, NullMarketDataSubscriber>();
            return services;
        }

        services.AddMarketDataClient(o =>
        {
            o.Endpoint = new Uri(opts.WsUrl);
            o.AutoResubscribeOnReconnect = true;
            o.BackPressure = BackPressurePolicy.DropOldest;
        });

        // SDK 0.4.0 (B3MarketDataPlatform #43 / #44 / #53) materialized
        // book layer — opt-in via MarketDataOptions.EnableBook. When on,
        // BookFeed attaches to the already-registered MarketDataClient
        // and the host-side SdkBookFeedAdapter replaces the no-op
        // InMemoryL2BookView registered above so MboPegBookPump sees live
        // BBO + depth ladders.
        if (opts.EnableBook)
        {
            services.AddMarketDataClient(_ => { }).WithBookFeed();
            services.AddSingleton<SdkBookFeedAdapter>(sp =>
                new SdkBookFeedAdapter(sp.GetRequiredService<IBookFeed>()));
            services.Replace(ServiceDescriptor.Singleton<IL2BookView>(sp =>
                sp.GetRequiredService<SdkBookFeedAdapter>()));
        }

        services.AddSingleton<IMarketDataSubscriber, SdkMarketDataSubscriber>();

        services.AddSingleton<MarketDataReferencePrice>(sp =>
            new MarketDataReferencePrice(
                sp.GetRequiredService<IMarketDataSubscriber>(),
                sp.GetRequiredService<ConfigReferencePrice>(),
                sp.GetRequiredService<IOptions<MarketDataOptions>>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MarketDataReferencePrice>>()));

        services.AddSingleton<IReferencePrice>(sp =>
            sp.GetRequiredService<MarketDataReferencePrice>());

        // Same singleton instance routed as IHostedService so the host
        // starts the subscriber loop AFTER DI has already attached our
        // event handlers in MarketDataReferencePrice's constructor.
        services.AddHostedService(sp => sp.GetRequiredService<MarketDataReferencePrice>());

        // Pass-1 review (#278) P1#3. Bridge MarketDataReferencePrice
        // ticks to the pnl.me WS channel so subscribers' unrealized
        // P&L tracks refprice without needing a fill in between.
        // Wired only here (i.e. only when the live MD feed is on) AND
        // only when the application-core composition has registered
        // SubscriptionManager + PnlKeeper + PositionKeeper. The
        // SubscriptionManager check keeps the MarketData-only test
        // scenarios (which don't compose the full hub) green.
        if (services.Any(d => d.ServiceType == typeof(B3.Trading.Api.WebSockets.SubscriptionManager))
            && services.Any(d => d.ServiceType == typeof(B3.Trading.Application.PnlKeeper))
            && services.Any(d => d.ServiceType == typeof(B3.Trading.Application.PositionKeeper)))
        {
            services.AddSingleton<B3.Trading.Api.WebSockets.PnlRefPriceFanOut>();
            services.AddHostedService(sp =>
                sp.GetRequiredService<B3.Trading.Api.WebSockets.PnlRefPriceFanOut>());
        }

        return services;
    }
}
