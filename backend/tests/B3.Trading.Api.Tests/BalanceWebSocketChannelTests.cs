using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.Api.Tests;

/// <summary>
/// #386. Coverage for the <c>balance.me</c> WS channel: snapshot on
/// subscribe + live deltas pushed by <see cref="WebSocketBalanceFanOut"/>.
/// </summary>
public class BalanceWebSocketChannelTests
{
    [Fact]
    public void Subscribe_EnqueuesBalanceSnapshot_ReflectingCurrentAvailable()
    {
        var cash = new CashLedger();
        var owner = new EndClientId("alice");
        cash.SeedIfAbsent(owner, 1_000m);

        var subs = new SubscriptionManager(
            new WorkingOrderBook(), new PositionKeeper(), new AlgoBook(), cash: cash);
        var client = new SubscribedClient(owner, "FIRM01");
        subs.Add(client);

        subs.SubscribeWithSnapshot(client, Channels.BalanceMe);

        Assert.True(client.Reader.TryRead(out var msg));
        Assert.Equal("snapshot", msg!.Type);
        Assert.Equal(Channels.BalanceMe, msg.Channel);
        var dto = Assert.IsType<BalanceDto>(msg.Data);
        Assert.Equal(1_000m, dto.Available);
    }

    [Fact]
    public void Subscribe_NoLedger_StillReturnsZeroSnapshot()
    {
        var owner = new EndClientId("alice");
        var subs = new SubscriptionManager(
            new WorkingOrderBook(), new PositionKeeper(), new AlgoBook());
        var client = new SubscribedClient(owner, "FIRM01");
        subs.Add(client);

        subs.SubscribeWithSnapshot(client, Channels.BalanceMe);

        Assert.True(client.Reader.TryRead(out var msg));
        var dto = Assert.IsType<BalanceDto>(msg!.Data);
        Assert.Equal(0m, dto.Available);
    }

    [Fact]
    public async Task FanOut_PublishesDelta_OnFill()
    {
        var cash = new CashLedger();
        var owner = new EndClientId("alice");
        cash.SeedIfAbsent(owner, 10_000m);

        var subs = new SubscriptionManager(
            new WorkingOrderBook(), new PositionKeeper(), new AlgoBook(), cash: cash);
        await using var fan = new WebSocketBalanceFanOut(cash, subs);
        await fan.StartAsync(CancellationToken.None);

        var client = new SubscribedClient(owner, "FIRM01");
        subs.Add(client);
        subs.SubscribeWithSnapshot(client, Channels.BalanceMe);
        client.Reader.TryRead(out _); // discard snapshot

        cash.ApplyFill(owner, OrderSide.Buy, 100, 30m); // -3000

        var delta = await ReadWithTimeoutAsync(client);
        Assert.Equal("delta", delta!.Type);
        Assert.Equal(Channels.BalanceMe, delta.Channel);
        Assert.Equal(1, delta.Seq);
        var dto = Assert.IsType<BalanceDto>(delta.Data);
        Assert.Equal(7_000m, dto.Available);

        await fan.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FanOut_PublishesDelta_OnFee()
    {
        var cash = new CashLedger();
        var owner = new EndClientId("alice");
        cash.SeedIfAbsent(owner, 500m);

        var subs = new SubscriptionManager(
            new WorkingOrderBook(), new PositionKeeper(), new AlgoBook(), cash: cash);
        await using var fan = new WebSocketBalanceFanOut(cash, subs);
        await fan.StartAsync(CancellationToken.None);

        var client = new SubscribedClient(owner, "FIRM01");
        subs.Add(client);
        subs.SubscribeWithSnapshot(client, Channels.BalanceMe);
        client.Reader.TryRead(out _); // snapshot

        cash.ApplyFee(owner, 12.34m);

        var delta = await ReadWithTimeoutAsync(client);
        var dto = Assert.IsType<BalanceDto>(delta!.Data);
        Assert.Equal(487.66m, dto.Available);

        await fan.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FanOut_CoalescesIdenticalAvailable_NoRedundantDelta()
    {
        var cash = new CashLedger();
        var owner = new EndClientId("alice");
        cash.SeedIfAbsent(owner, 100m);

        var subs = new SubscriptionManager(
            new WorkingOrderBook(), new PositionKeeper(), new AlgoBook(), cash: cash);
        await using var fan = new WebSocketBalanceFanOut(cash, subs);
        await fan.StartAsync(CancellationToken.None);

        var client = new SubscribedClient(owner, "FIRM01");
        subs.Add(client);
        subs.SubscribeWithSnapshot(client, Channels.BalanceMe);
        client.Reader.TryRead(out _); // snapshot

        // Same buy + sell at same price returns Available to 100, but
        // every fill raises BalanceChanged. We expect a delta for the
        // intermediate state (Buy: 90) and another back to 100 — both
        // distinct Available values so neither is coalesced. To test the
        // coalesce path, fire two mutations that DO collapse: two
        // ApplyFee(0) calls (no-op, no event) followed by one real fee.
        cash.ApplyFee(owner, 0m);
        cash.ApplyFee(owner, 0m);
        cash.ApplyFee(owner, 25m);

        var delta = await ReadWithTimeoutAsync(client);
        Assert.Equal(75m, ((BalanceDto)delta!.Data!).Available);

        // No extra delta should arrive
        await Task.Delay(100);
        Assert.False(client.Reader.TryRead(out _));

        await fan.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FanOut_OwnerScoped_DoesNotLeakAcrossOwners()
    {
        var cash = new CashLedger();
        var alice = new EndClientId("alice");
        var bob = new EndClientId("bob");
        cash.SeedIfAbsent(alice, 1_000m);
        cash.SeedIfAbsent(bob, 2_000m);

        var subs = new SubscriptionManager(
            new WorkingOrderBook(), new PositionKeeper(), new AlgoBook(), cash: cash);
        await using var fan = new WebSocketBalanceFanOut(cash, subs);
        await fan.StartAsync(CancellationToken.None);

        var bobClient = new SubscribedClient(bob, "FIRM01");
        subs.Add(bobClient);
        subs.SubscribeWithSnapshot(bobClient, Channels.BalanceMe);
        bobClient.Reader.TryRead(out _); // snapshot

        cash.ApplyFee(alice, 50m); // alice mutates; bob must not see it

        await Task.Delay(100);
        Assert.False(bobClient.Reader.TryRead(out _));

        await fan.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Channels_BalanceMe_IsRecognised()
    {
        Assert.Contains(Channels.BalanceMe, Channels.All);
    }

    private static async Task<OutboundMessage?> ReadWithTimeoutAsync(SubscribedClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (client.Reader.TryRead(out var msg)) return msg;
            await Task.Delay(10);
        }
        return null;
    }
}
