using System.Diagnostics.Metrics;
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
        var measurements = new List<(string Name, long Value, string Symbol)>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == MarketMakerMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, GetSymbol(tags))));
        listener.Start();

        metrics.RecordFillResult("PETR4", FillApplyStatus.Applied);
        metrics.RecordFillResult("PETR4", FillApplyStatus.Duplicate);
        metrics.RecordFillResult("PETR4", FillApplyStatus.Invalid);
        metrics.RecordFillResult("PETR4", FillApplyStatus.Inconsistent);
        metrics.RecordUnknownOrderFill();

        Assert.Contains(("bot.pnl.fills_applied", 1, "PETR4"), measurements);
        Assert.Contains(("bot.pnl.fills_duplicate", 1, "PETR4"), measurements);
        Assert.Contains(("bot.pnl.fills_invalid", 1, "PETR4"), measurements);
        Assert.Contains(("bot.pnl.fills_inconsistent", 1, "PETR4"), measurements);
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
        var measurements = new List<(string Name, double Value, string Symbol)>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == MarketMakerMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, GetSymbol(tags))));
        listener.Start();

        listener.RecordObservableInstruments();
        Assert.DoesNotContain(measurements, item => item.Name == "bot.pnl.unrealized");

        prices.OnTrade("PETR4", 31m);
        prices.SetConnected(true);
        measurements.Clear();
        listener.RecordObservableInstruments();

        var unrealized = Assert.Single(measurements, item => item.Name == "bot.pnl.unrealized");
        Assert.Equal(100d, unrealized.Value);
        Assert.Equal("PETR4", unrealized.Symbol);

        clock.Advance(TimeSpan.FromSeconds(11));
        measurements.Clear();
        listener.RecordObservableInstruments();
        Assert.DoesNotContain(measurements, item => item.Name == "bot.pnl.unrealized");
    }

    private static string GetSymbol(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
            if (tag.Key == "symbol")
                return Assert.IsType<string>(tag.Value);
        return string.Empty;
    }
}
