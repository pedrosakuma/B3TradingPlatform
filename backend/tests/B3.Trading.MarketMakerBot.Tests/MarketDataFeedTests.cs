using B3.Trading.MarketMakerBot;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.MarketMakerBot.Tests;

public class MarketDataFeedTests
{
    [Fact]
    public void NotifySymbolDelisted_UpdatesAvailabilityBeforeRaisingSignal()
    {
        var tracker = new MarketPriceTracker();
        var estimator = new VolatilitySpreadEstimator(
            Options.Create(new MarketMakerBotOptions()), TimeProvider.System);
        var feed = new MarketDataFeed(tracker, estimator, NullLogger.Instance);
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

    [Fact]
    public void TradeUpdates_SignalOnlyWhenEffectiveAdditionalTicksChange()
    {
        var tracker = new MarketPriceTracker();
        var options = Options.Create(new MarketMakerBotOptions
        {
            Instruments =
            [
                new InstrumentConfig
                {
                    Symbol = "PETR4",
                    TickSize = 0.01m,
                    VolatilitySpread = new VolatilitySpreadConfig
                    {
                        Enabled = true,
                        MinSamples = 1,
                        MaxSamples = 10,
                        Window = TimeSpan.FromMinutes(1),
                        Multiplier = 1m,
                        MaxAdditionalSpreadTicks = 20,
                    },
                },
            ],
        });
        var estimator = new VolatilitySpreadEstimator(options, TimeProvider.System);
        var feed = new MarketDataFeed(tracker, estimator, NullLogger.Instance);
        var signals = new List<string>();
        feed.VolatilitySpreadChanged += signals.Add;
        feed.NotifyConnectionState(true);

        feed.NotifyTrade("PETR4", 30m);
        feed.NotifyInfoSnapshot("PETR4", 100m, 100m);
        feed.NotifyInfoSnapshot("PETR4", 101m, 101m);
        feed.NotifyTrade("PETR4", 30.02m);
        feed.NotifyTrade("PETR4", 30.04m);
        feed.NotifyTrade("PETR4", 30.04m);
        feed.NotifyTrade("PETR4", 30.04m);

        Assert.Equal(["PETR4", "PETR4"], signals);
        Assert.Equal(1, estimator.GetSnapshot("PETR4").AdditionalSpreadTicks);
    }

    [Fact]
    public void DisconnectAndReconnect_SignalStaticFallbackAndRetainedWidening()
    {
        var tracker = new MarketPriceTracker();
        var options = Options.Create(new MarketMakerBotOptions
        {
            Instruments =
            [
                new InstrumentConfig
                {
                    Symbol = "PETR4",
                    TickSize = 0.01m,
                    VolatilitySpread = new VolatilitySpreadConfig
                    {
                        Enabled = true,
                        MinSamples = 1,
                        MaxSamples = 10,
                        Window = TimeSpan.FromMinutes(1),
                        Multiplier = 1m,
                        MaxAdditionalSpreadTicks = 20,
                    },
                },
            ],
        });
        var estimator = new VolatilitySpreadEstimator(options, TimeProvider.System);
        var feed = new MarketDataFeed(tracker, estimator, NullLogger.Instance);
        var signals = 0;
        feed.VolatilitySpreadChanged += _ => signals++;
        feed.NotifyConnectionState(true);
        feed.NotifyTrade("PETR4", 30m);
        feed.NotifyTrade("PETR4", 30.02m);
        signals = 0;

        feed.NotifyConnectionState(false);
        Assert.Equal(0, estimator.GetSnapshot("PETR4").AdditionalSpreadTicks);
        feed.NotifyConnectionState(true);

        Assert.Equal(2, signals);
        Assert.Equal(2, estimator.GetSnapshot("PETR4").AdditionalSpreadTicks);
    }
}
