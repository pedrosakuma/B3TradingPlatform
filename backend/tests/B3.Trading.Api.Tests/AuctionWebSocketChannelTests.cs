using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.MarketData;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace B3.Trading.Api.Tests;

public class AuctionWebSocketChannelTests
{
    private static (SubscriptionManager subs, AuctionStateStore store, WebSocketAuctionEventSink sink, FakeMdSubscriber feed) Build()
    {
        var feed = new FakeMdSubscriber();
        var store = new AuctionStateStore(feed, TimeProvider.System, NullLogger<AuctionStateStore>.Instance);
        var subs = new SubscriptionManager(new WorkingOrderBook(), new PositionKeeper(), new AlgoBook());
        var sink = new WebSocketAuctionEventSink(subs, store);
        sink.StartAsync(default).GetAwaiter().GetResult();
        return (subs, store, sink, feed);
    }

    [Fact]
    public void TryParsePublic_recognises_phases_and_auction_with_valid_symbol()
    {
        Assert.True(Channels.TryParsePublic("phases.PETR4", out var k1, out var s1));
        Assert.Equal(PublicChannelKind.Phases, k1);
        Assert.Equal("PETR4", s1);

        Assert.True(Channels.TryParsePublic("auction.VALE3", out var k2, out var s2));
        Assert.Equal(PublicChannelKind.Auction, k2);
        Assert.Equal("VALE3", s2);
    }

    [Theory]
    [InlineData("phases.")]
    [InlineData("phases.PETR4 ")]
    [InlineData("phases.PETR-4")]
    [InlineData("phases.thisistoolongasymbolname")]
    [InlineData("orders.me")]
    [InlineData("auction")]
    public void TryParsePublic_rejects_invalid_inputs(string ch)
    {
        Assert.False(Channels.TryParsePublic(ch, out var k, out _));
        Assert.Equal(PublicChannelKind.None, k);
    }

    [Fact]
    public void Phases_subscribe_emits_Unknown_snapshot_when_no_signal_seen()
    {
        var (subs, _, sink, _) = Build();
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "phases.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Phases, "PETR4"));

        Assert.True(client.Reader.TryRead(out var msg));
        Assert.Equal("snapshot", msg!.Type);
        Assert.Equal("phases.PETR4", msg.Channel);
        var dto = Assert.IsType<PhaseSnapshotDto>(msg.Data);
        Assert.Equal("Unknown", dto.Phase);
        Assert.Null(dto.At);
    }

    [Fact]
    public void Phases_subscribe_then_TheoreticalOpening_pushes_delta_OpeningCall()
    {
        var (subs, _, sink, feed) = Build();
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "phases.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Phases, "PETR4"));
        client.Reader.TryRead(out _); // discard snapshot

        feed.RaiseTheoreticalOpening("PETR4", 30m, 100, DateTimeOffset.UtcNow);

        Assert.True(client.Reader.TryRead(out var delta));
        Assert.Equal("delta", delta!.Type);
        Assert.Equal("phases.PETR4", delta.Channel);
        Assert.Equal(1, delta.Seq);
        var dto = Assert.IsType<PhaseSnapshotDto>(delta.Data);
        Assert.Equal("OpeningCall", dto.Phase);
    }

    [Fact]
    public void Auction_subscribe_after_TheoreticalOpening_emits_populated_snapshot()
    {
        var (subs, _, sink, feed) = Build();
        feed.RaiseTheoreticalOpening("PETR4", 30.5m, 1000, DateTimeOffset.UtcNow);
        feed.RaiseAuctionImbalance("PETR4", 250, OrderSide.Sell, DateTimeOffset.UtcNow);

        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "auction.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Auction, "PETR4"));

        Assert.True(client.Reader.TryRead(out var snap));
        Assert.Equal("snapshot", snap!.Type);
        var dto = Assert.IsType<AuctionSnapshotDto>(snap.Data);
        Assert.Equal(30.5m, dto.Top);
        Assert.Equal(250, dto.Imbalance);
        Assert.Equal("Sell", dto.ImbalanceSide);
    }

    [Fact]
    public void Auction_print_pushes_AuctionPrintDto_delta()
    {
        var (subs, _, sink, feed) = Build();
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "auction.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Auction, "PETR4"));
        client.Reader.TryRead(out _);

        feed.RaiseAuctionPrint("PETR4", AuctionPrintKind.Opening, 31m, 5000, DateTimeOffset.UtcNow);

        // Print delta itself (Q1.5 #257 wire shape).
        Assert.True(client.Reader.TryRead(out var delta));
        Assert.Equal("delta", delta!.Type);
        var dto = Assert.IsType<AuctionPrintDto>(delta.Data);
        Assert.Equal("Opening", dto.Kind);
        Assert.Equal(31m, dto.Price);

        // Followed by an empty top frame so subscribers stop seeing
        // the pre-cross indicative as current (P2 fix).
        Assert.True(client.Reader.TryRead(out var clear));
        Assert.Equal("delta", clear!.Type);
        var emptied = Assert.IsType<AuctionSnapshotDto>(clear.Data);
        Assert.Null(emptied.Top);
        Assert.Null(emptied.IndicativeMatchQty);
        Assert.Null(emptied.Imbalance);
        Assert.Null(emptied.ImbalanceSide);
        Assert.Null(emptied.At);
    }

