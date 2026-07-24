using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using B3.Trading.MarketMakerBot;
using Microsoft.Extensions.Options;

namespace B3.Trading.MarketMakerBot.Tests;

public class MarketMakerMetricsTests
{
    [Fact]
    public void FillOutcomeCounters_UseBoundedSymbolTags()
    {
        var ledger = new MarketMakerPnlLedger();
        var prices = new MarketPriceTracker();
        using var metrics = new MarketMakerMetrics(ledger, prices, Options.Create(new MarketMakerBotOptions()));
        using var listener = new MeterListener();
        var measurements = new ConcurrentBag<(string Name, long Value, string Symbol)>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter))
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, GetSymbol(tags))));
        listener.Start();

        metrics.RecordFillResult("PETR4", new(FillApplyStatus.Applied, null));
        metrics.RecordFillResult("PETR4", new(FillApplyStatus.Duplicate, null));
        metrics.RecordFillResult("PETR4", new(FillApplyStatus.Invalid, null));
        metrics.RecordFillResult("PETR4", new(FillApplyStatus.Inconsistent, null));
        metrics.RecordFillResult("PETR4", new(FillApplyStatus.Applied, null, 100, QuantityMismatch: true));
        metrics.RecordUnknownOrderFill();

        Assert.Contains(("bot.pnl.fills_applied", 1, "PETR4"), measurements);
        Assert.Contains(("bot.pnl.fills_duplicate", 1, "PETR4"), measurements);
        Assert.Contains(("bot.pnl.fills_invalid", 1, "PETR4"), measurements);
        Assert.Contains(("bot.pnl.fills_inconsistent", 1, "PETR4"), measurements);
        Assert.Contains(("bot.pnl.fill_delta_mismatch", 1, "PETR4"), measurements);
        Assert.Contains(("bot.pnl.fills_unknown_order", 1, "unknown"), measurements);
    }

    [Fact]
    public void ObservableGauges_UnrealizedPnlIsOmittedUntilFreshLiveMarkExists()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-24T00:00:00Z"));
        var ledger = new MarketMakerPnlLedger();
        var prices = new MarketPriceTracker(clock);
        var options = Options.Create(new MarketMakerBotOptions
        {
            Telemetry = new MarketMakerTelemetryOptions { MarkMaxAge = TimeSpan.FromSeconds(10) },
        });
        Assert.Equal(FillApplyStatus.Applied, ledger.Apply(new OwnFill(
            1, 1, "PETR4", true, 100, 30m, 100, 100, 0, true)).Status);

        using var metrics = new MarketMakerMetrics(ledger, prices, options);
        using var listener = new MeterListener();
        var measurements = new ConcurrentBag<(string Name, double Value, string Symbol)>();
        var publishedNames = new ConcurrentBag<string>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter))
            {
                publishedNames.Add(instrument.Name);
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, GetSymbol(tags))));
        listener.Start();

        Assert.Contains("bot.position.net_quantity", publishedNames);
        Assert.Contains("bot.position.average_entry_price", publishedNames);
        Assert.Contains("bot.pnl.total", publishedNames);

        listener.RecordObservableInstruments();
        Assert.DoesNotContain(measurements, item => item.Name == "bot.pnl.unrealized");
        Assert.DoesNotContain(measurements, item => item.Name == "bot.pnl.total");

        prices.OnTrade("PETR4", 31m);
        prices.SetConnected(true);
        while (measurements.TryTake(out _)) { }
        listener.RecordObservableInstruments();

        var unrealized = Assert.Single(measurements, item => item.Name == "bot.pnl.unrealized");
        Assert.Equal(100d, unrealized.Value);
        Assert.Equal("PETR4", unrealized.Symbol);
        var total = Assert.Single(measurements, item => item.Name == "bot.pnl.total");
        Assert.Equal(100d, total.Value);

        clock.Advance(TimeSpan.FromSeconds(11));
        while (measurements.TryTake(out _)) { }
        listener.RecordObservableInstruments();
        Assert.DoesNotContain(measurements, item => item.Name == "bot.pnl.unrealized");
        Assert.DoesNotContain(measurements, item => item.Name == "bot.pnl.total");
    }

    private static string GetSymbol(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
            if (tag.Key == "symbol")
                return Assert.IsType<string>(tag.Value);
        return string.Empty;
    }
}
