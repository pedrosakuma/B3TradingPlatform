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

        if (string.IsNullOrWhiteSpace(opts.WsUrl))
        {
            services.AddSingleton<IReferencePrice>(sp =>
                sp.GetRequiredService<ConfigReferencePrice>());
            return services;
        }

        services.AddMarketDataClient(o =>
        {
            o.Endpoint = new Uri(opts.WsUrl);
            o.AutoResubscribeOnReconnect = true;
            o.BackPressure = BackPressurePolicy.DropOldest;
        });

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

        return services;
    }
}
