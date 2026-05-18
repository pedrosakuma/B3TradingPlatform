using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Api.WebSockets;
using B3.Trading.Api.WebSockets.DropCopy;
using B3.Trading.Application;
using B3.Trading.Application.Audit;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q4.6 (#306). End-to-end tests for the compliance drop-copy WS feed
/// (<c>/ws/dropcopy</c>). Driven through <see cref="TestAppFactory"/>
/// so each test exercises the real JWT handshake, the real WS
/// upgrade, and the real dispatcher / fan-out path.
/// </summary>
public class DropCopyWebSocketTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string Firm01 = "FIRM01";
    private const string Firm02 = "FIRM02";

    // 1. Compliance sees orders/fills/cancels submitted by OTHER users in the same firm.
    [Fact]
    public async Task ComplianceUser_SeesEventsFromOtherUsersInSameFirm()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var sink = factory.Services.GetRequiredService<IExecutionEventSink>();
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var book = factory.Services.GetRequiredService<WorkingOrderBook>();
        var alice = registry.Register("alice");
        var bob = registry.Register("bob");
        book.TryAdd(new Order(101UL, alice, "PETR4", 9001UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: Firm01));
        book.TryAdd(new Order(102UL, bob, "VALE3", 9002UL, OrderSide.Buy, OrderType.Limit, 200, 60m, firmId: Firm01));

        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http, "dave", TestAppFactory.TestPassword);

        using var ws = await ConnectDropCopyAsync(factory, token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Three snapshot frames (orders/fills/cancels), order between channels is implementation-defined.
        var snapByChannel = await ReadSnapshotsAsync(ws, 3, cts.Token);
        var ordersSnap = snapByChannel[DropCopyManager.DropCopyChannels.Orders];
        var snapItems = ordersSnap.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(2, snapItems.Length);

        // Fill on alice's order — should appear on orders + fills.
        sink.Publish(new ExecutionEvent(alice, 101UL, "PETR4", OrderSide.Buy,
            OrderStatus.Filled, ExecKind.Fill, 0, 100, 100, 30m, null, DateTimeOffset.UtcNow, FirmId: Firm01));

        // Cancel on bob's order — should appear on orders + cancels.
        sink.Publish(new ExecutionEvent(bob, 102UL, "VALE3", OrderSide.Buy,
            OrderStatus.Cancelled, ExecKind.Canceled, 0, 0, 0, 0m, null, DateTimeOffset.UtcNow, FirmId: Firm01));

        var observed = await DrainDeltasUntilAsync(ws, cts.Token, deadline: TimeSpan.FromSeconds(5),
            condition: bag => bag.Contains((DropCopyManager.DropCopyChannels.Fills, "101")) &&
                              bag.Contains((DropCopyManager.DropCopyChannels.Cancels, "102")) &&
                              bag.Contains((DropCopyManager.DropCopyChannels.Orders, "101")) &&
                              bag.Contains((DropCopyManager.DropCopyChannels.Orders, "102")));
        Assert.Contains((DropCopyManager.DropCopyChannels.Fills, "101"), observed);
        Assert.Contains((DropCopyManager.DropCopyChannels.Cancels, "102"), observed);
        Assert.Contains((DropCopyManager.DropCopyChannels.Orders, "101"), observed);
        Assert.Contains((DropCopyManager.DropCopyChannels.Orders, "102"), observed);
    }

    // 2. Compliance does NOT see orders from a different firm.
    // 9. Multi-firm: a single drop-copy stream for FIRM01 receives nothing for FIRM02.
    [Fact]
    public async Task ComplianceUser_DoesNotSeeOtherFirmTraffic()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var sink = factory.Services.GetRequiredService<IExecutionEventSink>();
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var bob = registry.Register("bob");

        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http, "dave", TestAppFactory.TestPassword);

        using var ws = await ConnectDropCopyAsync(factory, token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = await ReadSnapshotsAsync(ws, 3, cts.Token);

        // FIRM02 event for the same end-client — must NEVER surface on dave's FIRM01 socket.
        sink.Publish(new ExecutionEvent(bob, 999UL, "VALE3", OrderSide.Buy,
            OrderStatus.Filled, ExecKind.Fill, 0, 50, 50, 60m, null, DateTimeOffset.UtcNow, FirmId: Firm02));

        // And one FIRM01 event so we have something to wait for.
        sink.Publish(new ExecutionEvent(bob, 888UL, "PETR4", OrderSide.Buy,
            OrderStatus.Filled, ExecKind.Fill, 0, 100, 100, 30m, null, DateTimeOffset.UtcNow, FirmId: Firm01));

        var observed = await DrainDeltasUntilAsync(ws, cts.Token, deadline: TimeSpan.FromSeconds(5),
            condition: bag => bag.Contains((DropCopyManager.DropCopyChannels.Fills, "888")));
        Assert.Contains((DropCopyManager.DropCopyChannels.Fills, "888"), observed);
        Assert.DoesNotContain((DropCopyManager.DropCopyChannels.Fills, "999"), observed);
        Assert.DoesNotContain((DropCopyManager.DropCopyChannels.Orders, "999"), observed);
    }

    // 3. Snapshot+stream consistency: N pre-connect orders → snapshot N entries; M after → stream M; no dupes/no gaps.
    [Fact]
    public async Task SnapshotPlusStream_HasNoGapsAndNoDuplicates()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var book = factory.Services.GetRequiredService<WorkingOrderBook>();
        var sink = factory.Services.GetRequiredService<IExecutionEventSink>();
        var alice = registry.Register("alice");

        const int N = 5;
        const int M = 4;
        for (var i = 0; i < N; i++)
            book.TryAdd(new Order((ulong)(200 + i), alice, "PETR4", 9001UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: Firm01));

        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http, "dave", TestAppFactory.TestPassword);

        using var ws = await ConnectDropCopyAsync(factory, token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var snaps = await ReadSnapshotsAsync(ws, 3, cts.Token);
        var snapItems = snaps[DropCopyManager.DropCopyChannels.Orders].GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(N, snapItems.Length);
        // snapshot frame seq is 0
        Assert.Equal(0, snaps[DropCopyManager.DropCopyChannels.Orders].GetProperty("seq").GetInt64());

        // Push M more orders post-connect — each will fan-out one orders delta + one fills delta.
        for (var i = 0; i < M; i++)
        {
            book.TryAdd(new Order((ulong)(300 + i), alice, "PETR4", 9001UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: Firm01));
            sink.Publish(new ExecutionEvent(alice, (ulong)(300 + i), "PETR4", OrderSide.Buy,
                OrderStatus.Filled, ExecKind.Fill, 0, 100, 100, 30m, null, DateTimeOffset.UtcNow, FirmId: Firm01));
        }

        // Collect at least M orders.* deltas. The drop-copy stream may
        // emit an unrelated cancels delta = 0; we filter on the orders
        // channel for the consistency assertion.
        var orderSeqs = new List<long>();
        var orderClOrdIds = new List<string>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && orderSeqs.Count < M)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            readCts.CancelAfter(TimeSpan.FromMilliseconds(500));
            JsonElement msg;
            try { msg = await ReadJsonAsync(ws, readCts.Token); }
            catch (OperationCanceledException) { continue; }
            if (msg.GetProperty("type").GetString() != "delta") continue;
            if (msg.GetProperty("channel").GetString() != DropCopyManager.DropCopyChannels.Orders) continue;
            orderSeqs.Add(msg.GetProperty("seq").GetInt64());
            orderClOrdIds.Add(msg.GetProperty("data").GetProperty("clOrdId").GetString()!);
        }

        // Exactly M deltas, strictly monotonic 1..M, no dupes, no gaps.
        Assert.Equal(M, orderSeqs.Count);
        Assert.Equal(Enumerable.Range(1, M).Select(i => (long)i), orderSeqs);
        Assert.Equal(orderClOrdIds.Distinct().Count(), orderClOrdIds.Count);
    }

    // 4. Admin can pass ?firmId=FIRM02 and see FIRM02 drop-copy.
    [Fact]
    public async Task AdminWithFirmIdQuery_OverridesAndSeesTargetFirm()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var sink = factory.Services.GetRequiredService<IExecutionEventSink>();
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var bob = registry.Register("bob");

        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http, "admin", TestAppFactory.TestPassword);

        var wsClient = factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(factory.Server.BaseAddress)
        {
            Scheme = "ws",
            Path = "/ws/dropcopy",
            Query = $"access_token={token}&firmId={Firm02}",
        }.Uri;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ws = await wsClient.ConnectAsync(uri, cts.Token);
        _ = await ReadSnapshotsAsync(ws, 3, cts.Token);

        sink.Publish(new ExecutionEvent(bob, 777UL, "VALE3", OrderSide.Buy,
            OrderStatus.Filled, ExecKind.Fill, 0, 100, 100, 60m, null, DateTimeOffset.UtcNow, FirmId: Firm02));

        var observed = await DrainDeltasUntilAsync(ws, cts.Token, deadline: TimeSpan.FromSeconds(5),
            condition: bag => bag.Contains((DropCopyManager.DropCopyChannels.Fills, "777")));
        Assert.Contains((DropCopyManager.DropCopyChannels.Fills, "777"), observed);
    }

    // 5. Admin without ?firmId defaults to its own firm.
    [Fact]
    public async Task AdminWithoutFirmIdQuery_DefaultsToOwnFirm()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var sink = factory.Services.GetRequiredService<IExecutionEventSink>();
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var alice = registry.Register("alice");

        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http, "admin", TestAppFactory.TestPassword);

        using var ws = await ConnectDropCopyAsync(factory, token); // no firmId override
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = await ReadSnapshotsAsync(ws, 3, cts.Token);

        // admin's default firm in the seed is "default"; route an event
        // tagged with "default" so it must arrive.
        sink.Publish(new ExecutionEvent(alice, 555UL, "PETR4", OrderSide.Buy,
            OrderStatus.Filled, ExecKind.Fill, 0, 100, 100, 30m, null, DateTimeOffset.UtcNow, FirmId: "default"));

        var observed = await DrainDeltasUntilAsync(ws, cts.Token, deadline: TimeSpan.FromSeconds(5),
            condition: bag => bag.Contains((DropCopyManager.DropCopyChannels.Fills, "555")));
        Assert.Contains((DropCopyManager.DropCopyChannels.Fills, "555"), observed);
    }

    // 6. Compliance passing ?firmId is IGNORED (returns its own firm's drop-copy).
    [Fact]
    public async Task ComplianceFirmIdOverride_IsIgnored()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var sink = factory.Services.GetRequiredService<IExecutionEventSink>();
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var bob = registry.Register("bob");

        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http, "dave", TestAppFactory.TestPassword);

        var wsClient = factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(factory.Server.BaseAddress)
        {
            Scheme = "ws",
            Path = "/ws/dropcopy",
            Query = $"access_token={token}&firmId={Firm02}",
        }.Uri;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ws = await wsClient.ConnectAsync(uri, cts.Token);
        _ = await ReadSnapshotsAsync(ws, 3, cts.Token);

        // FIRM02 event must NOT arrive (override ignored — dave is FIRM01).
        sink.Publish(new ExecutionEvent(bob, 666UL, "VALE3", OrderSide.Buy,
            OrderStatus.Filled, ExecKind.Fill, 0, 100, 100, 60m, null, DateTimeOffset.UtcNow, FirmId: Firm02));
        // FIRM01 event must arrive.
        sink.Publish(new ExecutionEvent(bob, 667UL, "PETR4", OrderSide.Buy,
            OrderStatus.Filled, ExecKind.Fill, 0, 100, 100, 30m, null, DateTimeOffset.UtcNow, FirmId: Firm01));

        var observed = await DrainDeltasUntilAsync(ws, cts.Token, deadline: TimeSpan.FromSeconds(5),
            condition: bag => bag.Contains((DropCopyManager.DropCopyChannels.Fills, "667")));
        Assert.Contains((DropCopyManager.DropCopyChannels.Fills, "667"), observed);
        Assert.DoesNotContain((DropCopyManager.DropCopyChannels.Fills, "666"), observed);
    }

    // 7. Non-compliance/non-admin user is rejected (WS close 1008).
    [Fact]
    public async Task RegularUser_IsRejected_WithPolicyViolationClose()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http); // alice/user

        var wsClient = factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(factory.Server.BaseAddress)
        {
            Scheme = "ws",
            Path = "/ws/dropcopy",
            Query = $"access_token={token}",
        }.Uri;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // The server accepts then closes with PolicyViolation. ConnectAsync
        // returns successfully because the close is post-handshake; the
        // close frame surfaces on the first Receive.
        using var ws = await wsClient.ConnectAsync(uri, cts.Token);
        var buf = new byte[1024];
        var res = await ws.ReceiveAsync(buf, cts.Token);
        Assert.Equal(WebSocketMessageType.Close, res.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, res.CloseStatus);
    }

    // Anonymous (no token) is rejected at the JWT bearer layer.
    [Fact]
    public async Task AnonymousConnect_IsRejected()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var wsClient = factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws/dropcopy" }.Uri;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => wsClient.ConnectAsync(uri, cts.Token));
    }

    // 8. Audit emission: connect event lands in /admin/audit with role + firm.
    [Fact]
    public async Task DropCopyConnect_EmitsAuditEntry()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http, "dave", TestAppFactory.TestPassword);
        using var ws = await ConnectDropCopyAsync(factory, token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = await ReadSnapshotsAsync(ws, 3, cts.Token);

        var keeper = factory.Services.GetRequiredService<AuditLogKeeper>();
        var now = DateTimeOffset.UtcNow;
        // Audit emission is best-effort but synchronous through the
        // dispatcher; one keeper query is enough.
        AuditQueryResult result = default!;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            result = keeper.Query(now.AddMinutes(-1), now.AddMinutes(1), user: "dave",
                typePattern: AuditEventTypes.DropCopyConnect, outcome: null, limit: 50, cursorSeq: null);
            if (result.Entries.Count > 0) break;
            await Task.Delay(50, cts.Token);
        }
        Assert.NotEmpty(result.Entries);
        var entry = result.Entries[0];
        Assert.Equal(AuditEventTypes.DropCopyConnect, entry.EventType);
        Assert.Equal("compliance", entry.ActorRole);
        Assert.Equal(Firm01, entry.ActorFirm);
        Assert.NotNull(entry.Details);
        Assert.Equal(Firm01, entry.Details!["firmIdViewed"]);
    }

    // 10. Disconnect cleanup: closing the WS removes the subscriber (no leak).
    [Fact]
    public async Task Disconnect_RemovesSubscriber_NoLeak()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var manager = factory.Services.GetRequiredService<DropCopyManager>();
        Assert.Equal(0, manager.SubscriberCount(Firm01));

        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http, "dave", TestAppFactory.TestPassword);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ws = await ConnectDropCopyAsync(factory, token);
        _ = await ReadSnapshotsAsync(ws, 3, cts.Token);
        // Verify subscriber landed.
        await PollAsync(() => manager.SubscriberCount(Firm01) >= 1, TimeSpan.FromSeconds(2));
        Assert.Equal(1, manager.SubscriberCount(Firm01));

        // Client-initiated close. TestHost's WebSocket is sensitive to
        // ordering; an Abort is enough to flush the server's finally
        // block (which is what removes the subscriber) without racing
        // a graceful close handshake.
        ws.Abort();
        ws.Dispose();

        await PollAsync(() => manager.SubscriberCount(Firm01) == 0, TimeSpan.FromSeconds(5));
        Assert.Equal(0, manager.SubscriberCount(Firm01));
    }

    // ----------------- helpers -----------------

    private static async Task<WebSocket> ConnectDropCopyAsync(TestAppFactory factory, string token)
    {
        var wsClient = factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(factory.Server.BaseAddress)
        {
            Scheme = "ws",
            Path = "/ws/dropcopy",
            Query = $"access_token={token}",
        }.Uri;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await wsClient.ConnectAsync(uri, cts.Token);
    }

    private static async Task<Dictionary<string, JsonElement>> ReadSnapshotsAsync(WebSocket ws, int count, CancellationToken ct)
    {
        var bag = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var frame = await ReadJsonAsync(ws, ct);
            Assert.Equal("snapshot", frame.GetProperty("type").GetString());
            bag[frame.GetProperty("channel").GetString()!] = frame;
        }
        return bag;
    }

    private static async Task<HashSet<(string Channel, string ClOrdId)>> DrainDeltasUntilAsync(
        WebSocket ws,
        CancellationToken outerCt,
        TimeSpan deadline,
        Predicate<HashSet<(string Channel, string ClOrdId)>> condition)
    {
        var observed = new HashSet<(string, string)>();
        var endsAt = DateTime.UtcNow + deadline;
        while (DateTime.UtcNow < endsAt && !condition(observed))
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            readCts.CancelAfter(TimeSpan.FromMilliseconds(300));
            JsonElement msg;
            try { msg = await ReadJsonAsync(ws, readCts.Token); }
            catch (OperationCanceledException) { continue; }
            if (msg.GetProperty("type").GetString() != "delta") continue;
            var ch = msg.GetProperty("channel").GetString()!;
            var clOrdId = msg.GetProperty("data").GetProperty("clOrdId").GetString()!;
            observed.Add((ch, clOrdId));
        }
        return observed;
    }

    private static async Task<JsonElement> ReadJsonAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var sb = new StringBuilder();
        WebSocketReceiveResult res;
        do
        {
            res = await ws.ReceiveAsync(buf, ct);
            if (res.MessageType == WebSocketMessageType.Close)
                throw new OperationCanceledException("ws closed");
            sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
        } while (!res.EndOfMessage);
        return JsonSerializer.Deserialize<JsonElement>(sb.ToString());
    }

    private static async Task PollAsync(Func<bool> condition, TimeSpan timeout)
    {
        var endsAt = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < endsAt)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
    }
}
