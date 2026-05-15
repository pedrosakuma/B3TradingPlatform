using B3.Trading.Api;
using B3.Trading.Api.Auth;
using B3.Trading.Api.Lifecycle;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.EntryPointListener;
using B3.Trading.EntryPointListener.Hosting.Admin;
using B3.Trading.Host.Lifecycle;
using B3.Trading.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace B3.Trading.Host.Composition;

/// <summary>
/// Maps every public HTTP/WebSocket endpoint surface exposed by the trading
/// host. Mounted in registration order so Map* side-effects (route table
/// + endpoint metadata) match pre-#187 Program.cs exactly.
/// </summary>
public static class TradingEndpointsExtensions
{
    public static WebApplication MapTradingEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => Results.Ok(new { service = "B3TradingPlatform", status = "bootstrap" }));
        app.MapHealth();

        app.MapAuth();
        app.MapOrders();
        app.MapAlgo();
        app.MapPositions();
        app.MapBalance();
        app.MapPolicy();
        app.MapAdmin();
        {
            // #188: simulator/er moved to Infrastructure (it's the only consumer
            // of MockEntryPointClient + ExecutionReportEnvelope). Mounted here
            // conditionally so the Mock-only route stays gated identically to
            // the legacy in-AdminEndpoints check.
            var exchangeOpts = app.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value;
            if (exchangeOpts.ResolveMode() == ExchangeMode.Mock && exchangeOpts.AllowErInjection)
            {
                app.MapSimulatorEndpoints();
            }
        }
        {
            var lo = app.Services.GetRequiredService<IOptions<EntryPointListenerOptions>>().Value;
            if (lo.Enabled) app.MapAdminFixp();
        }
        app.MapUserBotCredentials();
        app.MapWebSocketHub();

        return app;
    }
}
