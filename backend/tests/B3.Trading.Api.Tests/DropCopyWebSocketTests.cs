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

    /// <summary>
    /// #435 Part B. Resolve the masked drop-copy form of a raw ClOrdId.
    /// The integration tests publish ExecutionEvents with raw ulong
    /// ClOrdIds; the drop-copy DTO emitted on the wire carries the
    /// opaque mask, so assertions need the masked equivalent.
    /// </summary>
    private static string Mask(TestAppFactory factory, string firmId, ulong clOrdId) =>
        factory.Services.GetRequiredService<IClOrdIdMasker>().MaskClOrdId(firmId, clOrdId);

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
            condition: bag => bag.Contains((DropCopyManager.DropCopyChannels.Fills, Mask(factory, Firm01, 101UL))) &&
                              bag.Contains((DropCopyManager.DropCopyChannels.Cancels, Mask(factory, Firm01, 102UL))) &&
                              bag.Contains((DropCopyManager.DropCopyChannels.Orders, Mask(factory, Firm01, 101UL))) &&
                              bag.Contains((DropCopyManager.DropCopyChannels.Orders, Mask(factory, Firm01, 102UL))));
        Assert.Contains((DropCopyManager.DropCopyChannels.Fills, Mask(factory, Firm01, 101UL)), observed);
        Assert.Contains((DropCopyManager.DropCopyChannels.Cancels, Mask(factory, Firm01, 102UL)), observed);
        Assert.Contains((DropCopyManager.DropCopyChannels.Orders, Mask(factory, Firm01, 101UL)), observed);
        Assert.Contains((DropCopyManager.DropCopyChannels.Orders, Mask(factory, Firm01, 102UL)), observed);
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
            condition: bag => bag.Contains((DropCopyManager.DropCopyChannels.Fills, Mask(factory, Firm01, 888UL))));
        Assert.Contains((DropCopyManager.DropCopyChannels.Fills, Mask(factory, Firm01, 888UL)), observed);
        Assert.DoesNotContain((DropCopyManager.DropCopyChannels.Fills, Mask(factory, Firm02, 999UL)), observed);
        Assert.DoesNotContain((DropCopyManager.DropCopyChannels.Orders, Mask(factory, Firm02, 999UL)), observed);
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
            condition: bag => bag.Contains((DropCopyManager.DropCopyChannels.Fills, Mask(factory, Firm02, 777UL))));
        Assert.Contains((DropCopyManager.DropCopyChannels.Fills, Mask(factory, Firm02, 777UL)), observed);
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
            condition: bag => bag.Contains((DropCopyManager.DropCopyChannels.Fills, Mask(factory, "default", 555UL))));
        Assert.Contains((DropCopyManager.DropCopyChannels.Fills, Mask(factory, "default", 555UL)), observed);
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
            condition: bag => bag.Contains((DropCopyManager.DropCopyChannels.Fills, Mask(factory, Firm01, 667UL))));
        Assert.Contains((DropCopyManager.DropCopyChannels.Fills, Mask(factory, Firm01, 667UL)), observed);
        Assert.DoesNotContain((DropCopyManager.DropCopyChannels.Fills, Mask(factory, Firm02, 666UL)), observed);
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

    // 11. Pass-3 review (#323): sink overflow must fail-closed by
    //     disconnecting every active subscriber so they re-snapshot.
    [Fact]
    public void DisconnectAllForResync_MarksEverySubscriberForReconnect()
    {
        var book = new WorkingOrderBook();
        var manager = new DropCopyManager(book, NullClOrdIdMasker.Instance);

        var a = new DropCopyClient(Firm01, "dave", "compliance");
        var b = new DropCopyClient(Firm01, "eve", "admin");
        var c = new DropCopyClient(Firm02, "frank", "compliance");
        manager.Add(a);
        manager.Add(b);
        manager.Add(c);

        manager.DisconnectAllForResync("drop_copy_sink_overflow_resync_required");

        Assert.True(a.MarkedForDisconnect);
        Assert.True(b.MarkedForDisconnect);
        Assert.True(c.MarkedForDisconnect);
        Assert.Equal("drop_copy_sink_overflow_resync_required", a.DisconnectReason);
        Assert.Equal("drop_copy_sink_overflow_resync_required", b.DisconnectReason);
        Assert.Equal("drop_copy_sink_overflow_resync_required", c.DisconnectReason);
        // Signal trips so hub teardown observes it.
        Assert.True(a.DisconnectRequested.IsCompleted);
        Assert.True(b.DisconnectRequested.IsCompleted);
        Assert.True(c.DisconnectRequested.IsCompleted);
    }

    // 12. Pass-5 review (#323): repeated overflow drops in the same
    //     burst must coalesce into a single disconnect walk; a fresh
    //     subscriber added after the burst must re-arm the gate so a
    //     later burst's walk reaches it.
    [Fact]
    public void DisconnectAllForResync_CoalescesWithinBurst_ReArmsOnAdd()
    {
        var book = new WorkingOrderBook();
        var manager = new DropCopyManager(book, NullClOrdIdMasker.Instance);

        var a = new DropCopyClient(Firm01, "dave", "compliance");
        manager.Add(a);

        // First call disconnects a, then "consumes" the armed gate.
        manager.DisconnectAllForResync("first");
        Assert.True(a.MarkedForDisconnect);
        Assert.Equal("first", a.DisconnectReason);

        // Subsequent calls within the same burst are no-ops on a fresh
        // subscriber added without Add() (b is NOT registered) — the
        // gate is consumed so the walk shouldn't touch anyone.
        var b = new DropCopyClient(Firm01, "eve", "admin");
        // b not added, so even if we walked, we'd skip it. Add a real
        // post-burst subscriber to validate re-arm.
        manager.DisconnectAllForResync("second_in_burst");
        manager.DisconnectAllForResync("third_in_burst");
        // a's reason stays "first" (RequestResyncDisconnect is idempotent).
        Assert.Equal("first", a.DisconnectReason);

        // Re-arm via Add() — a new burst should now disconnect c.
        var c = new DropCopyClient(Firm02, "frank", "compliance");
        manager.Add(c);
        manager.DisconnectAllForResync("second_burst");
        Assert.True(c.MarkedForDisconnect);
        Assert.Equal("second_burst", c.DisconnectReason);
    }

    // 13. Pass-6 review (#323): registration + per-firm arm must be
    //     atomic with the disconnect walk's consume. Stress test:
    //     interleave Add() with concurrent DisconnectAllForResync()
    //     and assert no client added at time T can survive a drop at
    //     time T'>T without being marked for disconnect.
    [Fact]
    public async Task ConcurrentAddAndDisconnectAllForResync_NeverLeavesClientUnmarked()
    {
        var book = new WorkingOrderBook();
        var manager = new DropCopyManager(book, NullClOrdIdMasker.Instance);
        var clients = new List<DropCopyClient>(capacity: 500);
        var cts = new CancellationTokenSource();

        var dropWalker = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
                manager.DisconnectAllForResync("race_drop");
        });

        for (int i = 0; i < 500; i++)
        {
            var c = new DropCopyClient($"FIRM{i % 5:00}", $"user{i}", "compliance");
            clients.Add(c);
            manager.Add(c);
        }
        // Let the walker observe one more burst after the last Add.
        manager.DisconnectAllForResync("final");
        await Task.Delay(50);
        cts.Cancel();
        await dropWalker;
        // Final pass to drain anything armed after Cancel.
        manager.DisconnectAllForResync("post_cancel_drain");

        foreach (var c in clients)
            Assert.True(c.MarkedForDisconnect, $"client {c.Username} for firm {c.FirmId} was not marked");
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

    // 11. P1 regression — concurrent subscribe must not drop deltas (atomicity).
    //
    // Pre-fix the manager read the per-firm subscriber set BEFORE
    // taking the per-firm lock, so a Publish racing an Add() could
    // iterate a stale snapshot (no new subscriber) and silently drop
    // the live delta — the new subscriber received its snapshot but
    // missed the delta that crossed the registration boundary.
    //
    // The fix moves the subscriber-set read inside the lock. This test
    // hammers the race: a worker continuously publishes deltas to
    // FIRM01 while a separate task registers a new subscriber. After
    // Add() returns we keep publishing for a small window; every
    // delta emitted in that post-registration window MUST land in the
    // new subscriber's outbound channel (no gap). The test runs many
    // iterations to reliably catch the race window if the fix is
    // reverted.
    [Fact]
    public async Task DropCopyManager_Add_AtomicWithConcurrentPublish_NoDeltaGap()
    {
        const int Iterations = 200;
        const string Firm = Firm01;
        const string Channel = DropCopyManager.DropCopyChannels.Orders;

        for (var iter = 0; iter < Iterations; iter++)
        {
            var book = new WorkingOrderBook();
            var manager = new DropCopyManager(book, NullClOrdIdMasker.Instance);
            var client = new DropCopyClient(Firm, "compliance-" + iter, "compliance");

            long published = 0;
            long stop = 0;

            var publisher = Task.Run(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    var seq = Interlocked.Increment(ref published);
                    manager.Publish(Firm, Channel, seq);
                    // Keep the channel from overflowing across iterations
                    // (DropCopyClient.OutboundCapacity = 4096): the
                    // post-Add window is intentionally short.
                    if (seq > DropCopyClient.OutboundCapacity - 64) break;
                }
            });

            // Let the publisher spin up so we're truly racing.
            Thread.SpinWait(2_000);

            manager.Add(client);

            // Boundary: any seq strictly greater than this was assigned
            // by an Interlocked.Increment that happened-after Add()
            // returned, so its Publish() call entered after Add's lock
            // was released. The fix guarantees every such delta lands
            // on the new subscriber. With the pre-fix code, a
            // Publish() that had already read the stale subscriber
            // set could silently iterate it under the lock acquired
            // after Add — and the matching delta would never reach
            // the client.
            //
            // Note we cannot use the strict "Publish entered after
            // Add returned" boundary directly: in the pre-fix code
            // the race window is exactly the period during which
            // Publish has captured the stale set but not yet locked.
            // The simplest deterministic check is to publish a few
            // more items AFTER Add returns and require the client to
            // observe them — this still exercises the path that was
            // broken, because the publisher's pre-Add captures may
            // still be racing for the lock when these post-Add items
            // are issued.
            const int PostAddDeltas = 32;
            var postAddSeqs = new long[PostAddDeltas];
            for (var i = 0; i < PostAddDeltas; i++)
            {
                var seq = Interlocked.Increment(ref published);
                postAddSeqs[i] = seq;
                manager.Publish(Firm, Channel, seq);
            }

            Volatile.Write(ref stop, 1);
            await publisher;
            client.Complete();

            var seen = new HashSet<long>();
            await foreach (var msg in client.Reader.ReadAllAsync())
            {
                if (msg.Type != "delta") continue;
                if (msg.Data is long s) seen.Add(s);
            }

            foreach (var s in postAddSeqs)
            {
                Assert.True(
                    seen.Contains(s),
                    $"iter {iter}: post-registration delta seq={s} was dropped — Publish-vs-Add race regression.");
            }

            // Sanity: the client must not have been disconnected for
            // channel overflow; that would mask a real gap by
            // converting it to a slow-consumer drop. The publisher
            // bounds itself well under OutboundCapacity.
            Assert.False(client.MarkedForDisconnect,
                $"iter {iter}: client unexpectedly marked for disconnect (channel overflow); test bound too loose.");
        }
    }

    // 12. P2 regression — a slow consumer (idle peer that never reads
    // and never sends) must still be torn down promptly: when the
    // outbound channel fills, the send loop marks the client for
    // disconnect, but pre-fix the hub then awaited Task.WhenAll(send,
    // recv) — and the receive loop blocked forever on ReceiveAsync
    // for an idle peer, so manager.Remove(client) was never called
    // and the subscriber leaked.
    //
    // The fix wires both loops through a linked CTS, cancels it after
    // WhenAny, and aborts the socket so the receive loop unblocks.
    // We assert the subscriber count returns to zero within a
    // reasonable timeout once the channel fills.
    [Fact]
    public async Task SlowConsumer_FillsChannel_SubscriberRemovedWithoutLeak()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var manager = factory.Services.GetRequiredService<DropCopyManager>();
        var sink = factory.Services.GetRequiredService<IExecutionEventSink>();
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var book = factory.Services.GetRequiredService<WorkingOrderBook>();
        var alice = registry.Register("alice");

        Assert.Equal(0, manager.SubscriberCount(Firm01));

        using var http = factory.CreateClient();
        var token = await factory.LoginAsync(http, "dave", TestAppFactory.TestPassword);

        // Connect but DO NOT read — this is the slow / idle consumer.
        // We don't call ReadSnapshotsAsync; we don't pump the WS at all.
        var ws = await ConnectDropCopyAsync(factory, token);

        // Wait for the subscriber to land in the manager.
        await PollAsync(() => manager.SubscriberCount(Firm01) >= 1, TimeSpan.FromSeconds(5));
        Assert.Equal(1, manager.SubscriberCount(Firm01));

        // Submit far more events than the outbound channel can hold.
        // Each TryAdd + Publish fans out one orders delta + one fills
        // delta, so 2× per event lands on the outbound channel.
        // DropCopyClient.OutboundCapacity = 4096; we submit 8000
        // events to guarantee the bounded channel saturates and
        // Enqueue marks the client for disconnect.
        var ordersCount = DropCopyClient.OutboundCapacity * 2 + 1000;
        for (var i = 0; i < ordersCount; i++)
        {
            var clOrdId = (ulong)(10_000 + i);
            book.TryAdd(new Order(clOrdId, alice, "PETR4", 9001UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: Firm01));
            sink.Publish(new ExecutionEvent(alice, clOrdId, "PETR4", OrderSide.Buy,
                OrderStatus.Filled, ExecKind.Fill, 0, 100, 100, 30m, null, DateTimeOffset.UtcNow, FirmId: Firm01));
        }

        // The send loop will detect the full channel inside Enqueue,
        // mark the client for disconnect, complete the writer, and
        // exit. The hub's finally block (post-fix) must cancel the
        // linked CTS and abort the socket so the receive loop also
        // exits — only THEN does manager.Remove(client) run.
        await PollAsync(() => manager.SubscriberCount(Firm01) == 0, TimeSpan.FromSeconds(15));
        Assert.Equal(0, manager.SubscriberCount(Firm01));

        try { ws.Abort(); } catch { /* best-effort */ }
        ws.Dispose();
    }
}
