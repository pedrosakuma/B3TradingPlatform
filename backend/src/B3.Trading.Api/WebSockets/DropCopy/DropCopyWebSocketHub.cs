using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api.WebSockets.DropCopy;

/// <summary>
/// Q4.6 (#306). Compliance drop-copy WebSocket endpoint
/// (<c>/ws/dropcopy</c>). Auth: JWT required; role must be
/// <see cref="Roles.Compliance"/> or <see cref="Roles.Admin"/>. The
/// session is firm-scoped and auto-subscribes to every drop-copy
/// channel (<see cref="DropCopyManager.DropCopyChannels"/>) on accept
/// — there is no inbound subscribe/unsubscribe protocol on this
/// endpoint, by design (drop-copy is "all traffic, no opt-out").
///
/// <para><b>Firm selection.</b> Compliance principals always observe
/// their own JWT firm claim — the <c>?firmId=</c> query is silently
/// IGNORED for compliance (it is not an authorisation bypass — a
/// compliance user with the wrong firm claim has no path to view
/// another firm's flow). Admin principals MAY pass <c>?firmId=</c> to
/// override and view that firm; omitted, an admin defaults to its
/// own firm claim.</para>
///
/// <para><b>Audit.</b> A best-effort <c>audit.dropcopy.connect</c>
/// event is emitted on accept, and <c>audit.dropcopy.disconnect</c>
/// on the finally-block teardown — capturing the actor, role, firmId
/// being viewed, and source IP. Per spec these go through
/// <see cref="IAuditLogger.Log"/> (best-effort) so audit-WAL
/// backpressure does not refuse the WS connection itself.</para>
///
/// <para><b>Atomicity.</b> The initial snapshot frames are enqueued
/// under <see cref="DropCopyManager"/>'s per-firm lock, so any delta
/// fan-out for the same firm racing the accept lands AFTER the
/// snapshot — same contract <c>orders.me</c> uses for the per-user
/// hub (RFC §4.3 / §5.2).</para>
/// </summary>
public static class DropCopyWebSocketHub
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapDropCopyWebSocket(this IEndpointRouteBuilder app)
    {
        app.MapGet("/ws/dropcopy", [Authorize] async (
            HttpContext ctx,
            DropCopyManager manager) =>
        {
            // Role gate: only compliance / admin may open a drop-copy
            // session. Non-WS shape returns 403; WS upgrade attempt
            // accepts and closes with policy-violation (1008) so a
            // browser-side handshake surfaces a structured error.
            var role = ctx.User.FindFirstValue(JwtIssuer.RoleClaim);
            var isCompliance = string.Equals(role, Roles.Compliance, StringComparison.OrdinalIgnoreCase);
            var isAdmin = string.Equals(role, Roles.Admin, StringComparison.OrdinalIgnoreCase);
            if (!isCompliance && !isAdmin)
            {
                if (!ctx.WebSockets.IsWebSocketRequest)
                    return Results.StatusCode(StatusCodes.Status403Forbidden);

                using var rejected = await ctx.WebSockets.AcceptWebSocketAsync();
                await rejected.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "drop-copy requires compliance or admin role",
                    CancellationToken.None);
                return Results.Empty;
            }

            if (!ctx.WebSockets.IsWebSocketRequest)
                return Results.BadRequest(new { error = "websocket required" });

            var sub = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            if (string.IsNullOrEmpty(sub))
                return Results.Unauthorized();

            var jwtFirm = ctx.User.FindFirstValue(JwtIssuer.FirmClaim) ?? "default";

            // Firm selection: admin may override via ?firmId=; compliance
            // ALWAYS observes its JWT firm. The compliance override is
            // silently ignored (not 4xx) so a misconfigured client does
            // not break — the audit record still reflects the effective
            // firm being observed.
            string effectiveFirm = jwtFirm;
            if (isAdmin && ctx.Request.Query.TryGetValue("firmId", out var qFirm))
            {
                var requested = qFirm.ToString();
                if (!string.IsNullOrWhiteSpace(requested))
                    effectiveFirm = requested;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var client = new DropCopyClient(effectiveFirm, sub, role!);

            // Audit the connect BEFORE we register so the audit log
            // records the operator's intent even if the snapshot build
            // throws. Best-effort: a WAL-backpressured audit append
            // must not refuse the WS session.
            EmitAuditIfWired(ctx, AuditEventTypes.DropCopyConnect, AuditOutcomes.Success, effectiveFirm, sub, role!);

            manager.Add(client);
            MetricsRegistry.WsConnectionsActive.Add(1);

            try
            {
                var sendTask = SendLoopAsync(ws, client, ctx.RequestAborted);
                var recvTask = ReceiveLoopAsync(ws, ctx.RequestAborted);
                await Task.WhenAny(sendTask, recvTask);
                client.Complete();
                await Task.WhenAll(SafeAwait(sendTask), SafeAwait(recvTask));
            }
            finally
            {
                manager.Remove(client);
                MetricsRegistry.WsConnectionsActive.Add(-1);
                EmitAuditIfWired(ctx, AuditEventTypes.DropCopyDisconnect, AuditOutcomes.Success, effectiveFirm, sub, role!);

                if (ws.State == WebSocketState.Open)
                {
                    var reason = client.DisconnectReason ?? "closing";
                    try
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
                    }
                    catch (WebSocketException) { /* best-effort */ }
                }
            }

            return Results.Empty;
        });

        return app;
    }

    private static async Task SendLoopAsync(WebSocket ws, DropCopyClient client, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in client.Reader.ReadAllAsync(ct))
            {
                if (ws.State != WebSocketState.Open)
                    return;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
                MetricsRegistry.WsMessagesSent.Add(1,
                    new KeyValuePair<string, object?>("channel", msg.Channel ?? "unknown"));
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (WebSocketException) { /* peer gone */ }
    }

    /// <summary>
    /// Drop-copy has no inbound subscribe/unsubscribe protocol — the
    /// session auto-subscribes to all channels on accept. We still
    /// drain inbound frames so the WS half-close (client → server
    /// close frame) is handled cleanly and large rogue frames are
    /// detected.
    /// </summary>
    private static async Task ReceiveLoopAsync(WebSocket ws, CancellationToken ct)
    {
        const int MaxInboundFrameBytes = 8 * 1024;
        var buffer = new byte[MaxInboundFrameBytes];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (sb.Length > MaxInboundFrameBytes)
                        return;
                } while (!result.EndOfMessage);
                // Body intentionally ignored — drop-copy is server-push only.
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (WebSocketException) { /* peer gone */ }
    }

    private static async Task SafeAwait(Task t)
    {
        try { await t; } catch { /* already-handled in loop */ }
    }

    private static void EmitAuditIfWired(
        HttpContext ctx,
        string eventType,
        string outcome,
        string firmIdViewed,
        string actorUserId,
        string actorRole)
    {
        var audit = ctx.RequestServices.GetService(typeof(IAuditLogger)) as IAuditLogger;
        if (audit is null) return;
        var jwtFirm = ctx.User.FindFirstValue(JwtIssuer.FirmClaim);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["firmIdViewed"] = firmIdViewed,
        };
        if (!string.IsNullOrEmpty(jwtFirm) && !string.Equals(jwtFirm, firmIdViewed, StringComparison.OrdinalIgnoreCase))
            details["firmIdOverride"] = "true";

        audit.Log(new AuditLogEvent
        {
            EventType = eventType,
            Outcome = outcome,
            ActorUserId = actorUserId,
            ActorUsername = actorUserId,
            ActorFirm = jwtFirm,
            ActorRole = actorRole,
            SourceIp = ctx.Connection.RemoteIpAddress?.ToString(),
            ResourcePath = "/ws/dropcopy",
            Details = details,
        });
    }
}
