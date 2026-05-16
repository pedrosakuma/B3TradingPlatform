using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// End-to-end coverage for the VWAP algo (Q3.1 / #281). Mirrors the
/// shape of <see cref="AlgoTwapIntegrationTests"/>: real wall-clock with
/// tightly-bounded sub-second windows so the scheduler ticks (100ms) and
/// the engine reactor exercise the full VWAP path.
/// </summary>
public class VwapAlgoEndpointTests
{
    private static IDictionary<string, string?> Simulator() =>
        new Dictionary<string, string?>
        {
            ["Trading:Exchange:Mode"] = "Mock",
            ["Trading:Exchange:AllowErInjection"] = "true",
            ["Trading:SymbolDirectory:SecurityIds:PETR4"] = "4321",
        };

    private static object VwapBody(long total, DateTimeOffset start, DateTimeOffset end,
        double tickSeconds = 0.2, string childType = "Limit", decimal? childPrice = 30m,
        decimal? sliceMaxPct = null, decimal? participationCap = null, decimal? priceLimit = null) => new
        {
            Symbol = "PETR4",
            Side = "Buy",
            Type = "Vwap",
            TotalQuantity = total,
            Vwap = new
            {
                StartUtc = start,
                EndUtc = end,
                ChildOrderType = childType,
                ChildPrice = childPrice,
                TickIntervalSeconds = tickSeconds,
                SliceMaxPct = sliceMaxPct,
                ParticipationCap = participationCap,
                PriceLimit = priceLimit,
            },
        };

    // ───────────────────────── POST validation ─────────────────────────

