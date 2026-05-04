using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Coverage for <c>GET /admin/marketdata/reference-prices</c> — the
/// diagnostics surface that lets ops introspect the reference-price
/// plumbing without deriving it from metric tags. Slice A of
/// real-stack v2: this endpoint is the contract the v2 conformance
/// spec will assert against.
/// </summary>
public class AdminMarketDataEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AdminMarketDataEndpointTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task ReferencePrices_RequiresAdminRole()
    {
        using var user = await _factory.CreateAuthedClientAsync(); // role=user
        var resp = await user.GetAsync("/admin/marketdata/reference-prices?symbols=PETR4");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ReferencePrices_DefaultFactory_NoMarketData_ReportsFallbackOnly()
    {
        // The default test factory leaves Trading:MarketData:WsUrl unset,
        // so MarketDataReferencePrice is not registered and the endpoint
        // gracefully degrades to a fallback-only view.
        using var f = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Risk:ReferencePrices:PETR4"] = "32.50",
        });
        using var admin = await f.CreateAuthedClientAsync("admin");

        var resp = await admin.GetAsync("/admin/marketdata/reference-prices?symbols=PETR4");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.False(body.GetProperty("marketDataEnabled").GetBoolean());

        var entries = body.GetProperty("symbols");
        Assert.Equal(1, entries.GetArrayLength());
        var entry = entries[0];
        Assert.Equal("PETR4", entry.GetProperty("symbol").GetString());
        Assert.Equal("Fallback", entry.GetProperty("effectiveSource").GetString());
        Assert.Equal(32.50m, entry.GetProperty("effectivePrice").GetDecimal());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("live").ValueKind);
        Assert.Equal(32.50m, entry.GetProperty("fallbackPrice").GetDecimal());
    }

    [Fact]
    public async Task ReferencePrices_UnknownSymbol_ReportsMissing()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");

        var resp = await admin.GetAsync("/admin/marketdata/reference-prices?symbols=NEVERHEARD");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var entry = body.GetProperty("symbols")[0];
        Assert.Equal("NEVERHEARD", entry.GetProperty("symbol").GetString());
        Assert.Equal("Missing", entry.GetProperty("effectiveSource").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("effectivePrice").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("live").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("fallbackPrice").ValueKind);
    }

    [Fact]
    public async Task ReferencePrices_NoSymbolsQuery_ReturnsUnionOfConfigured()
    {
        // With no `symbols` query param, the endpoint should enumerate
        // everything it knows about: MD-subscribed symbols ∪ static
        // fallback keys, de-duplicated. Default factory has no MD
        // subscriptions, so this exercises just the static keys.
        using var f = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Risk:ReferencePrices:PETR4"] = "32.50",
            ["Trading:Risk:ReferencePrices:VALE3"] = "65.00",
        });
        using var admin = await f.CreateAuthedClientAsync("admin");

        var resp = await admin.GetAsync("/admin/marketdata/reference-prices");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var symbols = body.GetProperty("symbols")
            .EnumerateArray()
            .Select(e => e.GetProperty("symbol").GetString())
            .ToHashSet();
        Assert.Contains("PETR4", symbols);
        Assert.Contains("VALE3", symbols);
    }

    [Fact]
    public async Task ReferencePrices_DuplicateSymbolsInQuery_AreDeduped()
    {
        using var f = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Risk:ReferencePrices:PETR4"] = "32.50",
        });
        using var admin = await f.CreateAuthedClientAsync("admin");

        var resp = await admin.GetAsync("/admin/marketdata/reference-prices?symbols=PETR4,PETR4,petr4");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(1, body.GetProperty("symbols").GetArrayLength());
    }

    [Fact]
    public async Task ReferencePrices_ReportsExchangeMode()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.GetAsync("/admin/marketdata/reference-prices?symbols=PETR4");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var mode = body.GetProperty("mode").GetString();
        Assert.False(string.IsNullOrEmpty(mode));
    }
}
