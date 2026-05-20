using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.MarketData;
using B3.Trading.Domain;
using Microsoft.Extensions.Options;
using Xunit;

namespace B3.Trading.Api.Tests;

public class MboBookWebSocketChannelTests
{
    private sealed class FakeMboSource : IMboBookEventSource
    {
        public event Action<MarketBookSnapshot>? BookSnapshot;
        public event Action<MarketOrderAdded>? OrderAdded;
        public event Action<MarketOrderUpdated>? OrderUpdated;
        public event Action<MarketOrderDeleted>? OrderDeleted;
        public event Action<MarketBookCleared>? BookCleared;

        public void RaiseSnapshot(MarketBookSnapshot s) => BookSnapshot?.Invoke(s);
        public void RaiseAdded(MarketOrderAdded a) => OrderAdded?.Invoke(a);
        public void RaiseUpdated(MarketOrderUpdated u) => OrderUpdated?.Invoke(u);
        public void RaiseDeleted(MarketOrderDeleted d) => OrderDeleted?.Invoke(d);
        public void RaiseCleared(MarketBookCleared c) => BookCleared?.Invoke(c);
    }

    private static (SubscriptionManager subs, FakeMboSource src, WebSocketMboBookEventSink sink) Build(bool enableBook = true)
    {
        var src = new FakeMboSource();
        var subs = new SubscriptionManager(new WorkingOrderBook(), new PositionKeeper(), new AlgoBook());
        var opts = Options.Create(new MarketDataOptions { EnableBook = enableBook });
        var sink = new WebSocketMboBookEventSink(subs, src, opts);
        sink.StartAsync(default).GetAwaiter().GetResult();
        return (subs, src, sink);
    }

    private static MarketOrderAdded Add(string sym, ulong id, MarketBookSide side, decimal price, long qty) =>
        new(sym, 4321UL, id, side, price, qty, DateTimeOffset.UtcNow);

    // ── Channel parsing ────────────────────────────────────────────

    [Fact]
    public void TryParsePublic_recognises_bookmbo_with_valid_symbol()
    {
        Assert.True(Channels.TryParsePublic("bookmbo.PETR4", out var k, out var s));
        Assert.Equal(PublicChannelKind.BookMbo, k);
        Assert.Equal("PETR4", s);
    }

    [Theory]
    [InlineData("bookmbo.")]
    [InlineData("bookmbo.PETR 4")]
    [InlineData("bookmbo.PETR-4")]
    [InlineData("bookmbo.thisistoolongasymbolname")]
    public void TryParsePublic_rejects_invalid_bookmbo_inputs(string ch)
    {
        Assert.False(Channels.TryParsePublic(ch, out var k, out _));
        Assert.Equal(PublicChannelKind.None, k);
    }

    [Fact]
    public void TryParsePublic_does_not_confuse_bookmbo_with_book()
    {
        // bookmbo.X must NOT parse as Book("mbo.X") — explicit prefix
        // discrimination is what makes the two channels coexist.
        Assert.True(Channels.TryParsePublic("bookmbo.PETR4", out var k1, out _));
        Assert.Equal(PublicChannelKind.BookMbo, k1);

        Assert.True(Channels.TryParsePublic("book.PETR4", out var k2, out _));
        Assert.Equal(PublicChannelKind.Book, k2);
    }

    // ── Cold snapshot ──────────────────────────────────────────────

    [Fact]
    public void Subscribe_emits_empty_snapshot_when_no_frame_seen()
    {
        var (subs, _, sink) = Build();
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "bookmbo.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.BookMbo, "PETR4"));

        Assert.True(client.Reader.TryRead(out var msg));
        Assert.Equal("snapshot", msg!.Type);
        Assert.Equal("bookmbo.PETR4", msg.Channel);
        var dto = Assert.IsType<MboBookSnapshotDto>(msg.Data);
        Assert.Equal("PETR4", dto.Symbol);
        Assert.Empty(dto.Bids);
        Assert.Empty(dto.Asks);
        Assert.Null(dto.UpdatedUtc);
        Assert.Null(dto.Sequence);
    }

    // ── Deltas: add/update/delete/cleared ──────────────────────────

