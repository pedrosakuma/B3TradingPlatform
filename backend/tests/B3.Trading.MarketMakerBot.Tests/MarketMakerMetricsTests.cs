using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using B3.Trading.MarketMakerBot;
using Microsoft.Extensions.Options;

namespace B3.Trading.MarketMakerBot.Tests;

public class MarketMakerMetricsTests
{
    [Fact]
    public void MandatoryMetricSeries_ExposePresentZeroValuesForConfiguredSymbols()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-24T00:00:00Z"));
        var prices = new MarketPriceTracker(clock);
        var options = Options.Create(new MarketMakerBotOptions
        {
            MarketData = new MarketDataOptions { FeedLossPolicy = FeedLossPolicy.PauseAndCancel },
            Instruments =
            [
                new InstrumentConfig { Symbol = "PETR4" },
                new InstrumentConfig { Symbol = "VALE3" },
            ],
        });
        using var listener = new MeterListener();
        var longs = new ConcurrentBag<(string Name, long Value, string Symbol)>();
        var doubles = new ConcurrentBag<(string Name, double Value, string Symbol)>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == MarketMakerMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            longs.Add((instrument.Name, value, GetSymbol(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            doubles.Add((instrument.Name, value, GetSymbol(tags))));
        listener.Start();

        var volatility = new VolatilitySpreadEstimator(options, clock);
        using var metrics = new MarketMakerMetrics(
            new MarketMakerPnlLedger(clock),
            new OrderTracker(clock),
            prices,
            volatility,
            options);
        prices.SetConnected(true);
        prices.OnInfoSnapshot("PETR4", 30m, null);
        prices.OnInfoSnapshot("VALE3", 70m, null);
        listener.RecordObservableInstruments();

        foreach (var symbol in new[] { "PETR4", "VALE3" })
        {
            Assert.Contains(("bot.orders.submit_failed", 0L, symbol), longs);
            Assert.Contains(("bot.orders.ttl_refresh", 0L, symbol), longs);
            Assert.Contains(("bot.orders.ttl_refresh_cancel_rejected", 0L, symbol), longs);
            Assert.Contains(("bot.orders.ttl_refresh_cancel_submit_failed", 0L, symbol), longs);
            Assert.Contains(("bot.orders.quote_restore_rejected", 0L, symbol), longs);
            Assert.Contains(("bot.pnl.fills_duplicate", 0L, symbol), longs);
            Assert.Contains(("bot.orders.safety_cap_hit", 0L, symbol), longs);
            Assert.Contains(("bot.orders.feed_unavailable_cancel", 0L, symbol), longs);
            Assert.Contains(("bot.market_data.reference_eligible_current", 1L, symbol), longs);
            Assert.Contains(("bot.position.net_quantity", 0L, symbol), longs);
            Assert.Contains(("bot.position.average_entry_price", 0d, symbol), doubles);
            Assert.Contains(("bot.pnl.realized", 0d, symbol), doubles);
            Assert.Contains(("bot.pnl.unrealized", 0d, symbol), doubles);
            Assert.Contains(("bot.pnl.total", 0d, symbol), doubles);
        }
        Assert.Contains(("bot.pnl.fills_unknown_order", 0L, "unknown"), longs);
    }

    [Fact]
    public void FillOutcomeCounters_UseBoundedSymbolTags()
    {
        var ledger = new MarketMakerPnlLedger();
        var prices = new MarketPriceTracker();
        var options = Options.Create(new MarketMakerBotOptions());
        var volatility = new VolatilitySpreadEstimator(options, TimeProvider.System);
        using var metrics = new MarketMakerMetrics(ledger, new OrderTracker(), prices, volatility, options);
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
        listener.RecordObservableInstruments();

        Assert.Contains(("bot.pnl.fills_applied", 2, "PETR4"), measurements);
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

        var volatility = new VolatilitySpreadEstimator(options, clock);
        using var metrics = new MarketMakerMetrics(ledger, new OrderTracker(), prices, volatility, options);
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

    [Fact]
    public void InventorySkewGauge_UsesConfiguredSymbolsWithoutDuplicatingPositionState()
    {
        var ledger = new MarketMakerPnlLedger();
        var prices = new MarketPriceTracker();
        var options = Options.Create(new MarketMakerBotOptions
        {
            Instruments =
            [
                new InstrumentConfig
                {
                    Symbol = "PETR4",
                    SecurityId = 1,
                    RefPrice = 30m,
                    TickSize = 0.01m,
                    LotSize = 100,
                    InventorySkew = new InventorySkewConfig
                    {
                        Enabled = true,
                        FullSkewAtLots = 10,
                        MaxSkewTicks = 5m,
                    },
                },
            ],
        });
        Assert.Equal(FillApplyStatus.Applied, ledger.Apply(new OwnFill(
            1, 1, "PETR4", true, 500, 30m, 500, 500, 0, true)).Status);

        var volatility = new VolatilitySpreadEstimator(options, TimeProvider.System);
        using var metrics = new MarketMakerMetrics(ledger, new OrderTracker(), prices, volatility, options);
        using var listener = new MeterListener();
        var measurements = new ConcurrentBag<(string Name, double Value, string Symbol)>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter))
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, GetSymbol(tags))));
        listener.Start();

        listener.RecordObservableInstruments();

        Assert.Contains(("bot.strategy.inventory_skew_ticks", 2.5d, "PETR4"), measurements);
    }

    [Fact]
    public void VolatilityGauges_ReportEstimateAndEffectiveAdditionalTicksBySymbol()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-24T00:00:00Z"));
        var ledger = new MarketMakerPnlLedger();
        var prices = new MarketPriceTracker(clock);
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
                        Window = TimeSpan.FromMinutes(1),
                        MaxSamples = 10,
                        MinSamples = 1,
                        Multiplier = 1.5m,
                        MaxAdditionalSpreadTicks = 20,
                    },
                },
            ],
        });
        var volatility = new VolatilitySpreadEstimator(options, clock);
        volatility.SetConnected(true);
        volatility.OnTrade("PETR4", 30m);
        volatility.OnTrade("PETR4", 30.02m);
        var orders = new OrderTracker(clock);
        Assert.True(orders.TryRegisterSubmit(1, "PETR4", 30m, 100, isBuy: true));
        using var metrics = new MarketMakerMetrics(ledger, orders, prices, volatility, options);
        using var listener = new MeterListener();
        var doubles = new ConcurrentBag<(string Name, double Value, string Symbol)>();
        var longs = new ConcurrentBag<(string Name, long Value, string Symbol)>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter))
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            doubles.Add((instrument.Name, value, GetSymbol(tags))));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            longs.Add((instrument.Name, value, GetSymbol(tags))));
        listener.Start();

        listener.RecordObservableInstruments();

        Assert.Contains(("bot.strategy.volatility_move_estimate_ticks", 2d, "PETR4"), doubles);
        Assert.Contains(("bot.strategy.volatility_additional_half_spread_ticks", 3L, "PETR4"), longs);
        Assert.Contains(("bot.orders.open", 1L, "PETR4"), longs);
        Assert.Contains(("bot.strategy.configured_half_spread_ticks", 5L, "PETR4"), longs);
        Assert.Contains(("bot.strategy.effective_half_spread_ticks", 8L, "PETR4"), longs);
    }

    [Fact]
    public void FeedPolicyMetrics_UseBoundedSymbolReasonAndSourceTags()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-24T00:00:00Z"));
        var ledger = new MarketMakerPnlLedger(clock);
        var prices = new MarketPriceTracker(clock);
        var options = Options.Create(new MarketMakerBotOptions
        {
            MarketData = new MarketDataOptions
            {
                FeedLossPolicy = FeedLossPolicy.PauseAndCancel,
                MaxReferenceAge = TimeSpan.FromSeconds(10),
            },
            Instruments = [new InstrumentConfig { Symbol = "PETR4" }],
        });
        var volatility = new VolatilitySpreadEstimator(options, clock);
        using var metrics = new MarketMakerMetrics(ledger, new OrderTracker(clock), prices, volatility, options);
        using var listener = new MeterListener();
        var counters = new ConcurrentBag<string>();
        var gauges = new ConcurrentBag<string>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter))
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (value == 1)
                counters.Add(instrument.Name);
        });
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            if (tags.ToArray().Any(tag =>
                    tag.Key == "source" && Equals(tag.Value, "trading_reference_price")))
            {
                gauges.Add(instrument.Name);
            }
        });
        listener.Start();

        metrics.RecordFeedAvailabilityTransition(
            "PETR4",
            available: false,
            FeedUnavailableReason.Disconnected);
        metrics.RecordFeedSuppressedDecision(
            "PETR4",
            isBuy: true,
            FeedUnavailableReason.Disconnected);
        metrics.RecordFeedCancel("PETR4", isBuy: true);
        metrics.RecordFeedCancelRejected("PETR4");
        metrics.RecordFeedCancelSubmitFailed("PETR4");
        metrics.RecordFeedCancelRetry("PETR4");
        metrics.RecordCancelAcknowledgementExpired("PETR4", CancelReason.FeedUnavailable);
        prices.SetConnected(true);
        prices.OnInfoSnapshot("PETR4", 30m, null);
        listener.RecordObservableInstruments();

        Assert.Contains("bot.market_data.availability_transition", counters);
        Assert.Contains("bot.market_data.quote_suppressed", counters);
        Assert.Contains("bot.orders.feed_unavailable_cancel", counters);
        Assert.Contains("bot.orders.feed_unavailable_cancel_rejected", counters);
        Assert.Contains("bot.orders.feed_unavailable_cancel_submit_failed", counters);
        Assert.Contains("bot.orders.feed_unavailable_cancel_retry", counters);
        Assert.Contains("bot.orders.cancel_ack_expired", counters);
        Assert.Contains("bot.market_data.reference_age_seconds", gauges);
    }

    private static string GetSymbol(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
            if (tag.Key == "symbol")
                return Assert.IsType<string>(tag.Value);
        return string.Empty;
    }
}
