using B3.Trading.Application.MarketData;
using B3.Trading.Domain;
using Xunit;

namespace B3.Trading.Application.Tests.MarketData;

public class AuctionProjectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 19, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public void TheoreticalOpening_fires_on_first_snapshot_with_price_and_size()
    {
        var p = new AuctionProjector();
        var hits = new List<MarketTheoreticalOpening>();
        p.TheoreticalOpening += hits.Add;

        p.OnInfoSnapshot("PETR4", securityId: 100, theoreticalOpeningPrice: 30.5m,
            theoreticalOpeningSize: 1_000, imbalanceSize: null, imbalanceSide: null,
            tradingStatus: null, receivedUtc: Now);

        var ev = Assert.Single(hits);
        Assert.Equal("PETR4", ev.Symbol);
        Assert.Equal(100ul, ev.SecurityId);
        Assert.Equal(30.5m, ev.Price);
        Assert.Equal(1_000L, ev.Qty);
        Assert.Equal(Now, ev.ReceivedUtc);
    }

    [Fact]
    public void TheoreticalOpening_does_not_fire_when_only_price_present()
    {
        var p = new AuctionProjector();
        var hits = new List<MarketTheoreticalOpening>();
        p.TheoreticalOpening += hits.Add;

        p.OnInfoSnapshot("PETR4", 100, theoreticalOpeningPrice: 30.5m,
            theoreticalOpeningSize: null, imbalanceSize: null, imbalanceSide: null,
            tradingStatus: null, receivedUtc: Now);

        Assert.Empty(hits);
    }

    [Fact]
    public void TheoreticalOpening_collapses_unchanged_snapshots()
    {
        var p = new AuctionProjector();
        var hits = new List<MarketTheoreticalOpening>();
        p.TheoreticalOpening += hits.Add;

        p.OnInfoSnapshot("PETR4", 100, 30.5m, 1_000, null, null, null, Now);
        p.OnInfoSnapshot("PETR4", 100, 30.5m, 1_000, null, null, null, Now.AddSeconds(1));

        Assert.Single(hits);
    }

    [Fact]
    public void TheoreticalOpening_fires_again_on_price_or_qty_change()
    {
        var p = new AuctionProjector();
        var hits = new List<MarketTheoreticalOpening>();
        p.TheoreticalOpening += hits.Add;

        p.OnInfoSnapshot("PETR4", 100, 30.5m, 1_000, null, null, null, Now);
        p.OnInfoSnapshot("PETR4", 100, 30.6m, 1_000, null, null, null, Now.AddSeconds(1));
        p.OnInfoSnapshot("PETR4", 100, 30.6m, 1_200, null, null, null, Now.AddSeconds(2));

        Assert.Equal(3, hits.Count);
        Assert.Equal(30.6m, hits[1].Price);
        Assert.Equal(1_200L, hits[2].Qty);
    }

    [Fact]
    public void AuctionImbalance_fires_with_buy_side_when_more_buyers()
    {
        var p = new AuctionProjector();
        var hits = new List<MarketAuctionImbalance>();
        p.AuctionImbalance += hits.Add;

        p.OnInfoSnapshot("PETR4", 100, null, null, imbalanceSize: 500,
            imbalanceSide: OrderSide.Buy, tradingStatus: null, receivedUtc: Now);

        var ev = Assert.Single(hits);
        Assert.Equal(OrderSide.Buy, ev.Side);
        Assert.Equal(500L, ev.Quantity);
    }

    [Fact]
    public void AuctionImbalance_suppressed_when_side_null()
    {
        var p = new AuctionProjector();
        var hits = new List<MarketAuctionImbalance>();
        p.AuctionImbalance += hits.Add;

        // Balanced book or null condition → no delta fires.
        p.OnInfoSnapshot("PETR4", 100, null, null, imbalanceSize: 0,
            imbalanceSide: null, tradingStatus: null, receivedUtc: Now);

        Assert.Empty(hits);
    }

    [Fact]
    public void AuctionImbalance_collapses_unchanged_and_fires_on_side_flip()
    {
        var p = new AuctionProjector();
        var hits = new List<MarketAuctionImbalance>();
        p.AuctionImbalance += hits.Add;

        p.OnInfoSnapshot("PETR4", 100, null, null, 500, OrderSide.Buy, null, Now);
        p.OnInfoSnapshot("PETR4", 100, null, null, 500, OrderSide.Buy, null, Now.AddSeconds(1));
        p.OnInfoSnapshot("PETR4", 100, null, null, 500, OrderSide.Sell, null, Now.AddSeconds(2));

        Assert.Equal(2, hits.Count);
        Assert.Equal(OrderSide.Buy, hits[0].Side);
        Assert.Equal(OrderSide.Sell, hits[1].Side);
    }

    [Fact]
    public void AuctionPrint_defaults_to_opening_kind_when_no_trading_status_seen()
    {
        var p = new AuctionProjector();
        var hits = new List<MarketAuctionPrint>();
        p.AuctionPrint += hits.Add;

        p.OnAuctionTrade("PETR4", 100, price: 30.0m, qty: 5_000, Now);

        var ev = Assert.Single(hits);
        Assert.Equal(AuctionPrintKind.Opening, ev.Kind);
        Assert.Equal(30.0m, ev.Price);
        Assert.Equal(5_000L, ev.Qty);
    }

    [Fact]
    public void AuctionPrint_is_closing_when_last_status_is_final_closing_call()
    {
        var p = new AuctionProjector();
        var hits = new List<MarketAuctionPrint>();
        p.AuctionPrint += hits.Add;

        p.OnInfoSnapshot("PETR4", 100, null, null, null, null,
            tradingStatus: AuctionProjector.FinalClosingCallTradingStatus, receivedUtc: Now);
        p.OnAuctionTrade("PETR4", 100, 30.0m, 5_000, Now.AddSeconds(1));

        Assert.Equal(AuctionPrintKind.Closing, Assert.Single(hits).Kind);
    }

    [Fact]
    public void AuctionPrint_is_opening_when_last_status_is_reserved()
    {
        var p = new AuctionProjector();
        var hits = new List<MarketAuctionPrint>();
        p.AuctionPrint += hits.Add;

        p.OnInfoSnapshot("PETR4", 100, null, null, null, null,
            tradingStatus: AuctionProjector.ReservedTradingStatus, receivedUtc: Now);
        p.OnAuctionTrade("PETR4", 100, 30.0m, 5_000, Now.AddSeconds(1));

        Assert.Equal(AuctionPrintKind.Opening, Assert.Single(hits).Kind);
    }

    [Fact]
    public void AuctionPrint_resets_memoized_top_and_imbalance()
    {
        var p = new AuctionProjector();
        var tops = new List<MarketTheoreticalOpening>();
        var imbs = new List<MarketAuctionImbalance>();
        p.TheoreticalOpening += tops.Add;
        p.AuctionImbalance += imbs.Add;

        p.OnInfoSnapshot("PETR4", 100, 30.5m, 1_000, 500, OrderSide.Buy, null, Now);
        p.OnAuctionTrade("PETR4", 100, 30.5m, 5_000, Now.AddSeconds(1));
        // Same top + imbalance values: would normally collapse, but the cross
        // dropped the memo so both should re-publish.
        p.OnInfoSnapshot("PETR4", 100, 30.5m, 1_000, 500, OrderSide.Buy, null, Now.AddSeconds(2));

        Assert.Equal(2, tops.Count);
        Assert.Equal(2, imbs.Count);
    }

    [Fact]
    public void Symbols_are_tracked_independently()
    {
        var p = new AuctionProjector();
        var hits = new List<MarketTheoreticalOpening>();
        p.TheoreticalOpening += hits.Add;

        p.OnInfoSnapshot("PETR4", 100, 30.5m, 1_000, null, null, null, Now);
        p.OnInfoSnapshot("VALE3", 200, 30.5m, 1_000, null, null, null, Now);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Symbol == "PETR4");
        Assert.Contains(hits, h => h.Symbol == "VALE3");
    }
}
