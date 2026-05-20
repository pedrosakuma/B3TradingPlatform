using B3.Trading.Application.MarketData;

namespace B3.Trading.Application.Tests.MarketData;

/// <summary>
/// Q3.6 Stage A (#286). Unit tests for InMemoryL2BookView (L3 maintenance +
/// derived L2 top-of-book).
/// </summary>
public class InMemoryL2BookViewTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 19, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TopOfBook_returns_null_for_unknown_symbol()
    {
        var s = new InMemoryL2BookView();
        Assert.Null(s.GetTopOfBook("UNKN"));
    }

    [Fact]
    public void Snapshot_seeds_book_and_top_picks_best_bid_and_best_ask()
    {
        var s = new InMemoryL2BookView();
        s.ApplySnapshot(new MarketBookSnapshot
        {
            Symbol = "PETR4",
            SecurityId = 4321,
            RptSeq = 1,
            Bids = new[]
            {
                new MarketBookOrder(101, 30.10m, 100),
                new MarketBookOrder(102, 30.20m, 200), // best
                new MarketBookOrder(103, 30.20m, 50),  // ties on best
            },
            Asks = new[]
            {
                new MarketBookOrder(201, 30.40m, 300), // best
                new MarketBookOrder(202, 30.50m, 100),
            },
            ReceivedUtc = T0,
        });

        var top = Assert.NotNull(s.GetTopOfBook("PETR4"));
        Assert.Equal(30.20m, top.Bid.Price);
        Assert.Equal(250, top.Bid.TotalQty);
        Assert.Equal(2, top.Bid.OrderCount);
        Assert.Equal(30.40m, top.Ask.Price);
        Assert.Equal(300, top.Ask.TotalQty);
        Assert.Equal(1, top.Ask.OrderCount);
    }

    [Fact]
    public void OrderAdded_appends_and_can_become_new_best()
    {
        var s = SeedSimple();
        s.ApplyAdded(new MarketOrderAdded("PETR4", 4321, 999, MarketBookSide.Bid, 30.30m, 80, T0.AddSeconds(1)));

        var top = s.GetTopOfBook("PETR4")!.Value;
        Assert.Equal(30.30m, top.Bid.Price);
        Assert.Equal(80, top.Bid.TotalQty);
        Assert.Equal(1, top.Bid.OrderCount);
    }

    [Fact]
    public void OrderUpdated_zero_qty_is_treated_as_delete()
    {
        var s = SeedSimple();
        // Existing best bid (102 @ 30.20 / 200). Update to qty=0 should remove it.
        s.ApplyUpdated(new MarketOrderUpdated("PETR4", 4321, 102, MarketBookSide.Bid, 30.20m, 0, T0.AddSeconds(1)));
        var top = s.GetTopOfBook("PETR4")!.Value;
        // Remaining bids: 101 @ 30.10 / 100 and 103 @ 30.20 / 50 → best still 30.20 / 50 / 1.
        Assert.Equal(30.20m, top.Bid.Price);
        Assert.Equal(50, top.Bid.TotalQty);
        Assert.Equal(1, top.Bid.OrderCount);
    }

    [Fact]
    public void OrderUpdated_qty_change_aggregates_in_top()
    {
        var s = SeedSimple();
        s.ApplyUpdated(new MarketOrderUpdated("PETR4", 4321, 103, MarketBookSide.Bid, 30.20m, 150, T0.AddSeconds(1)));
        var top = s.GetTopOfBook("PETR4")!.Value;
        // 102 (200) + 103 (150) = 350 at 30.20.
        Assert.Equal(30.20m, top.Bid.Price);
        Assert.Equal(350, top.Bid.TotalQty);
        Assert.Equal(2, top.Bid.OrderCount);
    }

    [Fact]
    public void OrderDeleted_drops_and_recomputes_top()
    {
        var s = SeedSimple();
        s.ApplyDeleted(new MarketOrderDeleted("PETR4", 4321, 102, MarketBookSide.Bid, T0.AddSeconds(1)));
        s.ApplyDeleted(new MarketOrderDeleted("PETR4", 4321, 103, MarketBookSide.Bid, T0.AddSeconds(1)));
        var top = s.GetTopOfBook("PETR4")!.Value;
        Assert.Equal(30.10m, top.Bid.Price);
        Assert.Equal(100, top.Bid.TotalQty);
        Assert.Equal(1, top.Bid.OrderCount);
    }

    [Fact]
    public void BookCleared_both_empties_state_and_top_returns_null()
    {
        var s = SeedSimple();
        s.ApplyCleared(new MarketBookCleared("PETR4", 4321, MarketBookClearSide.Both, T0.AddSeconds(1)));
        Assert.Null(s.GetTopOfBook("PETR4"));
        var (b, a) = s.GetOrderCounts("PETR4");
        Assert.Equal(0, b);
        Assert.Equal(0, a);
    }

    [Fact]
    public void BookCleared_single_side_keeps_other()
    {
        var s = SeedSimple();
        s.ApplyCleared(new MarketBookCleared("PETR4", 4321, MarketBookClearSide.Bid, T0.AddSeconds(1)));
        var top = s.GetTopOfBook("PETR4")!.Value;
        Assert.Equal(0, top.Bid.OrderCount);
        Assert.Equal(30.40m, top.Ask.Price);
    }

    [Fact]
    public void Snapshot_replaces_prior_state()
    {
        var s = SeedSimple();
        s.ApplySnapshot(new MarketBookSnapshot
        {
            Symbol = "PETR4",
            SecurityId = 4321,
            RptSeq = 5,
            Bids = new[] { new MarketBookOrder(500, 31.00m, 10) },
            Asks = new[] { new MarketBookOrder(501, 31.05m, 20) },
            ReceivedUtc = T0.AddSeconds(2),
        });
        var top = s.GetTopOfBook("PETR4")!.Value;
        Assert.Equal(31.00m, top.Bid.Price);
        Assert.Equal(10, top.Bid.TotalQty);
        Assert.Equal(1, top.Bid.OrderCount);
        Assert.Equal(31.05m, top.Ask.Price);
        var (b, a) = s.GetOrderCounts("PETR4");
        Assert.Equal(1, b);
        Assert.Equal(1, a);
    }

    [Fact]
    public void OrderAdded_with_nonpositive_qty_is_ignored()
    {
        var s = new InMemoryL2BookView();
        s.ApplyAdded(new MarketOrderAdded("PETR4", 4321, 1, MarketBookSide.Bid, 30m, 0, T0));
        s.ApplyAdded(new MarketOrderAdded("PETR4", 4321, 2, MarketBookSide.Bid, 30m, -5, T0));
        Assert.Null(s.GetTopOfBook("PETR4"));
    }

    [Fact]
    public void Top_unknown_symbol_after_only_one_side_present_returns_value_with_empty_other()
    {
        var s = new InMemoryL2BookView();
        s.ApplyAdded(new MarketOrderAdded("PETR4", 4321, 1, MarketBookSide.Ask, 30m, 100, T0));
        var top = s.GetTopOfBook("PETR4")!.Value;
        Assert.Equal(30m, top.Ask.Price);
        Assert.Equal(100, top.Ask.TotalQty);
        Assert.Equal(1, top.Ask.OrderCount);
        Assert.Equal(0, top.Bid.OrderCount);
    }

    [Fact]
    public void Symbol_lookup_is_case_insensitive_and_trims_whitespace()
    {
        var s = SeedSimple();
        Assert.NotNull(s.GetTopOfBook("petr4"));
        Assert.NotNull(s.GetTopOfBook("  PETR4  "));
    }

    private static InMemoryL2BookView SeedSimple()
    {
        var s = new InMemoryL2BookView();
        s.ApplySnapshot(new MarketBookSnapshot
        {
            Symbol = "PETR4",
            SecurityId = 4321,
            RptSeq = 1,
            Bids = new[]
            {
                new MarketBookOrder(101, 30.10m, 100),
                new MarketBookOrder(102, 30.20m, 200),
                new MarketBookOrder(103, 30.20m, 50),
            },
            Asks = new[]
            {
                new MarketBookOrder(201, 30.40m, 300),
                new MarketBookOrder(202, 30.50m, 100),
            },
            ReceivedUtc = T0,
        });
        return s;
    }
}
