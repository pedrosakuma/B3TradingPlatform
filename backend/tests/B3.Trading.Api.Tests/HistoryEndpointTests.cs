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
