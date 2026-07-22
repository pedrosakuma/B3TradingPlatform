using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q3.4 (#284). REST surface for the native iceberg / reserve
/// display-qty fields on <c>POST /api/orders</c>. Asserts both the
/// happy-path accept (display fields land on the WorkingOrderBook
/// intact) and the validation rejections — DisplayQty &gt; Quantity
/// and DisplayQty &lt;= 0 must come back as 4xx before the WAL
/// append, so a malformed iceberg never enters recovery.
/// </summary>
public class OrdersIcebergEndpointTests
{
    [Fact]
    public async Task POST_orders_WithDisplayQty_AcceptsAndPersistsFields()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var resp = await PostIceberg(http, token, qty: 100, price: 30m,
            displayQty: 10, policy: "Always");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var ack = await resp.Content.ReadFromJsonAsync<OrderAck>();
        Assert.NotNull(ack);

        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        Assert.True(book.TryGet(ulong.Parse(ack!.ClOrdId), out var order));
        Assert.Equal(10L, order!.DisplayQty);
        Assert.Equal(DisplayResetPolicy.Always, order.DisplayResetPolicy);
    }

    [Theory]
    [InlineData("OnPartialFill")]
    [InlineData("Never")]
    [InlineData("onPartialFill")]
    [InlineData("never")]
    public async Task POST_orders_UnsupportedPolicy_RejectedAtBoundary(string policy)
    {
        // Pass-1 review (#297, follow-up #298). B3.EntryPoint.Client
        // 0.14.3 has no refresh-policy field, so accepting anything other
        // than Always would silently downgrade at the venue and break the
        // Never contract. The REST boundary MUST reject before allocating
        // a ClOrdID / appending to the WAL.
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var resp = await PostIceberg(http, token, qty: 100, price: 30m, displayQty: 10, policy: policy);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.NotNull(body);
        Assert.Contains("not supported by the current entrypoint SDK", body!.Error);
        Assert.Contains("Always", body.Error);
        Assert.Contains("#298", body.Error);
    }

    [Fact]
    public async Task POST_orders_DisplayQtyDefaultsToAlwaysPolicy()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        // No explicit policy field on the request body — the
        // pipeline must default to Always when DisplayQty is set.
        var resp = await PostIceberg(http, token, qty: 200, price: 30m, displayQty: 50, policy: null);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var ack = await resp.Content.ReadFromJsonAsync<OrderAck>();

        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        Assert.True(book.TryGet(ulong.Parse(ack!.ClOrdId), out var order));
        Assert.Equal(50L, order!.DisplayQty);
        Assert.Equal(DisplayResetPolicy.Always, order.DisplayResetPolicy);
    }

    [Theory]
    [InlineData(0, "DisplayQty must be positive")]
    [InlineData(-5, "DisplayQty must be positive")]
    [InlineData(150, "must not exceed order Quantity")]
    public async Task POST_orders_InvalidDisplayQty_RejectedAsAccepted_WithRiskRejection(long badDisplayQty, string expectedFragment)
    {
        // The Domain.Order ctor invariant surfaces as BadRequest from
        // OrderSubmissionService, which the endpoint maps to 400.
        // (Note: zero/negative values are caught by the ctor before
        // the WAL append — same path as the other Q1.1 cross-field
        // validations.)
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var resp = await PostIceberg(http, token, qty: 100, price: 30m, displayQty: badDisplayQty, policy: "Always");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.NotNull(body);
        Assert.Contains(expectedFragment, body!.Error);
    }

    [Fact]
    public async Task POST_orders_InvalidPolicyString_ReturnsBadRequest()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var resp = await PostIceberg(http, token, qty: 100, price: 30m, displayQty: 10, policy: "Sometimes");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("invalid displayResetPolicy", body!.Error);
    }

    [Fact]
    public async Task GET_orders_AndWsSnapshot_SurfaceDisplayFields()
    {
        // Pass-1 review (#297) P2. OrderDto.ToDto must expose the
        // persisted DisplayQty + DisplayResetPolicy so REST GET /api/orders
        // and WS orders.me snapshots surface the iceberg state — the
        // trader's intent that is already on the WAL must also be
        // visible to operators and dashboards.
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var submit = await PostIceberg(http, token, qty: 100, price: 30m, displayQty: 25, policy: "Always");
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);

        // 1) REST GET /api/orders surfaces the fields.
        var get = new HttpRequestMessage(HttpMethod.Get, "/api/orders/");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var listResp = await http.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        var dto = list.EnumerateArray().Single();
        Assert.Equal(25L, dto.GetProperty("displayQty").GetInt64());
        Assert.Equal("Always", dto.GetProperty("displayResetPolicy").GetString());

        // 2) WS orders.me snapshot surfaces the fields too.
        var wsClient = f.Server.CreateWebSocketClient();
        var uri = new UriBuilder(f.Server.BaseAddress) { Scheme = "ws", Path = "/ws", Query = $"access_token={token}" }.Uri;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var ws = await wsClient.ConnectAsync(uri, cts.Token);

        var subscribe = JsonSerializer.SerializeToUtf8Bytes(
            new { type = "subscribe", channels = new[] { Channels.OrdersMe } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await ws.SendAsync(subscribe, WebSocketMessageType.Text, true, cts.Token);

        var snap = await ReadWsJsonAsync(ws, cts.Token);
        Assert.Equal("snapshot", snap.GetProperty("type").GetString());
        Assert.Equal(Channels.OrdersMe, snap.GetProperty("channel").GetString());
        var order = snap.GetProperty("data").EnumerateArray().Single();
        Assert.Equal(25L, order.GetProperty("displayQty").GetInt64());
        Assert.Equal("Always", order.GetProperty("displayResetPolicy").GetString());
    }

    private static async Task<JsonElement> ReadWsJsonAsync(WebSocket ws, CancellationToken ct)
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

    private static async Task<HttpResponseMessage> PostIceberg(
        HttpClient http, string token, int qty, decimal price, long displayQty, string? policy)
    {
        var payload = policy is null
            ? (object)new
            {
                Symbol = "PETR4",
                SecurityId = 4321UL,
                Side = "Buy",
                Type = "Limit",
                Quantity = qty,
                Price = price,
                DisplayQty = displayQty,
            }
            : new
            {
                Symbol = "PETR4",
                SecurityId = 4321UL,
                Side = "Buy",
                Type = "Limit",
                Quantity = qty,
                Price = price,
                DisplayQty = displayQty,
                DisplayResetPolicy = policy,
            };
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req);
    }

    private sealed record OrderAck(string ClOrdId, string? Status, string? Reason);
    private sealed record ErrorBody(string Error);
}