    [Fact]
    public void Auction_subscribe_after_print_serves_empty_snapshot()
    {
        var (subs, _, sink, feed) = Build();
        var t = DateTimeOffset.UtcNow;
        feed.RaiseTheoreticalOpening("PETR4", 30.5m, 1000, t);
        feed.RaiseAuctionImbalance("PETR4", 250, OrderSide.Sell, t);
        feed.RaiseAuctionPrint("PETR4", AuctionPrintKind.Opening, 30.25m, 5000, t);

        var client = new SubscribedClient(new EndClientId("late"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "auction.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Auction, "PETR4"));

        Assert.True(client.Reader.TryRead(out var snap));
        Assert.Equal("snapshot", snap!.Type);
        var dto = Assert.IsType<AuctionSnapshotDto>(snap.Data);
        Assert.Null(dto.Top);
        Assert.Null(dto.IndicativeMatchQty);
        Assert.Null(dto.Imbalance);
        Assert.Null(dto.ImbalanceSide);
        Assert.Null(dto.At);
    }

    [Fact]
    public void BroadcastPublic_fans_out_to_all_owners()
    {
        var (subs, _, sink, feed) = Build();
        var alice = new SubscribedClient(new EndClientId("alice"), "TEST");
        var bob = new SubscribedClient(new EndClientId("bob"), "TEST");
        subs.SubscribePublicWithSnapshot(alice, "phases.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Phases, "PETR4"));
        subs.SubscribePublicWithSnapshot(bob, "phases.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Phases, "PETR4"));
        alice.Reader.TryRead(out _);
        bob.Reader.TryRead(out _);

        feed.RaiseTheoreticalOpening("PETR4", 30m, 100, DateTimeOffset.UtcNow);

        Assert.True(alice.Reader.TryRead(out var a));
        Assert.True(bob.Reader.TryRead(out var b));
        Assert.Equal("delta", a!.Type);
        Assert.Equal("delta", b!.Type);
    }

    [Fact]
    public void RemoveFromPublic_stops_further_deltas()
    {
        var (subs, _, sink, feed) = Build();
        var client = new SubscribedClient(new EndClientId("alice"), "TEST");
        subs.SubscribePublicWithSnapshot(client, "phases.PETR4",
            () => sink.GetSnapshot(PublicChannelKind.Phases, "PETR4"));
        client.Reader.TryRead(out _);

        subs.RemoveFromPublic(client);
        client.Unsubscribe("phases.PETR4");

        feed.RaiseTheoreticalOpening("PETR4", 30m, 100, DateTimeOffset.UtcNow);

        Assert.False(client.Reader.TryRead(out _));
    }

    private sealed class FakeMdSubscriber : IMarketDataSubscriber
    {
#pragma warning disable CS0067
#pragma warning disable CS0067
        public event Action<MarketTrade>? Trade;
        public event Action<MarketInfoSnapshot>? InfoSnapshot;
        public event Action<MarketDataConnectionState>? ConnectionStateChanged;
        public event Action<MarketSubscribeError>? SubscribeError;
#pragma warning restore CS0067
        public event Action<MarketTheoreticalOpening>? TheoreticalOpening;
        public event Action<MarketAuctionImbalance>? AuctionImbalance;
        public event Action<MarketAuctionPrint>? AuctionPrint;
#pragma warning disable CS0067
        public event Action<MarketBookSnapshot>? BookSnapshot;
        public event Action<MarketOrderAdded>? OrderAdded;
        public event Action<MarketOrderUpdated>? OrderUpdated;
        public event Action<MarketOrderDeleted>? OrderDeleted;
        public event Action<MarketBookCleared>? BookCleared;
#pragma warning restore CS0067

        public MarketDataConnectionState State => MarketDataConnectionState.Connected;
        public long DroppedEventCount => 0;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask SubscribeAsync(string symbol, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void RaiseTheoreticalOpening(string s, decimal p, long q, DateTimeOffset t) =>
            TheoreticalOpening?.Invoke(new MarketTheoreticalOpening(s, 0UL, p, q, t));
        public void RaiseAuctionImbalance(string s, long q, OrderSide side, DateTimeOffset t) =>
            AuctionImbalance?.Invoke(new MarketAuctionImbalance(s, 0UL, q, side, t));
        public void RaiseAuctionPrint(string s, AuctionPrintKind k, decimal p, long q, DateTimeOffset t) =>
            AuctionPrint?.Invoke(new MarketAuctionPrint(s, 0UL, k, p, q, t));
    }
}