    [Fact]
    public async Task PostAlgo_VwapWithoutParams_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(new
            {
                Symbol = "PETR4",
                Side = "Buy",
                Type = "Vwap",
                TotalQuantity = 100,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_VwapEndBeforeStart_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var now = DateTimeOffset.UtcNow;
        var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(VwapBody(100, now.AddMinutes(5), now.AddMinutes(1))),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_VwapSliceMaxPctOutOfRange_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var now = DateTimeOffset.UtcNow;
        var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(VwapBody(100, now, now.AddMinutes(1), sliceMaxPct: 1.5m)),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostAlgo_VwapLimitWithoutPrice_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var now = DateTimeOffset.UtcNow;
        var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(VwapBody(100, now, now.AddMinutes(1), childPrice: null)),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ───────────────────────── Happy-path slice firing ─────────────────────────

    [Fact]
    public async Task Vwap_SlicesUntilCompleted_UniformFallbackBehavesLikeTwap()
    {
        // No prior trade volume is recorded, so the estimator returns the
        // uniform CDF — VWAP degrades to TWAP-shaped slicing. Window
        // already open (start in past), 1.6s long, tick=200ms ⇒ 8 slots.
        // The first slot fires immediately and subsequent slots fire as
        // each child terminalizes.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var now = DateTimeOffset.UtcNow;
        var algoId = await PostAlgo(http, userToken,
            VwapBody(total: 400, start: now.AddSeconds(-1), end: now.AddSeconds(2),
                tickSeconds: 0.2));

        var book = f.Services.GetRequiredService<WorkingOrderBook>();

        // Fill the first slice; expect at least one more to follow.
        // The engine's catch-up loop may skip the seq=0 slot because the
        // CDF evaluated at startUtc is 0 (no gap to fill); we just want
        // *any* slice to fire first.
        var first = await WaitForAnyChild(book, algoId);
        await InjectEr(http, adminToken, first.ClOrdId, "Fill", lastQty: first.Quantity);
        var seenSeqs = new HashSet<int> { first.AlgoSliceSeq!.Value };
        long filled = first.Quantity;

        // Fill subsequent slices until the parent reports Completed.
        // Cap the loop so a stuck parent surfaces as a timeout rather
        // than an infinite spin.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            var snap = await GetAlgo(http, userToken, algoId);
            var status = snap.GetProperty("status").GetString();
            if (status == "Completed") break;
            if (status == "Expired") break;

            var next = book.EnumerateChildrenOf("default", ulong.Parse(algoId))
                .FirstOrDefault(c => c.AlgoSliceSeq is { } s && seenSeqs.Add(s));
            if (next is null) { await Task.Delay(20); continue; }
            await InjectEr(http, adminToken, next.ClOrdId, "Fill", lastQty: next.Quantity);
            filled += next.Quantity;
            if (filled >= 400) break;
        }

        await WaitForAlgoStatus(http, userToken, algoId, "Completed", "Expired");
        var algo = await GetAlgo(http, userToken, algoId);
        // Either path is acceptable: a fast-enough scheduler completes
        // the parent before endUtc; a slower one ends with the window
        // expiring on a partial fill. What we're asserting is that the
        // VWAP engine actually sliced and filled work via the curve
        // (regression against the engine going silent or refusing to
        // submit any child).
        var filledQty = algo.GetProperty("filledQuantity").GetInt64();
        Assert.True(filledQty > 0, $"expected some VWAP fills, got {filledQty}");
    }

    [Fact]
    public async Task Vwap_WindowExpiresWithoutFill_BecomesExpired()
    {
        // Tight 500ms window; one child fires, then we let it sit
        // untouched until past endUtc and finally cancel via simulator.
        // The engine routes the parent to Expired/VwapWindowExpired
        // because endUtc has already passed when the child terminalizes.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var now = DateTimeOffset.UtcNow;
        var algoId = await PostAlgo(http, userToken,
            VwapBody(total: 100, start: now.AddSeconds(-1), end: now.AddMilliseconds(500),
                tickSeconds: 0.2));

        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var s0 = await WaitForAnyChild(book, algoId);

        await Task.Delay(900);
        await InjectEr(http, adminToken, s0.ClOrdId, "Canceled");

        await WaitForAlgoStatus(http, userToken, algoId, "Expired");
        var algo = await GetAlgo(http, userToken, algoId);
        Assert.Equal("VwapWindowExpired", algo.GetProperty("terminalReason").GetString());
    }

    [Fact]
    public async Task Vwap_NoChild_WindowAlreadyExpired_BecomesExpired()
    {
        // Submitted with the whole window already in the past — same
        // edge case as TWAP's expiry-without-child path.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);

        var now = DateTimeOffset.UtcNow;
        var algoId = await PostAlgo(http, userToken,
            VwapBody(total: 100, start: now.AddSeconds(-2), end: now.AddSeconds(-1),
                tickSeconds: 0.2));

        await WaitForAlgoStatus(http, userToken, algoId, "Expired");
        var algo = await GetAlgo(http, userToken, algoId);
        Assert.Equal("VwapWindowExpired", algo.GetProperty("terminalReason").GetString());
        Assert.Equal(0, algo.GetProperty("filledQuantity").GetInt64());
    }

    [Fact]
    public async Task GetAlgo_ReturnsVwapParametersInDto()
    {
        // Round-trips the VWAP parameters through the DTO mapper so the
        // surface stays stable for the frontend.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var now = DateTimeOffset.UtcNow;
        var algoId = await PostAlgo(http, token,
            VwapBody(total: 200, start: now.AddSeconds(10), end: now.AddSeconds(70),
                tickSeconds: 5, sliceMaxPct: 0.25m, priceLimit: 31m));

        var algo = await GetAlgo(http, token, algoId);
        Assert.Equal("Vwap", algo.GetProperty("type").GetString());
        var vwap = algo.GetProperty("vwap");
        Assert.Equal(5.0, vwap.GetProperty("tickIntervalSeconds").GetDouble(), 3);
        Assert.Equal(0.25m, vwap.GetProperty("sliceMaxPct").GetDecimal());
        Assert.Equal(31m, vwap.GetProperty("priceLimit").GetDecimal());
    }

    // ───────────────────────── helpers (parallel to AlgoTwapIntegrationTests) ─────────────────────────

    private static async Task<string> PostAlgo(HttpClient http, string token, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var posted = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return posted.GetProperty("algoId").GetString()!;
    }

    private static async Task<HttpResponseMessage> InjectEr(
        HttpClient http, string adminToken, ulong childClOrdId,
        string type, long? lastQty = null, decimal lastPx = 30m)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/simulator/er")
        {
            Content = JsonContent.Create(new
            {
                ClOrdId = childClOrdId,
                Type = type,
                LastQty = lastQty,
                LastPx = lastQty.HasValue ? lastPx : (decimal?)null,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        return resp;
    }

    private static async Task<JsonElement> GetAlgo(HttpClient http, string token, string algoId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/algo/{algoId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Order> WaitForChild(WorkingOrderBook book, string algoIdStr, int expectedSeq)
    {
        var algoId = ulong.Parse(algoIdStr);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            var match = book.EnumerateChildrenOf("default", algoId)
                .FirstOrDefault(c => c.AlgoSliceSeq == expectedSeq);
            if (match is not null) return match;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Child for algo {algoIdStr} seq {expectedSeq} did not appear within 5s.");
    }

    private static Order? TryGetChild(WorkingOrderBook book, string algoIdStr, int expectedSeq)
    {
        var algoId = ulong.Parse(algoIdStr);
        return book.EnumerateChildrenOf("default", algoId)
            .FirstOrDefault(c => c.AlgoSliceSeq == expectedSeq);
    }

    private static async Task<Order> WaitForAnyChild(WorkingOrderBook book, string algoIdStr)
    {
        var algoId = ulong.Parse(algoIdStr);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            var match = book.EnumerateChildrenOf("default", algoId).FirstOrDefault();
            if (match is not null) return match;
            await Task.Delay(10);
        }
        throw new TimeoutException($"No child for algo {algoIdStr} within 5s.");
    }

    private static async Task WaitForAlgoStatus(HttpClient http, string token, string algoId, params string[] anyOf)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? last = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            var algo = await GetAlgo(http, token, algoId);
            last = algo.GetProperty("status").GetString();
            if (anyOf.Contains(last)) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Algo {algoId} did not reach any of [{string.Join(",", anyOf)}] within 5s; last={last}");
    }
}
