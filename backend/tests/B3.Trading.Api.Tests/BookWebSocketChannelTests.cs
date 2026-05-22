using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.MarketData;
using B3.Trading.Domain;
using Microsoft.Extensions.Options;
using Xunit;

namespace B3.Trading.Api.Tests;

public class BookWebSocketChannelTests
{
    private static readonly DateTimeOffset FixedClockNow =
        new(2026, 5, 22, 14, 0, 0, TimeSpan.Zero);

    private static (SubscriptionManager subs, InMemoryL2BookView store, WebSocketBookEventSink sink) Build(int maxLevels = 10)
    {
        var store = new InMemoryL2BookView();
        var subs = new SubscriptionManager(new WorkingOrderBook(), new PositionKeeper(), new AlgoBook());
        var opts = Options.Create(new MarketDataOptions { BookChannelMaxLevels = maxLevels });
        var clock = new FakeTimeProvider(FixedClockNow);
        var sink = new WebSocketBookEventSink(subs, store, opts, clock);
        sink.StartAsync(default).GetAwaiter().GetResult();
        return (subs, store, sink);
    }

    private static MarketOrderAdded Add(string sym, ulong id, MarketBookSide side, decimal price, long qty) =>
        new(sym, 4321UL, id, side, price, qty, DateTimeOffset.UtcNow);

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

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
        var dto = Assert.IsType<L2LadderDto>(msg.Data);
        Assert.Equal("PETR4", dto.Symbol);
        Assert.Empty(dto.Bids);
        Assert.Empty(dto.Asks);
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
        var dto = Assert.IsType<L2LadderDto>(delta.Data);
        Assert.Equal("PETR4", dto.Symbol);
        var bid = Assert.Single(dto.Bids);
        Assert.Equal(30.10m, bid.Price);
        Assert.Equal(100, bid.TotalQty);
        Assert.Equal(1, bid.OrderCount);
        Assert.Empty(dto.Asks);
    }

    [Fact]
    public void Book_ladder_aggregates_orders_at_same_price()
    {
        var (subs, store, sink) = Build();
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "book.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Book, "PETR4"));
        client.Reader.TryRead(out _); // snapshot

        store.ApplyAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 30.10m, 100));
        client.Reader.TryRead(out _); // first delta
        store.ApplyAdded(Add("PETR4", 2UL, MarketBookSide.Bid, 30.10m, 200));

        Assert.True(client.Reader.TryRead(out var delta));
        var dto = Assert.IsType<L2LadderDto>(delta!.Data);
        var bid = Assert.Single(dto.Bids);
        Assert.Equal(30.10m, bid.Price);
        Assert.Equal(300, bid.TotalQty);
        Assert.Equal(2, bid.OrderCount);
    }

    [Fact]
    public void Book_ladder_sorts_bids_descending_and_asks_ascending()
    {
        var (subs, store, sink) = Build();
        store.ApplyAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 30.10m, 100));
        store.ApplyAdded(Add("PETR4", 2UL, MarketBookSide.Bid, 30.05m, 200));
        store.ApplyAdded(Add("PETR4", 3UL, MarketBookSide.Bid, 30.20m, 150));
        store.ApplyAdded(Add("PETR4", 4UL, MarketBookSide.Ask, 30.40m, 50));
        store.ApplyAdded(Add("PETR4", 5UL, MarketBookSide.Ask, 30.30m, 75));

        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "book.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Book, "PETR4"));

        Assert.True(client.Reader.TryRead(out var msg));
        var dto = Assert.IsType<L2LadderDto>(msg!.Data);
        Assert.Equal(new[] { 30.20m, 30.10m, 30.05m }, dto.Bids.Select(b => b.Price));
        Assert.Equal(new[] { 30.30m, 30.40m }, dto.Asks.Select(a => a.Price));
    }

    [Fact]
    public void Book_ladder_respects_BookChannelMaxLevels()
    {
        var (subs, store, sink) = Build(maxLevels: 3);
        for (var i = 0; i < 5; i++)
            store.ApplyAdded(Add("PETR4", (ulong)(100 + i), MarketBookSide.Bid, 30m + i * 0.01m, 10));

        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "book.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Book, "PETR4"));

        Assert.True(client.Reader.TryRead(out var msg));
        var dto = Assert.IsType<L2LadderDto>(msg!.Data);
        // Best 3 levels (highest prices): 30.04, 30.03, 30.02.
        Assert.Equal(new[] { 30.04m, 30.03m, 30.02m }, dto.Bids.Select(b => b.Price));
    }

    [Fact]
    public void Book_coalesces_updates_beyond_visible_depth()
    {
        var (subs, store, sink) = Build(maxLevels: 2);
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "book.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Book, "PETR4"));
        client.Reader.TryRead(out _); // snapshot

        // Build top-2 first; each emission is a real delta.
        store.ApplyAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 30.20m, 100));
        Assert.True(client.Reader.TryRead(out _));
        store.ApplyAdded(Add("PETR4", 2UL, MarketBookSide.Bid, 30.10m, 100));
        Assert.True(client.Reader.TryRead(out _));

        // Add an order at level 3 (below visible window) — coalesced.
        store.ApplyAdded(Add("PETR4", 3UL, MarketBookSide.Bid, 30.00m, 500));

        Assert.False(client.Reader.TryRead(out var maybe),
            $"expected coalesced (no delta) but got {maybe?.Type} {maybe?.Data}");
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
        var dto = Assert.IsType<L2LadderDto>(msg.Data);
        Assert.Equal(30.10m, dto.Bids[0].Price);
        Assert.Equal(30.20m, dto.Asks[0].Price);
        Assert.Equal(50, dto.Asks[0].TotalQty);
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

    [Fact]
    public void GetLadder_returns_null_for_unseen_symbol_and_empty_state()
    {
        var (_, store, _) = Build();
        Assert.Null(store.GetLadder("UNKNOWN", 10));

        store.ApplyAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 30m, 1));
        store.ApplyDeleted(new MarketOrderDeleted("PETR4", 4321UL, 1UL, MarketBookSide.Bid, DateTimeOffset.UtcNow));
        Assert.Null(store.GetLadder("PETR4", 10));
    }

    /// <summary>
    /// #382 follow-up regression. Until this fix, OnBookChanged early-returned
    /// when <see cref="IL2BookView.GetLadder"/> returned null on the populated →
    /// empty edge (last resting order filled / cancelled). The FE never got the
    /// "book emptied" frame and the DOB kept rendering the last populated
    /// ladder until a hard refresh refetched the cold-start snapshot.
    /// </summary>
    [Fact]
    public void Book_emptied_after_fill_broadcasts_empty_dto()
    {
        var (subs, store, sink) = Build();
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "book.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Book, "PETR4"));
        client.Reader.TryRead(out _); // cold snapshot

        store.ApplyAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 32.50m, 100));
        Assert.True(client.Reader.TryRead(out var populatedDelta));
        var populated = Assert.IsType<L2LadderDto>(populatedDelta!.Data);
        Assert.Single(populated.Bids);

        // Last resting order gone → store returns null ladder.
        store.ApplyDeleted(new MarketOrderDeleted("PETR4", 4321UL, 1UL, MarketBookSide.Bid, DateTimeOffset.UtcNow));

        Assert.True(client.Reader.TryRead(out var emptyDelta), "FE must receive a delta when the book empties");
        Assert.Equal("delta", emptyDelta!.Type);
        var emptyDto = Assert.IsType<L2LadderDto>(emptyDelta.Data);
        Assert.Equal("PETR4", emptyDto.Symbol);
        Assert.Empty(emptyDto.Bids);
        Assert.Empty(emptyDto.Asks);
        // #379. Live-empty marker: UpdatedUtc is non-null (stamped with the
        // sink's TimeProvider) so the FE reducer flips ready=true and the
        // DOB renders "empty" per-side instead of "no book — check MD
        // settings". Cold-start L2LadderDto.Empty(symbol) keeps UpdatedUtc=null
        // (see Book_subscribe_emits_empty_snapshot_when_no_frame_seen).
        Assert.Equal(FixedClockNow, emptyDto.UpdatedUtc);
    }

    /// <summary>
    /// #379. A late subscriber to a symbol whose book has emptied since the
    /// last populated broadcast must see the live-empty marker (non-null
    /// UpdatedUtc), not the cold-start shape. Otherwise the DOB shows
    /// "check MD settings" to a trader whose MD subscription is perfectly
    /// healthy — the symbol is just quiet right now.
    /// </summary>
    [Fact]
    public void Book_late_subscriber_after_empty_sees_live_empty_marker()
    {
        var (subs, store, sink) = Build();
        // Drive the populated → empty cycle without any subscriber to
        // prove _lastSent is populated purely by OnBookChanged.
        store.ApplyAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 32.50m, 100));
        store.ApplyDeleted(new MarketOrderDeleted("PETR4", 4321UL, 1UL, MarketBookSide.Bid, DateTimeOffset.UtcNow));

        var client = new SubscribedClient(new EndClientId("bob"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "book.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Book, "PETR4"));

        Assert.True(client.Reader.TryRead(out var snap));
        Assert.Equal("snapshot", snap!.Type);
        var dto = Assert.IsType<L2LadderDto>(snap.Data);
        Assert.Empty(dto.Bids);
        Assert.Empty(dto.Asks);
        Assert.Equal(FixedClockNow, dto.UpdatedUtc);
    }

    /// <summary>
    /// #379. Cold-start contract preserved: a subscriber arriving before
    /// any BookChanged has fired for the symbol still sees the null
    /// UpdatedUtc marker so the FE shows "awaiting book snapshot…" → then
    /// "check MD settings" after the timeout, which IS the right copy
    /// when MD truly hasn't spoken.
    /// </summary>
    [Fact]
    public void Book_cold_start_snapshot_still_carries_null_updated_utc()
    {
        var (subs, _, sink) = Build();
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "book.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Book, "PETR4"));
        Assert.True(client.Reader.TryRead(out var snap));
        var dto = Assert.IsType<L2LadderDto>(snap!.Data);
        Assert.Empty(dto.Bids);
        Assert.Empty(dto.Asks);
        Assert.Null(dto.UpdatedUtc);
    }

    /// <summary>
    /// #382 follow-up. Empty-state broadcasts must coalesce: a steady-state
    /// empty book (e.g. nobody trading the symbol) should not spam a delta
    /// every time the store fires BookChanged.
    /// </summary>
    [Fact]
    public void Book_empty_steady_state_coalesces_subsequent_empty_events()
    {
        var (subs, store, sink) = Build();
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "book.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Book, "PETR4"));
        client.Reader.TryRead(out _); // cold snapshot

        store.ApplyAdded(Add("PETR4", 1UL, MarketBookSide.Bid, 32.50m, 100));
        client.Reader.TryRead(out _); // populated delta

        store.ApplyDeleted(new MarketOrderDeleted("PETR4", 4321UL, 1UL, MarketBookSide.Bid, DateTimeOffset.UtcNow));
        Assert.True(client.Reader.TryRead(out _)); // first empty delta

        // Second populated → empty cycle exercises the coalesce gate: the
        // store's empty state hasn't moved since the prior broadcast, so no
        // new delta hits the wire.
        store.ApplyAdded(Add("PETR4", 2UL, MarketBookSide.Bid, 32.40m, 50));
        Assert.True(client.Reader.TryRead(out _)); // re-populated delta
        store.ApplyDeleted(new MarketOrderDeleted("PETR4", 4321UL, 2UL, MarketBookSide.Bid, DateTimeOffset.UtcNow));
        Assert.True(client.Reader.TryRead(out _)); // empty delta after second cycle

        // No further deltas: any extra BookChanged that resolves to empty
        // must be swallowed.
        Assert.False(client.Reader.TryRead(out _));
    }
}
