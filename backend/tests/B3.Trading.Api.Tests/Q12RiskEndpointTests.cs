using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q1.2 (#254) end-to-end coverage for the new risk gates over the
/// Q1.1 order surface. Reuses the deterministic <see cref="TestAppFactory"/>
/// (PETR4 reference price seeded at 30.0; Risk pipeline up).
///
/// <para>Submit risk rejections come back as <c>202 Accepted</c> with
/// <c>Status="Rejected"</c> — the existing contract on <c>POST /api/orders</c>;
/// modify risk rejections come back as <c>422 UnprocessableEntity</c>.
/// We assert the reason string carries the machine-readable prefix so
/// downstream consumers (UI / FIXP bot) can branch on it.</para>
/// </summary>
public class Q12RiskEndpointTests
{
    [Fact]
    public async Task POST_StopLoss_BuyBelowRef_RejectsWithStopTriggerReason()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        // PETR4 ref = 30. Buy stop below ref is invalid.
        var resp = await PostStop(http, token, "Buy", "StopLoss", stop: 25m, price: null);
        var ack = await ReadAck(resp);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal("Rejected", ack.Status);
        Assert.StartsWith("stop_trigger_invalid", ack.Reason);
    }

    [Fact]
    public async Task POST_StopLimit_BuyLimitBelowStop_RejectsWithStopLimitPriceReason()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        // Buy stop above ref OK (32 > 30), but limit below stop is invalid for buy.
        var resp = await PostStop(http, token, "Buy", "StopLimit", stop: 32m, price: 31m);
        var ack = await ReadAck(resp);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.StartsWith("stop_limit_price_invalid", ack.Reason);
    }

    [Fact]
    public async Task POST_MarketWithLeftover_IOC_RejectsWithTifIncompatReason()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var resp = await PostBase(http, token, new
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "MarketWithLeftover",
            Quantity = 100,
            Price = (decimal?)null,
            TimeInForce = "IOC",
        });
        var ack = await ReadAck(resp);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.StartsWith("tif_incompatible_with_market_with_leftover", ack.Reason);
    }

    [Fact]
    public async Task POST_GoodForAuction_OutsideAuction_Rejects()
    {
        // Default IPhaseProvider (NoPhaseProvider) reports Open ⇒ rejects every GFA.
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var resp = await PostBase(http, token, new
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
            TimeInForce = "GoodForAuction",
        });
        var ack = await ReadAck(resp);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.StartsWith("gfa_outside_auction_phase", ack.Reason);
    }

    [Fact]
    public async Task POST_GTD_BeyondHorizon_Rejects()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var beyond = DateTimeOffset.UtcNow.AddDays(60); // default horizon = 30
        var resp = await PostBase(http, token, new
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
            TimeInForce = "GTD",
            GoodTillDate = beyond,
        });
        var ack = await ReadAck(resp);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.StartsWith("gtd_invalid", ack.Reason);
    }

    [Fact]
    public async Task POST_GTD_WithinHorizon_Accepted()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var ok = DateTimeOffset.UtcNow.AddDays(7);
        var resp = await PostBase(http, token, new
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
            TimeInForce = "GTD",
            GoodTillDate = ok,
        });
        var ack = await ReadAck(resp);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.True(ack.Status is null or "" or not "Rejected", $"unexpected reject: {ack.Reason}");
    }

    [Fact]
    public async Task PUT_Modify_StopTrigger_ViolatedOnReplace_Rejects422()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        // Submit a valid Buy StopLimit (ref 30, stop 31 ≥ ref, limit 32 ≥ stop).
        var posted = await PostStop(http, token, "Buy", "StopLimit", stop: 31m, price: 32m);
        var origAck = await ReadAck(posted);
        Assert.True(origAck.Status != "Rejected", $"unexpected submit reject: {origAck.Reason}");

        // Modify pulls the stop below ref ⇒ stop_trigger_invalid.
        var put = await PutModify(http, token, origAck.ClOrdId, new
        {
            Quantity = 100L,
            Price = (decimal?)32m,
            StopPrice = (decimal?)25m,
        });
        var bodyText = await put.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Contains("stop_trigger_invalid", bodyText);
    }

    [Fact]
    public async Task PUT_Modify_GTD_BeyondHorizon_Rejects422()
    {
        using var f = new TestAppFactory();
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        // Plain Day order first — TIF/GTD upgraded on the modify.
        var posted = await PostBase(http, token, new
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
        });
        var origAck = await ReadAck(posted);
        Assert.True(string.IsNullOrEmpty(origAck.Reason));

        var beyond = DateTimeOffset.UtcNow.AddDays(60);
        var put = await PutModify(http, token, origAck.ClOrdId, new
        {
            Quantity = 100L,
            Price = (decimal?)30m,
            TimeInForce = "GTD",
            GoodTillDate = beyond,
        });
        var bodyText = await put.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Contains("gtd_invalid", bodyText);
    }

    // ---- helpers ----

    private static async Task<HttpResponseMessage> PostBase(HttpClient http, string token, object payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req);
    }

    private static Task<HttpResponseMessage> PostStop(
        HttpClient http, string token, string side, string type,
        decimal stop, decimal? price) =>
        PostBase(http, token, new
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = side,
            Type = type,
            Quantity = 100,
            Price = price,
            StopPrice = (decimal?)stop,
        });

    private static async Task<HttpResponseMessage> PutModify(
        HttpClient http, string token, string clOrdId, object payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/orders/{clOrdId}")
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req);
    }

    private static async Task<OrderAck> ReadAck(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        var ack = JsonSerializer.Deserialize<OrderAck>(body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(ack);
        return ack!;
    }

    private sealed record OrderAck(string ClOrdId, string? Status, string? Reason, string? Code);
}
