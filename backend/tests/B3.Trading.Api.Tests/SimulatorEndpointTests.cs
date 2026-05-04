using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace B3.Trading.Api.Tests;

/// <summary>
/// End-to-end coverage for slice 4 (Simulator gateway). Boots the host
/// with <c>Trading:Exchange:Mode=Simulator</c>, submits a real order via
/// <c>POST /orders</c>, then drives synthetic ERs via
/// <c>POST /admin/simulator/er</c> and asserts that
/// <c>WorkingOrderBook</c> state mutates as if a venue had emitted them.
/// </summary>
public class SimulatorEndpointTests
{
    private static IDictionary<string, string?> Simulator() =>
        new Dictionary<string, string?>
        {
            ["Trading:Exchange:Mode"] = "Simulator",
        };

    private static IDictionary<string, string?> WithMock() =>
        new Dictionary<string, string?>
        {
            ["Trading:Exchange:Mode"] = "Mock",
        };

    [Fact]
    public async Task Fill_FullQuantity_DrivesOrderToFilled()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var clOrdId = await SubmitOrder(http, userToken, qty: 100, price: 30m);

        var er = await InjectEr(http, adminToken, new
        {
            ClOrdId = clOrdId,
            Type = "Fill",
            LastQty = 100L,
            LastPx = 30m,
        });
        Assert.Equal(HttpStatusCode.Accepted, er.StatusCode);

        var listed = await ListOrders(http, userToken);
        var order = listed.Single(o => o.GetProperty("clOrdId").GetString() == clOrdId.ToString());
        Assert.Equal("Filled", order.GetProperty("status").GetString());
        Assert.Equal(100, order.GetProperty("cumulativeQuantity").GetInt64());
        Assert.Equal(0, order.GetProperty("leavesQuantity").GetInt64());
    }

    [Fact]
    public async Task PartialFill_ThenFill_AccumulatesCorrectly()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var clOrdId = await SubmitOrder(http, userToken, qty: 100, price: 30m);

        Assert.Equal(HttpStatusCode.Accepted,
            (await InjectEr(http, adminToken, new { ClOrdId = clOrdId, Type = "PartialFill", LastQty = 40L, LastPx = 30m })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,
            (await InjectEr(http, adminToken, new { ClOrdId = clOrdId, Type = "Fill", LastQty = 60L, LastPx = 30m })).StatusCode);

        var order = (await ListOrders(http, userToken)).Single(o => o.GetProperty("clOrdId").GetString() == clOrdId.ToString());
        Assert.Equal("Filled", order.GetProperty("status").GetString());
        Assert.Equal(100, order.GetProperty("cumulativeQuantity").GetInt64());
    }

    [Fact]
    public async Task Canceled_Shortcut_IgnoresLastQty_AndCancelsOrder()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var clOrdId = await SubmitOrder(http, userToken, qty: 100, price: 30m);

        // Caller omits lastQty/lastPx — atalho Canceled deve aceitar.
        var er = await InjectEr(http, adminToken, new
        {
            ClOrdId = clOrdId,
            Type = "Canceled",
        });
        Assert.Equal(HttpStatusCode.Accepted, er.StatusCode);

        var order = (await ListOrders(http, userToken)).Single(o => o.GetProperty("clOrdId").GetString() == clOrdId.ToString());
        Assert.Equal("Cancelled", order.GetProperty("status").GetString());
        // Note: WorkingOrderBook leaves the leaves field as-is on cancel
        // (status carries the terminal state). The simulator envelope
        // still carries leaves=0 so any consumer that reads it from
        // executions.me sees a consistent shape.
    }

    [Fact]
    public async Task Rejected_Without_Reason_Returns_400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var clOrdId = await SubmitOrder(http, userToken, qty: 10, price: 30m);

        var er = await InjectEr(http, adminToken, new
        {
            ClOrdId = clOrdId,
            Type = "Rejected",
        });
        Assert.Equal(HttpStatusCode.BadRequest, er.StatusCode);
    }

    [Fact]
    public async Task Overfill_Returns_400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var clOrdId = await SubmitOrder(http, userToken, qty: 100, price: 30m);

        var er = await InjectEr(http, adminToken, new
        {
            ClOrdId = clOrdId,
            Type = "Fill",
            LastQty = 101L,
            LastPx = 30m,
        });
        Assert.Equal(HttpStatusCode.BadRequest, er.StatusCode);
    }

    [Fact]
    public async Task UnknownClOrdId_Returns_404()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var adminToken = await f.LoginAsync(http, "admin");

        var er = await InjectEr(http, adminToken, new
        {
            ClOrdId = 99999999UL,
            Type = "Fill",
            LastQty = 1L,
            LastPx = 1m,
        });
        Assert.Equal(HttpStatusCode.NotFound, er.StatusCode);
    }

    [Fact]
    public async Task Mode_NotSimulator_Route_Returns_404()
    {
        using var f = TestAppFactory.WithOverrides(WithMock());
        using var http = f.CreateClient();
        var adminToken = await f.LoginAsync(http, "admin");

        var er = await InjectEr(http, adminToken, new
        {
            ClOrdId = 1UL,
            Type = "Fill",
            LastQty = 1L,
            LastPx = 1m,
        });
        Assert.Equal(HttpStatusCode.NotFound, er.StatusCode);
    }

    [Fact]
    public async Task NonAdmin_User_Returns_403()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http); // alice -> role=user

        var er = await InjectEr(http, userToken, new
        {
            ClOrdId = 1UL,
            Type = "Fill",
            LastQty = 1L,
            LastPx = 1m,
        });
        Assert.Equal(HttpStatusCode.Forbidden, er.StatusCode);
    }

    [Fact]
    public async Task Replaced_Type_Returns_400_OutOfV0Scope()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var clOrdId = await SubmitOrder(http, userToken, qty: 10, price: 30m);

        var er = await InjectEr(http, adminToken, new
        {
            ClOrdId = clOrdId,
            Type = "Replaced",
            LastQty = 0L,
        });
        Assert.Equal(HttpStatusCode.BadRequest, er.StatusCode);
    }

    private static async Task<HttpResponseMessage> InjectEr(HttpClient http, string token, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/simulator/er")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req);
    }

    private static async Task<ulong> SubmitOrder(HttpClient http, string token, int qty, decimal price)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new
            {
                Symbol = "PETR4",
                SecurityId = 4321UL,
                Side = "Buy",
                Type = "Limit",
                Quantity = qty,
                Price = price,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return ulong.Parse(body.GetProperty("clOrdId").GetString()!);
    }

    private static async Task<JsonElement[]> ListOrders(HttpClient http, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/orders/");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement[]>())!;
    }
}
