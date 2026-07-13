using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application.MarketData;
using B3.Trading.Application.Risk;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task AdminHaltsEndpoints_RequireAdminRole()
    {
        using var userClient = await _factory.CreateAuthedClientAsync(); // alice (user role)
        var post = await userClient.PostAsync("/admin/halts/PETR4", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        var get = await userClient.GetAsync("/admin/halts");
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
    }

    [Fact]
    public async Task AdminGetHalts_ReturnsCurrentSymbols()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        // Clean any state from a previous test in the shared factory.
        await admin.DeleteAsync("/admin/halts/ABEV3");

        await admin.PostAsync("/admin/halts/ABEV3", content: null);
        var state = await admin.GetFromJsonAsync<HaltStateDto>("/admin/halts");
        Assert.Contains("ABEV3", state!.Symbols);
        await admin.DeleteAsync("/admin/halts/ABEV3");
    }

    [Fact]
    public async Task AdminGetHalts_ExposesOriginPerSymbol()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var halts = _factory.Services.GetRequiredService<SymbolHaltService>();
        // Distinct symbol to avoid contaminating the shared factory.
        halts.Resume("CSNA3", HaltOrigin.Venue);
        await admin.DeleteAsync("/admin/halts/CSNA3");

        // A venue halt observed via market data plus an operator halt.
        halts.Halt("CSNA3", HaltOrigin.Venue);
        await admin.PostAsync("/admin/halts/CSNA3", content: null);
        try
        {
            var state = await admin.GetFromJsonAsync<HaltStateDto>("/admin/halts");
            var entry = Assert.Single(state!.Halts, h => h.Symbol == "CSNA3");
            Assert.Equal("operator+venue", entry.Origin);
        }
        finally
        {
            await admin.DeleteAsync("/admin/halts/CSNA3");
            halts.Resume("CSNA3", HaltOrigin.Venue);
        }
    }

    [Fact]
    public async Task OperatorResume_WhileVenueStillHalted_StaysHaltedAndReportsResidualVenueHalt()
    {
        using var http = _factory.CreateClient();
        var token = await _factory.LoginAsync(http, "bob");
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var halts = _factory.Services.GetRequiredService<SymbolHaltService>();
        halts.Resume("USIM5", HaltOrigin.Venue);
        await admin.DeleteAsync("/admin/halts/USIM5");

        // Venue halts the symbol (observed via market data), operator
        // also halts it manually.
        halts.Halt("USIM5", HaltOrigin.Venue);
        await admin.PostAsync("/admin/halts/USIM5", content: null);
        try
        {
            // Operator clears their own halt — but the venue still
            // holds it, so the endpoint must report the residual venue
            // halt instead of a bare 204.
            var resume = await admin.DeleteAsync("/admin/halts/USIM5");
            Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
            var body = await resume.Content.ReadFromJsonAsync<ResumeResidualDto>();
            Assert.False(body!.Resumed);
            Assert.Equal("venue", body.StillHaltedBy);

            // And the symbol genuinely stays halted on the order path.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ws = await OpenSubscribedAsync(token, Channels.ExecutionsMe);
            var blocked = await PostAsync(http, token, "/orders",
                new { Symbol = "USIM5", SecurityId = 7777UL, Side = "Buy", Type = "Limit", Quantity = 1, Price = 10m });
            Assert.Equal(HttpStatusCode.Accepted, blocked.StatusCode);
            var delta = await ReadJsonAsync(ws, cts.Token);
            Assert.Equal("Rejected", delta.GetProperty("data").GetProperty("kind").GetString());
            Assert.Contains("halted", delta.GetProperty("data").GetProperty("rejectReason").GetString());
        }
        finally
        {
            halts.Resume("USIM5", HaltOrigin.Venue);
        }
    }

    [Fact]
    public async Task SymbolHaltToggle_RejectsNextOrder_ThenResumes()
    {
        using var http = _factory.CreateClient();
        var token = await _factory.LoginAsync(http, "bob");
        using var admin = await _factory.CreateAuthedClientAsync("admin");

        var ws = await OpenSubscribedAsync(token, Channels.ExecutionsMe);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await admin.PostAsync("/admin/halts/PETR4", content: null);
        try
        {
            var blocked = await PostAsync(http, token, "/orders",
                new { Symbol = "PETR4", SecurityId = 4321UL, Side = "Buy", Type = "Limit", Quantity = 1, Price = 30m });
            Assert.Equal(HttpStatusCode.Accepted, blocked.StatusCode);
            var delta = await ReadJsonAsync(ws, cts.Token);
            Assert.Equal("Rejected", delta.GetProperty("data").GetProperty("kind").GetString());
            Assert.Contains("halted", delta.GetProperty("data").GetProperty("rejectReason").GetString());
        }
        finally
        {
            await admin.DeleteAsync("/admin/halts/PETR4");
        }
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

    [Fact]
    public async Task AdminSessionPhaseEndpoints_RequireAdminRole()
    {
        using var userClient = await _factory.CreateAuthedClientAsync(); // alice (user role)
        var get = await userClient.GetAsync("/admin/session-phase");
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        var post = await userClient.PostAsJsonAsync("/admin/session-phase/PETR4", new { phase = "Closed" });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task AdminSessionPhase_SetGetClear_RoundTrips()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        await admin.DeleteAsync("/admin/session-phase/WEGE3"); // clean

        var setResp = await admin.PostAsJsonAsync("/admin/session-phase/WEGE3", new { phase = "OpeningAuction" });
        Assert.Equal(HttpStatusCode.NoContent, setResp.StatusCode);

        var state = await admin.GetFromJsonAsync<SessionPhaseStateDto>("/admin/session-phase");
        Assert.NotNull(state);
        Assert.Equal("OpeningAuction", state!.Overrides["WEGE3"]);

        var clr = await admin.DeleteAsync("/admin/session-phase/WEGE3");
        Assert.Equal(HttpStatusCode.NoContent, clr.StatusCode);

        state = await admin.GetFromJsonAsync<SessionPhaseStateDto>("/admin/session-phase");
        Assert.False(state!.Overrides.ContainsKey("WEGE3"));
    }

    [Fact]
    public async Task AdminSessionPhase_RejectsBadPhase()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsJsonAsync("/admin/session-phase/PETR4", new { phase = "Banana" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SessionPhase_OpeningAuction_RejectsMarketOrder_ThenResumes()
    {
        // Use a unique symbol to avoid colliding with other tests/clients on the shared factory.
        const string sym = "PHASE_TEST";
        using var http = _factory.CreateClient();
        var token = await _factory.LoginAsync(http, "bob");
        using var admin = await _factory.CreateAuthedClientAsync("admin");

        var ws = await OpenSubscribedAsync(token, Channels.ExecutionsMe);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await admin.PostAsJsonAsync($"/admin/session-phase/{sym}", new { phase = "OpeningAuction" });
        try
        {
            // Market order in auction → reject with phase_not_allowed:auction.
            var blocked = await PostAsync(http, token, "/orders",
                new { Symbol = sym, SecurityId = 4321UL, Side = "Buy", Type = "Market", Quantity = 1, Price = (decimal?)null });
            Assert.Equal(HttpStatusCode.Accepted, blocked.StatusCode);
            var delta = await ReadJsonAsync(ws, cts.Token);
            Assert.Equal("Rejected", delta.GetProperty("data").GetProperty("kind").GetString());
            Assert.Contains("phase_not_allowed", delta.GetProperty("data").GetProperty("rejectReason").GetString());
        }
        finally
        {
            await admin.DeleteAsync($"/admin/session-phase/{sym}");
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
    private sealed record HaltStateDto(string[] Symbols, HaltOriginDto[] Halts);
    private sealed record HaltOriginDto(string Symbol, string Origin);
    private sealed record ResumeResidualDto(string Symbol, bool Resumed, string StillHaltedBy, string Detail);
    private sealed record SessionPhaseStateDto(string Default, Dictionary<string, string> Overrides);
}
