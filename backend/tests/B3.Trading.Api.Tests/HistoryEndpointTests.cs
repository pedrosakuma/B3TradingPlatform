using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q2.1 (#268). Integration coverage for the WAL-projected history
/// endpoints. Each test gets a fresh per-test data directory so the
/// FileEventStore is exercised in isolation; the simulator path is
/// enabled to drive ERs synchronously without an upstream venue.
/// </summary>
public class HistoryEndpointTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "b3-history-tests-" + Guid.NewGuid().ToString("N"));

    private IDictionary<string, string?> Overrides() => new Dictionary<string, string?>
    {
        ["Trading:Exchange:Mode"] = "Mock",
        ["Trading:Exchange:AllowErInjection"] = "true",
        ["Trading:Persistence:Enabled"] = "true",
        ["Trading:Persistence:DataDirectory"] = _dataDir,
        ["Trading:Persistence:FirmId"] = "test",
        // Snap interval is irrelevant for history queries (we replay the
        // raw WAL), but a long cadence avoids the snapshot service
        // racing with the test's own writes.
        ["Trading:Persistence:SnapshotInterval"] = "00:10:00",
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; tmp dir leaks are acceptable noise.
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task OrdersHistory_PaginationRoundTrip_ReturnsAllItemsExactlyOnce()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        const int totalOrders = 7;
        var posted = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < totalOrders; i++)
            posted.Add(await SubmitOrder(http, token, qty: 10, price: 30m + i));

        // limit=3 forces ⌈7/3⌉ = 3 pages; final page carries no nextCursor.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var pages = 0;
        do
        {
            pages++;
            Assert.True(pages <= 5, "pagination did not terminate");
            var page = await GetOrdersHistory(http, token, limit: 3, cursor: cursor);
            foreach (var item in page.Items)
            {
                Assert.True(seen.Add(item.GetProperty("clOrdId").GetString()!),
                    "duplicate ClOrdId returned across pages");
            }
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(posted, seen);
    }

    [Fact]
    public async Task OrdersHistory_FromInFuture_ReturnsEmpty()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        await SubmitOrder(http, token, qty: 10, price: 30m);

        var fromFuture = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
        var toFurtherFuture = DateTimeOffset.UtcNow.AddHours(2).ToString("O");
        var page = await GetOrdersHistory(http, token, from: fromFuture, to: toFurtherFuture);
        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task OrdersHistory_SymbolFilter_OnlyReturnsMatchingSymbol()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        await SubmitOrder(http, token, qty: 10, price: 30m, symbol: "PETR4");

        // No order on UNKNOWN_SYM was submitted.
        var unknown = await GetOrdersHistory(http, token, symbol: "ZZZZ99");
        Assert.Empty(unknown.Items);

        var matching = await GetOrdersHistory(http, token, symbol: "PETR4");
        Assert.NotEmpty(matching.Items);
        foreach (var item in matching.Items)
            Assert.Equal("PETR4", item.GetProperty("symbol").GetString());
    }

    [Fact]
    public async Task OrdersHistory_FirmIsolation_AliceDoesNotSeeBobsOrders()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var aliceToken = await f.LoginAsync(http);
        var bobToken = await f.LoginAsync(http, user: "bob");

        var aliceClOrdId = await SubmitOrder(http, aliceToken, qty: 10, price: 30m);
        var bobClOrdId = await SubmitOrder(http, bobToken, qty: 10, price: 30m);

        var alicePage = await GetOrdersHistory(http, aliceToken);
        var aliceIds = alicePage.Items.Select(o => o.GetProperty("clOrdId").GetString()).ToHashSet();
        Assert.Contains(aliceClOrdId, aliceIds);
        Assert.DoesNotContain(bobClOrdId, aliceIds);

        var bobPage = await GetOrdersHistory(http, bobToken);
        var bobIds = bobPage.Items.Select(o => o.GetProperty("clOrdId").GetString()).ToHashSet();
        Assert.Contains(bobClOrdId, bobIds);
        Assert.DoesNotContain(aliceClOrdId, bobIds);
    }

    [Fact]
    public async Task OrdersHistory_Determinism_TwoCallsReturnSameItems()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        for (var i = 0; i < 4; i++)
            await SubmitOrder(http, token, qty: 10, price: 30m + i);

        var first = await GetOrdersHistory(http, token);
        var second = await GetOrdersHistory(http, token);

        Assert.Equal(first.Items.Count, second.Items.Count);
        for (var i = 0; i < first.Items.Count; i++)
        {
            Assert.Equal(
                first.Items[i].GetProperty("clOrdId").GetString(),
                second.Items[i].GetProperty("clOrdId").GetString());
        }
    }

    [Fact]
    public async Task OrdersHistory_MalformedCursor_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var req = new HttpRequestMessage(HttpMethod.Get,
            "/orders/history?cursor=" + Uri.EscapeDataString("!!!not-base64!!!"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task OrdersHistory_LimitOver500_IsClamped()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        await SubmitOrder(http, token, qty: 10, price: 30m);

        // Asking for 10_000 must NOT 400 — clamped to 500. With one
        // order in the WAL the page necessarily fits below the cap.
        var page = await GetOrdersHistory(http, token, limit: 10_000);
        Assert.NotEmpty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task OrdersHistory_ToBeforeFrom_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var from = DateTimeOffset.UtcNow.ToString("O");
        var to = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"/orders/history?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task OrdersHistory_Unauthenticated_Returns401()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var resp = await http.GetAsync("/orders/history");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ExecutionsHistory_AfterFill_ReturnsErRows()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var clOrdId = await SubmitOrder(http, token, qty: 10, price: 30m);
        await InjectEr(http, adminToken, new
        {
            ClOrdId = ulong.Parse(clOrdId),
            Type = "Fill",
            LastQty = 10L,
            LastPx = 30m,
        });

        var page = await GetExecutionsHistory(http, token);
        Assert.NotEmpty(page.Items);
        var matching = page.Items.Where(e => e.GetProperty("clOrdId").GetString() == clOrdId).ToList();
        Assert.NotEmpty(matching);
        Assert.Contains(matching, e => e.GetProperty("kind").GetString() == "Fill");
    }

    [Fact]
    public async Task ExecutionsHistory_FirmIsolation_AliceDoesNotSeeBobsExecutions()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var aliceToken = await f.LoginAsync(http);
        var bobToken = await f.LoginAsync(http, user: "bob");
        var adminToken = await f.LoginAsync(http, "admin");

        var bobClOrdId = await SubmitOrder(http, bobToken, qty: 10, price: 30m);
        await InjectEr(http, adminToken, new
        {
            ClOrdId = ulong.Parse(bobClOrdId),
            Type = "Fill",
            LastQty = 10L,
            LastPx = 30m,
        });

        var alicePage = await GetExecutionsHistory(http, aliceToken);
        Assert.DoesNotContain(alicePage.Items,
            e => e.GetProperty("clOrdId").GetString() == bobClOrdId);
    }

    [Fact]
    public async Task OrdersHistory_ReplaceThenPartialFill_OriginalIsReplacedAndNewIsPartiallyFilled()
    {
        // Regression for #275 P1: the projector used to retarget the
        // Replaced ER to the original (via OrigClOrdId) and never
        // hydrate the new ClOrdID — leaving the new order stuck at
        // PendingNew even though the venue had already accepted the
        // replacement and the platform's runtime treated it as Working
        // (or PartiallyFilled after fills). The fix mirrors
        // ExecutionReportProcessor.ApplyReplaceAccepted +
        // Order.HydrateReplacement: terminalize the original at
        // Replaced, hydrate the new one with the ER's leaves/cum
        // baseline, and let subsequent ERs accumulate normally.
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var origClOrdIdStr = await SubmitOrder(http, token, qty: 10, price: 30m);
        var origClOrdId = ulong.Parse(origClOrdIdStr);
        // Drive original to Working — the runtime's modify pipeline
        // refuses to replace orders that haven't been venue-acked.
        await InjectEr(http, adminToken, new
        {
            ClOrdId = origClOrdId,
            Type = "New",
        });

        // PUT modifies → the platform writes OrderReplaceRequestedEvent
        // for the new ClOrdID and dispatches a CancelReplace to the wire.
        var modifyReq = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origClOrdIdStr}")
        {
            Content = JsonContent.Create(new { Quantity = 10L, Price = 31m }),
        };
        modifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modifyResp = await http.SendAsync(modifyReq);
        Assert.Equal(HttpStatusCode.Accepted, modifyResp.StatusCode);
        var modifyBody = await modifyResp.Content.ReadFromJsonAsync<JsonElement>();
        var newClOrdId = ulong.Parse(modifyBody.GetProperty("clOrdId").GetString()!);

        // Mock-emit the venue's Replaced ER. ClOrdId targets the new
        // order; OrigClOrdId points back at the original. The runtime
        // path is unchanged (it goes through ApplyReplaceAccepted +
        // HydrateReplacement); the WAL append it triggers is what the
        // history projector will later replay.
        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: newClOrdId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Replaced,
            LeavesQuantity: 10,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: origClOrdId));

        // Partial fill on the NEW ClOrdID. The simulator endpoint
        // resolves the order from WorkingOrderBook — proves the
        // hydrate path also wired the runtime book correctly, but
        // more importantly the resulting WAL entry exercises the
        // post-Replaced accumulation that the projector must surface.
        await InjectEr(http, adminToken, new
        {
            ClOrdId = newClOrdId,
            Type = "PartialFill",
            LastQty = 4L,
            LastPx = 31m,
        });

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        Assert.True(byId.ContainsKey(origClOrdIdStr), "original order must appear in history");
        Assert.True(byId.ContainsKey(newClOrdId.ToString()), "replacement order must appear in history");

        var orig = byId[origClOrdIdStr];
        Assert.Equal("Replaced", orig.GetProperty("status").GetString());

        var replacement = byId[newClOrdId.ToString()];
        Assert.Equal("PartiallyFilled", replacement.GetProperty("status").GetString());
        Assert.Equal(4L, replacement.GetProperty("cumulativeQuantity").GetInt64());
        Assert.Equal(6L, replacement.GetProperty("leavesQuantity").GetInt64());
    }

    [Fact]
    public async Task OrdersHistory_ReplaceRacingFinalFill_OriginalStaysFilled()
    {
        // P1 regression for #275 pass-2 review: when the WAL ordering is
        // [ReplaceRequested(A→B), Fill(A), Replaced(B,Orig=A)] the
        // projector must mirror Order.MarkReplaced and leave A at Filled.
        // The previous ApplyReplacedTerminal unconditionally overwrote
        // status with Replaced, so /orders/history reported A=Replaced
        // while the live runtime (which short-circuits the late Replaced
        // ack via MarkReplaced's terminal-state guard) reported Filled.
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var origClOrdIdStr = await SubmitOrder(http, token, qty: 10, price: 30m);
        var origClOrdId = ulong.Parse(origClOrdIdStr);
        await InjectEr(http, adminToken, new { ClOrdId = origClOrdId, Type = "New" });

        // Kick off a replace — A is still in the book, so the modify is
        // accepted and a new ClOrdID B is registered.
        var modifyReq = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origClOrdIdStr}")
        {
            Content = JsonContent.Create(new { Quantity = 10L, Price = 31m }),
        };
        modifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modifyResp = await http.SendAsync(modifyReq);
        Assert.Equal(HttpStatusCode.Accepted, modifyResp.StatusCode);
        var modifyBody = await modifyResp.Content.ReadFromJsonAsync<JsonElement>();
        var newClOrdId = ulong.Parse(modifyBody.GetProperty("clOrdId").GetString()!);

        // Fill A fully BEFORE the venue's Replaced ack lands. In the
        // runtime this terminalises A=Filled; the late Replaced ER's
        // MarkReplaced is then a no-op for A's status (margin transfer
        // is aborted because A is no longer in the book).
        await InjectEr(http, adminToken, new
        {
            ClOrdId = origClOrdId,
            Type = "Fill",
            LastQty = 10L,
            LastPx = 30m,
        });

        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: newClOrdId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Replaced,
            LeavesQuantity: 10,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: origClOrdId));

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        Assert.True(byId.ContainsKey(origClOrdIdStr), "original order must appear in history");
        var orig = byId[origClOrdIdStr];
        Assert.Equal("Filled", orig.GetProperty("status").GetString());
        Assert.Equal(10L, orig.GetProperty("cumulativeQuantity").GetInt64());
    }

    [Fact]
    public async Task OrdersHistory_ReplaceRacingCancel_OriginalStaysCancelled()
    {
        // Edge of the same P1: WAL [ReplaceRequested(A→B), Cancel(A),
        // Replaced(B,Orig=A)] must leave A=Cancelled. MarkReplaced
        // preserves Cancelled the same way it preserves Filled.
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var origClOrdIdStr = await SubmitOrder(http, token, qty: 10, price: 30m);
        var origClOrdId = ulong.Parse(origClOrdIdStr);
        await InjectEr(http, adminToken, new { ClOrdId = origClOrdId, Type = "New" });

        var modifyReq = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origClOrdIdStr}")
        {
            Content = JsonContent.Create(new { Quantity = 10L, Price = 31m }),
        };
        modifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modifyResp = await http.SendAsync(modifyReq);
        Assert.Equal(HttpStatusCode.Accepted, modifyResp.StatusCode);
        var modifyBody = await modifyResp.Content.ReadFromJsonAsync<JsonElement>();
        var newClOrdId = ulong.Parse(modifyBody.GetProperty("clOrdId").GetString()!);

        await InjectEr(http, adminToken, new { ClOrdId = origClOrdId, Type = "Canceled" });

        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: newClOrdId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Replaced,
            LeavesQuantity: 10,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: origClOrdId));

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        var orig = byId[origClOrdIdStr];
        Assert.Equal("Cancelled", orig.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OrdersHistory_ChainedReplaceWithMidChainCancel_PreservesEachLink()
    {
        // P1 chained-edge: A→B→C with B cancelled mid-chain. Expected
        // projection (matches runtime):
        //   A = Replaced (terminalised by the first Replaced ER while
        //       still non-terminal)
        //   B = Cancelled (Cancel ER lands before the second Replaced
        //       ER; the late Replaced(C,Orig=B) must NOT regress B)
        //   C = Working   (hydrated from the second Replaced ER's
        //       leaves/cum baseline)
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var aIdStr = await SubmitOrder(http, token, qty: 10, price: 30m);
        var aId = ulong.Parse(aIdStr);
        await InjectEr(http, adminToken, new { ClOrdId = aId, Type = "New" });

        // First replace A→B.
        var put1 = new HttpRequestMessage(HttpMethod.Put, $"/orders/{aIdStr}")
        {
            Content = JsonContent.Create(new { Quantity = 10L, Price = 31m }),
        };
        put1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var put1Resp = await http.SendAsync(put1);
        Assert.Equal(HttpStatusCode.Accepted, put1Resp.StatusCode);
        var bId = ulong.Parse((await put1Resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("clOrdId").GetString()!);

        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        // First Replaced ER terminalises A and hydrates B as Working.
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: bId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Replaced,
            LeavesQuantity: 10,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: aId));

        // Second replace B→C (B is now Working in the runtime book).
        var put2 = new HttpRequestMessage(HttpMethod.Put, $"/orders/{bId}")
        {
            Content = JsonContent.Create(new { Quantity = 10L, Price = 32m }),
        };
        put2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var put2Resp = await http.SendAsync(put2);
        Assert.Equal(HttpStatusCode.Accepted, put2Resp.StatusCode);
        var cId = ulong.Parse((await put2Resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("clOrdId").GetString()!);

        // Cancel B before the second Replaced lands.
        await InjectEr(http, adminToken, new { ClOrdId = bId, Type = "Canceled" });

        // Late second Replaced ER: must NOT regress B from Cancelled.
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: cId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Replaced,
            LeavesQuantity: 10,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: bId));

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        Assert.Equal("Replaced", byId[aIdStr].GetProperty("status").GetString());
        Assert.Equal("Cancelled", byId[bId.ToString()].GetProperty("status").GetString());
        Assert.Equal("Working", byId[cId.ToString()].GetProperty("status").GetString());
    }

    [Fact]
    public async Task OrdersHistory_ReplacePairAtPageBoundary_BothSiblingsAppearAcrossPages()
    {
        // P1 regression for #275 pass-3: a Replaced ER updates BOTH the
        // original and the replacement projection with the same LastSeq.
        // The pre-fix cursor filter (a.Seq < c.Seq) silently dropped the
        // sibling at the boundary whenever pagination split the pair.
        // The composite (LastSeq, ClOrdId) keyset cursor must surface
        // both rows across the two pages exactly once.
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        // One filler order so we can paginate with limit=1 without the
        // "single result fits the page" short-circuit.
        var fillerStr = await SubmitOrder(http, token, qty: 10, price: 29m);
        await InjectEr(http, adminToken, new { ClOrdId = ulong.Parse(fillerStr), Type = "New" });

        // The replace pair: A submitted + acked, then replaced into B.
        // The Replaced ER touches both A (terminal) and B (hydrate)
        // at the same WAL seq.
        var origStr = await SubmitOrder(http, token, qty: 10, price: 30m);
        var origId = ulong.Parse(origStr);
        await InjectEr(http, adminToken, new { ClOrdId = origId, Type = "New" });

        var modifyReq = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origStr}")
        {
            Content = JsonContent.Create(new { Quantity = 10L, Price = 31m }),
        };
        modifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modifyResp = await http.SendAsync(modifyReq);
        Assert.Equal(HttpStatusCode.Accepted, modifyResp.StatusCode);
        var newId = ulong.Parse((await modifyResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("clOrdId").GetString()!);

        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: newId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Replaced,
            LeavesQuantity: 10,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: origId));

        // Walk every page with limit=1 — boundary necessarily falls
        // between every adjacent pair, including the replace siblings.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var pages = 0;
        do
        {
            pages++;
            Assert.True(pages <= 10, "pagination did not terminate");
            var page = await GetOrdersHistory(http, token, limit: 1, cursor: cursor);
            foreach (var item in page.Items)
                Assert.True(seen.Add(item.GetProperty("clOrdId").GetString()!),
                    "duplicate ClOrdId returned across pages");
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Contains(origStr, seen);
        Assert.Contains(newId.ToString(), seen);
        Assert.Contains(fillerStr, seen);
    }

    [Fact]
    public async Task OrdersHistory_LateFillOnCancelledOrder_PreservesCancelledStatus()
    {
        // P1 regression for #275 pass-3: WAL [Canceled(A), Fill(A)] —
        // the runtime's Order.ApplyCumulativeFill keeps A=Cancelled
        // (terminal status preserved on late fill). The pre-fix
        // ApplyEr unconditionally re-mapped Fill → Filled, so
        // /orders/history reported A=Filled while the live runtime
        // reported A=Cancelled.
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var clOrdIdStr = await SubmitOrder(http, token, qty: 10, price: 30m);
        var clOrdId = ulong.Parse(clOrdIdStr);
        await InjectEr(http, adminToken, new { ClOrdId = clOrdId, Type = "New" });
        await InjectEr(http, adminToken, new { ClOrdId = clOrdId, Type = "Canceled" });
        // Late fill ER directly via the mock — the simulator endpoint
        // refuses to inject fills against an order the runtime book
        // considers terminal, but the venue can still legally deliver
        // one (see ExecutionReportProcessor late-fill path).
        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: clOrdId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Fill,
            LeavesQuantity: 0,
            CumulativeQuantity: 10,
            LastQuantity: 10,
            LastPrice: 30m,
            RejectReason: null,
            OrigClOrdId: 0));

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        var orig = byId[clOrdIdStr];
        Assert.Equal("Cancelled", orig.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OrdersHistory_CancelAckWithMissingOrigClOrdId_ResolvesViaCancelLinkMap()
    {
        // P1 regression for #275 pass-3: when the venue's Canceled ER
        // arrives with OrigClOrdId=0 (some EntryPoint SDK versions drop
        // the field) the runtime falls back to the cancel-link map
        // populated by OrderCancelService. The history projector now
        // mirrors that fallback by replaying OrderCancelRequestedEvent
        // → cancelLinks[cancelClOrdId] = originalClOrdId. Without this
        // the cancel ack is silently stranded and the original order
        // never transitions to Cancelled in the history view.
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var origStr = await SubmitOrder(http, token, qty: 10, price: 30m);
        var origId = ulong.Parse(origStr);
        await InjectEr(http, adminToken, new { ClOrdId = origId, Type = "New" });

        // DELETE writes OrderCancelRequestedEvent + dispatches a
        // OrderCancelRequest with a freshly-allocated cancel-side ClOrdID
        // to the wire. We grab that cancel-side ID from the mock.
        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        var cancelsBefore = mock.SubmittedCancels.Count;
        var del = new HttpRequestMessage(HttpMethod.Delete, $"/orders/{origStr}");
        del.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var delResp = await http.SendAsync(del);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);
        var cancelReq = mock.SubmittedCancels.Skip(cancelsBefore).Single();
        var cancelClOrdId = cancelReq.ClOrdId;
        Assert.NotEqual(0UL, cancelClOrdId);

        // Venue Canceled ER targeting the cancel-side ID with
        // OrigClOrdId=0. The history projector must resolve it via the
        // cancel-link map and apply the cancel to `origId`.
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: cancelClOrdId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Canceled,
            LeavesQuantity: 10,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: 0));

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        Assert.True(byId.ContainsKey(origStr), "original must surface in history");
        Assert.Equal("Cancelled", byId[origStr].GetProperty("status").GetString());

        // /executions/history: the cancel ack itself must also appear,
        // resolved through the cancel-link map for firm-isolation.
        var execPage = await GetExecutionsHistory(http, token);
        Assert.Contains(execPage.Items,
            e => e.GetProperty("clOrdId").GetString() == cancelClOrdId.ToString()
                 && e.GetProperty("kind").GetString() == "Canceled");
    }

    [Fact]
    public async Task OrdersHistory_ReplaceAckWithMissingOrigClOrdId_ResolvesViaReplaceLinkMap()
    {
        // P1 regression for #275 pass-3: same fallback as the cancel
        // case above, but the venue's Replaced ER drops OrigClOrdId.
        // The history projector must replay
        // OrderReplaceRequestedEvent → replaceLinks[newClOrdId] =
        // originalClOrdId, recover the original from there, and project
        // both sides (terminal Replaced on original; hydrate new).
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var origStr = await SubmitOrder(http, token, qty: 10, price: 30m);
        var origId = ulong.Parse(origStr);
        await InjectEr(http, adminToken, new { ClOrdId = origId, Type = "New" });

        var modifyReq = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origStr}")
        {
            Content = JsonContent.Create(new { Quantity = 10L, Price = 31m }),
        };
        modifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modifyResp = await http.SendAsync(modifyReq);
        Assert.Equal(HttpStatusCode.Accepted, modifyResp.StatusCode);
        var newId = ulong.Parse((await modifyResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("clOrdId").GetString()!);

        // Venue Replaced ER with OrigClOrdId=0 — must resolve via
        // replaceLinks[newId] = origId.
        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: newId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Replaced,
            LeavesQuantity: 10,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: 0));

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        Assert.True(byId.ContainsKey(origStr), "original must appear");
        Assert.True(byId.ContainsKey(newId.ToString()), "replacement must appear");
        Assert.Equal("Replaced", byId[origStr].GetProperty("status").GetString());
        // Replacement is hydrated as Working (leaves==NewQty, cum==0).
        Assert.Equal("Working", byId[newId.ToString()].GetProperty("status").GetString());
        Assert.Equal(10L, byId[newId.ToString()].GetProperty("leavesQuantity").GetInt64());
    }

    [Fact]
    public async Task OrdersHistory_CancelAsReplace_OriginalIsReplacedAndNewIsHydrated()
    {
        // P1 regression for #275 pass-4: B3MatchingPlatform's "priority-lost"
        // cancel-as-replace path (issue #241). When the venue implements a
        // modify by emitting Cancel(B, OrigClOrdId=0) under the replacement's
        // NEW ClOrdID — never an ExecType=Replaced — the runtime intercepts
        // via PendingReplacementRegistry.TryConsume and funnels through
        // ApplyReplaceAccepted. The history projector must mirror that
        // contract: original A goes Replaced, new B is hydrated from the
        // ER's leaves/cum, just as if a Replaced ER had been received.
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var origStr = await SubmitOrder(http, token, qty: 10, price: 30m);
        var origId = ulong.Parse(origStr);
        await InjectEr(http, adminToken, new { ClOrdId = origId, Type = "New" });

        var modifyReq = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origStr}")
        {
            Content = JsonContent.Create(new { Quantity = 10L, Price = 31m }),
        };
        modifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modifyResp = await http.SendAsync(modifyReq);
        Assert.Equal(HttpStatusCode.Accepted, modifyResp.StatusCode);
        var newId = ulong.Parse((await modifyResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("clOrdId").GetString()!);

        // Venue cancel-as-replace: Canceled ER under newId, OrigClOrdId=0.
        // Real venue shape — LeavesQuantity=0 / CumulativeQuantity=0 (the
        // ER is reporting a cancel of the original). The runtime intercepts
        // via the replacement registry and funnels through
        // ApplyReplaceAccepted(erLeaves: intent.NewQuantity, erCum: 0); the
        // history projector must mirror that, hydrating B from the originating
        // OrderReplaceRequestedEvent's NewQuantity rather than the ER's
        // zeroed leaves/cum (which would otherwise mark B as Filled).
        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: newId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Canceled,
            LeavesQuantity: 0,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: 0));

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        Assert.True(byId.ContainsKey(origStr), "original must appear");
        Assert.True(byId.ContainsKey(newId.ToString()), "replacement must appear");
        Assert.Equal("Replaced", byId[origStr].GetProperty("status").GetString());
        // Replacement hydrated as Working with leaves==NewQuantity (10) and
        // cum==0 — proving the projector pulls leaves from the replace
        // intent, not from the ER's (zeroed) LeavesQuantity. Was previously
        // masked by a test that happened to set LeavesQuantity==NewQuantity.
        Assert.Equal("Working", byId[newId.ToString()].GetProperty("status").GetString());
        Assert.Equal(10L, byId[newId.ToString()].GetProperty("leavesQuantity").GetInt64());
        Assert.Equal(0L, byId[newId.ToString()].GetProperty("cumulativeQuantity").GetInt64());
    }

    [Fact]
    public async Task OrdersHistory_CancelAsReplaceThenRealCancel_NewIsCancelled()
    {
        // P1 pass-4: after the cancel-as-replace consumes the replace link,
        // a subsequent Canceled ER on the new ClOrdID must be processed as
        // a regular cancel of the new order — not re-intercepted as another
        // cancel-as-replace. Mirrors PendingReplacementRegistry.TryConsume's
        // remove-on-success contract.
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var origStr = await SubmitOrder(http, token, qty: 10, price: 30m);
        var origId = ulong.Parse(origStr);
        await InjectEr(http, adminToken, new { ClOrdId = origId, Type = "New" });

        var modifyReq = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origStr}")
        {
            Content = JsonContent.Create(new { Quantity = 10L, Price = 31m }),
        };
        modifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modifyResp = await http.SendAsync(modifyReq);
        Assert.Equal(HttpStatusCode.Accepted, modifyResp.StatusCode);
        var newId = ulong.Parse((await modifyResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("clOrdId").GetString()!);

        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();

        // First Canceled ER under newId — cancel-as-replace intercept.
        // Real venue shape: LeavesQuantity=0 / CumulativeQuantity=0.
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: newId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Canceled,
            LeavesQuantity: 0,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: 0));

        // Second Canceled ER under newId — must be a regular cancel of the
        // new (now Working) order, since the link has been consumed.
        await InjectEr(http, adminToken, new { ClOrdId = newId, Type = "Canceled" });

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        Assert.Equal("Replaced", byId[origStr].GetProperty("status").GetString());
        Assert.Equal("Cancelled", byId[newId.ToString()].GetProperty("status").GetString());
    }

    [Fact]
    public async Task OrdersHistory_CancelAsReplaceAfterOriginalFilled_OriginalStaysFilled()
    {
        // P1 pass-4 edge: when the original was already Filled before the
        // venue's cancel-as-replace lands, the projector's terminal-preservation
        // (ApplyReplacedTerminal) keeps A=Filled while still hydrating B from
        // the ER. Mirrors Order.MarkReplaced's terminal-state guard, exactly
        // as in the ReplaceRacingFinalFill test for the Replaced-ER variant.
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var origStr = await SubmitOrder(http, token, qty: 10, price: 30m);
        var origId = ulong.Parse(origStr);
        await InjectEr(http, adminToken, new { ClOrdId = origId, Type = "New" });

        var modifyReq = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origStr}")
        {
            Content = JsonContent.Create(new { Quantity = 10L, Price = 31m }),
        };
        modifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modifyResp = await http.SendAsync(modifyReq);
        Assert.Equal(HttpStatusCode.Accepted, modifyResp.StatusCode);
        var newId = ulong.Parse((await modifyResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("clOrdId").GetString()!);

        // Fill A fully BEFORE the venue's cancel-as-replace lands.
        await InjectEr(http, adminToken, new
        {
            ClOrdId = origId,
            Type = "Fill",
            LastQty = 10L,
            LastPx = 30m,
        });

        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        // Real venue shape: cancel-side ER reports LeavesQuantity=0.
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: newId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Canceled,
            LeavesQuantity: 0,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: 0));

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        Assert.Equal("Filled", byId[origStr].GetProperty("status").GetString());
        Assert.Equal(10L, byId[origStr].GetProperty("cumulativeQuantity").GetInt64());
        // B still hydrated as the replacement.
        Assert.True(byId.ContainsKey(newId.ToString()), "replacement must appear");
        Assert.Equal("Working", byId[newId.ToString()].GetProperty("status").GetString());
        Assert.Equal(10L, byId[newId.ToString()].GetProperty("leavesQuantity").GetInt64());
    }

    [Fact]
    public async Task OrdersHistory_CancelAsReplaceWithPartiallyFilledOriginal_ReplacementCumResetsToZero()
    {
        // P1 #275 pass-5: when the original has accumulated partial fills
        // before the venue's cancel-as-replace lands, the runtime's
        // ApplyReplaceAccepted resets the replacement to (leaves: NewQuantity,
        // cum: 0) — the new ClOrdID is a brand-new order in the book and does
        // not inherit the predecessor's fills. The history projector must
        // mirror that: B's leaves come from the OrderReplaceRequestedEvent's
        // NewQuantity, NOT from the original's residual leaves, and cum is 0
        // regardless of the predecessor's cumulative.
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var origStr = await SubmitOrder(http, token, qty: 10, price: 30m);
        var origId = ulong.Parse(origStr);
        await InjectEr(http, adminToken, new { ClOrdId = origId, Type = "New" });

        // Partial fill: 3 of 10 done, leaves 7.
        await InjectEr(http, adminToken, new
        {
            ClOrdId = origId,
            Type = "Fill",
            LastQty = 3L,
            LastPx = 30m,
        });

        // Modify to a DIFFERENT new quantity (8) so the assertion proves the
        // value came from the replace intent, not coincidentally from the
        // original quantity, leaves, or cum.
        var modifyReq = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origStr}")
        {
            Content = JsonContent.Create(new { Quantity = 8L, Price = 31m }),
        };
        modifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modifyResp = await http.SendAsync(modifyReq);
        Assert.Equal(HttpStatusCode.Accepted, modifyResp.StatusCode);
        var newId = ulong.Parse((await modifyResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("clOrdId").GetString()!);

        // Real venue cancel-as-replace shape: LeavesQuantity=0 / cum=0 under newId.
        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: newId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Canceled,
            LeavesQuantity: 0,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: 0));

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        // Original goes Replaced (terminal-preservation doesn't apply: it
        // was PartiallyFilled, not terminal).
        Assert.Equal("Replaced", byId[origStr].GetProperty("status").GetString());
        // Replacement: leaves=NewQuantity (8), cum reset to 0, Working —
        // mirrors ExecutionReportProcessor.ApplyReplaceAccepted.
        Assert.True(byId.ContainsKey(newId.ToString()), "replacement must appear");
        Assert.Equal("Working", byId[newId.ToString()].GetProperty("status").GetString());
        Assert.Equal(8L, byId[newId.ToString()].GetProperty("leavesQuantity").GetInt64());
        Assert.Equal(0L, byId[newId.ToString()].GetProperty("cumulativeQuantity").GetInt64());
    }

    [Fact]
    public async Task OrdersHistory_CancelEr_PreservesLeavesAndCumFromRuntimeBook()
    {
        // P1 regression for #275 pass-6: Order.MarkCancelled only flips
        // Status — it does NOT touch leaves/cum. A real venue Canceled
        // ER typically carries LeavesQuantity=0, so the previous
        // ApplyEr (which copied er.LeavesQuantity into the projection)
        // showed a 10-lot working order as leaves=0 in /orders/history
        // while the live runtime kept leaves=10.
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var clOrdIdStr = await SubmitOrder(http, token, qty: 10, price: 30m);
        var clOrdId = ulong.Parse(clOrdIdStr);
        await InjectEr(http, adminToken, new { ClOrdId = clOrdId, Type = "New" });

        // Direct mock emit so we control the Canceled ER's leaves
        // exactly (the simulator endpoint synthesises leaves=0 by
        // default, but going through the mock makes the contract
        // explicit in the test).
        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: clOrdId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Canceled,
            LeavesQuantity: 0,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: 0));

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        var orig = byId[clOrdIdStr];
        Assert.Equal("Cancelled", orig.GetProperty("status").GetString());
        // Leaves/cum must mirror runtime parity (MarkCancelled is
        // status-only). Original 10-lot has had no fills.
        Assert.Equal(10L, orig.GetProperty("leavesQuantity").GetInt64());
        Assert.Equal(0L, orig.GetProperty("cumulativeQuantity").GetInt64());
    }

    [Fact]
    public async Task OrdersHistory_RejectedEr_PreservesLeavesAndCumFromRuntimeBook()
    {
        // P1 regression for #275 pass-6: Order.MarkRejected is also
        // status-only. A Rejected ER on a 10-lot pending order with
        // leaves=0 in the ER must still surface leaves=10 in the
        // projection (matching the runtime book).
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);

        var clOrdIdStr = await SubmitOrder(http, token, qty: 10, price: 30m);
        var clOrdId = ulong.Parse(clOrdIdStr);

        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: clOrdId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Rejected,
            LeavesQuantity: 0,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: "venue rejected",
            OrigClOrdId: 0));

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        var orig = byId[clOrdIdStr];
        Assert.Equal("Rejected", orig.GetProperty("status").GetString());
        Assert.Equal(10L, orig.GetProperty("leavesQuantity").GetInt64());
        Assert.Equal(0L, orig.GetProperty("cumulativeQuantity").GetInt64());
    }

    [Fact]
    public async Task OrdersHistory_QuantityOnlyReplaceOfGtdOrder_InheritsTifAndGoodTillDate()
    {
        // P1 regression for #275 pass-6: Order.HydrateReplacement /
        // Order.MergeReplacementOptionals INHERIT TIF / StopPrice /
        // GoodTillDate from the original when the modify request omits
        // them. The projector previously treated the request fields as
        // final values, so a quantity-only replace of a GTD order
        // surfaced TIF=Day + GoodTillDate=null in /orders/history while
        // the live runtime kept GTD + the original expiry.
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var gtd = DateTimeOffset.UtcNow.AddDays(20);
        var origStr = await SubmitGtdOrder(http, token, qty: 10, price: 30m, goodTillDate: gtd);
        var origId = ulong.Parse(origStr);
        await InjectEr(http, adminToken, new { ClOrdId = origId, Type = "New" });

        // Quantity-only modify — TIF / StopPrice / GoodTillDate omitted.
        var modifyReq = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origStr}")
        {
            Content = JsonContent.Create(new { Quantity = 20L }),
        };
        modifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modifyResp = await http.SendAsync(modifyReq);
        if (modifyResp.StatusCode != HttpStatusCode.Accepted)
        {
            var errBody = await modifyResp.Content.ReadAsStringAsync();
            Assert.Fail($"modify failed: {modifyResp.StatusCode} {errBody}");
        }
        var modifyBody = await modifyResp.Content.ReadFromJsonAsync<JsonElement>();
        var newId = ulong.Parse(modifyBody.GetProperty("clOrdId").GetString()!);

        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: newId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Replaced,
            LeavesQuantity: 20,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: origId));

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        var replacement = byId[newId.ToString()];
        Assert.Equal("GTD", replacement.GetProperty("timeInForce").GetString());
        Assert.Equal(gtd, replacement.GetProperty("goodTillDate").GetDateTimeOffset());
        Assert.Equal(20L, replacement.GetProperty("quantity").GetInt64());
    }

    [Fact]
    public async Task OrdersHistory_QuantityOnlyReplaceOfStopOrder_InheritsStopPrice()
    {
        // P1 regression for #275 pass-6: a quantity-only replace of a
        // stop order must inherit the original StopPrice — the modify
        // pipeline does so via MergeReplacementOptionals; the projector
        // now mirrors the same merge.
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var origStr = await SubmitStopLimitOrder(
            http, token, qty: 10, price: 31m, stopPrice: 30m);
        var origId = ulong.Parse(origStr);
        await InjectEr(http, adminToken, new { ClOrdId = origId, Type = "New" });

        var modifyReq = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origStr}")
        {
            // Price is supplied explicitly because the modify pipeline
            // requires NewPrice on stop-limit (limit-vs-stop sanity is
            // re-evaluated in risk). The Q1.1 inheritance under test is
            // for StopPrice / TIF / GoodTillDate only — those are the
            // optionals MergeReplacementOptionals merges.
            Content = JsonContent.Create(new { Quantity = 20L, Price = 31m }),
        };
        modifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modifyResp = await http.SendAsync(modifyReq);
        if (modifyResp.StatusCode != HttpStatusCode.Accepted)
        {
            var errBody = await modifyResp.Content.ReadAsStringAsync();
            Assert.Fail($"modify failed: {modifyResp.StatusCode} {errBody}");
        }
        var modifyBody = await modifyResp.Content.ReadFromJsonAsync<JsonElement>();
        var newId = ulong.Parse(modifyBody.GetProperty("clOrdId").GetString()!);

        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: newId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Replaced,
            LeavesQuantity: 20,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: origId));

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        var replacement = byId[newId.ToString()];
        Assert.Equal("Day", replacement.GetProperty("timeInForce").GetString());
        Assert.Equal(30m, replacement.GetProperty("stopPrice").GetDecimal());
        Assert.Equal(20L, replacement.GetProperty("quantity").GetInt64());
    }

    [Fact]
    public async Task OrdersHistory_ReplaceTifGtdToDay_ClearsGoodTillDate()
    {
        // P1 regression for #275 pass-6: when the replace explicitly
        // moves TIF GTD → Day, MergeReplacementOptionals auto-clears
        // GoodTillDate (the trick callers use to shed an inherited
        // expiry without redundantly nulling it). The projector must
        // mirror that auto-clear so the history view doesn't drag the
        // original expiry forward onto a Day order.
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var token = await f.LoginAsync(http);
        var adminToken = await f.LoginAsync(http, "admin");

        var gtd = DateTimeOffset.UtcNow.AddDays(20);
        var origStr = await SubmitGtdOrder(http, token, qty: 10, price: 30m, goodTillDate: gtd);
        var origId = ulong.Parse(origStr);
        await InjectEr(http, adminToken, new { ClOrdId = origId, Type = "New" });

        var modifyReq = new HttpRequestMessage(HttpMethod.Put, $"/orders/{origStr}")
        {
            Content = JsonContent.Create(new { Quantity = 10L, TimeInForce = "Day" }),
        };
        modifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var modifyResp = await http.SendAsync(modifyReq);
        Assert.Equal(HttpStatusCode.Accepted, modifyResp.StatusCode);
        var modifyBody = await modifyResp.Content.ReadFromJsonAsync<JsonElement>();
        var newId = ulong.Parse(modifyBody.GetProperty("clOrdId").GetString()!);

        var mock = (B3.Trading.Infrastructure.MockEntryPointClient)
            f.Services.GetRequiredService<B3.Trading.Infrastructure.IEntryPointClient>();
        mock.EmitExecutionReport(new B3.Trading.Infrastructure.ExecutionReportEnvelope(
            ClOrdId: newId,
            ExecType: B3.Trading.Infrastructure.EpExecType.Replaced,
            LeavesQuantity: 10,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: origId));

        var page = await GetOrdersHistory(http, token);
        var byId = page.Items.ToDictionary(
            o => o.GetProperty("clOrdId").GetString()!,
            o => o,
            StringComparer.Ordinal);

        var replacement = byId[newId.ToString()];
        Assert.Equal("Day", replacement.GetProperty("timeInForce").GetString());
        var gtdProp = replacement.GetProperty("goodTillDate");
        Assert.Equal(JsonValueKind.Null, gtdProp.ValueKind);
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private sealed record HistoryPage(IReadOnlyList<JsonElement> Items, string? NextCursor);

    private static async Task<HistoryPage> GetOrdersHistory(
        HttpClient http, string token, int? limit = null, string? cursor = null,
        string? from = null, string? to = null, string? symbol = null)
    {
        return await GetHistory(http, token, "/orders/history", limit, cursor, from, to, symbol);
    }

    private static async Task<HistoryPage> GetExecutionsHistory(
        HttpClient http, string token, int? limit = null, string? cursor = null,
        string? from = null, string? to = null, string? symbol = null)
    {
        return await GetHistory(http, token, "/executions/history", limit, cursor, from, to, symbol);
    }

    private static async Task<HistoryPage> GetHistory(
        HttpClient http, string token, string path,
        int? limit, string? cursor, string? from, string? to, string? symbol)
    {
        var qs = new StringBuilder();
        void Add(string k, string? v)
        {
            if (string.IsNullOrEmpty(v)) return;
            qs.Append(qs.Length == 0 ? '?' : '&');
            qs.Append(k); qs.Append('=');
            qs.Append(Uri.EscapeDataString(v));
        }
        if (limit is { } l) Add("limit", l.ToString());
        Add("cursor", cursor);
        Add("from", from);
        Add("to", to);
        Add("symbol", symbol);

        var req = new HttpRequestMessage(HttpMethod.Get, path + qs.ToString());
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        var nextCursor = body.TryGetProperty("nextCursor", out var nc) && nc.ValueKind == JsonValueKind.String
            ? nc.GetString() : null;
        return new HistoryPage(items, nextCursor);
    }

    private static async Task<string> SubmitOrder(
        HttpClient http, string token, int qty, decimal price, string symbol = "PETR4")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new
            {
                Symbol = symbol,
                SecurityId = 4321UL,
                Side = "Buy",
                Type = "Limit",
                Quantity = qty,
                Price = price,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("clOrdId").GetString()!;
    }

    private static async Task<string> SubmitGtdOrder(
        HttpClient http, string token, int qty, decimal price, DateTimeOffset goodTillDate,
        string symbol = "PETR4")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new
            {
                Symbol = symbol,
                SecurityId = 4321UL,
                Side = "Buy",
                Type = "Limit",
                Quantity = qty,
                Price = price,
                TimeInForce = "GTD",
                GoodTillDate = goodTillDate,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("clOrdId").GetString()!;
    }

    private static async Task<string> SubmitStopLimitOrder(
        HttpClient http, string token, int qty, decimal price, decimal stopPrice,
        string symbol = "PETR4")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new
            {
                Symbol = symbol,
                SecurityId = 4321UL,
                Side = "Buy",
                Type = "StopLimit",
                Quantity = qty,
                Price = price,
                StopPrice = stopPrice,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("clOrdId").GetString()!;
    }

    private static async Task<HttpResponseMessage> InjectEr(HttpClient http, string token, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/simulator/er")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        return resp;
    }
}
