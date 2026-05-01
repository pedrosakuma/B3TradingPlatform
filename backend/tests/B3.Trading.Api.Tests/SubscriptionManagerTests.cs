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

        var mgr = new SubscriptionManager(book, positions);
        var client = new SubscribedClient(owner);
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

        var mgr = new SubscriptionManager(book, positions);
        var client = new SubscribedClient(owner);
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

        var mgr = new SubscriptionManager(book, positions);
        var bobClient = new SubscribedClient(bob);
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

        var mgr = new SubscriptionManager(book, positions);
        var client = new SubscribedClient(owner);
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
    public void Fill_PublishesToAllThreeChannels()
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        positions.ApplyFill(owner, "PETR4", OrderSide.Buy, 100, 30m);
        order.MarkWorking();
        order.ApplyFill(100);

        var mgr = new SubscriptionManager(book, positions);
        var client = new SubscribedClient(owner);
        mgr.Add(client);
        mgr.SubscribeWithSnapshot(client, Channels.OrdersMe);
        mgr.SubscribeWithSnapshot(client, Channels.ExecutionsMe);
        mgr.SubscribeWithSnapshot(client, Channels.PositionsMe);
        // Drain 3 snapshots
        client.Reader.TryRead(out _);
        client.Reader.TryRead(out _);
        client.Reader.TryRead(out _);

        var sink = new WebSocketExecutionEventSink(mgr, book, positions);
        sink.Publish(new ExecutionEvent(
            owner, 1UL, "PETR4", OrderSide.Buy, order.Status, ExecKind.Fill,
            0, 100, 100, 30m, null, DateTimeOffset.UtcNow));

        var msgs = new List<OutboundMessage>();
        while (client.Reader.TryRead(out var m)) msgs.Add(m);
        var channels = msgs.Select(m => m.Channel).ToHashSet();
        Assert.Contains(Channels.OrdersMe, channels);
        Assert.Contains(Channels.ExecutionsMe, channels);
        Assert.Contains(Channels.PositionsMe, channels);
    }

    [Fact]
    public void Cancel_PublishesToOrdersAndExecutions_NotPositions()
    {
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        order.MarkCancelled();

        var mgr = new SubscriptionManager(book, positions);
        var client = new SubscribedClient(owner);
        mgr.Add(client);
        mgr.SubscribeWithSnapshot(client, Channels.OrdersMe);
        mgr.SubscribeWithSnapshot(client, Channels.ExecutionsMe);
        mgr.SubscribeWithSnapshot(client, Channels.PositionsMe);
        client.Reader.TryRead(out _);
        client.Reader.TryRead(out _);
        client.Reader.TryRead(out _);

        var sink = new WebSocketExecutionEventSink(mgr, book, positions);
        sink.Publish(new ExecutionEvent(
            owner, 1UL, "PETR4", OrderSide.Buy, order.Status, ExecKind.Canceled,
            0, 0, 0, 0m, null, DateTimeOffset.UtcNow));

        var msgs = new List<OutboundMessage>();
        while (client.Reader.TryRead(out var m)) msgs.Add(m);
        var channels = msgs.Select(m => m.Channel).ToHashSet();
        Assert.Contains(Channels.OrdersMe, channels);
        Assert.Contains(Channels.ExecutionsMe, channels);
        Assert.DoesNotContain(Channels.PositionsMe, channels);
    }
}
