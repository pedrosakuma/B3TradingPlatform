using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_HTTP_Algo;

/// <summary>
/// Spec — Iceberg engine end-to-end (RFC algo-orders-v0 §4.5). Covers the
/// reactor's three contract points against a deployed peer:
/// (1) Created → first child sliced at <c>displayQuantity</c>;
/// (2) Filled child → next child auto-refilled by the engine;
/// (3) <c>DELETE /api/algo/{id}</c> + cancel ER on the live child →
///     parent terminalises to <c>Cancelled/UserRequested</c>.
///
/// Skipped unless the peer opted into ER injection
/// (<c>B3T_ER_INJECTION=true</c>; legacy <c>B3T_SIMULATOR_MODE=true</c>
/// honored as fallback after the #163 mode collapse) and admin credentials
/// are configured (<c>B3T_ADMIN_USER</c>/<c>B3T_ADMIN_PASS</c>) — the
/// synthetic ER injection is the admin-only seam from slice 4.
/// </summary>
[Trait("Category", "Conformance")]
public class IcebergLifecycleSpecTests
{
    [ConformanceFact(RequiresAdmin = true, RequiresErInjection = true)]
    public async Task Iceberg_FirstChild_ThenRefill_ThenCancel()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };

        var userAuth = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);
        var adminAuth = await LoginHelper.LoginAsync(http, peer.AdminUsername!, peer.AdminPassword!);

        // 1. Create the parent: total=300, display=100. The engine must
        //    submit slice 0 immediately and refill on terminal-fill.
        var algoId = await CreateIcebergAsync(http, userAuth, total: 300, display: 100, price: 30m);

        // 2. Slice 0 should appear on /api/orders for the same end-client,
        //    tagged with the parent algoId + sliceSeq=0.
        var slice0 = await WaitForChildAsync(http, userAuth, algoId, expectedSeq: 0);
        Assert.Equal(100, slice0.Quantity);

        // 3. Fully fill slice 0 via the simulator. Engine reactor sees
        //    the terminal fill and submits slice 1.
        await InjectErAsync(http, adminAuth, slice0.ClOrdId, "Fill", lastQty: 100, lastPx: 30m);

        var slice1 = await WaitForChildAsync(http, userAuth, algoId, expectedSeq: 1);
        Assert.Equal(100, slice1.Quantity);

        // 4. Cancel the parent while slice 1 is still live. The reactor
        //    flips parent to Cancelling and waits for a terminal ER on
        //    the live child before terminalising — without an explicit
        //    Canceled ER the parent stays in Cancelling forever.
        var cancelResp = await SendAsync(http, HttpMethod.Delete, $"/api/algo/{algoId}", userAuth);
        Assert.True(cancelResp.StatusCode == HttpStatusCode.Accepted ||
                    cancelResp.StatusCode == HttpStatusCode.NoContent,
            $"DELETE /api/algo expected 202/204, got {(int)cancelResp.StatusCode}");

        // 5. Drive the simulator to ack the cancel on slice 1.
        await InjectErAsync(http, adminAuth, slice1.ClOrdId, "Canceled");

        // 6. Parent should now report Cancelled with the user-requested
        //    terminal reason; remaining quantity is 100 (only slice 0 filled).
        await WaitForAlgoStatusAsync(http, userAuth, algoId, "Cancelled");
        var algo = await GetAlgoAsync(http, userAuth, algoId);
        Assert.Equal("UserRequested", algo.GetProperty("terminalReason").GetString());
        Assert.Equal(100, algo.GetProperty("filledQuantity").GetInt64());
    }

    // ----- Helpers (kept inline to avoid leaking algo HTTP shape into
    // shared infrastructure; the simulator spec uses the same pattern). -----

    private static async Task<string> CreateIcebergAsync(
        HttpClient http, System.Net.Http.Headers.AuthenticationHeaderValue auth, long total, long display, decimal price)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/algo/")
        {
            Content = JsonContent.Create(new
            {
                Symbol = "PETR4",
                SecurityId = 4321UL,
                Side = "Buy",
                Type = "Iceberg",
                TotalQuantity = total,
                Iceberg = new { DisplayQuantity = display, LimitPrice = price },
            }),
        };
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("algoId").GetString()!;
    }

    private static async Task<(ulong ClOrdId, long Quantity)> WaitForChildAsync(
        HttpClient http, System.Net.Http.Headers.AuthenticationHeaderValue auth, string algoId, int expectedSeq)
    {
        // Conformance polls 15s — UAT peers can carry HTTP/scheduler/WAL
        // latency well above the in-process integration baseline.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/orders/");
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
                    var clOrdId = ulong.Parse(o.GetProperty("clOrdId").GetString()!);
                    var qty = o.GetProperty("quantity").GetInt64();
                    return (clOrdId, qty);
                }
            }
            await Task.Delay(150);
        }
        throw new TimeoutException(
            $"Algo {algoId} child slice {expectedSeq} did not appear in /api/orders within 15s.");
    }

    private static async Task InjectErAsync(
        HttpClient http, System.Net.Http.Headers.AuthenticationHeaderValue auth, ulong clOrdId, string type,
        long? lastQty = null, decimal? lastPx = null)
    {
        object body = (lastQty, lastPx) switch
        {
            (long q, decimal p) => new { ClOrdId = clOrdId, Type = type, LastQty = q, LastPx = p },
            (long q, null) => new { ClOrdId = clOrdId, Type = type, LastQty = q },
            _ => new { ClOrdId = clOrdId, Type = type },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/simulator/er")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    private static async Task WaitForAlgoStatusAsync(
        HttpClient http, System.Net.Http.Headers.AuthenticationHeaderValue auth, string algoId, string expectedStatus)
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
            $"Algo {algoId} did not reach status={expectedStatus} within 15s (last observed={observed}).");
    }

    private static async Task<JsonElement> GetAlgoAsync(
        HttpClient http, System.Net.Http.Headers.AuthenticationHeaderValue auth, string algoId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/algo/{algoId}");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, HttpMethod method, string path, System.Net.Http.Headers.AuthenticationHeaderValue auth)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = auth;
        return await http.SendAsync(req);
    }
}
