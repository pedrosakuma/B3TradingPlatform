using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Slice 4 of #122 — HTTP integration coverage for
/// <c>PUT /orders/{clOrdId}</c>. Asserts the mapping from
/// <see cref="B3.Trading.Application.OrderModifyResultKind"/> values
/// to status codes and the side-effect contract (in-flight guard,
/// owner-isolation, cum-quantity floor).
/// </summary>
public class OrdersModifyEndpointTests
{
    [Fact]
    public async Task PUT_orders_HappyPath_Returns202WithNewClOrdId()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var posted = await PostOrder(http, token, qty: 100, price: 30m);
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var origAck = await posted.Content.ReadFromJsonAsync<OrderAck>();

        var put = await PutModify(http, token, origAck!.ClOrdId, qty: 200, price: 30m);
        var bodyText = await put.Content.ReadAsStringAsync();
        Assert.True(put.StatusCode == HttpStatusCode.Accepted, $"Expected 202, got {put.StatusCode}: {bodyText}");
        var ack = System.Text.Json.JsonSerializer.Deserialize<ModifyAck>(bodyText,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(ack);
        Assert.Equal(origAck.ClOrdId, ack!.OriginalClOrdId);
        Assert.NotEqual(origAck.ClOrdId, ack.ClOrdId);
        Assert.False(string.IsNullOrEmpty(ack.ClOrdId));
    }

    [Fact]
    public async Task PUT_orders_InvalidClOrdIdFormat_Returns404()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var put = await PutModify(http, token, "not-a-number", qty: 200, price: 30m);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task PUT_orders_UnknownClOrdId_Returns404()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        // The ClOrdID space is per-end-client and starts non-zero; a
        // very large number will never have been generated.
        var put = await PutModify(http, token, "99999999999999999", qty: 200, price: 30m);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task PUT_orders_OwnerMismatch_Returns404_NoCrossOwnerLeak()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var aliceToken = await f.LoginAsync(http);
        var posted = await PostOrder(http, aliceToken, qty: 100, price: 30m);
        var origAck = await posted.Content.ReadFromJsonAsync<OrderAck>();

        // bob is a separately-seeded user (env-seeded alongside alice).
        var bobToken = await f.LoginAsync(http, user: "bob", password: "wonderland");
        var put = await PutModify(http, bobToken, origAck!.ClOrdId, qty: 200, price: 30m);
        // Same status as a non-existent order — do not leak existence
        // across owners.
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task PUT_orders_ZeroQuantity_Returns400()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var posted = await PostOrder(http, token, qty: 100, price: 30m);
        var origAck = await posted.Content.ReadFromJsonAsync<OrderAck>();

        var put = await PutModify(http, token, origAck!.ClOrdId, qty: 0, price: 30m);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task PUT_orders_SecondModifyForSameOrig_Returns409()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var posted = await PostOrder(http, token, qty: 100, price: 30m);
        var origAck = await posted.Content.ReadFromJsonAsync<OrderAck>();

        var first = await PutModify(http, token, origAck!.ClOrdId, qty: 200, price: 30m);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        // Mock gateway just queues the cancel-replace request; no ER
        // arrives, so the in-flight intent stays pending.
        var second = await PutModify(http, token, origAck.ClOrdId, qty: 250, price: 30m);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostOrder(
        HttpClient http, string token, int qty, decimal price, string side = "Buy")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new
            {
                Symbol = "PETR4",
                SecurityId = 4321UL,
                Side = side,
                Type = "Limit",
                Quantity = qty,
                Price = price,
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> PutModify(
        HttpClient http, string token, string clOrdId, int qty, decimal? price)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/orders/{clOrdId}")
        {
            Content = JsonContent.Create(new { Quantity = qty, Price = price })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req);
    }

    private sealed record OrderAck(string ClOrdId, string? Status, string? Reason);
    private sealed record ModifyAck(string ClOrdId, string OriginalClOrdId);
}
