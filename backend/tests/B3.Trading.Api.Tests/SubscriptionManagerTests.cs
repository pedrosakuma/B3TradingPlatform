using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.Api.Tests;

public class SubscriptionManagerTests
{
    [Fact]
    public void Subscribe_EnqueuesSnapshotWithSeqZero()
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var owner = new EndClientId("alice");
        book.TryAdd(new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m));

        var mgr = new SubscriptionManager(book, positions, new AlgoBook());
        var client = new SubscribedClient(owner, "TEST");
        mgr.Add(client);
        mgr.SubscribeWithSnapshot(client, Channels.OrdersMe);

        Assert.True(client.Reader.TryRead(out var msg));
        Assert.Equal("snapshot", msg!.Type);
        Assert.Equal(0, msg.Seq);
    }

    [Fact]
    public void Publish_EnqueuesDeltaWithMonotonicSeq()
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var owner = new EndClientId("alice");

        var mgr = new SubscriptionManager(book, positions, new AlgoBook());
        var client = new SubscribedClient(owner, "TEST");
        mgr.Add(client);
        mgr.SubscribeWithSnapshot(client, Channels.ExecutionsMe);
        client.Reader.TryRead(out _); // discard snapshot

        mgr.Publish(owner, Channels.ExecutionsMe, new { x = 1 });
        mgr.Publish(owner, Channels.ExecutionsMe, new { x = 2 });

        client.Reader.TryRead(out var d1);
        client.Reader.TryRead(out var d2);
        Assert.Equal(1, d1!.Seq);
        Assert.Equal(2, d2!.Seq);
    }

    [Fact]
    public void Publish_DoesNotFanOutToOtherOwners()
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var alice = new EndClientId("alice");
        var bob = new EndClientId("bob");

        var mgr = new SubscriptionManager(book, positions, new AlgoBook());
        var bobClient = new SubscribedClient(bob, "TEST");
        mgr.Add(bobClient);
        mgr.SubscribeWithSnapshot(bobClient, Channels.ExecutionsMe);
        bobClient.Reader.TryRead(out _); // snapshot

        mgr.Publish(alice, Channels.ExecutionsMe, new { x = 1 });

        Assert.False(bobClient.Reader.TryRead(out _));
    }

    [Fact]
    public void SlowConsumer_IsMarkedForDisconnectOnFullChannel()
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var owner = new EndClientId("alice");

        var mgr = new SubscriptionManager(book, positions, new AlgoBook());
        var client = new SubscribedClient(owner, "TEST");
        mgr.Add(client);
        mgr.SubscribeWithSnapshot(client, Channels.ExecutionsMe);

        for (var i = 0; i < SubscribedClient.OutboundCapacity + 100; i++)
            mgr.Publish(owner, Channels.ExecutionsMe, new { i });

        Assert.True(client.MarkedForDisconnect);
        Assert.Equal("slow_consumer_resync_required", client.DisconnectReason);
    }
}

public class WebSocketExecutionEventSinkTests
{
    [Fact]
    public async Task Fill_PublishesToAllThreeChannels()
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        positions.ApplyFill(owner, "PETR4", OrderSide.Buy, 100, 30m);
        order.MarkWorking();
        order.ApplyFill(100);

        var mgr = new SubscriptionManager(book, positions, new AlgoBook());
        var client = new SubscribedClient(owner, "TEST");
        mgr.Add(client);
        mgr.SubscribeWithSnapshot(client, Channels.OrdersMe);
        mgr.SubscribeWithSnapshot(client, Channels.ExecutionsMe);
        mgr.SubscribeWithSnapshot(client, Channels.PositionsMe);
        // Drain 3 snapshots
        client.Reader.TryRead(out _);
        client.Reader.TryRead(out _);
        client.Reader.TryRead(out _);

        var sink = new WebSocketExecutionEventSink(mgr, book, positions);
        await sink.StartAsync(default);
        try
        {
            sink.Publish(new ExecutionEvent(
                owner, 1UL, "PETR4", OrderSide.Buy, order.Status, ExecKind.Fill,
                0, 100, 100, 30m, null, DateTimeOffset.UtcNow));

            // RFC §5.2: the sink is channel-backed; wait briefly for the
            // drain task to consume the event and call into SubscriptionManager.
            var deadline = Environment.TickCount64 + 2_000;
            while (Environment.TickCount64 < deadline)
            {
                if (client.Reader.Count >= 3) break;
                await Task.Delay(5);
            }
        }
        finally { await sink.StopAsync(default); }

