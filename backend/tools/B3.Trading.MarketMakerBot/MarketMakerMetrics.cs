using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace B3.Trading.MarketMakerBot;

/// <summary>
/// Per-host metric publisher. The instance owns and disposes its meter, which
/// prevents observable callbacks from retaining test or host singletons after
/// their service provider is disposed.
/// </summary>
public sealed class MarketMakerMetrics : IDisposable
{
    public const string MeterName = "B3.Trading.MarketMakerBot";
    private const string UnknownSymbol = "unknown";

    private readonly MarketMakerPnlLedger _ledger;
    private readonly OrderTracker _orders;
    private readonly MarketPriceTracker _prices;
    private readonly VolatilitySpreadEstimator _volatilitySpread;
    private readonly IReadOnlyList<InstrumentConfig> _instruments;
    private readonly TimeSpan _markMaxAge;
    private readonly TimeSpan _maxReferenceAge;
    private readonly FeedLossPolicy _feedLossPolicy;
    private readonly Meter _meter;
    private readonly ObservableMetricCounter _ordersSubmitted;
    private readonly ObservableMetricCounter _ordersSubmitFailed;
    private readonly ObservableMetricCounter _fillsReceived;
    private readonly ObservableMetricCounter _fillsApplied;
    private readonly ObservableMetricCounter _fillsUnknownOrder;
    private readonly ObservableMetricCounter _fillsDuplicate;
    private readonly ObservableMetricCounter _fillsInvalid;
    private readonly ObservableMetricCounter _fillsInconsistent;
    private readonly ObservableMetricCounter _fillDeltaMismatch;
    private readonly ObservableMetricCounter _rejects;
    private readonly ObservableMetricCounter _cancelled;
    private readonly ObservableMetricCounter _staleOrdersCancelled;
    private readonly ObservableMetricCounter _staleCancelRejected;
    private readonly ObservableMetricCounter _staleCancelSubmitFailed;
    private readonly ObservableMetricCounter _safetyCapHits;
    private readonly ObservableMetricCounter _bookDrivenRequotes;
    private readonly ObservableMetricCounter _bookDrivenRequoteSubmitFailed;
    private readonly ObservableMetricCounter _bookDrivenRequoteCancelRejected;
    private readonly ObservableMetricCounter _feedAvailabilityTransitions;
    private readonly ObservableMetricCounter _feedSuppressedDecisions;
    private readonly ObservableMetricCounter _feedCancels;
    private readonly ObservableMetricCounter _feedCancelRejected;
    private readonly ObservableMetricCounter _feedCancelSubmitFailed;
    private readonly ObservableMetricCounter _feedCancelRetries;
    private readonly ObservableMetricCounter _cancelAcknowledgementsExpired;

