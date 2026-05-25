using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using xRetry;

namespace B3.Trading.Api.Tests;

/// <summary>
/// HTTP surface for algo orders v0 (RFC §4.9). v0 has no engine — POST
/// records intent + assigns an id, GET reflects in-memory state, DELETE
/// drives the parent into <c>Cancelling</c>. No child orders are
/// generated and no terminal transition happens automatically.
/// </summary>
public class AlgoEndpointsTests
{
    private static TestAppFactory NewFactory()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Trading:SymbolDirectory:SecurityIds:PETR4"] = "4321",
            ["Trading:SymbolDirectory:SecurityIds:VALE3"] = "1234",
        };
        return TestAppFactory.WithOverrides(overrides);
    }

    private static object IcebergBody(long total = 1000, long display = 100, decimal? price = 30m) => new
    {
        Symbol = "PETR4",
        Side = "Buy",
        Type = "Iceberg",
        TotalQuantity = total,
        Iceberg = new { DisplayQuantity = display, LimitPrice = price },
    };

    private static object TwapBody(string childType = "Limit", decimal? childPrice = 30m, int sliceCount = 5,
        DateTimeOffset? start = null, DateTimeOffset? end = null) => new
        {
            Symbol = "VALE3",
            Side = "Sell",
            Type = "Twap",
            TotalQuantity = 5000,
            Twap = new
            {
                StartUtc = start ?? DateTimeOffset.UtcNow,
                EndUtc = end ?? DateTimeOffset.UtcNow.AddMinutes(10),
                SliceCount = sliceCount,
                ChildOrderType = childType,
                ChildPrice = childPrice,
            },
        };

    // Known host-dispose race (ObjectDisposedException at
    // WebSocketBalanceFanOut.StopAsync); retry 3x.
    [RetryFact(maxRetries: 3, delayBetweenRetriesMs: 250)]
    public async Task PostAlgo_Iceberg_HappyPath_ReturnsAcceptedWithPendingNew()
    {
        using var factory = NewFactory();
        using var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/algo/", IcebergBody());
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("algoId").GetString()));
        Assert.Equal("PendingNew", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostAlgo_Twap_HappyPath()
    {
        using var factory = NewFactory();
        using var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/algo/", TwapBody());
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_Iceberg_DisplayExceedsTotal_Returns400()
    {
        using var factory = NewFactory();
        using var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/algo/", IcebergBody(total: 100, display: 200));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_Twap_LimitWithoutChildPrice_Returns400()
    {
        // OQ-2: TWAP+Limit MUST carry a child price.
        using var factory = NewFactory();
        using var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/algo/", TwapBody(childType: "Limit", childPrice: null));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("childPrice", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostAlgo_Twap_MarketWithoutChildPrice_IsAccepted()
    {
        // Market children don't carry a price; the explicit nullable
        // childPrice is allowed. This is the inverse of the Limit case
        // above — the API rejects ambiguous Limit submissions but accepts
        // Market with no price.
        using var factory = NewFactory();
        using var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/algo/", TwapBody(childType: "Market", childPrice: null));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_Twap_EndBeforeStart_Returns400()
    {
        using var factory = NewFactory();
        using var client = await factory.CreateAuthedClientAsync();
        var now = DateTimeOffset.UtcNow;

        var resp = await client.PostAsJsonAsync("/algo/", TwapBody(start: now.AddMinutes(10), end: now));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_NegativeQuantity_Returns400()
    {
        using var factory = NewFactory();
        using var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/algo/", new
        {
            Symbol = "PETR4",
            Side = "Buy",
            Type = "Iceberg",
            TotalQuantity = -1,
            Iceberg = new { DisplayQuantity = 10, LimitPrice = (decimal?)30m },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_InvalidType_Returns400()
    {
        using var factory = NewFactory();
        using var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/algo/", new
        {
            Symbol = "PETR4",
            Side = "Buy",
            Type = "Sniper", // not a valid algo type
            TotalQuantity = 100,
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_Unauthenticated_Returns401()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/algo/", IcebergBody());
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task GetAlgo_ListReturnsSubmittedAlgo_ById_RoundTrips()
    {
        using var factory = NewFactory();
        using var client = await factory.CreateAuthedClientAsync();

        var post = await client.PostAsJsonAsync("/algo/", IcebergBody());
        var posted = await post.Content.ReadFromJsonAsync<JsonElement>();
        var algoId = posted.GetProperty("algoId").GetString()!;

        var list = await client.GetFromJsonAsync<JsonElement>("/algo/");
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        Assert.Equal(1, list.GetArrayLength());

        var get = await client.GetFromJsonAsync<JsonElement>($"/algo/{algoId}");
        Assert.Equal(algoId, get.GetProperty("algoId").GetString());
        Assert.Equal("PETR4", get.GetProperty("symbol").GetString());
        // Engine may have already promoted PendingNew → Working by submitting
        // the first slice through Mock (no ER comes back so it stays Working
        // indefinitely). Accept either; GET should never see a terminal state.
        var status = get.GetProperty("status").GetString();
        Assert.Contains(status, new[] { "PendingNew", "Working" });
        // Discriminated parameter shape: iceberg block populated, twap null.
        Assert.Equal(JsonValueKind.Object, get.GetProperty("iceberg").ValueKind);
        Assert.Equal(JsonValueKind.Null, get.GetProperty("twap").ValueKind);
    }

    [Fact]
    public async Task GetAlgo_OtherUsersAlgo_Returns404()
    {
        using var factory = NewFactory();
        using var aliceClient = await factory.CreateAuthedClientAsync();
        using var bobClient = await factory.CreateAuthedClientAsync(user: "bob");

        var post = await aliceClient.PostAsJsonAsync("/algo/", IcebergBody());
        var posted = await post.Content.ReadFromJsonAsync<JsonElement>();
        var algoId = posted.GetProperty("algoId").GetString()!;

        var bobView = await bobClient.GetAsync($"/algo/{algoId}");
        Assert.Equal(HttpStatusCode.NotFound, bobView.StatusCode);

        // And bob's list does not see alice's algo.
        var bobList = await bobClient.GetFromJsonAsync<JsonElement>("/algo/");
        Assert.Equal(0, bobList.GetArrayLength());
    }

    [Fact]
    public async Task DeleteAlgo_HappyPath_ReturnsAcceptedWithCancelling()
    {
        using var factory = NewFactory();
        using var client = await factory.CreateAuthedClientAsync();

        var post = await client.PostAsJsonAsync("/algo/", IcebergBody());
        var posted = await post.Content.ReadFromJsonAsync<JsonElement>();
        var algoId = posted.GetProperty("algoId").GetString()!;

        var del = await client.DeleteAsync($"/algo/{algoId}");
        Assert.Equal(HttpStatusCode.Accepted, del.StatusCode);
        var body = await del.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Cancelling", body.GetProperty("status").GetString());

        // Subsequent GET reflects the new status. Mock gateway never delivers
        // a child cancel-ack so the engine stays in Cancelling indefinitely;
        // the only racy variant is the engine processing the cancel before a
        // live child existed (no child to cancel → Cancelled immediately).
        var get = await client.GetFromJsonAsync<JsonElement>($"/algo/{algoId}");
        var status = get.GetProperty("status").GetString();
        Assert.Contains(status, new[] { "Cancelling", "Cancelled" });
    }

    [Fact]
    public async Task DeleteAlgo_UnknownId_Returns404()
    {
        using var factory = NewFactory();
        using var client = await factory.CreateAuthedClientAsync();

        var del = await client.DeleteAsync("/algo/99999999");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
    }

    [Fact]
    public async Task DeleteAlgo_OtherUsersAlgo_Returns404()
    {
        using var factory = NewFactory();
        using var aliceClient = await factory.CreateAuthedClientAsync();
        using var bobClient = await factory.CreateAuthedClientAsync(user: "bob");

        var post = await aliceClient.PostAsJsonAsync("/algo/", IcebergBody());
        var posted = await post.Content.ReadFromJsonAsync<JsonElement>();
        var algoId = posted.GetProperty("algoId").GetString()!;

        var del = await bobClient.DeleteAsync($"/algo/{algoId}");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
    }

    [Fact]
    public async Task DeleteAlgo_AlreadyCancelling_IsIdempotent()
    {
        // Per RFC: Cancelling-on-Cancelling is a no-op via the aggregate
        // and the API returns 202 again. Once the engine drives the parent
        // to a terminal state (Cancelled), subsequent DELETEs return 409
        // — that's the documented "you're racing the engine" signal.
        using var factory = NewFactory();
        using var client = await factory.CreateAuthedClientAsync();

        var post = await client.PostAsJsonAsync("/algo/", IcebergBody());
        var posted = await post.Content.ReadFromJsonAsync<JsonElement>();
        var algoId = posted.GetProperty("algoId").GetString()!;

        var first = await client.DeleteAsync($"/algo/{algoId}");
        var second = await client.DeleteAsync($"/algo/{algoId}");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Contains(second.StatusCode, new[] { HttpStatusCode.Accepted, HttpStatusCode.Conflict });
    }
}