        var msgs = new List<OutboundMessage>();
        while (client.Reader.TryRead(out var m)) msgs.Add(m);
        var channels = msgs.Select(m => m.Channel).ToHashSet();
        Assert.Contains(Channels.OrdersMe, channels);
        Assert.Contains(Channels.ExecutionsMe, channels);
        Assert.Contains(Channels.PositionsMe, channels);
    }

    [Fact]
    public async Task Cancel_PublishesToOrdersAndExecutions_NotPositions()
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        order.MarkCancelled();

        var mgr = new SubscriptionManager(book, positions, new AlgoBook());
        var client = new SubscribedClient(owner, "TEST");
        mgr.Add(client);
        mgr.SubscribeWithSnapshot(client, Channels.OrdersMe);
        mgr.SubscribeWithSnapshot(client, Channels.ExecutionsMe);
        mgr.SubscribeWithSnapshot(client, Channels.PositionsMe);
        client.Reader.TryRead(out _);
        client.Reader.TryRead(out _);
        client.Reader.TryRead(out _);

        var sink = new WebSocketExecutionEventSink(mgr, book, positions);
        await sink.StartAsync(default);
        try
        {
            sink.Publish(new ExecutionEvent(
                owner, 1UL, "PETR4", OrderSide.Buy, order.Status, ExecKind.Canceled,
                0, 0, 0, 0m, null, DateTimeOffset.UtcNow));

            var deadline = Environment.TickCount64 + 2_000;
            while (Environment.TickCount64 < deadline)
            {
                if (client.Reader.Count >= 2) break;
                await Task.Delay(5);
            }
        }
        finally { await sink.StopAsync(default); }

        var msgs = new List<OutboundMessage>();
        while (client.Reader.TryRead(out var m)) msgs.Add(m);
        var channels = msgs.Select(m => m.Channel).ToHashSet();
        Assert.Contains(Channels.OrdersMe, channels);
        Assert.Contains(Channels.ExecutionsMe, channels);
        Assert.DoesNotContain(Channels.PositionsMe, channels);
    }

    [Fact]
    public void Publish_WithFirmId_DoesNotFanOutAcrossFirms()
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var owner = new EndClientId("alice");

        var mgr = new SubscriptionManager(book, positions, new AlgoBook());
        var firm1 = new SubscribedClient(owner, "FIRM01");
        var firm2 = new SubscribedClient(owner, "FIRM02");
        mgr.Add(firm1);
        mgr.Add(firm2);
        mgr.SubscribeWithSnapshot(firm1, Channels.ExecutionsMe);
        mgr.SubscribeWithSnapshot(firm2, Channels.ExecutionsMe);
        firm1.Reader.TryRead(out _);
        firm2.Reader.TryRead(out _);

        mgr.Publish(owner, "FIRM01", Channels.ExecutionsMe, new { x = 1 });

        Assert.True(firm1.Reader.TryRead(out var got));
        Assert.Equal(1, got!.Seq);
        Assert.False(firm2.Reader.TryRead(out _));
    }

    [Fact]
    public void SubscribeSnapshot_FiltersOrdersAndPositionsByFirm()
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var owner = new EndClientId("alice");

        var orderFirm1 = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: "FIRM01");
        var orderFirm2 = new Order(2UL, owner, "PETR4", 4322UL, OrderSide.Buy, OrderType.Limit, 200, 31m, firmId: "FIRM02");
        book.TryAdd(orderFirm1);
        book.TryAdd(orderFirm2);
        positions.ApplyFill("FIRM01", owner, "PETR4", OrderSide.Buy, 100, 30m);
        positions.ApplyFill("FIRM02", owner, "VALE3", OrderSide.Buy, 50, 60m);

        var mgr = new SubscriptionManager(book, positions, new AlgoBook());
        var client = new SubscribedClient(owner, "FIRM01");
        mgr.Add(client);
        mgr.SubscribeWithSnapshot(client, Channels.OrdersMe);
        mgr.SubscribeWithSnapshot(client, Channels.PositionsMe);

        Assert.True(client.Reader.TryRead(out var ordersSnap));
        Assert.True(client.Reader.TryRead(out var positionsSnap));

        var ordersJson = System.Text.Json.JsonSerializer.Serialize(ordersSnap!.Data);
        var posJson = System.Text.Json.JsonSerializer.Serialize(positionsSnap!.Data);
        Assert.Contains("4321", ordersJson);
        Assert.DoesNotContain("4322", ordersJson);
        Assert.Contains("PETR4", posJson);
        Assert.DoesNotContain("VALE3", posJson);
    }
}