    public MarketMakerMetrics(
        MarketMakerPnlLedger ledger,
        OrderTracker orders,
        MarketPriceTracker prices,
        VolatilitySpreadEstimator volatilitySpread,
        IOptions<MarketMakerBotOptions> options)
    {
        _ledger = ledger;
        _orders = orders;
        _prices = prices;
        _volatilitySpread = volatilitySpread;
        _instruments = options.Value.Instruments;
        _markMaxAge = options.Value.Telemetry.MarkMaxAge;
        _maxReferenceAge = options.Value.MarketData.MaxReferenceAge;
        _feedLossPolicy = options.Value.MarketData.FeedLossPolicy;
        _meter = new Meter(MeterName, "1.0.0");
        _ordersSubmitted = new(_meter, "bot.orders.submitted");
        _ordersSubmitFailed = new(_meter, "bot.orders.submit_failed");
        _fillsReceived = new(_meter, "bot.fills.received");
        _fillsApplied = new(_meter, "bot.pnl.fills_applied");
        _fillsUnknownOrder = new(_meter, "bot.pnl.fills_unknown_order");
        _fillsDuplicate = new(_meter, "bot.pnl.fills_duplicate");
        _fillsInvalid = new(_meter, "bot.pnl.fills_invalid");
        _fillsInconsistent = new(_meter, "bot.pnl.fills_inconsistent");
        _fillDeltaMismatch = new(_meter, "bot.pnl.fill_delta_mismatch");
        _rejects = new(_meter, "bot.orders.rejected");
        _cancelled = new(_meter, "bot.orders.cancelled");
        _staleOrdersCancelled = new(_meter, "bot.orders.stale_cancelled");
        _staleCancelRejected = new(_meter, "bot.orders.stale_cancel_rejected");
        _staleCancelSubmitFailed = new(_meter, "bot.orders.stale_cancel_submit_failed");
        _safetyCapHits = new(_meter, "bot.orders.safety_cap_hit");
        _bookDrivenRequotes = new(_meter, "bot.orders.book_driven_requote");
        _bookDrivenRequoteSubmitFailed =
            new(_meter, "bot.orders.book_driven_requote_submit_failed");
        _bookDrivenRequoteCancelRejected =
            new(_meter, "bot.orders.book_driven_requote_cancel_rejected");
        _feedAvailabilityTransitions =
            new(_meter, "bot.market_data.availability_transition");
        _feedSuppressedDecisions =
            new(_meter, "bot.market_data.quote_suppressed");
        _feedCancels = new(_meter, "bot.orders.feed_unavailable_cancel");
        _feedCancelRejected =
            new(_meter, "bot.orders.feed_unavailable_cancel_rejected");
        _feedCancelSubmitFailed =
            new(_meter, "bot.orders.feed_unavailable_cancel_submit_failed");
        _feedCancelRetries =
            new(_meter, "bot.orders.feed_unavailable_cancel_retry");
        _cancelAcknowledgementsExpired =
            new(_meter, "bot.orders.cancel_ack_expired");
        InitializeCounterSeries();

        _meter.CreateObservableGauge("bot.position.net_quantity", ObservePositions);
        _meter.CreateObservableGauge(
            "bot.pnl.reconciliation_required",
            () => _ledger.ReconciliationRequired ? 1L : 0L);
        _meter.CreateObservableGauge("bot.position.average_entry_price", ObserveAverageCosts);
        _meter.CreateObservableGauge("bot.orders.open", ObserveOpenOrders);
        _meter.CreateObservableGauge(
            "bot.strategy.configured_half_spread_ticks",
            ObserveConfiguredHalfSpreadTicks);
        _meter.CreateObservableGauge(
            "bot.strategy.effective_half_spread_ticks",
            ObserveEffectiveHalfSpreadTicks);
        _meter.CreateObservableGauge("bot.strategy.inventory_skew_ticks", ObserveInventorySkewTicks);
        _meter.CreateObservableGauge("bot.strategy.volatility_move_estimate_ticks",
            ObserveVolatilityMoveEstimateTicks);
        _meter.CreateObservableGauge("bot.strategy.volatility_additional_half_spread_ticks",
            ObserveVolatilityAdditionalHalfSpreadTicks);
        _meter.CreateObservableGauge("bot.pnl.realized", ObserveRealizedPnl);
        _meter.CreateObservableGauge("bot.pnl.unrealized", ObserveUnrealizedPnl);
        _meter.CreateObservableGauge("bot.pnl.total", ObserveTotalPnl);
        _meter.CreateObservableGauge("bot.market_data.reference_age_seconds", ObserveReferenceAge);
        _meter.CreateObservableGauge("bot.market_data.reference_eligible", ObserveReferenceEligibility);
        _meter.CreateObservableGauge(
            "bot.market_data.reference_eligible_current",
            ObserveCurrentReferenceEligibility);
    }

    public void RecordOrderSubmitted(string symbol, bool isBuy) =>
        _ordersSubmitted.Add(1, SymbolTag(symbol), SideTag(isBuy));

    public void RecordOrderSubmitFailed(string symbol) =>
        _ordersSubmitFailed.Add(1, SymbolTag(symbol));

    public void RecordFillReceived(string? symbol) =>
        _fillsReceived.Add(1, SymbolTag(symbol));

