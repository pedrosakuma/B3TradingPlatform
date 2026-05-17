using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Application;
using B3.Trading.Application.MarketData;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q3.5 (#285). End-to-end coverage for the operator algo modify
/// (cancel-replace) endpoint. The endpoint enqueues an
/// <c>AlgoModifyRequestedSignal</c> onto the engine reactor; the engine
/// resolves the live child, validates qty/price against the current
/// cumulative fills, dispatches <c>OrderReplaceRequestedEvent</c>, and
/// then issues <c>IExchangeGateway.CancelReplaceAsync</c>. The mock
/// records the wire request in <c>SubmittedReplaces</c>; tests inject a
/// <c>Replaced</c> ER through the simulator to drive the convergence
/// path in <c>ExecutionReportProcessor.ApplyReplaceAccepted</c>.
/// </summary>
public class AlgoModifyEndpointTests
{
    private static IDictionary<string, string?> Simulator() =>
        new Dictionary<string, string?>
        {
            ["Trading:Exchange:Mode"] = "Mock",
            ["Trading:Exchange:AllowErInjection"] = "true",
            ["Trading:SymbolDirectory:SecurityIds:PETR4"] = "4321",
        };

    private static object PeggedBody(long total) => new
    {
        Symbol = "PETR4",
        Side = "Buy",
        Type = "Pegged",
        TotalQuantity = total,
        Pegged = new
        {
            Ref = "Mid",
            OffsetTicks = 0,
            RepegIntervalMs = 100,
            TickSize = 0.5m,
        },
    };

    // ───────────────────────── Validation ─────────────────────────

    [Fact]
    public async Task Modify_UnknownAlgo_Returns404()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var req = new HttpRequestMessage(HttpMethod.Post, "/algo/9999999/modify")
        {
            Content = JsonContent.Create(new { NewPrice = 30.0m }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Modify_MissingBothFields_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        // Use a real algoId so we hit the validator before the ownership
        // / not-found short circuit. The pegged book-top seed lets the
        // POST succeed deterministically.
        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);
        var algoId = await PostAlgo(http, token, PeggedBody(100));

        var req = new HttpRequestMessage(HttpMethod.Post, $"/algo/{algoId}/modify")
        {
            Content = JsonContent.Create(new { Reason = "no-op" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Modify_NonPositiveQuantity_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);
        var algoId = await PostAlgo(http, token, PeggedBody(100));

        var req = new HttpRequestMessage(HttpMethod.Post, $"/algo/{algoId}/modify")
        {
            Content = JsonContent.Create(new { NewQuantity = 0 }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Modify_TerminalAlgo_Returns409()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);
        var algoId = await PostAlgo(http, token, PeggedBody(100));

        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));

        // Drive the algo to Filled (terminal) so the modify is rejected.
        await InjectEr(http, adminToken, child.ClOrdId, "Fill", lastQty: 100);
        await WaitForAlgoStatus(http, token, algoId, "Completed", "Filled");

        var req = new HttpRequestMessage(HttpMethod.Post, $"/algo/{algoId}/modify")
        {
            Content = JsonContent.Create(new { NewPrice = 31.0m }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    // ───────────────────────── Happy path ─────────────────────────

    [Fact]
    public async Task Modify_LiveChild_IssuesReplaceAndConverges()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child1 = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        Assert.Equal(30.0m, child1.Price);

        // Operator modify: bump the limit one tick up. Quantity stays.
        var req = new HttpRequestMessage(HttpMethod.Post, $"/algo/{algoId}/modify")
        {
            Content = JsonContent.Create(new { NewPrice = 30.5m, Reason = "OperatorModify" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        // Engine must have issued exactly one cancel-replace targeting
        // the live child (not a plain CancelAsync — that's the whole
        // point of Q3.5).
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(
            () => mock.SubmittedReplaces.Any(r =>
                r.OriginalClOrdId == child1.ClOrdId
                && r.NewPrice == 30.5m
                && r.NewQuantity == child1.Quantity),
            TimeSpan.FromSeconds(3),
            "engine never issued CancelReplace for the operator modify");

        var replace = mock.SubmittedReplaces.Single(r =>
            r.OriginalClOrdId == child1.ClOrdId);

        // Drive the venue ack — Replaced ER for the new ClOrdID with
        // OrigClOrdID echoing the original. The processor will hydrate
        // the new child into the book and re-emit
        // ChildExecutionObservedSignal so the engine retargets cleanly.
        await InjectReplacedEr(http, adminToken,
            newClOrdId: replace.NewClOrdId,
            origClOrdId: replace.OriginalClOrdId,
            leavesQuantity: replace.NewQuantity);

        var newChild = await WaitForChildOtherThan(book, algoId, child1.ClOrdId,
            TimeSpan.FromSeconds(3));
        Assert.Equal(30.5m, newChild.Price);
        Assert.Equal(100, newChild.Quantity);
        Assert.Equal(replace.NewClOrdId, newChild.ClOrdId);
    }

    [Fact]
    public async Task Modify_QuantityBelowFilled_Rejected()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(200));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));

        // Partial fill: cum=120, leaves=80.
        await InjectEr(http, adminToken, child.ClOrdId, "PartialFill",
            lastQty: 120, lastPx: 30.0m);

        // Wait until the engine has consumed the partial-fill ER so
        // RecomputeCumQty has propagated; the easiest signal is the
        // child Quantity reflecting the unchanged total (cum is on
        // the parent runtime, not the child Quantity, so just sleep
        // briefly to let the consumer loop drain).
        await Task.Delay(150);

        var req = new HttpRequestMessage(HttpMethod.Post, $"/algo/{algoId}/modify")
        {
            // newQty=100 is below cum=120 → engine rejects.
            Content = JsonContent.Create(new { NewQuantity = 100 }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        // Endpoint accepts (validation is per-engine); engine rejects
        // asynchronously and bumps the rejected counter. Either way
        // no CancelReplace is issued to the wire.
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        await Task.Delay(300);
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        Assert.DoesNotContain(mock.SubmittedReplaces,
            r => r.OriginalClOrdId == child.ClOrdId);
    }

    [Fact]
    public async Task Modify_InvalidReason_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);
        var algoId = await PostAlgo(http, token, PeggedBody(100));

        var req = new HttpRequestMessage(HttpMethod.Post, $"/algo/{algoId}/modify")
        {
            // Pass-1 review (#299) P2-A. Reason becomes a metric tag, so
            // only the closed allowlist is accepted. An arbitrary
            // operator string must be rejected at validation time.
            Content = JsonContent.Create(new { NewPrice = 30.5m, Reason = "free-form-reason" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ────────────── Pass-1 review (#299) regressions ──────────────

    [Fact]
    public async Task Modify_FillOnOldChildBeforeReplacedEr_NoDoubleCounting()
    {
        // Pass-1 review (#299) P1-A. With the pre-fix code the engine
        // re-targeted rt.LiveChildClOrdId to the new ClOrdID at
        // dispatch time AND seeded ChildBookedCum[new] = old.Cum, so a
        // Fill ER for the OLD child landing in the in-flight window
        // booked the fill twice on the parent (once via the OLD child
        // delta, once via the replacement's seeded cum carry-over on
        // the next ER for the new child). The fix defers adoption to
        // the actual Replaced-ER observation; parent.FilledQuantity
        // must reflect exactly one fill.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var oldChild = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));

        // Operator modify in flight — engine writes WAL + registers
        // intent + dispatches CancelReplace, but does NOT yet move
        // rt.LiveChildClOrdId off the OLD child.
        var modReq = new HttpRequestMessage(HttpMethod.Post, $"/algo/{algoId}/modify")
        {
            Content = JsonContent.Create(new { NewPrice = 30.5m }),
        };
        modReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modResp = await http.SendAsync(modReq);
        Assert.Equal(HttpStatusCode.Accepted, modResp.StatusCode);

        // Wait for the CancelReplace wire-call so the intent is
        // registered before we drive the Replaced ER. The fill MUST
        // land for the OLD child id BEFORE the Replaced ER hydrates
        // the replacement — that is the race we're regression-testing.
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(
            () => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == oldChild.ClOrdId),
            TimeSpan.FromSeconds(3),
            "engine never dispatched CancelReplace");
        var replace = mock.SubmittedReplaces.Single(r => r.OriginalClOrdId == oldChild.ClOrdId);

        // Partial fill of 30 on the OLD child while the replace is
        // still in flight. The engine's child-ER path books delta=30
        // against the OLD child slot (parent.FilledQuantity=30).
        await InjectEr(http, adminToken, oldChild.ClOrdId, "PartialFill",
            lastQty: 30, lastPx: 30.0m);
        await Task.Delay(150);
        var algoMid = await GetAlgo(http, token, algoId);
        Assert.Equal(30L, algoMid.GetProperty("filledQuantity").GetInt64());

        // Venue Replaced ER, carrying erCum=30 (venue echoes the OLD
        // child's cum). Processor hydrates the new child with
        // CumulativeQuantity=30, leaves=70, and fans out a
        // ChildExecutionObservedSignal for the new ClOrdID; the engine
        // adopts the replacement and seeds its booked-cum baseline so
        // the carry-over is NOT re-booked.
        await InjectReplacedEr(http, adminToken,
            newClOrdId: replace.NewClOrdId,
            origClOrdId: replace.OriginalClOrdId,
            leavesQuantity: 70,
            cumQty: 30);

        var newChild = await WaitForChildOtherThan(book, algoId, oldChild.ClOrdId,
            TimeSpan.FromSeconds(3));
        Assert.Equal(replace.NewClOrdId, newChild.ClOrdId);
        Assert.Equal(30L, newChild.CumulativeQuantity);

        // Settle, then assert parent cum still equals the single
        // observed fill — no double-counting.
        await Task.Delay(150);
        var algoAfter = await GetAlgo(http, token, algoId);
        Assert.Equal(30L, algoAfter.GetProperty("filledQuantity").GetInt64());
        Assert.Equal(70L, algoAfter.GetProperty("remainingQuantity").GetInt64());
    }

    [Fact]
    public async Task Modify_GatewayThrowsButVenueAccepted_LateReplacedErIsHonoured()
    {
        // Pass-1 review (#299) P1-B. CancelReplaceAsync exception is
        // semantically AMBIGUOUS — the venue may already have accepted
        // the request. The fix keeps the PendingReplacementRegistry
        // intent + ownership link in place so a late Replaced ER still
        // resolves correctly through the early intercept; previously
        // the intent was rolled back, the Replaced ER bypassed the
        // registry and the new child was silently dropped.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        // Arm the mock to throw on the cancel-replace dispatch BUT
        // still enqueue the request first (mirrors a real ambiguous
        // send — wire write succeeded, ack timed out / errored).
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        mock.ReplaceFailureInjector = _ => new InvalidOperationException("simulated SDK ambiguous send");

        var algoId = await PostAlgo(http, token, PeggedBody(100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var oldChild = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));

        var modReq = new HttpRequestMessage(HttpMethod.Post, $"/algo/{algoId}/modify")
        {
            Content = JsonContent.Create(new { NewPrice = 30.5m }),
        };
        modReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modResp = await http.SendAsync(modReq);
        Assert.Equal(HttpStatusCode.Accepted, modResp.StatusCode);

        await WaitFor(
            () => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == oldChild.ClOrdId),
            TimeSpan.FromSeconds(3),
            "engine never dispatched CancelReplace");
        var replace = mock.SubmittedReplaces.Single(r => r.OriginalClOrdId == oldChild.ClOrdId);

        // Disarm so any further dispatches don't trip the injector.
        mock.ReplaceFailureInjector = null;

        // Drive the late Replaced ER (venue had actually accepted the
        // request before timing out). With the fix the intent is still
        // registered, so the processor's early intercept fires, the
        // new child is hydrated, and the engine adopts it as the live
        // slot on the resulting ChildExecutionObservedSignal.
        await InjectReplacedEr(http, adminToken,
            newClOrdId: replace.NewClOrdId,
            origClOrdId: replace.OriginalClOrdId,
            leavesQuantity: replace.NewQuantity);

        var newChild = await WaitForChildOtherThan(book, algoId, oldChild.ClOrdId,
            TimeSpan.FromSeconds(3));
        Assert.Equal(30.5m, newChild.Price);
        Assert.Equal(replace.NewClOrdId, newChild.ClOrdId);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static async Task<JsonElement> GetAlgo(HttpClient http, string token, string algoId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/algo/{algoId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

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

    private static async Task<HttpResponseMessage> InjectReplacedEr(
        HttpClient http, string adminToken, ulong newClOrdId, ulong origClOrdId, long leavesQuantity,
        long? cumQty = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/simulator/er")
        {
            Content = JsonContent.Create(new
            {
                ClOrdId = newClOrdId,
                Type = "Replaced",
                // LastQty is repurposed as "leavesQuantity" hint for the
                // Replaced injector arm (no Last fill on a replace ack).
                LastQty = leavesQuantity,
                OrigClOrdId = origClOrdId,
                CumQty = cumQty,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        return resp;
    }

    private static async Task<Order> WaitForAnyChild(WorkingOrderBook book, string algoIdStr, TimeSpan timeout)
    {
        var algoId = ulong.Parse(algoIdStr);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var match = book.EnumerateChildrenOf("default", algoId).FirstOrDefault();
            if (match is not null) return match;
            await Task.Delay(10);
        }
        throw new TimeoutException($"No child for algo {algoIdStr} within {timeout.TotalSeconds}s.");
    }

    private static async Task<Order> WaitForChildOtherThan(
        WorkingOrderBook book, string algoIdStr, ulong excludeClOrdId, TimeSpan timeout)
    {
        var algoId = ulong.Parse(algoIdStr);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var match = book.EnumerateChildrenOf("default", algoId)
                .FirstOrDefault(c => c.ClOrdId != excludeClOrdId);
            if (match is not null) return match;
            await Task.Delay(10);
        }
        throw new TimeoutException(
            $"No new child (other than {excludeClOrdId}) for algo {algoIdStr} within {timeout.TotalSeconds}s.");
    }

    private static async Task WaitFor(Func<bool> predicate, TimeSpan timeout, string failMessage)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(failMessage + $" (waited {timeout.TotalSeconds}s)");
    }

    private static async Task WaitForAlgoStatus(HttpClient http, string token, string algoId, params string[] anyOf)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? last = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/algo/{algoId}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var algo = await resp.Content.ReadFromJsonAsync<JsonElement>();
            last = algo.GetProperty("status").GetString();
            if (anyOf.Contains(last)) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Algo {algoId} did not reach any of [{string.Join(",", anyOf)}] within 5s; last={last}");
    }
}
