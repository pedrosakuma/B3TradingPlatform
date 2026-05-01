using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using B3.Trading.Application;
using B3.Trading.Application.Observability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api.WebSockets;

public static class WebSocketHub
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxInboundFrameBytes = 8 * 1024;

    public static IEndpointRouteBuilder MapWebSocketHub(this IEndpointRouteBuilder app)
    {
        app.MapGet("/ws", [Authorize] async (HttpContext ctx, SubscriptionManager subs, EndClientRegistry registry) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
                return Results.BadRequest(new { error = "websocket required" });

            var sub = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            if (string.IsNullOrEmpty(sub))
                return Results.Unauthorized();

            var owner = registry.Register(sub);
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var client = new SubscribedClient(owner);
            subs.Add(client);
            MetricsRegistry.WsConnectionsActive.Add(1);

            try
            {
                var sendTask = SendLoopAsync(ws, client, ctx.RequestAborted);
                var recvTask = ReceiveLoopAsync(ws, client, subs, ctx.RequestAborted);
                await Task.WhenAny(sendTask, recvTask);
                client.Complete();
                await Task.WhenAll(
                    SafeAwait(sendTask),
                    SafeAwait(recvTask));
            }
            finally
            {
                subs.Remove(client);
                MetricsRegistry.WsConnectionsActive.Add(-1);
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

    private static async Task SendLoopAsync(WebSocket ws, SubscribedClient client, CancellationToken ct)
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

    private static async Task ReceiveLoopAsync(WebSocket ws, SubscribedClient client, SubscriptionManager subs, CancellationToken ct)
    {
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
                    {
                        client.Enqueue(new OutboundMessage("error", null, 0, null, "frame_too_large", "Inbound frame exceeded max size."));
                        return;
                    }
                } while (!result.EndOfMessage);

                HandleCommand(sb.ToString(), client, subs);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (WebSocketException) { /* peer gone */ }
    }

    private static void HandleCommand(string json, SubscribedClient client, SubscriptionManager subs)
    {
        InboundCommand? cmd;
        try
        {
            cmd = JsonSerializer.Deserialize<InboundCommand>(json, JsonOptions);
        }
        catch (JsonException)
        {
            client.Enqueue(new OutboundMessage("error", null, 0, null, "invalid_json", "Could not parse command."));
            return;
        }

        if (cmd is null || string.IsNullOrWhiteSpace(cmd.Type))
        {
            client.Enqueue(new OutboundMessage("error", null, 0, null, "invalid_command", "Missing 'type' field."));
            return;
        }

        switch (cmd.Type)
        {
            case "subscribe":
                foreach (var ch in cmd.Channels ?? Array.Empty<string>())
                {
                    if (!Channels.All.Contains(ch))
                    {
                        client.Enqueue(new OutboundMessage("error", ch, 0, null, "unknown_channel", $"Channel '{ch}' is not supported."));
                        continue;
                    }
                    subs.SubscribeWithSnapshot(client, ch);
                }
                break;
            case "unsubscribe":
                foreach (var ch in cmd.Channels ?? Array.Empty<string>())
                    client.Unsubscribe(ch);
                break;
            default:
                client.Enqueue(new OutboundMessage("error", null, 0, null, "unknown_command", $"Command '{cmd.Type}' is not supported."));
                break;
        }
    }

    private static async Task SafeAwait(Task t)
    {
        try { await t; } catch { /* already-handled in loop */ }
    }
}
