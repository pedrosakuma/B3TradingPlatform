using B3.Trading.MarketMakerBot;

namespace B3.Trading.MarketMakerBot.Tests;

public class MarketPriceTrackerTests
{
    [Fact]
    public void TryGetReferencePrice_ReturnsFalse_BeforeAnyUpdate()
    {
        var tracker = new MarketPriceTracker();
        Assert.False(tracker.TryGetReferencePrice("PETR4", out _));
    }

    [Fact]
    public void OnTrade_UpdatesReferencePrice()
    {
        var tracker = new MarketPriceTracker();
        tracker.OnTrade("PETR4", 31.50m);
        Assert.True(tracker.TryGetReferencePrice("PETR4", out var price));
        Assert.Equal(31.50m, price);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void OnTrade_IgnoresNonPositivePrices(decimal badPrice)
    {
        var tracker = new MarketPriceTracker();
        tracker.OnTrade("PETR4", badPrice);
        Assert.False(tracker.TryGetReferencePrice("PETR4", out _));
    }

    [Fact]
    public void OnInfoSnapshot_PrefersTradingReferencePriceOverLastTradePrice()
    {
        var tracker = new MarketPriceTracker();
        tracker.OnInfoSnapshot("PETR4", tradingReferencePrice: 32m, lastTradePrice: 31m);
        Assert.True(tracker.TryGetReferencePrice("PETR4", out var price));
        Assert.Equal(32m, price);
    }

    [Fact]
    public void OnInfoSnapshot_FallsBackToLastTradePrice_WhenTradingReferencePriceMissing()
    {
        var tracker = new MarketPriceTracker();
        tracker.OnInfoSnapshot("PETR4", tradingReferencePrice: null, lastTradePrice: 31m);
        Assert.True(tracker.TryGetReferencePrice("PETR4", out var price));
        Assert.Equal(31m, price);
    }

    [Fact]
    public void OnInfoSnapshot_NoOp_WhenBothPricesMissing()
    {
        var tracker = new MarketPriceTracker();
        tracker.OnInfoSnapshot("PETR4", tradingReferencePrice: null, lastTradePrice: null);
        Assert.False(tracker.TryGetReferencePrice("PETR4", out _));
    }

    [Fact]
    public void IsDelisted_IsFalseByDefault_AndTrueAfterNotification()
    {
        var tracker = new MarketPriceTracker();
        Assert.False(tracker.IsDelisted("PETR4"));
        tracker.OnSymbolDelisted("PETR4");
        Assert.True(tracker.IsDelisted("PETR4"));
    }

    [Fact]
    public void PricesAndDelistingAreTrackedIndependentlyPerSymbol()
    {
        var tracker = new MarketPriceTracker();
        tracker.OnTrade("PETR4", 30m);
        tracker.OnTrade("VALE3", 60m);
        Assert.True(tracker.TryGetReferencePrice("PETR4", out var petr));
        Assert.True(tracker.TryGetReferencePrice("VALE3", out var vale));
        Assert.Equal(30m, petr);
        Assert.Equal(60m, vale);
    }
}
