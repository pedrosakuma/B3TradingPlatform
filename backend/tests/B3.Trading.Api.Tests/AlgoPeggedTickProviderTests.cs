using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace B3.Trading.Api.Tests;

/// <summary>
/// #454 Fase 1. Exercises the POST /api/algo Pegged path through the new
/// <c>ITickSizeProvider</c> seam: explicit override wins, provider
/// resolves from <c>SymbolDirectory</c> when override is omitted, and
/// the request rejects (400) when neither is available — closing the
/// silent <c>0.01m</c> BRL-equity fallback that used to mask
/// venue-tick mismatches.
/// </summary>
public class AlgoPeggedTickProviderTests
{
    private static IDictionary<string, string?> BaseConfig() =>
        new Dictionary<string, string?>
        {
            ["Trading:Exchange:Mode"] = "Mock",
            ["Trading:Exchange:AllowErInjection"] = "true",
            ["Trading:SymbolDirectory:SecurityIds:PETR4"] = "4321",
            ["Trading:SymbolDirectory:SecurityIds:VALE3"] = "4322",
            ["Trading:SymbolDirectory:Specs:PETR4:TickSize"] = "0.01",
        };

    private static object PeggedBody(string symbol, decimal? tickSize) => new
    {
        Symbol = symbol,
        Side = "Buy",
        Type = "Pegged",
        TotalQuantity = 100L,
        Pegged = new
        {
            Ref = "Mid",
            OffsetTicks = 0,
            RepegIntervalMs = 100,
            TickSize = tickSize,
            ChildOrderType = (string?)null,
            PriceLimit = (decimal?)null,
        },
    };

    [Fact]
    public async Task Pegged_ExplicitOverride_Wins()
    {
        using var f = TestAppFactory.WithOverrides(BaseConfig());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var algoId = await PostAlgo(http, token, PeggedBody("PETR4", tickSize: 0.02m));
        var algo = await GetAlgo(http, token, algoId);
        var pgd = algo.GetProperty("pegged");
        Assert.Equal(0.02m, pgd.GetProperty("tickSize").GetDecimal());
    }

    [Fact]
    public async Task Pegged_NoOverride_ResolvesFromProvider()
    {
        using var f = TestAppFactory.WithOverrides(BaseConfig());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var algoId = await PostAlgo(http, token, PeggedBody("PETR4", tickSize: null));
        var algo = await GetAlgo(http, token, algoId);
        var pgd = algo.GetProperty("pegged");
        // PETR4 spec configured at 0.01.
        Assert.Equal(0.01m, pgd.GetProperty("tickSize").GetDecimal());
    }

    [Fact]
    public async Task Pegged_NoOverride_NoSpec_Returns400_NoSilentDefault()
    {
        // VALE3 has a SecurityId but no Specs entry → provider returns false
        // → endpoint must reject (the legacy 0.01m fallback is gone).
        using var f = TestAppFactory.WithOverrides(BaseConfig());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/algo/")
        {
            Content = JsonContent.Create(PeggedBody("VALE3", tickSize: null)),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("pegged.tickSize is required for symbol 'VALE3'", body);
        Assert.Contains("no per-symbol tick configured", body);
    }

    private static async Task<string> PostAlgo(HttpClient http, string token, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/algo/")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var posted = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return posted.GetProperty("algoId").GetString()!;
    }

    private static async Task<JsonElement> GetAlgo(HttpClient http, string token, string algoId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/algo/{algoId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }
}
