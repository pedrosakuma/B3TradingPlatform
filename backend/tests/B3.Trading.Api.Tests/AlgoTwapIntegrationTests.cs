using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// End-to-end coverage for the algo orders v0 TWAP engine (RFC §4.6 +
/// §4.8). Boots the host with <c>Trading:Exchange:Mode=Mock</c> +
/// <c>AllowErInjection=true</c> so
/// the scheduler-driven child orders land in <c>WorkingOrderBook</c> and
/// synthetic ERs can be driven via <c>POST /admin/simulator/er</c>.
///
/// <para>
/// These tests use real wall-clock time deliberately. Tightly-bounded
/// TWAP windows (sub-second) keep them fast; the scheduler ticks every
/// 100ms in the host so a slice typically fires within ~150ms of becoming
/// due. Generous polling timeouts (5s) absorb CI scheduling jitter.
/// </para>
/// </summary>
public class AlgoTwapIntegrationTests
{
    private static IDictionary<string, string?> Simulator() =>
        new Dictionary<string, string?>
        {
            ["Trading:Exchange:Mode"] = "Mock",
            ["Trading:Exchange:AllowErInjection"] = "true",
            ["Trading:SymbolDirectory:SecurityIds:PETR4"] = "4321",
        };

    private static object TwapBody(long total, int sliceCount, DateTimeOffset start, DateTimeOffset end,
        string childType = "Limit", decimal? childPrice = 30m) => new
        {
            Symbol = "PETR4",
            Side = "Buy",
            Type = "Twap",
            TotalQuantity = total,
            Twap = new
            {
                StartUtc = start,
                EndUtc = end,
                SliceCount = sliceCount,
                ChildOrderType = childType,
                ChildPrice = childPrice,
            },
        };

    [Fact]
    public async Task Twap_AllSlicesDueAtSubmit_FiresOnePerTickUntilCompleted()
    {
        // Window opens 5s in the past and closes 5s in the future:
        // every slice's plannedAtUtc is already <= now, so the
        // scheduler immediately starts firing slices. The "no catch-up
        // burst" rule (RFC §4.6) means slices fire one-at-a-time at
        // tick granularity; the engine's per-parent serialisation
        // (one in-flight child) keeps them strictly sequential.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var now = DateTimeOffset.UtcNow;
        var algoId = await PostAlgo(http, userToken,
            TwapBody(total: 300, sliceCount: 3,
                start: now.AddSeconds(-5), end: now.AddSeconds(5)));

        var book = f.Services.GetRequiredService<WorkingOrderBook>();

        // Slices 0..2 each carry 100 (300/3 even split).
        var s0 = await WaitForChild(book, algoId, expectedSeq: 0);
        Assert.Equal(100, s0.Quantity);
        await InjectEr(http, adminToken, s0.ClOrdId, "Fill", lastQty: 100);

        var s1 = await WaitForChild(book, algoId, expectedSeq: 1);
        Assert.Equal(100, s1.Quantity);
        await InjectEr(http, adminToken, s1.ClOrdId, "Fill", lastQty: 100);

        var s2 = await WaitForChild(book, algoId, expectedSeq: 2);
        Assert.Equal(100, s2.Quantity);
        await InjectEr(http, adminToken, s2.ClOrdId, "Fill", lastQty: 100);

        await WaitForAlgoStatus(http, userToken, algoId, "Completed");
        var algo = await GetAlgo(http, userToken, algoId);
        Assert.Equal(300, algo.GetProperty("filledQuantity").GetInt64());
        Assert.Equal("None", algo.GetProperty("terminalReason").GetString());
    }

    [Fact]
    public async Task Twap_LastSliceCarriesRemainder()
    {
        // 1003 / 3 = 334 floor → slices 0,1 = 334 each, slice 2 = 335.
        // Verifies §4.8 last-slice-remainder rule end-to-end.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var now = DateTimeOffset.UtcNow;
        var algoId = await PostAlgo(http, userToken,
            TwapBody(total: 1003, sliceCount: 3,
                start: now.AddSeconds(-15), end: now.AddSeconds(5)));

        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var s0 = await WaitForChild(book, algoId, expectedSeq: 0);
        Assert.Equal(334, s0.Quantity);
        await InjectEr(http, adminToken, s0.ClOrdId, "Fill", lastQty: 334);

        var s1 = await WaitForChild(book, algoId, expectedSeq: 1);
        Assert.Equal(334, s1.Quantity);
        await InjectEr(http, adminToken, s1.ClOrdId, "Fill", lastQty: 334);

        var s2 = await WaitForChild(book, algoId, expectedSeq: 2);
        Assert.Equal(335, s2.Quantity); // remainder lands on the last slice
        await InjectEr(http, adminToken, s2.ClOrdId, "Fill", lastQty: 335);

        await WaitForAlgoStatus(http, userToken, algoId, "Completed");
        var algo = await GetAlgo(http, userToken, algoId);
        Assert.Equal(1003, algo.GetProperty("filledQuantity").GetInt64());
    }

    [Fact]
    public async Task Twap_WindowExpiresWithoutFill_ParentBecomesExpired()
    {
        // Tight window: slice 0 fires immediately, then we let the
        // window pass without injecting any fill on the live child.
        // Eventually we cancel the child via the simulator; engine
        // observes child terminalize past endUtc and routes the
        // parent to Expired/TwapWindowExpired (RFC §4.6 "window
        // passed during downtime AND child was live").
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var now = DateTimeOffset.UtcNow;
        var algoId = await PostAlgo(http, userToken,
            TwapBody(total: 100, sliceCount: 1,
                start: now.AddSeconds(-1), end: now.AddMilliseconds(500)));

        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var s0 = await WaitForChild(book, algoId, expectedSeq: 0);

        // Wait past the window end so the scheduler's expiry path is
        // armed by the time the child terminalizes.
        await Task.Delay(800);

        // Cancel the live child so the engine can re-evaluate the
        // parent. Per the engine's window-aware cancel handling, this
        // routes the parent to Expired (not VenueCancelled) because
        // endUtc < now at the moment the child terminalizes.
        await InjectEr(http, adminToken, s0.ClOrdId, "Canceled");

        await WaitForAlgoStatus(http, userToken, algoId, "Expired");
        var algo = await GetAlgo(http, userToken, algoId);
        Assert.Equal("TwapWindowExpired", algo.GetProperty("terminalReason").GetString());
    }

    [Fact]
    public async Task Twap_NoChildSubmitted_WindowExpires_SchedulerDrivesExpired()
    {
        // Edge case: a TWAP submitted with the entire window already in
        // the past (clock-skew or operator-replay scenario). No child
        // is — or can be — submitted; the engine's first reactor pass
        // (driven by the immediate AlgoCreatedSignal POST emits) sees
        // now >= endUtc with no live child and routes to Expired.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);

        var now = DateTimeOffset.UtcNow;
        var algoId = await PostAlgo(http, userToken,
            TwapBody(total: 100, sliceCount: 1,
                start: now.AddSeconds(-2), end: now.AddSeconds(-1)));

        await WaitForAlgoStatus(http, userToken, algoId, "Expired");
        var algo = await GetAlgo(http, userToken, algoId);
        Assert.Equal("TwapWindowExpired", algo.GetProperty("terminalReason").GetString());
        Assert.Equal(0, algo.GetProperty("filledQuantity").GetInt64());
    }

    // ───────────────────────── helpers (parallel to AlgoEngineIntegrationTests) ─────────────────────────

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