    public void RecordFillResult(string symbol, FillApplyResult result)
    {
        var counter = result.Status switch
        {
            FillApplyStatus.Applied => _fillsApplied,
            FillApplyStatus.Duplicate => _fillsDuplicate,
            FillApplyStatus.Invalid => _fillsInvalid,
            FillApplyStatus.Inconsistent => _fillsInconsistent,
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
        counter.Add(1, SymbolTag(symbol));
        if (result.QuantityMismatch)
            _fillDeltaMismatch.Add(1, SymbolTag(symbol));
    }

    public void RecordUnknownOrderFill() =>
        _fillsUnknownOrder.Add(1, SymbolTag(null));

    public void RecordRejected(string? symbol) => _rejects.Add(1, SymbolTag(symbol));
    public void RecordCancelled() => _cancelled.Add(1);
    public void RecordStaleOrderCancelled(string symbol) => _staleOrdersCancelled.Add(1, SymbolTag(symbol));
    public void RecordStaleCancelRejected(string symbol) => _staleCancelRejected.Add(1, SymbolTag(symbol));
    public void RecordStaleCancelSubmitFailed(string symbol) => _staleCancelSubmitFailed.Add(1, SymbolTag(symbol));
    public void RecordSafetyCapHit(string symbol) => _safetyCapHits.Add(1, SymbolTag(symbol));
    public void RecordBookDrivenRequote(string symbol, bool isBuy) =>
        _bookDrivenRequotes.Add(1, SymbolTag(symbol), SideTag(isBuy));
    public void RecordBookDrivenRequoteSubmitFailed(string symbol) =>
        _bookDrivenRequoteSubmitFailed.Add(1, SymbolTag(symbol));
    public void RecordBookDrivenRequoteCancelRejected(string symbol) =>
        _bookDrivenRequoteCancelRejected.Add(1, SymbolTag(symbol));
    public void RecordFeedAvailabilityTransition(
        string symbol,
        bool available,
        FeedUnavailableReason reason) =>
        _feedAvailabilityTransitions.Add(
            1,
            SymbolTag(symbol),
            new("available", available),
            new("reason", FeedReason(reason)));
    public void RecordFeedSuppressedDecision(string symbol, bool isBuy, FeedUnavailableReason reason) =>
        _feedSuppressedDecisions.Add(
            1,
            SymbolTag(symbol),
            SideTag(isBuy),
            new("reason", FeedReason(reason)));
    public void RecordFeedCancel(string symbol, bool isBuy) =>
        _feedCancels.Add(1, SymbolTag(symbol), SideTag(isBuy));
    public void RecordFeedCancelRejected(string symbol) =>
        _feedCancelRejected.Add(1, SymbolTag(symbol));
    public void RecordFeedCancelSubmitFailed(string symbol) =>
        _feedCancelSubmitFailed.Add(1, SymbolTag(symbol));
    public void RecordFeedCancelRetry(string symbol) =>
        _feedCancelRetries.Add(1, SymbolTag(symbol));
    public void RecordCancelAcknowledgementExpired(string symbol, CancelReason reason) =>
        _cancelAcknowledgementsExpired.Add(
            1,
            SymbolTag(symbol),
            new("reason", CancelReasonName(reason)));

    public void Dispose() => _meter.Dispose();

    internal Meter Meter => _meter;

    private void InitializeCounterSeries()
    {
        _fillsUnknownOrder.Add(0, SymbolTag(null));
        _cancelled.Add(0);

        foreach (var instrument in _instruments)
        {
            var symbol = SymbolTag(instrument.Symbol);
            foreach (var isBuy in new[] { true, false })
            {
                var side = SideTag(isBuy);
                _ordersSubmitted.Add(0, symbol, side);
                _bookDrivenRequotes.Add(0, symbol, side);
                _feedCancels.Add(0, symbol, side);
                _feedSuppressedDecisions.Add(
                    0,
                    symbol,
                    side,
                    new("reason", FeedReason(FeedUnavailableReason.Disconnected)));
            }

            _ordersSubmitFailed.Add(0, symbol);
            _fillsReceived.Add(0, symbol);
            _fillsApplied.Add(0, symbol);
            _fillsDuplicate.Add(0, symbol);
            _fillsInvalid.Add(0, symbol);
            _fillsInconsistent.Add(0, symbol);
            _fillDeltaMismatch.Add(0, symbol);
            _rejects.Add(0, symbol);
            _staleOrdersCancelled.Add(0, symbol);
            _staleCancelRejected.Add(0, symbol);
            _staleCancelSubmitFailed.Add(0, symbol);
            _safetyCapHits.Add(0, symbol);
            _bookDrivenRequoteSubmitFailed.Add(0, symbol);
            _bookDrivenRequoteCancelRejected.Add(0, symbol);
            _feedAvailabilityTransitions.Add(
                0,
                symbol,
                new("available", false),
                new("reason", FeedReason(FeedUnavailableReason.Disconnected)));
            _feedCancelRejected.Add(0, symbol);
            _feedCancelSubmitFailed.Add(0, symbol);
            _feedCancelRetries.Add(0, symbol);
            _cancelAcknowledgementsExpired.Add(
                0,
                symbol,
                new("reason", CancelReasonName(CancelReason.FeedUnavailable)));
        }
    }

    private IEnumerable<Measurement<long>> ObservePositions() =>
        ObservePositionSymbols().Select(symbol =>
            new Measurement<long>(
                _ledger.TryGetSnapshot(symbol, out var snapshot) ? snapshot.Position : 0L,
                SymbolTag(symbol)));

    private IEnumerable<Measurement<double>> ObserveAverageCosts() =>
        ObservePositionSymbols().Select(symbol =>
            new Measurement<double>(
                _ledger.TryGetSnapshot(symbol, out var snapshot)
                    ? (double)snapshot.AverageCost
                    : 0d,
                SymbolTag(symbol)));

    private IEnumerable<Measurement<long>> ObserveOpenOrders() =>
        _instruments.Select(instrument =>
            new Measurement<long>(_orders.InFlightCount(instrument.Symbol), SymbolTag(instrument.Symbol)));

    private IEnumerable<Measurement<long>> ObserveConfiguredHalfSpreadTicks() =>
        _instruments.Select(instrument =>
            new Measurement<long>(instrument.SpreadTicks, SymbolTag(instrument.Symbol)));

    private IEnumerable<Measurement<long>> ObserveEffectiveHalfSpreadTicks()
    {
        foreach (var instrument in _instruments)
        {
            var additionalTicks = _volatilitySpread.GetSnapshot(instrument.Symbol).AdditionalSpreadTicks;
            yield return new Measurement<long>(
                checked(instrument.SpreadTicks + additionalTicks),
                SymbolTag(instrument.Symbol));
        }
    }

    private IEnumerable<Measurement<double>> ObserveInventorySkewTicks()
    {
        foreach (var instrument in _instruments)
        {
            if (!instrument.InventorySkew.Enabled)
                continue;
            var netQuantity = _ledger.TryGetSnapshot(instrument.Symbol, out var position)
                ? position.Position
                : 0L;
            var skew = InventorySkewCalculator.Calculate(
                instrument.InventorySkew,
                netQuantity,
                instrument.LotSize,
                instrument.TickSize);
            yield return new Measurement<double>((double)skew.SkewTicks, SymbolTag(instrument.Symbol));
        }
    }

    private IEnumerable<Measurement<double>> ObserveVolatilityMoveEstimateTicks()
    {
        foreach (var instrument in _instruments)
        {
            if (!instrument.VolatilitySpread.Enabled)
                continue;
            var snapshot = _volatilitySpread.GetSnapshot(instrument.Symbol);
            if (snapshot.MoveEstimateTicks is { } estimate)
                yield return new Measurement<double>((double)estimate, SymbolTag(instrument.Symbol));
        }
    }

    private IEnumerable<Measurement<long>> ObserveVolatilityAdditionalHalfSpreadTicks()
    {
        foreach (var instrument in _instruments)
        {
            if (!instrument.VolatilitySpread.Enabled)
                continue;
            var snapshot = _volatilitySpread.GetSnapshot(instrument.Symbol);
            yield return new Measurement<long>(
                snapshot.AdditionalSpreadTicks,
                SymbolTag(instrument.Symbol));
        }
    }

    private IEnumerable<Measurement<double>> ObserveRealizedPnl() =>
        ObservePositionSymbols().Select(symbol =>
            new Measurement<double>(
                _ledger.TryGetSnapshot(symbol, out var snapshot)
                    ? (double)snapshot.RealizedPnl
                    : 0d,
                SymbolTag(symbol)));

    private IEnumerable<Measurement<double>> ObserveUnrealizedPnl()
    {
        foreach (var symbol in ObservePositionSymbols())
        {
            if (_prices.TryGetFreshMark(symbol, _markMaxAge, out var mark))
            {
                var value = _ledger.TryGetSnapshot(symbol, out var snapshot)
                    ? (double)snapshot.UnrealizedPnl(mark.Price)
                    : 0d;
                yield return new Measurement<double>(
                    value,
                    SymbolTag(symbol));
            }
        }
    }

    private IEnumerable<Measurement<double>> ObserveTotalPnl()
    {
        foreach (var symbol in ObservePositionSymbols())
        {
            if (_prices.TryGetFreshMark(symbol, _markMaxAge, out var mark))
            {
                var value = _ledger.TryGetSnapshot(symbol, out var snapshot)
                    ? (double)snapshot.TotalPnl(mark.Price)
                    : 0d;
                yield return new Measurement<double>(
                    value,
                    SymbolTag(symbol));
            }
        }
    }

    private IEnumerable<string> ObservePositionSymbols() =>
        _instruments.Select(instrument => instrument.Symbol)
            .Concat(_ledger.SnapshotAll().Select(snapshot => snapshot.Symbol))
            .Distinct(StringComparer.Ordinal);

    private IEnumerable<Measurement<double>> ObserveReferenceAge()
    {
        foreach (var instrument in _instruments)
        {
            var availability = _prices.GetAvailability(instrument.Symbol, _maxReferenceAge);
            if (availability.ReferenceAge is not { } age ||
                availability.LastValidMark is not { } mark)
            {
                continue;
            }
            yield return new Measurement<double>(
                Math.Max(0d, age.TotalSeconds),
                SymbolTag(instrument.Symbol),
                new("source", ReferenceSource(mark.Source)));
        }
    }

    private IEnumerable<Measurement<long>> ObserveReferenceEligibility()
    {
        if (_feedLossPolicy != FeedLossPolicy.PauseAndCancel)
            yield break;
        foreach (var instrument in _instruments)
        {
            var availability = _prices.GetAvailability(instrument.Symbol, _maxReferenceAge);
            yield return new Measurement<long>(
                availability.IsEligible ? 1 : 0,
                SymbolTag(instrument.Symbol),
                new("reason", FeedReason(availability.UnavailableReason)));
        }
    }

    private IEnumerable<Measurement<long>> ObserveCurrentReferenceEligibility()
    {
        if (_feedLossPolicy != FeedLossPolicy.PauseAndCancel)
            yield break;
        foreach (var instrument in _instruments)
        {
            var availability = _prices.GetAvailability(instrument.Symbol, _maxReferenceAge);
            yield return new Measurement<long>(
                availability.IsEligible ? 1 : 0,
                SymbolTag(instrument.Symbol));
        }
    }

    private static KeyValuePair<string, object?> SymbolTag(string? symbol) =>
        new("symbol", string.IsNullOrWhiteSpace(symbol) ? UnknownSymbol : symbol);

    private static KeyValuePair<string, object?> SideTag(bool isBuy) =>
        new("side", isBuy ? "buy" : "sell");

    private static string FeedReason(FeedUnavailableReason reason) => reason switch
    {
        FeedUnavailableReason.None => "none",
        FeedUnavailableReason.Disconnected => "disconnected",
        FeedUnavailableReason.AwaitingCurrentEpochReference => "awaiting_current_epoch_reference",
        FeedUnavailableReason.SubscriptionError => "subscription_error",
        FeedUnavailableReason.StaleReference => "stale_reference",
        _ => "unknown",
    };

    private static string CancelReasonName(CancelReason reason) => reason switch
    {
        CancelReason.StaleOrder => "stale_order",
        CancelReason.PriceDrift => "price_drift",
        CancelReason.InventoryStrategy => "inventory_strategy",
        CancelReason.VolatilityStrategy => "volatility_strategy",
        CancelReason.FeedUnavailable => "feed_unavailable",
        _ => "unknown",
    };

    private static string ReferenceSource(ReferencePriceSource source) => source switch
    {
        ReferencePriceSource.Trade => "trade",
        ReferencePriceSource.TradingReferencePrice => "trading_reference_price",
        ReferencePriceSource.LastTradePrice => "last_trade_price",
        _ => "unknown",
    };

    private sealed class ObservableMetricCounter
    {
        private readonly ConcurrentDictionary<string, CounterState> _series = new(StringComparer.Ordinal);

        public ObservableMetricCounter(Meter meter, string name)
        {
            meter.CreateObservableCounter(name, Observe);
        }

        public void Add(long delta, params KeyValuePair<string, object?>[] tags)
        {
            var key = string.Join(
                '\u001f',
                tags.Select(tag => $"{tag.Key}={tag.Value}"));
            var state = _series.GetOrAdd(key, _ => new CounterState(tags));
            Interlocked.Add(ref state.Value, delta);
        }

        private IEnumerable<Measurement<long>> Observe() =>
            _series.Values.Select(state =>
                new Measurement<long>(Volatile.Read(ref state.Value), state.Tags));

        private sealed class CounterState(KeyValuePair<string, object?>[] tags)
        {
            public KeyValuePair<string, object?>[] Tags { get; } = tags;
            public long Value;
        }
    }
}
