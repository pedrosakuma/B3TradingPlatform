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

    [Fact]
    public async Task Modify_ReplacedErWithStaleZeroCum_ClampsBaselineToOriginal()
    {
        // Pass-2 review (#299) P1. Translators in
        // B3EntryPointClientGateway (OrderModified arm) and
        // SimulatorEndpoint (/admin/simulator/er Replaced arm) default
        // missing CumQty to 0. If the OLD child had prior fills (e.g.
        // partial fill of 30) and the venue / simulator drives a
        // Replaced ER with erCum=0, hydrating the replacement with
        // baseline cum=0 caused the next Fill ER for the NEW child id
        // to compute its delta against an under-seeded prevBooked,
        // re-booking the OLD child's 30 against the parent.
        //
        // Fix: ExecutionReportProcessor.ApplyReplaceAccepted clamps
        // seedCum upward to origOrder.CumulativeQuantity (and adjusts
        // seedLeaves) before HydrateReplacement, so the engine adopts
        // the new child at the correct cum baseline and subsequent
        // Fill ERs advance from there.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var oldChild = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));

        // Partial fill of 30 on the OLD child BEFORE the modify dispatch.
        await InjectEr(http, adminToken, oldChild.ClOrdId, "PartialFill",
            lastQty: 30, lastPx: 30.0m);
        await Task.Delay(150);
        var algoBefore = await GetAlgo(http, token, algoId);
        Assert.Equal(30L, algoBefore.GetProperty("filledQuantity").GetInt64());

        // Operator modify: keep quantity unchanged (100) but bump price.
        var modReq = new HttpRequestMessage(HttpMethod.Post, $"/algo/{algoId}/modify")
        {
            Content = JsonContent.Create(new { NewPrice = 30.5m }),
        };
        modReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modResp = await http.SendAsync(modReq);
        Assert.Equal(HttpStatusCode.Accepted, modResp.StatusCode);

        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(
            () => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == oldChild.ClOrdId),
            TimeSpan.FromSeconds(3),
            "engine never dispatched CancelReplace");
        var replace = mock.SubmittedReplaces.Single(r => r.OriginalClOrdId == oldChild.ClOrdId);

        // Venue Replaced ER with the legacy stale CumQty=0 default.
        // Without the clamp this would seed the new child at cum=0 and
        // the next Fill ER (cum=50) would book delta=50 on the parent
        // on top of the already-booked 30 (parent.FilledQuantity=80).
        // With the clamp the new child is seeded at cum=30, leaves=70,
        // so the Fill ER's cum=50 books delta=20 → parent cum=50.
        await InjectReplacedEr(http, adminToken,
            newClOrdId: replace.NewClOrdId,
            origClOrdId: replace.OriginalClOrdId,
            leavesQuantity: replace.NewQuantity,
            cumQty: 0);

        var newChild = await WaitForChildOtherThan(book, algoId, oldChild.ClOrdId,
            TimeSpan.FromSeconds(3));
        Assert.Equal(replace.NewClOrdId, newChild.ClOrdId);
        Assert.Equal(30L, newChild.CumulativeQuantity);
        Assert.Equal(70L, newChild.LeavesQuantity);

        // Drive a Fill ER for the NEW child taking total cum to 50.
        await InjectEr(http, adminToken, newChild.ClOrdId, "PartialFill",
            lastQty: 20, lastPx: 30.5m);
        await Task.Delay(150);

        var algoAfter = await GetAlgo(http, token, algoId);
        Assert.Equal(50L, algoAfter.GetProperty("filledQuantity").GetInt64());
        Assert.Equal(50L, algoAfter.GetProperty("remainingQuantity").GetInt64());
    }

    // ───────────── Pass-3 review (#299) regressions ─────────────

    [Fact]
    public async Task Modify_BuyChildPastAvailableCash_RejectedByMargin_NoGatewayCall()
    {
        // Pass-3 review (#299) P1. The algo modify path must run the
        // same pre-trade risk pipeline + margin Prepare gates the
        // operator-driven plain-order modify pipeline applies. Before
        // the fix the engine went straight from validation to gateway
        // dispatch and CommitReplace on the venue ack only rebalanced
        // — a Buy child could be price-modified past available cash
        // with no rejection. After the fix: pre-Prepare reject → no
        // CancelReplace on the wire, no AlgoChildModifiedEvent
        // emitted, and algo.modify_rejected_total{reason=margin_rejected}
        // bumps. (We assert the margin reason specifically — risk
        // pipeline + margin coordinator are wired in order; an upsize
        // past available cash trips the coordinator, not the pipeline.)
        var overrides = new Dictionary<string, string?>(Simulator())
        {
            ["Trading:Risk:Margin:Enabled"] = "true",
            // Exactly enough for the initial 100@30=3000 reservation;
            // any upsize delta on a subsequent modify must trip the
            // coordinator (delta > available=0).
            [$"Trading:Risk:Margin:Initial:{TestAppFactory.TestUser}"] = "3000",
            // Push other limits high so margin is the only gate that
            // could plausibly reject the modify.
            ["Trading:Risk:Default:MaxNotional"] = "999999999",
        };
        using var f = TestAppFactory.WithOverrides(overrides);
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        long rejectedByMargin = 0;
        long childModifies = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "trading.algo.modify_rejected_total"
                || instrument.Name == "trading.algo.child_modifies_total")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((inst, measurement, tags, _) =>
        {
            if (inst.Name == "trading.algo.modify_rejected_total")
            {
                string? reason = null;
                foreach (var t in tags)
                    if (t.Key == "reason") reason = t.Value as string;
                if (reason == "margin_rejected")
                    Interlocked.Add(ref rejectedByMargin, measurement);
            }
            else if (inst.Name == "trading.algo.child_modifies_total")
            {
                Interlocked.Add(ref childModifies, measurement);
            }
        });
        listener.Start();

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var child = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        Assert.Equal(30.0m, child.Price);

        // Operator modify: bump price one tick up → upsize delta of
        // 100 * 0.5 = 50 cash. Available is 0 (initial 3000 already
        // reserved against the original child). Margin coordinator
        // must reject; no CancelReplace must hit the wire.
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        var preReplaces = mock.SubmittedReplaces.Count;

        var modReq = new HttpRequestMessage(HttpMethod.Post, $"/algo/{algoId}/modify")
        {
            Content = JsonContent.Create(new { NewPrice = 30.5m }),
        };
        modReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modResp = await http.SendAsync(modReq);
        // Endpoint accepts (validation is per-engine async); engine
        // rejects on the consumer task.
        Assert.Equal(HttpStatusCode.Accepted, modResp.StatusCode);

        await Task.Delay(300);

        // No CancelReplace dispatched to the gateway for this child.
        Assert.Equal(preReplaces, mock.SubmittedReplaces.Count);
        Assert.DoesNotContain(mock.SubmittedReplaces,
            r => r.OriginalClOrdId == child.ClOrdId);

        // Margin-reason rejection observed; no child-modify success
        // counter bump (which would have implied an
        // AlgoChildModifiedEvent emit).
        listener.RecordObservableInstruments();
        Assert.True(Interlocked.Read(ref rejectedByMargin) >= 1,
            "expected algo.modify_rejected_total{reason=margin_rejected} to bump at least once");
        Assert.Equal(0, Interlocked.Read(ref childModifies));
    }

    [Fact]
    public async Task Modify_WithinRisk_StillSucceeds_HappyPathRegression()
    {
        // Pass-3 review (#299) P1. The risk + margin gates added to
        // the algo modify path must NOT regress the within-budget
        // happy path: a modify that stays inside the operator's cash
        // headroom and inside all risk limits must still produce a
        // gateway CancelReplace and converge on the Replaced ER. This
        // mirrors Modify_LiveChild_IssuesReplaceAndConverges but with
        // margin explicitly enabled and a generous balance to prove
        // the new gates pass on the green path.
        var overrides = new Dictionary<string, string?>(Simulator())
        {
            ["Trading:Risk:Margin:Enabled"] = "true",
            [$"Trading:Risk:Margin:Initial:{TestAppFactory.TestUser}"] = "1000000",
            ["Trading:Risk:Default:MaxNotional"] = "999999999",
        };
        using var f = TestAppFactory.WithOverrides(overrides);
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

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

        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        await WaitFor(
            () => mock.SubmittedReplaces.Any(r => r.OriginalClOrdId == oldChild.ClOrdId),
            TimeSpan.FromSeconds(3),
            "engine never dispatched CancelReplace on within-budget modify");

        var replace = mock.SubmittedReplaces.Single(r => r.OriginalClOrdId == oldChild.ClOrdId);
        await InjectReplacedEr(http, adminToken,
            newClOrdId: replace.NewClOrdId,
            origClOrdId: replace.OriginalClOrdId,
            leavesQuantity: replace.NewQuantity);

        var newChild = await WaitForChildOtherThan(book, algoId, oldChild.ClOrdId,
            TimeSpan.FromSeconds(3));
        Assert.Equal(30.5m, newChild.Price);
        Assert.Equal(replace.NewClOrdId, newChild.ClOrdId);
    }

    [Fact]
    public async Task Modify_RepeatedAdoptions_OverflowRetiredFifo_BumpsEvictionCounter()
    {
        // Pass-3 review (#299) P2. Mirror PR #296's CancelledChildRing
        // observability for the retired-child FIFO. The FIFO caps the
        // per-parent ChildBookedCum bookkeeping at 8 retired entries
        // (rationale lives on AlgoParentRuntime.RetiredChildSlotsCap);
        // each adoption past the cap evicts the eldest entry and MUST
        // bump trading.algo.modify_retired_child_evicted_total so
        // dashboards see sustained churn. We drive 9 successive
        // operator-modify-then-Replaced-ER cycles on the same parent
        // — the 9th adoption is the first to push the queue past cap
        // and trigger an eviction.
        using var f = TestAppFactory.WithOverrides(Simulator());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        long evicted = 0;
        string? evictedAlgoType = null;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "trading.algo.modify_retired_child_evicted_total")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            foreach (var t in tags)
                if (t.Key == "algoType") evictedAlgoType = t.Value as string;
            Interlocked.Add(ref evicted, measurement);
        });
        listener.Start();

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var algoId = await PostAlgo(http, token, PeggedBody(100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var liveChild = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));
        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        var engine = f.Services.GetRequiredService<AlgoEngine>();
        var algoIdNum = ulong.Parse(algoId);

        // Drive 9 modify→Replaced cycles. Bump NewQuantity to keep each
        // modify operative without pushing past the default
        // PriceCollarPercent=10 around the 30.0 reference price (which
        // would only allow a few price ticks before tripping the
        // collar — quantity-bump is the safe lever here).
        for (int cycle = 0; cycle < 9; cycle++)
        {
            // #329: each cycle must observe the previous cycle's
            // adoption committed in the engine runtime — NOT just the
            // book — before sending the next modify. The ER processor
            // hydrates the new child in the book and THEN enqueues
            // ChildExecutionObservedSignal; the engine consumer task
            // processes that signal asynchronously and only there does
            // rt.LiveChildClOrdId flip to the new id. Polling the book
            // (cycle 0 below) or sleeping a fixed delay (the previous
            // approach) races the signal under CI load: the next
            // modify reads the stale LiveChildClOrdId and dispatches a
            // replace with the wrong OriginalClOrdId, making the
            // WaitFor predicate below permanently false → 3s timeout.
            await WaitFor(
                () => engine.TryGetLiveChildClOrdId("default", algoIdNum) == liveChild.ClOrdId,
                TimeSpan.FromSeconds(3),
                () => $"cycle {cycle}: engine did not adopt child {liveChild.ClOrdId} (current LiveChild={engine.TryGetLiveChildClOrdId("default", algoIdNum)?.ToString() ?? "null"})");

            var newQty = 101 + cycle; // 101, 102, ..., 109
            var preReplaceCount = mock.SubmittedReplaces.Count;
            var modReq = new HttpRequestMessage(HttpMethod.Post, $"/algo/{algoId}/modify")
            {
                Content = JsonContent.Create(new { NewQuantity = newQty }),
            };
            modReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var modResp = await http.SendAsync(modReq);
            Assert.Equal(HttpStatusCode.Accepted, modResp.StatusCode);

            await WaitFor(
                () => mock.SubmittedReplaces.Count > preReplaceCount
                      && mock.SubmittedReplaces.Last().OriginalClOrdId == liveChild.ClOrdId,
                TimeSpan.FromSeconds(3),
                $"cycle {cycle}: engine did not dispatch CancelReplace for child {liveChild.ClOrdId}");
            var replace = mock.SubmittedReplaces.Last();

            await InjectReplacedEr(http, adminToken,
                newClOrdId: replace.NewClOrdId,
                origClOrdId: replace.OriginalClOrdId,
                leavesQuantity: newQty);

            // Wait for the new child to materialise in the book under
            // the expected ClOrdID. The generic WaitForChildOtherThan
            // would return any non-matching child (and prior cycles'
            // Replaced-terminal children stay in the book), so we
            // target the specific id from this cycle's replace
            // intent to keep the loop deterministic across N>1 cycles.
            var expectedNewId = replace.NewClOrdId;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Order? hydrated = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(3))
            {
                if (book.TryGet(expectedNewId, out var maybe) && maybe is not null)
                {
                    hydrated = maybe;
                    break;
                }
                await Task.Delay(10);
            }
            Assert.NotNull(hydrated);
            liveChild = hydrated!;
            // Adoption is awaited deterministically at the top of the
            // next iteration via engine.TryGetLiveChildClOrdId — no
            // arbitrary delay needed here.
        }

        // 9 adoptions on the same parent = 9 retired-child enqueues;
        // cap=8 ⇒ exactly 1 eviction; the algoType tag is the lowercased
        // AlgoType enum value (matches the algo.modify_rejected_total
        // taxonomy already in use).
        //
        // #329: the engine flips rt.LiveChildClOrdId BEFORE calling
        // RetireChildSlot + Counter.Add inside OnChildErAsync, so an
        // engine-state-based gate (TryGetLiveChildClOrdId) races the
        // metric emission under fast CPUs. Wait on the observable side
        // effect directly so the assertion runs strictly after the
        // eviction counter has been published.
        await WaitFor(
            () => Interlocked.Read(ref evicted) >= 1L,
            TimeSpan.FromSeconds(3),
            () => $"eviction counter never reached 1 (current={Interlocked.Read(ref evicted)}, finalLive={engine.TryGetLiveChildClOrdId("default", algoIdNum)?.ToString() ?? "null"}, replaces={mock.SubmittedReplaces.Count})");

        listener.RecordObservableInstruments();
        Assert.Equal(1L, Interlocked.Read(ref evicted));
        Assert.Equal("pegged", evictedAlgoType);
    }

    // ───── Pass-4 review (#299) P1 — ambiguous-send margin convergence ─────

    [Fact]
    public async Task Modify_GatewayThrowsButMarginEnabled_HoldsReservation_LateReplacedConvergesWithoutDoubleAdd_AndCompetingOrderRejectedForMargin()
    {
        // Pass-4 review (#299) P1. The pass-3 fix freed the upsize
        // delta on an ambiguous gateway dispatch failure under the
        // theory that holding it indefinitely was worse than the
        // accounting drift. That created a window in which a
        // competing order could consume the freed headroom; then a
        // late Replaced ER would land in CommitReplace which adds
        // the delta back on top of the already-reserved competing
        // notional, pushing reserved exposure above the owner's
        // cash cap.
        //
        // The pass-4 fix keeps the reservation tied to the intent:
        //   - on ambiguous send, mark the entry as
        //     AmbiguousMarginHeld but DO NOT call AbortReplace;
        //   - on a late Replaced ER, CommitReplace converges
        //     against the existing transient entry (no double-add);
        //   - a competing order placed in the window between
        //     ambiguous send and late ER must be rejected for
        //     margin because the held delta is still counted.
        //
        // This test exercises all three.
        var overrides = new Dictionary<string, string?>(Simulator())
        {
            ["Trading:Risk:Margin:Enabled"] = "true",
            // Initial 100 @ 30 = 3000 reserved; modify to 30.5 needs
            // delta = 50; cap of 3050 leaves NO headroom for the
            // competing order's 1 @ 30 = 30 below.
            [$"Trading:Risk:Margin:Initial:{TestAppFactory.TestUser}"] = "3050",
            ["Trading:Risk:Default:MaxNotional"] = "999999999",
        };
        using var f = TestAppFactory.WithOverrides(overrides);
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var cache = f.Services.GetRequiredService<PegBookTopCache>();
        cache.UpdateBookTop("PETR4", 29.5m, 30.5m, DateTimeOffset.UtcNow);

        var mock = f.Services.GetRequiredService<MockEntryPointClient>();
        mock.ReplaceFailureInjector = _ => new InvalidOperationException("simulated SDK ambiguous send");

        var algoId = await PostAlgo(http, token, PeggedBody(100));
        var book = f.Services.GetRequiredService<WorkingOrderBook>();
        var oldChild = await WaitForAnyChild(book, algoId, TimeSpan.FromSeconds(3));

        // Snapshot the reserved figure on the original alone (3000).
        // The submit path reserves margin BEFORE dispatching the New
        // event, so by the time the child is in the book + Working
        // the reservation is normally in place — but the algo-engine
        // consumer that originated the submit runs on a separate
        // task, so the visible-in-book vs. visible-in-ledger orderings
        // can briefly lag in either direction under CI load. Wait
        // for convergence before asserting the baseline.
        var margin = (B3.Trading.Application.Risk.ReserveOnSubmitMarginProvider)
            f.Services.GetRequiredService<B3.Trading.Application.Risk.IReplaceMarginCoordinator>();
        await WaitFor(
            () => margin.ReservedForTesting(TestAppFactory.TestUser) == 3000m,
            TimeSpan.FromSeconds(3),
            $"expected reserved=3000m baseline, got {margin.ReservedForTesting(TestAppFactory.TestUser)}");

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
        mock.ReplaceFailureInjector = null;

        // Reservation MUST still hold the upsize delta of 50 (the
        // pass-3 behaviour would have released it back to 3000).
        // Give the engine consumer a beat to finish processing the
        // ambiguous-failure arm.
        await WaitFor(
            () => margin.ReservedForTesting(TestAppFactory.TestUser) == 3050m,
            TimeSpan.FromSeconds(3),
            $"expected reserved=3050m post-ambiguous, got {margin.ReservedForTesting(TestAppFactory.TestUser)}");

        // A competing order trying to consume the freed delta MUST
        // be rejected for margin. /orders accepts the request with
        // 202 and validates asynchronously (mirrors the existing
        // pass-3 test's posture), so we assert on the ledger side:
        // reserved must NOT increase above 3050 after the submission
        // attempt — if margin had been freed back to 3000 by an
        // erroneous AbortReplace, the 30-notional competing order
        // would slot into the headroom and bump reserved to 3030.
        var orderReq = new HttpRequestMessage(HttpMethod.Post, "/orders/")
        {
            Content = JsonContent.Create(new
            {
                Symbol = "PETR4",
                Side = "Buy",
                Type = "Limit",
                Quantity = 1,
                Price = 30.0m,
            }),
        };
        orderReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await http.SendAsync(orderReq);
        // Give the submit pipeline a beat to attempt the reservation.
        await Task.Delay(200);
        Assert.Equal(3050m, margin.ReservedForTesting(TestAppFactory.TestUser));

        // Now drive the late Replaced ER. CommitReplace must consume
        // the existing transient delta — no double-add: reserved
        // stays at 3050 (newRemainingNotional = 30.5 * 100 = 3050).
        await InjectReplacedEr(http, adminToken,
            newClOrdId: replace.NewClOrdId,
            origClOrdId: replace.OriginalClOrdId,
            leavesQuantity: replace.NewQuantity);

        var newChild = await WaitForChildOtherThan(book, algoId, oldChild.ClOrdId,
            TimeSpan.FromSeconds(3));
        Assert.Equal(replace.NewClOrdId, newChild.ClOrdId);
        Assert.Equal(30.5m, newChild.Price);

        await WaitFor(
            () => margin.ReservedForTesting(TestAppFactory.TestUser) == 3050m,
            TimeSpan.FromSeconds(3),
            $"expected reserved=3050m post-Replaced (no double-add), got {margin.ReservedForTesting(TestAppFactory.TestUser)}");
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
        => await WaitFor(predicate, timeout, () => failMessage);

    private static async Task WaitFor(Func<bool> predicate, TimeSpan timeout, Func<string> failMessageFactory)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(failMessageFactory() + $" (waited {timeout.TotalSeconds}s)");
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
