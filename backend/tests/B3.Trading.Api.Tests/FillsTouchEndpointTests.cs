using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.MarketData;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q4.7 (#307). End-to-end coverage for the <c>GET /fills/{id}/touch</c>
/// REST surface and the <c>bookTouch</c> payload on the
/// <c>executions.me</c> WS channel. Pairs with
/// <c>BookTouchCaptureTests</c> (capture / WAL / projection unit tests).
/// </summary>
public class FillsTouchEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string Firm01 = "FIRM01";
    private const string Firm02 = "FIRM02";

    private static (FillRecord Record, string Id) SeedFill(
        TestAppFactory factory,
        string user,
        string firmId,
        ulong clOrdId,
        long cumQty,
        BookTouchSnapshot? touch,
        bool seedOrder = true,
        string symbol = "PETR4",
        OrderSide side = OrderSide.Buy,
        decimal lastPrice = 30m)
    {
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var fills = factory.Services.GetRequiredService<FillProjection>();
        var owner = registry.Register(user);
        if (seedOrder)
        {
            var book = factory.Services.GetRequiredService<WorkingOrderBook>();
            book.TryAdd(new Order(clOrdId, owner, symbol, 9000UL, side, OrderType.Limit, cumQty, lastPrice, firmId: firmId));
        }
        var record = fills.Record(
            clOrdId, cumQty, owner, firmId, symbol, side, cumQty, lastPrice,
            DateTimeOffset.UtcNow, touch);
        return (record, FillProjection.BuildId(clOrdId, cumQty));
    }

    private static BookTouchSnapshot FreshTouch() => new()
    {
        BestBid = 29.95m,
        BestAsk = 30.05m,
        MidPrice = 30.00m,
        LastTradePrice = 30.00m,
        CapturedAtUtc = DateTimeOffset.UtcNow,
        Stale = false,
    };

    [Fact]
    public async Task GetTouch_OwnFirm_ReturnsBookTouchPayload()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        var (_, id) = SeedFill(factory, "alice", Firm01, 101UL, 100, FreshTouch());

        var client = factory.CreateClient();
        var (token, _) = issuer.Issue("alice", "user", Firm01);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync($"/fills/{id}/touch");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<BookTouchDto>(JsonOptions);
        Assert.NotNull(dto);
        Assert.False(dto!.Stale);
        Assert.Equal(29.95m, dto.BestBid);
        Assert.Equal(30.05m, dto.BestAsk);
        Assert.Equal(30.00m, dto.MidPrice);
        Assert.Equal(30.00m, dto.LastTradePrice);
    }

    [Fact]
    public async Task GetTouch_UnknownId_Returns404()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var client = await factory.CreateAuthedClientAsync();
        var resp = await client.GetAsync("/fills/9999:42/touch");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetTouch_CrossFirm_Returns404NotLeakingExistence()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        var (_, id) = SeedFill(factory, "alice", Firm01, 201UL, 50, FreshTouch());

        var client = factory.CreateClient();
        var (token, _) = issuer.Issue("bob", "user", Firm02);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync($"/fills/{id}/touch");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetTouch_AdminFirmOverride_ReturnsCrossFirmFill()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var (_, id) = SeedFill(factory, "alice", Firm01, 301UL, 75, FreshTouch());

        var client = await factory.CreateAuthedClientAsync("admin");
        var resp = await client.GetAsync($"/fills/{id}/touch?firmId={Firm01}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<BookTouchDto>(JsonOptions);
        Assert.NotNull(dto);
        Assert.Equal(29.95m, dto!.BestBid);
    }

    [Fact]
    public async Task GetTouch_NonAdminFirmOverride_Returns403()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        var (_, id) = SeedFill(factory, "alice", Firm01, 401UL, 25, FreshTouch());

        var client = factory.CreateClient();
        var (token, _) = issuer.Issue("alice", "user", Firm01);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync($"/fills/{id}/touch?firmId={Firm02}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task GetTouch_LegacyFillWithNoSnapshot_ReturnsSyntheticStalePayload()
    {
        // Pre-#307 fill paths (or non-router test fixtures) record fills
        // without a BookTouch. The endpoint promises Stale=true + all
        // null prices so clients have a single uniform JSON shape.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        var (_, id) = SeedFill(factory, "alice", Firm01, 501UL, 100, touch: null);

        var client = factory.CreateClient();
        var (token, _) = issuer.Issue("alice", "user", Firm01);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync($"/fills/{id}/touch");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<BookTouchDto>(JsonOptions);
        Assert.NotNull(dto);
        Assert.True(dto!.Stale);
        Assert.Null(dto.BestBid);
        Assert.Null(dto.BestAsk);
        Assert.Null(dto.MidPrice);
        Assert.Null(dto.LastTradePrice);
    }

    [Fact]
    public async Task GetTouch_Unauthenticated_Returns401()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var (_, id) = SeedFill(factory, "alice", Firm01, 601UL, 100, FreshTouch());

        using var client = factory.CreateClient();
        var resp = await client.GetAsync($"/fills/{id}/touch");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ExecutionsMe_DeltaPayload_CarriesBookTouchOnFill()
    {
        // End-to-end: subscribe to executions.me, submit an order through
        // the API so the router knows the ClOrdId, seed the book-top
        // cache, emit a Fill via the mock client, and confirm the delta
        // frame carries the bookTouch payload.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);

        var wsClient = factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(factory.Server.BaseAddress)
        {
            Scheme = "ws",
            Path = "/ws",
            Query = $"access_token={token}",
        }.Uri;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ws = await wsClient.ConnectAsync(uri, cts.Token);

        await SendJsonAsync(ws, new { type = "subscribe", channels = new[] { Channels.ExecutionsMe } }, cts.Token);
        var snap = await ReadJsonAsync(ws, cts.Token);
        Assert.Equal("snapshot", snap.GetProperty("type").GetString());

        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new { Symbol = "PETR4", SecurityId = 4321UL, Side = "Buy", Type = "Limit", Quantity = 100, Price = 30m }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var submit = await http.SendAsync(req);
        var body = await submit.Content.ReadFromJsonAsync<JsonElement>();
        var clOrdId = ulong.Parse(body.GetProperty("clOrdId").GetString()!);

        // Prime the top-of-book cache so the router has fresh prices
        // to capture when the fill ER arrives.
        var cache = factory.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.99m, 30.01m, DateTimeOffset.UtcNow);
        cache.UpdateLast("PETR4", 30.00m, DateTimeOffset.UtcNow);

        var mock = (MockEntryPointClient)factory.Services.GetRequiredService<IEntryPointClient>();
        mock.EmitExecutionReport(new ExecutionReportEnvelope(clOrdId, EpExecType.Fill, 0, 100, 100, 30m, null));

        var delta = await ReadJsonAsync(ws, cts.Token);
        Assert.Equal("delta", delta.GetProperty("type").GetString());
        Assert.Equal(Channels.ExecutionsMe, delta.GetProperty("channel").GetString());
        var data = delta.GetProperty("data");
        Assert.True(data.TryGetProperty("bookTouch", out var touch),
            "executions.me delta should carry a bookTouch payload for Fill kinds.");
        Assert.False(touch.GetProperty("stale").GetBoolean());
        Assert.Equal(29.99m, touch.GetProperty("bestBid").GetDecimal());
        Assert.Equal(30.01m, touch.GetProperty("bestAsk").GetDecimal());
        Assert.Equal(30.00m, touch.GetProperty("lastTradePrice").GetDecimal());
    }

    [Fact]
    public async Task RestEndpoint_AfterLiveFillThroughRouter_ReturnsCapturedTouch()
    {
        // Full live path (no SeedFill shortcut): submit, prime cache,
        // emit fill, GET /fills/{id}/touch and confirm what the router
        // captured matches what the REST surface returns.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http);

        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new { Symbol = "PETR4", SecurityId = 4321UL, Side = "Buy", Type = "Limit", Quantity = 60, Price = 30m }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var submit = await http.SendAsync(req);
        var body = await submit.Content.ReadFromJsonAsync<JsonElement>();
        var clOrdId = ulong.Parse(body.GetProperty("clOrdId").GetString()!);

        var cache = factory.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.97m, 30.03m, DateTimeOffset.UtcNow);
        cache.UpdateLast("PETR4", 30.00m, DateTimeOffset.UtcNow);

        var mock = (MockEntryPointClient)factory.Services.GetRequiredService<IEntryPointClient>();
        mock.EmitExecutionReport(new ExecutionReportEnvelope(clOrdId, EpExecType.Fill, 0, 60, 60, 30m, null));

        // Brief wait for the dispatcher to fold the fill into the
        // projection — Dispatch is synchronous so this is normally
        // immediate, but allow a few iterations of slack on busy CI.
        var fills = factory.Services.GetRequiredService<FillProjection>();
        var id = FillProjection.BuildId(clOrdId, 60);
        for (var i = 0; i < 50 && !fills.TryGet(id, out _); i++)
            await Task.Delay(10);

        var authed = factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await authed.GetAsync($"/fills/{id}/touch");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<BookTouchDto>(JsonOptions);
        Assert.NotNull(dto);
        Assert.False(dto!.Stale);
        Assert.Equal(29.97m, dto.BestBid);
        Assert.Equal(30.03m, dto.BestAsk);
    }

    [Fact]
    public async Task SnapshotRestart_PreservesTouchEvidenceViaWalReplay()
    {
        // Persistence-on path: enable WAL + a temp data dir, emit a
        // fill, then dispose+recreate the factory with the same dir.
        // The recovery pre-pass folds historical Fill ERs back into
        // FillProjection so /fills/{id}/touch keeps working after cold
        // boot.
        var dataDir = Path.Combine(
            Path.GetTempPath(),
            "b3tp-fills-touch-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        ulong clOrdId;
        string fillId;
        try
        {
            await using (var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
            {
                ["Trading:Persistence:Enabled"] = "true",
                ["Trading:Persistence:DataDirectory"] = dataDir,
                ["Trading:Persistence:FirmId"] = "test",
                ["Trading:Persistence:FsyncOnFlush"] = "false",
            }))
            {
                using var http = factory.CreateClient();
                var token = await factory.LoginAsync(http);

                var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
                {
                    Content = JsonContent.Create(new { Symbol = "PETR4", SecurityId = 4321UL, Side = "Buy", Type = "Limit", Quantity = 40, Price = 30m }),
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var submit = await http.SendAsync(req);
                var body = await submit.Content.ReadFromJsonAsync<JsonElement>();
                clOrdId = ulong.Parse(body.GetProperty("clOrdId").GetString()!);

                var cache = factory.Services.GetRequiredService<PegBookTopCache>();
                cache.UpdateBookTop("PETR4", 29.90m, 30.10m, DateTimeOffset.UtcNow);
                cache.UpdateLast("PETR4", 30.00m, DateTimeOffset.UtcNow);

                var mock = (MockEntryPointClient)factory.Services.GetRequiredService<IEntryPointClient>();
                mock.EmitExecutionReport(new ExecutionReportEnvelope(clOrdId, EpExecType.Fill, 0, 40, 40, 30m, null));

                var fills = factory.Services.GetRequiredService<FillProjection>();
                fillId = FillProjection.BuildId(clOrdId, 40);
                for (var i = 0; i < 100 && !fills.TryGet(fillId, out _); i++)
                    await Task.Delay(10);
                Assert.True(fills.TryGet(fillId, out _),
                    "Live fill should have been recorded before restart.");

                // Make sure the WAL has actually flushed before disposing.
                var store = factory.Services.GetRequiredService<B3.Trading.Application.Persistence.IEventStore>();
                await store.FlushAsync();
            }

            // Cold restart against the same data directory.
            await using (var factory2 = TestAppFactory.WithOverrides(new Dictionary<string, string?>
            {
                ["Trading:Persistence:Enabled"] = "true",
                ["Trading:Persistence:DataDirectory"] = dataDir,
                ["Trading:Persistence:FirmId"] = "test",
                ["Trading:Persistence:FsyncOnFlush"] = "false",
            }))
            {
                await factory2.Services
                    .GetRequiredService<B3.Trading.Application.Outbound.IOutboundRecoveryGate>()
                    .WaitUntilClassificationCompleteAsync(CancellationToken.None);
                var fills = factory2.Services.GetRequiredService<FillProjection>();
                Assert.True(fills.TryGet(fillId, out var rec),
                    "Cold restart must rehydrate the fill from the WAL.");
                Assert.NotNull(rec.BookTouch);
                Assert.False(rec.BookTouch!.Stale);
                Assert.Equal(29.90m, rec.BookTouch.BestBid);
                Assert.Equal(30.10m, rec.BookTouch.BestAsk);

                using var http = factory2.CreateClient();
                var token = await factory2.LoginAsync(http);
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var resp = await http.GetAsync($"/fills/{fillId}/touch");
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                var dto = await resp.Content.ReadFromJsonAsync<BookTouchDto>(JsonOptions);
                Assert.NotNull(dto);
                Assert.False(dto!.Stale);
                Assert.Equal(29.90m, dto.BestBid);
                Assert.Equal(30.10m, dto.BestAsk);
            }
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
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
}
