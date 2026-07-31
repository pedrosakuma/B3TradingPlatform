using B3.Trading.Api.Lifecycle;
using B3.Trading.Application.Identity;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Outbound;
using B3.Trading.EntryPointListener;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Identity;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace B3.Trading.Host.Lifecycle;

/// <summary>
/// Kubernetes-/orchestrator-shaped lifecycle probes:
///
/// <list type="bullet">
///   <item><c>/live</c> — process is up. Always 200 unless the runtime
///   has died. Used by liveness probes to decide whether to restart.</item>
///   <item><c>/ready</c> — order ingress is safe: active-host fence held,
///   outbound recovery resolved, not draining, identity and WAL healthy, and
///   every required exchange session established.</item>
///   <item><c>/health</c> — rich JSON for humans + dashboards. Includes
///   uptime, drain state, persistence config snapshot.</item>
/// </list>
/// Mirrors the layout used by <c>B3MarketDataPlatform/WebSocketHost</c>.
///
/// <para>Lives in the Host project (#188 layering refactor) because the
/// rich /health body composes Infrastructure-owned types
/// (<see cref="ExchangeStatus"/>, <see cref="PersistenceOptions"/>) and
/// listener-owned types (<see cref="EntryPointListenerOptions"/>,
/// <see cref="Hosting.BotSessionConnectionDirectory"/>) that the Api layer
/// must not reference.</para>
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/live", () => Results.Ok("alive"));

        app.MapGet("/ready", async (
            HttpContext ctx,
            DrainState drain,
            ITradingUserDirectory directory,
            IEventStoreHealth wal,
            ExchangeStatus exchange,
            IOutboundRecoveryGate recovery,
            ActiveHostFence fence,
            CancellationToken ct) =>
        {
            var sessions = ctx.RequestServices.GetService<IFirmSessionStatusProvider>();
            if (drain.IsDraining
                || !wal.IsHealthy
                || !fence.IsHeld
                || !recovery.IsReady
                || !IsExchangeReady(exchange, sessions))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var identity = await directory.CheckHealthAsync(ct);
            return identity.Ready
                ? Results.Ok("ready")
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        });

        app.MapGet("/health", async (HttpContext ctx, DrainState drain, IOptions<PersistenceOptions> persist, IOptions<IdentityDirectoryOptions> identityOptions, ITradingUserDirectory directory, IEventStoreHealth wal, IOutboundRecoveryGate recovery, OutboundMutationLedger outboundLedger, ActiveHostFence fence, CancellationToken ct) =>
        {
            var p = persist.Value;
            var identity = await directory.CheckHealthAsync(ct);
            var exchange = ctx.RequestServices.GetService<ExchangeStatus>();
            var sessions = ctx.RequestServices.GetService<IFirmSessionStatusProvider>();
            var reconciliation = outboundLedger.GetReconciliationHealth(DateTimeOffset.UtcNow);
            var recoveryFirms = recovery.Snapshot();
            var faultDetails = PersistenceFaultDiagnostics.Describe(wal.TerminalFault, p);

            // FIXP listener status
            var listenerOpts = ctx.RequestServices.GetService<IOptions<EntryPointListenerOptions>>()?.Value;
            var sessionDir = ctx.RequestServices.GetService<B3.Trading.EntryPointListener.Hosting.BotSessionConnectionDirectory>();
            object? entryPointListener = null;
            if (listenerOpts is not null)
            {
                entryPointListener = new
                {
                    enabled = listenerOpts.Enabled,
                    listening = listenerOpts.Enabled,
                    activeSessions = sessionDir?.ActiveCount ?? 0,
                };
            }

            return Results.Json(new
            {
                status = drain.IsDraining
                    ? "draining"
                    : identity.Ready
                        && wal.IsHealthy
                        && fence.IsHeld
                        && recovery.IsReady
                        && (exchange is null || IsExchangeReady(exchange, sessions))
                        ? "ready"
                        : "unready",
                drainReason = drain.Reason,
                uptime = drain.Uptime.ToString(@"hh\:mm\:ss"),
                startedAt = drain.StartedAt,
                persistence = new
                {
                    enabled = p.Enabled,
                    firmId = p.FirmId,
                    dataDirectory = p.DataDirectory,
                    snapshotInterval = p.SnapshotInterval,
                    healthy = wal.IsHealthy,
                    terminalFault = wal.TerminalFault?.GetType().Name,
                    terminalFaultMessage = wal.TerminalFault?.Message,
                    terminalFaultDetails = faultDetails is null ? null : new
                    {
                        faultDetails.Code,
                        faultDetails.Message,
                        faultDetails.RecommendedAction,
                    },
                    walGeneration = wal.WalGeneration,
                    lastAdmittedSeq = wal.LastAdmittedSeq,
                    lastAppendedSeq = wal.LastAppendedSeq,
                    lastLogFsyncedSeq = wal.LastLogFsyncedSeq,
                    lastCommittedSeq = wal.LastCommittedSeq,
                },
                identityDirectory = new
                {
                    provider = identity.Provider,
                    ready = identity.Ready,
                    path = identity.Path,
                    schemaVersion = identity.SchemaVersion,
                    reason = identity.Reason,
                    hasActiveExternallyLinkedAdmin = identity.HasActiveExternallyLinkedAdmin,
                    busyTimeoutMilliseconds = identityOptions.Value.BusyTimeoutMilliseconds,
                },
                exchange = exchange is null ? null : BuildExchangeBlock(exchange, sessions),
                outboundRecovery = new
                {
                    phase = recovery.Phase.ToString(),
                    ready = recovery.IsReady,
                    failureReason = recovery.FailureReason,
                    fenceHeld = fence.IsHeld,
                    processEpoch = fence.IsHeld
                        ? ctx.RequestServices.GetRequiredService<OutboundProcessEpoch>().Sequence
                        : (long?)null,
                    unresolvedRequiredFirmCount = recoveryFirms.Count(firm =>
                        firm.Required && firm.BlockingMutations > 0),
                    reconciliation.UnresolvedMutationCount,
                    reconciliation.UnresolvedFirmCount,
                    reconciliation.OldestAmbiguityAgeSeconds,
                    reconciliation.OldestLegacyUnknownAgeSeconds,
                    firms = recoveryFirms,
                },
                entryPointListener,
            });
        });

        return app;
    }

    /// <summary>
    /// Compose the <c>exchange</c> block of <c>/health</c>. When live session
    /// state is available (Real mode), <c>readyForOrders</c> is the AND of
    /// the configuration-level <see cref="ExchangeStatus.ReadyForOrders"/>
    /// and "every configured firm has an <c>established</c> snapshot".
    /// A configured firm with a disconnected or missing session therefore
    /// reports <c>readyForOrders=false</c>, fixing the
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
                erInjectionEnabled = exchange.ErInjectionEnabled,
            };
        }

        var firms = sessions.Snapshot();
        var allEstablished = HasAllRequiredSessions(exchange, firms);
        return new
        {
            mode = exchange.Mode.ToString(),
            readyForOrders = exchange.ReadyForOrders && allEstablished,
            firmCount = exchange.FirmCount,
            erInjectionEnabled = exchange.ErInjectionEnabled,
            firms = firms.Select(f => new
            {
                firmId = f.FirmId,
                state = f.SessionState,
                reconnecting = f.IsReconnecting,
                sessionVerId = f.SessionVerId,
            }).ToArray(),
        };
    }

    private static bool IsExchangeReady(ExchangeStatus exchange, IFirmSessionStatusProvider? sessions)
    {
        if (!exchange.ReadyForOrders) return false;
        return sessions is null || HasAllRequiredSessions(exchange, sessions.Snapshot());
    }

    private static bool HasAllRequiredSessions(
        ExchangeStatus exchange,
        IReadOnlyList<FirmSessionStatus> firms) =>
        firms.Count >= exchange.FirmCount && firms.All(f => f.IsEstablished);
}