    [Fact]
    public void OrderAdded_pushes_added_delta_per_order()
    {
        var (subs, src, sink) = Build();
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "bookmbo.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.BookMbo, "PETR4"));
        client.Reader.TryRead(out _); // discard cold snapshot

        src.RaiseAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 30.10m, 100));

        Assert.True(client.Reader.TryRead(out var msg));
        Assert.Equal("delta", msg!.Type);
        Assert.Equal("bookmbo.PETR4", msg.Channel);
        Assert.Equal(1, msg.Seq);
        var dto = Assert.IsType<MboBookDeltaDto>(msg.Data);
        Assert.Equal("added", dto.Kind);
        Assert.Equal("PETR4", dto.Symbol);
        Assert.Equal("1", dto.OrderId);
        Assert.Equal("bid", dto.Side);
        Assert.Equal(30.10m, dto.Price);
        Assert.Equal(100, dto.Qty);
    }

    [Fact]
    public void OrderUpdated_pushes_updated_delta()
    {
        var (subs, src, sink) = Build();
        src.RaiseAdded(Add("PETR4", 7UL, MarketBookSide.Ask, 31m, 50));
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "bookmbo.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.BookMbo, "PETR4"));
        client.Reader.TryRead(out _); // snapshot has the original order

        src.RaiseUpdated(new MarketOrderUpdated("PETR4", 4321UL, 7UL,
            MarketBookSide.Ask, 31m, 30, DateTimeOffset.UtcNow));

        Assert.True(client.Reader.TryRead(out var msg));
        var dto = Assert.IsType<MboBookDeltaDto>(msg!.Data);
        Assert.Equal("updated", dto.Kind);
        Assert.Equal("7", dto.OrderId);
        Assert.Equal("ask", dto.Side);
        Assert.Equal(30, dto.Qty);
    }

    [Fact]
    public void OrderDeleted_pushes_deleted_delta_with_no_price_qty()
    {
        var (subs, src, sink) = Build();
        src.RaiseAdded(Add("PETR4", 9UL, MarketBookSide.Bid, 29m, 200));
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "bookmbo.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.BookMbo, "PETR4"));
        client.Reader.TryRead(out _);

        src.RaiseDeleted(new MarketOrderDeleted("PETR4", 4321UL, 9UL,
            MarketBookSide.Bid, DateTimeOffset.UtcNow));

        Assert.True(client.Reader.TryRead(out var msg));
        var dto = Assert.IsType<MboBookDeltaDto>(msg!.Data);
        Assert.Equal("deleted", dto.Kind);
        Assert.Equal("9", dto.OrderId);
        Assert.Null(dto.Price);
        Assert.Null(dto.Qty);
    }

    [Fact]
    public void BookCleared_both_sides_emits_null_side()
    {
        var (subs, src, sink) = Build();
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "bookmbo.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.BookMbo, "PETR4"));
        client.Reader.TryRead(out _);

        src.RaiseCleared(new MarketBookCleared("PETR4", 4321UL,
            MarketBookClearSide.Both, DateTimeOffset.UtcNow));

        Assert.True(client.Reader.TryRead(out var msg));
        var dto = Assert.IsType<MboBookDeltaDto>(msg!.Data);
        Assert.Equal("cleared", dto.Kind);
        Assert.Null(dto.Side);
    }

    [Fact]
    public void BookCleared_bid_only_emits_bid_side()
    {
        var (subs, src, sink) = Build();
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "bookmbo.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.BookMbo, "PETR4"));
        client.Reader.TryRead(out _);

        src.RaiseCleared(new MarketBookCleared("PETR4", 4321UL,
            MarketBookClearSide.Bid, DateTimeOffset.UtcNow));

        Assert.True(client.Reader.TryRead(out var msg));
        var dto = Assert.IsType<MboBookDeltaDto>(msg!.Data);
        Assert.Equal("cleared", dto.Kind);
        Assert.Equal("bid", dto.Side);
    }

    // ── Snapshot semantics ─────────────────────────────────────────

    [Fact]
    public void Snapshot_reflects_applied_state_after_add_update_delete()
    {
        var (subs, src, sink) = Build();
        src.RaiseAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 30m, 100));
        src.RaiseAdded(Add("PETR4", 2UL, MarketBookSide.Bid, 30.10m, 200));
        src.RaiseAdded(Add("PETR4", 3UL, MarketBookSide.Ask, 30.20m, 50));
        src.RaiseUpdated(new MarketOrderUpdated("PETR4", 4321UL, 1UL,
            MarketBookSide.Bid, 30m, 80, DateTimeOffset.UtcNow));
        src.RaiseDeleted(new MarketOrderDeleted("PETR4", 4321UL, 3UL,
            MarketBookSide.Ask, DateTimeOffset.UtcNow));

        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "bookmbo.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.BookMbo, "PETR4"));

        Assert.True(client.Reader.TryRead(out var msg));
        var dto = Assert.IsType<MboBookSnapshotDto>(msg!.Data);
        Assert.Equal(2, dto.Bids.Count);
        // Bids sorted best-first (descending price).
        Assert.Equal(30.10m, dto.Bids[0].Price);
        Assert.Equal(200, dto.Bids[0].Qty);
        Assert.Equal("2", dto.Bids[0].OrderId);
        Assert.Equal(30m, dto.Bids[1].Price);
        Assert.Equal(80, dto.Bids[1].Qty); // updated from 100 to 80
        Assert.Empty(dto.Asks); // order 3 was deleted
    }

    [Fact]
    public void Snapshot_after_BookSnapshot_rebuilds_state()
    {
        var (subs, src, sink) = Build();
        // Establish some prior state…
        src.RaiseAdded(Add("PETR4", 99UL, MarketBookSide.Bid, 10m, 10));
        // …then a fresh snapshot must wipe it.
        src.RaiseSnapshot(new MarketBookSnapshot
        {
            Symbol = "PETR4",
            SecurityId = 4321UL,
            RptSeq = 42,
            Bids = new[] { new MarketBookOrder(1UL, 30m, 100) },
            Asks = new[] { new MarketBookOrder(2UL, 31m, 50) },
            ReceivedUtc = DateTimeOffset.UtcNow,
        });

        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "bookmbo.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.BookMbo, "PETR4"));

        Assert.True(client.Reader.TryRead(out var msg));
        var dto = Assert.IsType<MboBookSnapshotDto>(msg!.Data);
        Assert.Equal(42L, dto.Sequence);
        Assert.Single(dto.Bids);
        Assert.Equal("1", dto.Bids[0].OrderId);
        Assert.Single(dto.Asks);
        Assert.Equal("2", dto.Asks[0].OrderId);
    }

    [Fact]
    public void Snapshot_after_BookCleared_is_empty_for_that_side()
    {
        var (subs, src, sink) = Build();
        src.RaiseAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 30m, 100));
        src.RaiseAdded(Add("PETR4", 2UL, MarketBookSide.Ask, 31m, 50));
        src.RaiseCleared(new MarketBookCleared("PETR4", 4321UL,
            MarketBookClearSide.Ask, DateTimeOffset.UtcNow));

        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "bookmbo.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.BookMbo, "PETR4"));

        Assert.True(client.Reader.TryRead(out var msg));
        var dto = Assert.IsType<MboBookSnapshotDto>(msg!.Data);
        Assert.Single(dto.Bids);
        Assert.Empty(dto.Asks);
    }

    // ── EnableBook=false ───────────────────────────────────────────

    [Fact]
    public void When_EnableBook_off_deltas_are_dropped()
    {
        var (subs, src, sink) = Build(enableBook: false);
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "bookmbo.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.BookMbo, "PETR4"));
        client.Reader.TryRead(out _); // snapshot (empty)

        src.RaiseAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 30m, 100));

        Assert.False(client.Reader.TryRead(out _));
    }

    // ── Multi-subscriber fan-out ───────────────────────────────────

    [Fact]
    public void Multiple_subscribers_each_receive_the_same_delta()
    {
        var (subs, src, sink) = Build();
        var c1 = new SubscribedClient(new EndClientId("alice"), "A");
        var c2 = new SubscribedClient(new EndClientId("bob"), "B");
        subs.SubscribePublicWithSnapshot(c1, "bookmbo.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.BookMbo, "PETR4"));
        subs.SubscribePublicWithSnapshot(c2, "bookmbo.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.BookMbo, "PETR4"));
        c1.Reader.TryRead(out _);
        c2.Reader.TryRead(out _);

        src.RaiseAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 30m, 100));

        Assert.True(c1.Reader.TryRead(out var m1));
        Assert.True(c2.Reader.TryRead(out var m2));
        var d1 = Assert.IsType<MboBookDeltaDto>(m1!.Data);
        var d2 = Assert.IsType<MboBookDeltaDto>(m2!.Data);
        Assert.Equal("added", d1.Kind);
        Assert.Equal("added", d2.Kind);
        Assert.Equal(d1.OrderId, d2.OrderId);
    }
}
