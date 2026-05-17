using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q3.4 (#284). REST surface for the native iceberg / reserve
/// display-qty fields on <c>POST /orders</c>. Asserts both the
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
            displayQty: 10, policy: "OnPartialFill");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var ack = await resp.Content.ReadFromJsonAsync<OrderAck>();
        Assert.NotNull(ack);

        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        Assert.True(book.TryGet(ulong.Parse(ack!.ClOrdId), out var order));
        Assert.Equal(10L, order!.DisplayQty);
        Assert.Equal(DisplayResetPolicy.OnPartialFill, order.DisplayResetPolicy);
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
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req);
    }

    private sealed record OrderAck(string ClOrdId, string? Status, string? Reason);
    private sealed record ErrorBody(string Error);
}
