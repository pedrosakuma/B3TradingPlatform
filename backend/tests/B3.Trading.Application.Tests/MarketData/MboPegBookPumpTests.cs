using B3.Trading.Application.MarketData;

namespace B3.Trading.Application.Tests.MarketData;

/// <summary>
/// Q3.6 Stage C (#286). Unit tests for <see cref="MboPegBookPump"/> —
/// bridges <see cref="InMemoryL2BookView.BookChanged"/> into
/// <see cref="PegBookTopCache.UpdateBookTop"/>.
/// </summary>
public class MboPegBookPumpTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 19, 14, 0, 0, TimeSpan.Zero);

    private static MarketBookSnapshot Snap(ulong bidOrderId, decimal bidPx, ulong askOrderId, decimal askPx) =>
        new()
        {
            Symbol = "PETR4",
            SecurityId = 4321,
            RptSeq = 1,
            Bids = new[] { new MarketBookOrder(bidOrderId, bidPx, 100) },
            Asks = new[] { new MarketBookOrder(askOrderId, askPx, 100) },
            ReceivedUtc = T0,
        };

    [Fact]
    public async Task BookChanged_pushes_top_of_book_into_cache()
    {
        var store = new InMemoryL2BookView();
        var cache = new PegBookTopCache();
        var pump = new MboPegBookPump(store, cache);
        try
        {
            store.ApplySnapshot(Snap(101UL, 30.20m, 201UL, 30.40m));

            var top = cache.TryGet("PETR4");
            Assert.NotNull(top);
            Assert.Equal(30.20m, top.Value.BestBid);
            Assert.Equal(30.40m, top.Value.BestAsk);
            Assert.Equal(30.30m, top.Value.Mid);
        }
        finally
        {
            await pump.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Subsequent_updates_replace_cached_bbo()
    {
        var store = new InMemoryL2BookView();
        var cache = new PegBookTopCache();
        var pump = new MboPegBookPump(store, cache);
        try
        {
            store.ApplySnapshot(Snap(101UL, 30.20m, 201UL, 30.40m));
            // Replace the bid with a higher one — top of book moves.
            store.ApplyAdded(new MarketOrderAdded(
                Symbol: "PETR4",
                SecurityId: 4321,
                OrderId: 102UL,
                Side: MarketBookSide.Bid,
                Price: 30.25m,
                Qty: 50,
                ReceivedUtc: T0.AddSeconds(1)));

            var top = cache.TryGet("PETR4");
            Assert.NotNull(top);
            Assert.Equal(30.25m, top.Value.BestBid);
            Assert.Equal(30.40m, top.Value.BestAsk);
        }
        finally
        {
            await pump.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Empty_side_preserves_known_leg_via_cache_merge()
    {
        var store = new InMemoryL2BookView();
        var cache = new PegBookTopCache();
        // Pre-seed both legs via a direct cache write so we can prove
        // the pump's "empty side -> null" path does not clobber the
        // existing value.
        cache.UpdateBookTop("PETR4", bestBid: 30.10m, bestAsk: 30.40m, receivedUtc: T0);

        var pump = new MboPegBookPump(store, cache);
        try
        {
            // Snapshot with only an ask side — GetTopOfBook returns a
            // sentinel (0,0,0) for the bid leg; the pump should send
            // null for that side, not zero.
            store.ApplySnapshot(new MarketBookSnapshot
            {
                Symbol = "PETR4",
                SecurityId = 4321,
                RptSeq = 5,
                Bids = Array.Empty<MarketBookOrder>(),
                Asks = new[] { new MarketBookOrder(201UL, 30.45m, 100) },
                ReceivedUtc = T0.AddSeconds(2),
            });

            var top = cache.TryGet("PETR4");
            Assert.NotNull(top);
            Assert.Equal(30.10m, top.Value.BestBid); // preserved
            Assert.Equal(30.45m, top.Value.BestAsk); // updated
        }
        finally
        {
            await pump.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsync_unsubscribes_from_BookChanged()
    {
        var store = new InMemoryL2BookView();
        var cache = new PegBookTopCache();
        var pump = new MboPegBookPump(store, cache);

        await pump.StopAsync(CancellationToken.None);

        // After Stop, further store mutations must not touch the cache.
        store.ApplySnapshot(Snap(101UL, 30.20m, 201UL, 30.40m));
        Assert.Null(cache.TryGet("PETR4"));
    }

    [Fact]
    public async Task Both_sides_empty_yields_no_cache_write()
    {
        var store = new InMemoryL2BookView();
        var cache = new PegBookTopCache();
        var pump = new MboPegBookPump(store, cache);
        try
        {
            store.ApplySnapshot(new MarketBookSnapshot
            {
                Symbol = "ZZZZ",
                SecurityId = 1,
                RptSeq = 1,
                Bids = Array.Empty<MarketBookOrder>(),
                Asks = Array.Empty<MarketBookOrder>(),
                ReceivedUtc = T0,
            });
            Assert.Null(cache.TryGet("ZZZZ"));
        }
        finally
        {
            await pump.StopAsync(CancellationToken.None);
        }
    }
}
