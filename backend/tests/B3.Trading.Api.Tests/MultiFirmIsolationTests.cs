using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q4.2 (#302). End-to-end multi-firm isolation suite. PR #316 hardened
/// firm-scoping on the individual endpoints (orders, positions, modify,
/// cancel) — these tests cover the broader composition: three distinct
/// owners under three distinct firms (alice/FIRM01, bob/FIRM02,
/// charlie/FIRM03) interacting simultaneously through HTTP and WS, and
/// asserting that no firm sees another firm's orders, positions, P&amp;L
/// or live execution events.
/// </summary>
public class MultiFirmIsolationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string Firm01 = "FIRM01";
    private const string Firm02 = "FIRM02";
    private const string Firm03 = "FIRM03";

    [Fact]
    public async Task GetOrders_ThreeFirms_StrictlyIsolated()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var book = factory.Services.GetRequiredService<WorkingOrderBook>();
        var issuer = factory.Services.GetRequiredService<JwtIssuer>();

        var alice = registry.Register("alice");
        var bob = registry.Register("bob");
        var charlie = registry.Register("charlie");

        book.TryAdd(new Order(11UL, alice, "PETR4", 9001UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: Firm01));
        book.TryAdd(new Order(21UL, bob, "VALE3", 9002UL, OrderSide.Buy, OrderType.Limit, 200, 60m, firmId: Firm02));
        book.TryAdd(new Order(31UL, charlie, "ITUB4", 9003UL, OrderSide.Buy, OrderType.Limit, 300, 25m, firmId: Firm03));

        var client = factory.CreateClient();

        async Task AssertSingleOrderFor(string user, string firm, ulong expectedClOrdId)
        {
            var (token, _) = issuer.Issue(user, "user", firm);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var got = await client.GetFromJsonAsync<List<OrderDto>>("/orders/");
            Assert.NotNull(got);
            Assert.Single(got!);
            Assert.Equal(expectedClOrdId.ToString(), got![0].ClOrdId);
        }

        await AssertSingleOrderFor("alice", Firm01, 11UL);
        await AssertSingleOrderFor("bob", Firm02, 21UL);
        await AssertSingleOrderFor("charlie", Firm03, 31UL);
    }

    [Fact]
    public async Task GetPositions_ThreeFirms_StrictlyIsolated()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var positions = factory.Services.GetRequiredService<PositionKeeper>();
        var issuer = factory.Services.GetRequiredService<JwtIssuer>();

        var alice = registry.Register("alice");
        var bob = registry.Register("bob");
        var charlie = registry.Register("charlie");

        positions.ApplyFill(Firm01, alice, "PETR4", OrderSide.Buy, 100, 30m);
        positions.ApplyFill(Firm02, bob, "VALE3", OrderSide.Buy, 200, 60m);
        positions.ApplyFill(Firm03, charlie, "ITUB4", OrderSide.Buy, 300, 25m);

        var client = factory.CreateClient();

        async Task AssertSinglePositionFor(string user, string firm, string symbol, long qty)
        {
            var (token, _) = issuer.Issue(user, "user", firm);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var got = await client.GetFromJsonAsync<List<PositionDto>>("/positions");
            Assert.NotNull(got);
            Assert.Single(got!);
            Assert.Equal(symbol, got![0].Symbol);
            Assert.Equal(qty, got[0].NetQuantity);
        }

        await AssertSinglePositionFor("alice", Firm01, "PETR4", 100);
        await AssertSinglePositionFor("bob", Firm02, "VALE3", 200);
        await AssertSinglePositionFor("charlie", Firm03, "ITUB4", 300);
    }

    [Fact]
    public async Task PnlToday_ThreeFirms_StrictlyIsolated()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Risk:ReferencePrices:PETR4"] = "32",
            ["Trading:Risk:ReferencePrices:VALE3"] = "62",
            ["Trading:Risk:ReferencePrices:ITUB4"] = "26",
        });
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var positions = factory.Services.GetRequiredService<PositionKeeper>();
        var pnl = factory.Services.GetRequiredService<PnlKeeper>();
        var issuer = factory.Services.GetRequiredService<JwtIssuer>();

        var alice = registry.Register("alice");
        var bob = registry.Register("bob");
        var charlie = registry.Register("charlie");

        // Seed avg-cost basis so unrealized P&L is computable. The
        // ApplyFillToAvgCost call mirrors what ExecutionReportProcessor
        // would do for a real fill, including under the firm key.
        positions.ApplyFill(Firm01, alice, "PETR4", OrderSide.Buy, 100, 30m);
        pnl.ApplyFillToAvgCost(Firm01, "alice", "PETR4", OrderSide.Buy, 100, 30m);
        positions.ApplyFill(Firm02, bob, "VALE3", OrderSide.Buy, 200, 60m);
        pnl.ApplyFillToAvgCost(Firm02, "bob", "VALE3", OrderSide.Buy, 200, 60m);
        positions.ApplyFill(Firm03, charlie, "ITUB4", OrderSide.Buy, 300, 25m);
        pnl.ApplyFillToAvgCost(Firm03, "charlie", "ITUB4", OrderSide.Buy, 300, 25m);

        var client = factory.CreateClient();

        async Task AssertSingleSymbolUnrealizedFor(string user, string firm, string symbol)
        {
            var (token, _) = issuer.Issue(user, "user", firm);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var dto = await client.GetFromJsonAsync<PnlTodayDto>("/pnl/today");
            Assert.NotNull(dto);
            // Each firm's view must contain its own symbol — and ONLY
            // its own symbol — in the unrealized leg. The other firms'
            // basis lives in different (firmId, owner) keys and must
            // not project into this user's response.
            Assert.Single(dto!.Unrealized);
            Assert.Equal(symbol, dto.Unrealized[0].Symbol);
        }

        await AssertSingleSymbolUnrealizedFor("alice", Firm01, "PETR4");
        await AssertSingleSymbolUnrealizedFor("bob", Firm02, "VALE3");
        await AssertSingleSymbolUnrealizedFor("charlie", Firm03, "ITUB4");
    }

    [Fact]
    public async Task SameLogin_SpanningTwoFirms_SeesOnlyClaimedFirm()
    {
        // Sanity smoke around PR #316's "same JWT sub, two firms" case
        // — both firm tokens must isolate cleanly under the multi-firm
        // composition.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var book = factory.Services.GetRequiredService<WorkingOrderBook>();
        var issuer = factory.Services.GetRequiredService<JwtIssuer>();

        var alice = registry.Register("alice");
        book.TryAdd(new Order(411UL, alice, "PETR4", 9001UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: Firm01));
        book.TryAdd(new Order(412UL, alice, "PETR4", 9001UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: Firm02));

        var client = factory.CreateClient();
        var (t1, _) = issuer.Issue("alice", "user", Firm01);
        var (t2, _) = issuer.Issue("alice", "user", Firm02);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t1);
        var f1 = await client.GetFromJsonAsync<List<OrderDto>>("/orders/");
        Assert.NotNull(f1);
        Assert.Single(f1!);
        Assert.Equal("411", f1![0].ClOrdId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t2);
        var f2 = await client.GetFromJsonAsync<List<OrderDto>>("/orders/");
        Assert.NotNull(f2);
        Assert.Single(f2!);
        Assert.Equal("412", f2![0].ClOrdId);
    }

    [Fact]
    public async Task WebSocket_OrdersMe_FirmScoped_DoesNotLeakAcrossFirms()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var book = factory.Services.GetRequiredService<WorkingOrderBook>();
        var sink = factory.Services.GetRequiredService<IExecutionEventSink>();
        var issuer = factory.Services.GetRequiredService<JwtIssuer>();

        var bob = registry.Register("bob");
        // FIRM02's own working order; the order for FIRM01 below shares
        // the same end-client name "bob" but a different firm — it must
        // NOT surface on FIRM02's snapshot or delta channel.
        book.TryAdd(new Order(521UL, bob, "VALE3", 9002UL, OrderSide.Buy, OrderType.Limit, 200, 60m, firmId: Firm02));
        book.TryAdd(new Order(511UL, bob, "PETR4", 9001UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: Firm01));

        var (token, _) = issuer.Issue("bob", "user", Firm02);

        var wsClient = factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(factory.Server.BaseAddress)
        {
            Scheme = "ws",
            Path = "/ws",
            Query = $"access_token={token}",
        }.Uri;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var ws = await wsClient.ConnectAsync(uri, cts.Token);

        await SendJsonAsync(ws, new { type = "subscribe", channels = new[] { Channels.OrdersMe } }, cts.Token);

        var snap = await ReadJsonAsync(ws, cts.Token);
        Assert.Equal("snapshot", snap.GetProperty("type").GetString());
        Assert.Equal(Channels.OrdersMe, snap.GetProperty("channel").GetString());
        var items = snap.GetProperty("data").EnumerateArray().ToArray();
        // Exactly the FIRM02 order; the FIRM01 order — same JWT sub —
        // must NOT appear in the firm-scoped snapshot.
        Assert.Single(items);
        Assert.Equal("521", items[0].GetProperty("clOrdId").GetString());

        // Publish a cross-firm execution: FIRM01 event for the SAME
        // owner. WS subscription is scoped on (owner, firm) — FIRM02's
        // socket must NOT receive it.
        sink.Publish(new ExecutionEvent(
            bob, 511UL, "PETR4", OrderSide.Buy,
            OrderStatus.Working, ExecKind.New,
            LeavesQuantity: 100, CumulativeQuantity: 0,
            LastQuantity: 0, LastPrice: 0m,
            RejectReason: null,
            TimestampUtc: DateTimeOffset.UtcNow,
            FirmId: Firm01));

        // And a FIRM02 event for the same owner — this one must arrive.
        sink.Publish(new ExecutionEvent(
            bob, 521UL, "VALE3", OrderSide.Buy,
            OrderStatus.PartiallyFilled, ExecKind.PartialFill,
            LeavesQuantity: 100, CumulativeQuantity: 100,
            LastQuantity: 100, LastPrice: 60m,
            RejectReason: null,
            TimestampUtc: DateTimeOffset.UtcNow,
            FirmId: Firm02));

        // Read until we see the FIRM02 delta for clOrdId 521. If a
        // FIRM01 delta (clOrdId 511) ever arrives we fail immediately.
        // Bound the wait so a starved channel doesn't hang the test.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        var sawFirm02 = false;
        while (DateTime.UtcNow < deadline && !sawFirm02)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            readCts.CancelAfter(TimeSpan.FromMilliseconds(500));
            JsonElement msg;
            try { msg = await ReadJsonAsync(ws, readCts.Token); }
            catch (OperationCanceledException) { continue; }
            if (msg.GetProperty("type").GetString() != "delta") continue;
            var data = msg.GetProperty("data");
            // Delta data is a single DTO; snapshot data is an array.
            // Normalize to an enumerable so we can scan both.
            var entries = data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().ToArray()
                : new[] { data };
            foreach (var entry in entries)
            {
                var clOrd = entry.TryGetProperty("clOrdId", out var c) ? c.GetString() : null;
                // The cross-firm event for clOrdId 511 must NEVER
                // surface on this FIRM02-scoped socket.
                Assert.NotEqual("511", clOrd);
                if (clOrd == "521") sawFirm02 = true;
            }
        }
        Assert.True(sawFirm02, "Expected to receive a FIRM02 delta on orders.me but none arrived.");
    }

    [Fact]
    public async Task ConcurrentSubmission_AcrossThreeFirms_NoCrossFirmLeak()
    {
        // Stress test: each firm submits orders in a tight loop on a
        // dedicated thread; afterwards every firm's GET /orders must
        // list ONLY its own orders. Catches accidental shared mutable
        // state on the submission hot path that would let one firm's
        // order land in another firm's book.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var book = factory.Services.GetRequiredService<WorkingOrderBook>();
        var issuer = factory.Services.GetRequiredService<JwtIssuer>();

        var alice = registry.Register("alice");
        var bob = registry.Register("bob");
        var charlie = registry.Register("charlie");

        const int perFirm = 60;
        var alicePrefix = 100_000UL;
        var bobPrefix = 200_000UL;
        var charliePrefix = 300_000UL;

        Task SeedFirm(EndClientId owner, string firm, ulong prefix) => Task.Run(() =>
        {
            for (var i = 0; i < perFirm; i++)
            {
                book.TryAdd(new Order(
                    prefix + (ulong)i, owner, "PETR4",
                    9001UL + (ulong)i, OrderSide.Buy, OrderType.Limit,
                    100, 30m, firmId: firm));
            }
        });

        await Task.WhenAll(
            SeedFirm(alice, Firm01, alicePrefix),
            SeedFirm(bob, Firm02, bobPrefix),
            SeedFirm(charlie, Firm03, charliePrefix));

        var client = factory.CreateClient();

        async Task AssertFirmSees(string user, string firm, ulong prefix)
        {
            var (token, _) = issuer.Issue(user, "user", firm);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var got = await client.GetFromJsonAsync<List<OrderDto>>("/orders/");
            Assert.NotNull(got);
            Assert.Equal(perFirm, got!.Count);
            // Every ClOrdId must fall in this firm's prefix bucket —
            // if any order leaked across firms it would show up with
            // a different prefix.
            foreach (var o in got)
            {
                var id = ulong.Parse(o.ClOrdId);
                Assert.InRange(id, prefix, prefix + (ulong)perFirm - 1);
            }
        }

        await AssertFirmSees("alice", Firm01, alicePrefix);
        await AssertFirmSees("bob", Firm02, bobPrefix);
        await AssertFirmSees("charlie", Firm03, charliePrefix);
    }

    private static async Task SendJsonAsync(WebSocket ws, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var sb = new StringBuilder();
        WebSocketReceiveResult res;
        do
        {
            res = await ws.ReceiveAsync(buf, ct);
            sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
        } while (!res.EndOfMessage);
        return JsonSerializer.Deserialize<JsonElement>(sb.ToString());
    }
}
