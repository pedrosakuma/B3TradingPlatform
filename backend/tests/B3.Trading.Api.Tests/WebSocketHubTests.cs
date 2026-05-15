using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using B3.Trading.Application;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using B3.Trading.Api.WebSockets;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

public class WebSocketHubTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public WebSocketHubTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Connect_Subscribe_ReceivesSnapshot_ThenDelta()
    {
        using var http = _factory.CreateClient();
        var token = await _factory.LoginAsync(http);

        var wsClient = _factory.Server.CreateWebSocketClient();
        // Verify the query-string token path works (browsers cannot
        // attach Authorization headers on the WS handshake).
        var uri = new UriBuilder(_factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws", Query = $"access_token={token}" }.Uri;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var ws = await wsClient.ConnectAsync(uri, cts.Token);

        await SendJsonAsync(ws, new { type = "subscribe", channels = new[] { Channels.OrdersMe, Channels.ExecutionsMe, Channels.PositionsMe } }, cts.Token);

        // Three snapshot frames (one per subscribed channel).
        var snap1 = await ReadJsonAsync(ws, cts.Token);
        var snap2 = await ReadJsonAsync(ws, cts.Token);
        var snap3 = await ReadJsonAsync(ws, cts.Token);
        Assert.Equal("snapshot", snap1.GetProperty("type").GetString());
        Assert.Equal("snapshot", snap2.GetProperty("type").GetString());
        Assert.Equal("snapshot", snap3.GetProperty("type").GetString());

        // Push a synthetic ER end-to-end: submit an order then have the
        // mock client emit a Fill ER for that ClOrdID.
        var submit = await PostAsAuthAsync(http, token, "/orders",
            new { Symbol = "PETR4", SecurityId = 4321UL, Side = "Buy", Type = "Limit", Quantity = 100, Price = 30m });
        var body = await submit.Content.ReadFromJsonAsync<JsonElement>();
        var clOrdIdStr = body.GetProperty("clOrdId").GetString()!;
        var clOrdId = ulong.Parse(clOrdIdStr);

        var mock = (MockEntryPointClient)_factory.Services.GetRequiredService<IEntryPointClient>();
        mock.EmitExecutionReport(new ExecutionReportEnvelope(clOrdId, EpExecType.Fill, 0, 100, 100, 30m, null));

        // Expect 3 deltas (executions.me, orders.me, positions.me) in any order.
        var d1 = await ReadJsonAsync(ws, cts.Token);
        var d2 = await ReadJsonAsync(ws, cts.Token);
        var d3 = await ReadJsonAsync(ws, cts.Token);
        var deltas = new[] { d1, d2, d3 };
        Assert.All(deltas, d => Assert.Equal("delta", d.GetProperty("type").GetString()));
        var channels = deltas.Select(d => d.GetProperty("channel").GetString()).ToHashSet();
        Assert.Contains(Channels.OrdersMe, channels);
        Assert.Contains(Channels.ExecutionsMe, channels);
        Assert.Contains(Channels.PositionsMe, channels);

        // Disposal via `using` cleans up; explicit CloseAsync races with
        // the server-initiated close in the in-process TestServer.
    }

    [Fact]
    public async Task UnknownChannel_ReceivesError()
    {
        using var http = _factory.CreateClient();
        var token = await _factory.LoginAsync(http);

        var wsClient = _factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(_factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws", Query = $"access_token={token}" }.Uri;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var ws = await wsClient.ConnectAsync(uri, cts.Token);

        await SendJsonAsync(ws, new { type = "subscribe", channels = new[] { "bogus.channel" } }, cts.Token);

        var msg = await ReadJsonAsync(ws, cts.Token);
        Assert.Equal("error", msg.GetProperty("type").GetString());
        Assert.Equal("unknown_channel", msg.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConnectWithoutToken_IsRejected()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(_factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws" }.Uri;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => wsClient.ConnectAsync(uri, cts.Token));
    }

    [Fact]
    public async Task PublicChannel_PhasesPetr4_ReceivesUnknownSnapshotOnSubscribe()
    {
        using var http = _factory.CreateClient();
        var token = await _factory.LoginAsync(http);

        var wsClient = _factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(_factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws", Query = $"access_token={token}" }.Uri;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var ws = await wsClient.ConnectAsync(uri, cts.Token);

        await SendJsonAsync(ws, new { type = "subscribe", channels = new[] { "phases.PETR4", "auction.PETR4" } }, cts.Token);

        var snap1 = await ReadJsonAsync(ws, cts.Token);
        var snap2 = await ReadJsonAsync(ws, cts.Token);
        Assert.Equal("snapshot", snap1.GetProperty("type").GetString());
        Assert.Equal("snapshot", snap2.GetProperty("type").GetString());
        var channels = new[] { snap1, snap2 }.Select(d => d.GetProperty("channel").GetString()).ToHashSet();
        Assert.Contains("phases.PETR4", channels);
        Assert.Contains("auction.PETR4", channels);

        // The phases snapshot should report Unknown (no auction frames
        // ever observed in the in-memory test host: SDK is the no-op
        // NullMarketDataSubscriber).
        var phasesSnap = snap1.GetProperty("channel").GetString() == "phases.PETR4" ? snap1 : snap2;
        Assert.Equal("Unknown", phasesSnap.GetProperty("data").GetProperty("phase").GetString());
    }

    [Fact]
    public async Task PublicChannel_BadAuctionSymbol_ReceivesUnknownChannelError()
    {
        using var http = _factory.CreateClient();
        var token = await _factory.LoginAsync(http);
        var wsClient = _factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(_factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws", Query = $"access_token={token}" }.Uri;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var ws = await wsClient.ConnectAsync(uri, cts.Token);

        await SendJsonAsync(ws, new { type = "subscribe", channels = new[] { "phases.bad-symbol" } }, cts.Token);

        var msg = await ReadJsonAsync(ws, cts.Token);
        Assert.Equal("error", msg.GetProperty("type").GetString());
        Assert.Equal("unknown_channel", msg.GetProperty("code").GetString());
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

    private static async Task<HttpResponseMessage> PostAsAuthAsync(HttpClient client, string token, string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(req);
    }
}
