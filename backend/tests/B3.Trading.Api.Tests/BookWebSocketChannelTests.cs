using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.MarketData;
using B3.Trading.Domain;
using Xunit;

namespace B3.Trading.Api.Tests;

public class BookWebSocketChannelTests
{
    private static (SubscriptionManager subs, MboBookStore store, WebSocketBookEventSink sink) Build()
    {
        var store = new MboBookStore();
        var subs = new SubscriptionManager(new WorkingOrderBook(), new PositionKeeper(), new AlgoBook());
        var sink = new WebSocketBookEventSink(subs, store);
        sink.StartAsync(default).GetAwaiter().GetResult();
        return (subs, store, sink);
    }

    private static MarketOrderAdded Add(string sym, ulong id, MarketBookSide side, decimal price, long qty) =>
        new(sym, 4321UL, id, side, price, qty, DateTimeOffset.UtcNow);

    [Fact]
    public void TryParsePublic_recognises_book_with_valid_symbol()
    {
        Assert.True(Channels.TryParsePublic("book.PETR4", out var k, out var s));
        Assert.Equal(PublicChannelKind.Book, k);
        Assert.Equal("PETR4", s);
    }

    [Theory]
    [InlineData("book.")]
    [InlineData("book.PETR 4")]
    [InlineData("book.PETR-4")]
    [InlineData("book.thisistoolongasymbolname")]
    public void TryParsePublic_rejects_invalid_book_inputs(string ch)
    {
        Assert.False(Channels.TryParsePublic(ch, out var k, out _));
        Assert.Equal(PublicChannelKind.None, k);
    }

    [Fact]
    public void Book_subscribe_emits_empty_snapshot_when_no_frame_seen()
    {
        var (subs, _, sink) = Build();
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "book.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Book, "PETR4"));

        Assert.True(client.Reader.TryRead(out var msg));
        Assert.Equal("snapshot", msg!.Type);
        Assert.Equal("book.PETR4", msg.Channel);
        var dto = Assert.IsType<L2TopOfBookDto>(msg.Data);
        Assert.Equal("PETR4", dto.Symbol);
        Assert.Null(dto.Bid);
        Assert.Null(dto.Ask);
        Assert.Null(dto.UpdatedUtc);
    }

    [Fact]
    public void Book_subscribe_then_OrderAdded_pushes_delta_with_new_top()
    {
        var (subs, store, sink) = Build();
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "book.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Book, "PETR4"));
        client.Reader.TryRead(out _); // discard cold snapshot

        store.ApplyAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 30.10m, 100));

        Assert.True(client.Reader.TryRead(out var delta));
        Assert.Equal("delta", delta!.Type);
        Assert.Equal("book.PETR4", delta.Channel);
        Assert.Equal(1, delta.Seq);
        var dto = Assert.IsType<L2TopOfBookDto>(delta.Data);
        Assert.Equal("PETR4", dto.Symbol);
        Assert.NotNull(dto.Bid);
        Assert.Equal(30.10m, dto.Bid!.Price);
        Assert.Equal(100, dto.Bid.TotalQty);
        Assert.Equal(1, dto.Bid.OrderCount);
        Assert.Null(dto.Ask);
    }

    [Fact]
    public void Book_subscribe_after_OrderAdded_serves_populated_snapshot()
    {
        var (subs, store, sink) = Build();
        store.ApplyAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 30.10m, 100));
        store.ApplyAdded(Add("PETR4", 2UL, MarketBookSide.Ask, 30.20m, 50));

        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "book.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Book, "PETR4"));

        Assert.True(client.Reader.TryRead(out var msg));
        Assert.Equal("snapshot", msg!.Type);
        var dto = Assert.IsType<L2TopOfBookDto>(msg.Data);
        Assert.Equal(30.10m, dto.Bid!.Price);
        Assert.Equal(30.20m, dto.Ask!.Price);
        Assert.Equal(50, dto.Ask.TotalQty);
    }

    [Fact]
    public void Book_coalesces_deep_book_updates_that_do_not_change_top()
    {
        var (subs, store, sink) = Build();
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "book.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Book, "PETR4"));
        client.Reader.TryRead(out _); // snapshot

        store.ApplyAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 30.10m, 100));
        Assert.True(client.Reader.TryRead(out _)); // first delta

        // Deeper bid at lower price — top of book unchanged.
        store.ApplyAdded(Add("PETR4", 2UL, MarketBookSide.Bid, 30.00m, 500));

        Assert.False(client.Reader.TryRead(out var maybe),
            $"expected coalesced (no delta) but got {maybe?.Type} {maybe?.Data}");
    }

    [Fact]
    public void Book_emits_only_to_subscribed_clients()
    {
        var (subs, store, sink) = Build();
        var alice = new SubscribedClient(new EndClientId("alice"), "TEST");
        var bob = new SubscribedClient(new EndClientId("bob"), "TEST");
        subs.SubscribePublicWithSnapshot(alice, "book.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Book, "PETR4"));
        alice.Reader.TryRead(out _);

        store.ApplyAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 30.10m, 100));

        Assert.True(alice.Reader.TryRead(out _));
        Assert.False(bob.Reader.TryRead(out _));
    }
}
