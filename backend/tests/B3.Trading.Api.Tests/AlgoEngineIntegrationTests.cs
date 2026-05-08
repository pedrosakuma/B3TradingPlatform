using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// End-to-end coverage for the algo orders v0 Iceberg engine (RFC §4).
/// Boots the host with <c>Trading:Exchange:Mode=Mock</c> +
/// <c>AllowErInjection=true</c> so
/// child orders the engine submits land in <c>WorkingOrderBook</c>
/// and synthetic ERs can be driven via <c>POST /admin/simulator/er</c>.
/// </summary>
public class AlgoEngineIntegrationTests
{
    private static IDictionary<string, string?> Simulator() =>
        new Dictionary<string, string?>
        {
            ["Trading:Exchange:Mode"] = "Mock",
            ["Trading:Exchange:AllowErInjection"] = "true",
            ["Trading:SymbolDirectory:SecurityIds:PETR4"] = "4321",
        };

    private static object IcebergBody(long total, long display, decimal price = 30m) => new
    {
        Symbol = "PETR4",
        Side = "Buy",
        Type = "Iceberg",
        TotalQuantity = total,
        Iceberg = new { DisplayQuantity = display, LimitPrice = (decimal?)price },
    };

    [Fact]
    public async Task Iceberg_RefillCycle_ReachesCompletedAfterAllSlicesFilled()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var algoId = await PostAlgo(http, userToken, IcebergBody(total: 300, display: 100));

        // Slice 1 → submitted by the engine. Wait for the child to appear
        // in the WorkingOrderBook (engine reactor is async).
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForChild(book, algoId, expectedSeq: 0);

        await InjectEr(http, adminToken, child1.ClOrdId, "Fill", lastQty: 100);

        // Slice 2 should be submitted automatically after the refill.
        var child2 = await WaitForChild(book, algoId, expectedSeq: 1);
        await InjectEr(http, adminToken, child2.ClOrdId, "Fill", lastQty: 100);

        var child3 = await WaitForChild(book, algoId, expectedSeq: 2);
        await InjectEr(http, adminToken, child3.ClOrdId, "Fill", lastQty: 100);

        // Parent should now reach Completed (no more remaining quantity).
        await WaitForAlgoStatus(http, userToken, algoId, "Completed");
        var algo = await GetAlgo(http, userToken, algoId);
        Assert.Equal(300, algo.GetProperty("filledQuantity").GetInt64());
        Assert.Equal(0, algo.GetProperty("remainingQuantity").GetInt64());
        Assert.Equal("None", algo.GetProperty("terminalReason").GetString());
    }

    [Fact]
    public async Task Iceberg_PartialFillBetweenSlices_AccumulatesIntoParent()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var algoId = await PostAlgo(http, userToken, IcebergBody(total: 200, display: 100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();

        var child1 = await WaitForChild(book, algoId, expectedSeq: 0);
        // Partial fill — child stays Working with 60 booked, 40 leaves.
        await InjectEr(http, adminToken, child1.ClOrdId, "PartialFill", lastQty: 60);
        // Engine must NOT submit slice 2 yet (child not terminal).
        await Task.Delay(100);
        Assert.DoesNotContain(book.EnumerateChildrenOf("default", ulong.Parse(algoId)),
            c => c.AlgoSliceSeq == 1);

        // Fill the remaining 40 — child terminal Filled, engine refills.
        await InjectEr(http, adminToken, child1.ClOrdId, "Fill", lastQty: 40);
        var child2 = await WaitForChild(book, algoId, expectedSeq: 1);
        await InjectEr(http, adminToken, child2.ClOrdId, "Fill", lastQty: 100);

        await WaitForAlgoStatus(http, userToken, algoId, "Completed");
    }

    [Fact]
    public async Task Iceberg_DeleteThenChildCancelAck_DrivesParentToCancelledUserCancelled()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var algoId = await PostAlgo(http, userToken, IcebergBody(total: 300, display: 100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForChild(book, algoId, expectedSeq: 0);

        // Operator cancels — engine asks the gateway to cancel the child;
        // until the cancel-ack ER lands, the parent is Cancelling.
        var del = await Authed(http, HttpMethod.Delete, $"/algo/{algoId}", userToken);
        Assert.Equal(HttpStatusCode.Accepted, del.StatusCode);
        await WaitForAlgoStatus(http, userToken, algoId, "Cancelling", "Cancelled");

        // Drive the cancel-ack — the engine should now mark the parent
        // Cancelled with UserCancelled because the parent was in Cancelling.
        await InjectEr(http, adminToken, child1.ClOrdId, "Canceled");
        await WaitForAlgoStatus(http, userToken, algoId, "Cancelled");

        var algo = await GetAlgo(http, userToken, algoId);
        Assert.Equal("UserCancelled", algo.GetProperty("terminalReason").GetString());
    }

    [Fact]
    public async Task Iceberg_VenueCancelOfChild_WithoutOperatorRequest_SuspendsParent()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var userToken = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var algoId = await PostAlgo(http, userToken, IcebergBody(total: 200, display: 100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForChild(book, algoId, expectedSeq: 0);

        // Venue cancels without operator request — engine must suspend
        // (auto-refilling against an unhappy venue can spin a tight loop).
        await InjectEr(http, adminToken, child1.ClOrdId, "Canceled");
        await WaitForAlgoStatus(http, userToken, algoId, "Suspended");

        var algo = await GetAlgo(http, userToken, algoId);
        Assert.Equal("VenueCancelled", algo.GetProperty("terminalReason").GetString());
    }

    // -- helpers ----------------------------------------------------------

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

    private static async Task<HttpResponseMessage> Authed(HttpClient http, HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req);
    }

    /// <summary>
    /// Polls the in-memory order book for a child slice with the given
    /// <c>AlgoSliceSeq</c>. Necessary because the engine reactor consumes
    /// signals off a Channel asynchronously — child orders appear after a
    /// short scheduling delay rather than synchronously with the API call.
    /// </summary>
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
