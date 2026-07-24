using B3.Trading.MarketMakerBot;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.MarketMakerBot.Tests;

public class MarketDataFeedTests
{
    [Fact]
    public void NotifySymbolDelisted_UpdatesAvailabilityBeforeRaisingSignal()
    {
        var tracker = new MarketPriceTracker();
        var feed = new MarketDataFeed(tracker, NullLogger.Instance);
        var observedDelisted = false;
        string? observedSymbol = null;
        feed.SymbolAvailabilityChanged += symbol =>
        {
            observedSymbol = symbol;
            observedDelisted = tracker.IsDelisted(symbol);
        };

        feed.NotifySymbolDelisted("PETR4");

        Assert.Equal("PETR4", observedSymbol);
        Assert.True(observedDelisted);
    }
}
