using B3.Trading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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

        app.MapGet("/health", (DrainState drain, IOptions<PersistenceOptions> persist) =>
        {
            var p = persist.Value;
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
            });
        });

        return app;
    }
}
