using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Lifecycle;

/// <summary>
/// Kubernetes-/orchestrator-shaped lifecycle probes:
///
/// <list type="bullet">
///   <item><c>/live</c> — process is up. Always 200 unless the runtime
///   has died. Used by liveness probes to decide whether to restart.</item>
///   <item><c>/ready</c> — accepting traffic. 503 while draining. Used by
///   readiness probes / load balancers to decide whether to route requests.</item>
///   <item><c>/health</c> — rich JSON for humans + dashboards. Includes
///   uptime, drain state, persistence config snapshot.</item>
/// </list>
/// Mirrors the layout used by <c>B3MarketDataPlatform/WebSocketHost</c>.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/live", () => Results.Ok("alive"));

        app.MapGet("/ready", (DrainState drain) =>
            drain.IsDraining
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : Results.Ok("ready"));

        app.MapGet("/health", (HttpContext ctx, DrainState drain, IOptions<PersistenceOptions> persist) =>
        {
            var p = persist.Value;
            // ExchangeStatus is registered by Program.cs whenever any
            // gateway is wired; it is optional from the API project's
            // perspective so legacy test hosts that skip the wire-side
            // setup still serve /health.
            var exchange = ctx.RequestServices.GetService<ExchangeStatus>();
            // Live FIXP session state per firm. Only registered in Real
            // mode (FirmGatewayRegistry); Mock/Stub/Unavailable hosts get
            // null here and the response collapses to the legacy shape
            // (no firms[] array; readyForOrders driven by mode alone).
            var sessions = ctx.RequestServices.GetService<IFirmSessionStatusProvider>();
            return Results.Json(new
            {
                status = drain.IsDraining ? "draining" : "ready",
                uptime = drain.Uptime.ToString(@"hh\:mm\:ss"),
                startedAt = drain.StartedAt,
                persistence = new
                {
                    enabled = p.Enabled,
                    firmId = p.FirmId,
                    dataDirectory = p.DataDirectory,
                    snapshotInterval = p.SnapshotInterval,
                },
                exchange = exchange is null ? null : BuildExchangeBlock(exchange, sessions),
            });
        });

        return app;
    }

    /// <summary>
    /// Compose the <c>exchange</c> block of <c>/health</c>. When live session
    /// state is available (Real mode), <c>readyForOrders</c> is the AND of
    /// the configuration-level <see cref="ExchangeStatus.ReadyForOrders"/>
    /// and "every firm in <c>established</c>". A configured-but-disconnected
    /// gateway therefore reports <c>readyForOrders=false</c>, fixing the
    /// long-standing surfacing bug where the badge stayed green while
    /// submits were silently rejected by the SDK guard. Without a session
    /// provider we keep the legacy shape (no <c>firms[]</c>, ready by mode
    /// alone) so Mock/Stub/Unavailable smoke tests don't have to be retaught.
    /// </summary>
    private static object BuildExchangeBlock(ExchangeStatus exchange, IFirmSessionStatusProvider? sessions)
    {
        if (sessions is null)
        {
            return new
            {
                mode = exchange.Mode.ToString(),
                readyForOrders = exchange.ReadyForOrders,
                firmCount = exchange.FirmCount,
            };
        }

        var firms = sessions.Snapshot();
        var allEstablished = firms.Count == 0 || firms.All(f => f.IsEstablished);
        return new
        {
            mode = exchange.Mode.ToString(),
            readyForOrders = exchange.ReadyForOrders && allEstablished,
            firmCount = exchange.FirmCount,
            firms = firms.Select(f => new
            {
                firmId = f.FirmId,
                state = f.SessionState,
                reconnecting = f.IsReconnecting,
                sessionVerId = f.SessionVerId,
            }).ToArray(),
        };
    }
}
