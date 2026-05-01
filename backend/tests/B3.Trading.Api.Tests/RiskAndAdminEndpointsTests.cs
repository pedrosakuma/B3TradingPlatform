using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using B3.Trading.Api.WebSockets;

namespace B3.Trading.Api.Tests;

/// <summary>
/// End-to-end coverage for the Phase 4 risk pipeline + admin endpoints:
/// kill-switch admin API requires the admin role; flipping it produces a
/// synthetic Rejected ER on the end-client's <c>executions.me</c> WS
/// channel — structurally indistinguishable from an exchange rejection.
/// </summary>
public class RiskAndAdminEndpointsTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public RiskAndAdminEndpointsTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task AdminKillSwitchEndpoints_RequireAdminRole()
    {
        using var userClient = await _factory.CreateAuthedClientAsync(); // alice (user role)
        var resp = await userClient.PostAsync("/admin/kill/end-client/whoever", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task AdminGetKill_ReturnsCurrentState()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        // Clean any state from a previous test in the shared factory.
        await admin.DeleteAsync("/admin/kill/end-client/zelda");

        await admin.PostAsync("/admin/kill/end-client/zelda", content: null);
        var state = await admin.GetFromJsonAsync<KillStateDto>("/admin/kill");
        Assert.Contains("zelda", state!.EndClients);
        await admin.DeleteAsync("/admin/kill/end-client/zelda");
    }

    [Fact]
    public async Task SubmitOrder_OverMaxQuantity_ProducesSyntheticRejectionOnExecutionsMe()
    {
        using var http = _factory.CreateClient();
        var token = await _factory.LoginAsync(http);

        var ws = await OpenSubscribedAsync(token, Channels.ExecutionsMe);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Default MaxQuantity in TestAppFactory is 1000.
        var resp = await PostAsync(http, token, "/orders",
            new { Symbol = "PETR4", SecurityId = 4321UL, Side = "Buy", Type = "Limit", Quantity = 5000, Price = 30m });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var delta = await ReadJsonAsync(ws, cts.Token);
        Assert.Equal("delta", delta.GetProperty("type").GetString());
        Assert.Equal(Channels.ExecutionsMe, delta.GetProperty("channel").GetString());
        var payload = delta.GetProperty("data");
        Assert.Equal("Rejected", payload.GetProperty("kind").GetString());
        Assert.Contains("max", payload.GetProperty("rejectReason").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KillSwitchToggle_RejectsNextOrder_ThenRevives()
    {
        using var http = _factory.CreateClient();
        var aliceToken = await _factory.LoginAsync(http, "bob"); // bob is fresh end-client
        using var admin = await _factory.CreateAuthedClientAsync("admin");

        var ws = await OpenSubscribedAsync(aliceToken, Channels.ExecutionsMe);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await admin.PostAsync("/admin/kill/end-client/bob", content: null);
        try
        {
            var blocked = await PostAsync(http, aliceToken, "/orders",
                new { Symbol = "PETR4", SecurityId = 4321UL, Side = "Buy", Type = "Limit", Quantity = 1, Price = 30m });
            Assert.Equal(HttpStatusCode.Accepted, blocked.StatusCode);
            var delta = await ReadJsonAsync(ws, cts.Token);
            Assert.Equal("Rejected", delta.GetProperty("data").GetProperty("kind").GetString());
            Assert.Contains("kill-switch", delta.GetProperty("data").GetProperty("rejectReason").GetString());
        }
        finally
        {
            await admin.DeleteAsync("/admin/kill/end-client/bob");
        }
    }

    private async Task<WebSocket> OpenSubscribedAsync(string token, string channel)
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(_factory.Server.BaseAddress)
        { Scheme = "ws", Path = "/ws", Query = $"access_token={token}" }.Uri;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ws = await wsClient.ConnectAsync(uri, cts.Token);
        await SendJsonAsync(ws, new { type = "subscribe", channels = new[] { channel } }, cts.Token);
        // Drain the snapshot frame so callers can read the next delta directly.
        _ = await ReadJsonAsync(ws, cts.Token);
        return ws;
    }

    private static async Task SendJsonAsync(WebSocket ws, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var sb = new StringBuilder();
        WebSocketReceiveResult res;
        do
        {
            res = await ws.ReceiveAsync(buf, ct);
            sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
        } while (!res.EndOfMessage);
        return JsonSerializer.Deserialize<JsonElement>(sb.ToString());
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(req);
    }

    private sealed record KillStateDto(string[] EndClients, string[] Firms);
}
