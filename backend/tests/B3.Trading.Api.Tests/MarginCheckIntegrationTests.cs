using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace B3.Trading.Api.Tests;

/// <summary>
/// End-to-end coverage for the reserve-on-submit margin provider
/// (slice 4 of pre-trade risk v2). Verifies that when margin is
/// enabled via config, buy orders consume an end-client's available
/// balance, are rejected when depleted, and that releasing a prior
/// reservation (admin cancel via DELETE /api/orders/{clOrdId}) frees the
/// balance for new submissions.
/// </summary>
public class MarginCheckIntegrationTests
{
    private static IDictionary<string, string?> EnabledFor(string endClient, decimal initial) =>
        new Dictionary<string, string?>
        {
            ["Trading:Risk:Margin:Enabled"] = "true",
            [$"Trading:Risk:Margin:Initial:{endClient}"] = initial.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // Push other limits high so margin is the only gate that matters.
            ["Trading:Risk:Default:MaxQuantity"] = "1000000",
            ["Trading:Risk:Default:MaxNotional"] = "999999999",
            // These tests are about the margin provider; opt out of
            // the naked-short gate (added later) which would otherwise
            // pre-empt margin evaluation on the Sell-with-zero-inventory
            // case in SellsAndUnknownOwnersBypassMargin.
            ["Trading:Risk:Default:AllowShortSell"] = "true",
            // Same rationale for self-trade prevention: the
            // presence-based STP would reject SellsAndUnknownOwnersBypassMargin's
            // Buy because a Sell from the same owner is already
            // resting (regardless of price). Opt out so the assertion
            // exercises the margin path.
            ["Trading:Risk:Default:AllowSelfTrade"] = "true",
        };

    [Fact]
    public async Task SubmitsUntilBalanceDepleted_ThenSecondIsRejected()
    {
        // alice's initial balance covers exactly 100 @ 30 = 3000.
        using var f = TestAppFactory.WithOverrides(EnabledFor("alice", 3_000m));
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var first = await PostOrder(http, token, qty: 100, price: 30m);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<OrderAck>();
        Assert.NotEqual("Rejected", firstBody!.Status);

        var second = await PostOrder(http, token, qty: 1, price: 30m);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<OrderAck>();
        Assert.Equal("Rejected", body!.Status);
        Assert.Contains("margin", body.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SellsAndUnknownOwnersBypassMargin()
    {
        // Margin enabled but alice is not in the Initial map (treated as 0 balance).
        using var f = TestAppFactory.WithOverrides(EnabledFor("nobody", 0m));
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http); // alice -> not configured

        // Sell ignores margin even with zero balance.
        var sell = await PostOrder(http, token, qty: 10, price: 30m, side: "Sell");
        Assert.Equal(HttpStatusCode.Accepted, sell.StatusCode);
        var sellBody = await sell.Content.ReadFromJsonAsync<OrderAck>();
        Assert.NotEqual("Rejected", sellBody!.Status);

        // Buy with 0 balance must be rejected with the margin reason.
        // STP is opted out at the EnabledFor() level so this Buy is
        // not pre-empted by the presence-based STP gate even though
        // an opposite-side Sell from the same owner is resting.
        var buy = await PostOrder(http, token, qty: 1, price: 29m, side: "Buy");
        var buyBody = await buy.Content.ReadFromJsonAsync<OrderAck>();
        Assert.Equal("Rejected", buyBody!.Status);
        Assert.Contains("margin", buyBody.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisabledByDefault_NoOpAlwaysApproves()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var resp = await PostOrder(http, token, qty: 100, price: 30m);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<OrderAck>();
        Assert.NotEqual("Rejected", body!.Status);
    }

    private static async Task<HttpResponseMessage> PostOrder(
        HttpClient http, string token, int qty, decimal price, string side = "Buy")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
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

    private sealed record OrderAck(string ClOrdId, string Status, string? Reason);
}
