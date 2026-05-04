using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_HTTP_Algo;

/// <summary>
/// Spec — TWAP scheduler end-to-end (RFC algo-orders-v0 §4.6 + §4.8).
/// Submits a 2-slice TWAP whose window opens in the recent past so both
/// slices are immediately due, fills them sequentially via the simulator,
/// and asserts the parent reaches <c>Completed</c> with the full quantity.
///
/// <para>
/// The window is intentionally minute-scale (<c>start = now-3m</c>,
/// <c>end = now+1m</c>) instead of seconds: client/server clock skew on
/// a UAT/staging peer can erase a sub-second margin and either expire the
/// parent mid-test or push slice 1's <c>plannedAtUtc</c> into the future.
/// 60s of headroom before <c>endUtc</c> tolerates realistic NTP drift and
/// HTTP latency without giving up determinism.
/// </para>
/// </summary>
[Trait("Category", "Conformance")]
public class TwapLifecycleSpecTests
{
    [ConformanceFact(RequiresAdmin = true, RequiresSimulator = true)]
    public async Task Twap_TwoSlices_FillsSequentially_ParentCompletes()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };

        var userAuth = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);
        var adminAuth = await LoginHelper.LoginAsync(http, peer.AdminUsername!, peer.AdminPassword!);

        // 2 slices over [now-3m, now+1m]:
        //   slice 0 plannedAt = start             = now-3m
        //   slice 1 plannedAt = start + window/2  = now-1m
        // Both already due → engine fires slice 0 immediately; after we
        // fill it, the scheduler's next tick fires slice 1. Window
        // remains open for ≥60s, far above any realistic conformance
        // latency budget.
        var now = DateTimeOffset.UtcNow;
        var algoId = await CreateTwapAsync(http, userAuth,
            total: 200, sliceCount: 2,
            start: now.AddMinutes(-3), end: now.AddMinutes(1),
            childPrice: 30m);

        // Engine enforces one live child per parent: slice 1 cannot
        // appear until slice 0 reaches a terminal state.
        var slice0 = await WaitForChildAsync(http, userAuth, algoId, expectedSeq: 0);
        Assert.Equal(100, slice0.Quantity);
        await InjectErAsync(http, adminAuth, slice0.ClOrdId, "Fill", lastQty: 100, lastPx: 30m);

        var slice1 = await WaitForChildAsync(http, userAuth, algoId, expectedSeq: 1);
        Assert.Equal(100, slice1.Quantity); // even split, no remainder
        await InjectErAsync(http, adminAuth, slice1.ClOrdId, "Fill", lastQty: 100, lastPx: 30m);

        await WaitForAlgoStatusAsync(http, userAuth, algoId, "Completed");
        var algo = await GetAlgoAsync(http, userAuth, algoId);
        Assert.Equal(200, algo.GetProperty("filledQuantity").GetInt64());
        Assert.Equal("None", algo.GetProperty("terminalReason").GetString());
    }

    // ----- Helpers (deliberately duplicated from IcebergLifecycleSpecTests
    // to keep each conformance scenario self-contained — the spec files
    // are the contract, not a shared test utility library). -----

    private static async Task<string> CreateTwapAsync(
        HttpClient http, AuthenticationHeaderValue auth,
        long total, int sliceCount, DateTimeOffset start, DateTimeOffset end, decimal? childPrice)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(new
            {
                Symbol = "PETR4",
                SecurityId = 4321UL,
                Side = "Buy",
                Type = "Twap",
                TotalQuantity = total,
                Twap = new
                {
                    StartUtc = start,
                    EndUtc = end,
                    SliceCount = sliceCount,
                    ChildOrderType = "Limit",
                    ChildPrice = childPrice,
                },
            }),
        };
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("algoId").GetString()!;
    }

    private static async Task<(ulong ClOrdId, long Quantity)> WaitForChildAsync(
        HttpClient http, AuthenticationHeaderValue auth, string algoId, int expectedSeq)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/orders/");
            req.Headers.Authorization = auth;
            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var orders = await resp.Content.ReadFromJsonAsync<JsonElement[]>();
            foreach (var o in orders!)
            {
                if (o.TryGetProperty("parentAlgoId", out var pid) &&
                    pid.ValueKind == JsonValueKind.String &&
                    pid.GetString() == algoId &&
                    o.TryGetProperty("algoSliceSeq", out var seq) &&
                    seq.ValueKind == JsonValueKind.Number &&
                    seq.GetInt32() == expectedSeq)
                {
                    return (
                        ulong.Parse(o.GetProperty("clOrdId").GetString()!),
                        o.GetProperty("quantity").GetInt64());
                }
            }
            await Task.Delay(150);
        }
        throw new TimeoutException(
            $"TWAP {algoId} child slice {expectedSeq} did not appear in /orders within 15s.");
    }

    private static async Task InjectErAsync(
        HttpClient http, AuthenticationHeaderValue auth, ulong clOrdId, string type,
        long? lastQty = null, decimal? lastPx = null)
    {
        object body = (lastQty, lastPx) switch
        {
            (long q, decimal p) => new { ClOrdId = clOrdId, Type = type, LastQty = q, LastPx = p },
            (long q, null) => new { ClOrdId = clOrdId, Type = type, LastQty = q },
            _ => new { ClOrdId = clOrdId, Type = type },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/admin/simulator/er")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    private static async Task WaitForAlgoStatusAsync(
        HttpClient http, AuthenticationHeaderValue auth, string algoId, string expectedStatus)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string? observed = null;
        while (DateTime.UtcNow < deadline)
        {
            var algo = await GetAlgoAsync(http, auth, algoId);
            observed = algo.GetProperty("status").GetString();
            if (observed == expectedStatus) return;
            await Task.Delay(150);
        }
        throw new TimeoutException(
            $"TWAP {algoId} did not reach status={expectedStatus} within 15s (last observed={observed}).");
    }

    private static async Task<JsonElement> GetAlgoAsync(
        HttpClient http, AuthenticationHeaderValue auth, string algoId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/algo/{algoId}");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }
}
