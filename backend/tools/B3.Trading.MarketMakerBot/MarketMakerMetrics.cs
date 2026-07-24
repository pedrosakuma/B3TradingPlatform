using Microsoft.Extensions.Options;
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
    private readonly MarketPriceTracker _prices;
    private readonly TimeSpan _markMaxAge;
    private readonly Meter _meter;
    private readonly Counter<long> _ordersSubmitted;
    private readonly Counter<long> _ordersSubmitFailed;
    private readonly Counter<long> _fillsReceived;
    private readonly Counter<long> _fillsApplied;
    private readonly Counter<long> _fillsUnknownOrder;
    private readonly Counter<long> _fillsDuplicate;
    private readonly Counter<long> _fillsInvalid;
    private readonly Counter<long> _fillsInconsistent;
    private readonly Counter<long> _rejects;
    private readonly Counter<long> _cancelled;
    private readonly Counter<long> _staleOrdersCancelled;
    private readonly Counter<long> _staleCancelRejected;
    private readonly Counter<long> _staleCancelSubmitFailed;
    private readonly Counter<long> _safetyCapHits;
    private readonly Counter<long> _bookDrivenRequotes;
    private readonly Counter<long> _bookDrivenRequoteSubmitFailed;
    private readonly Counter<long> _bookDrivenRequoteCancelRejected;

    public MarketMakerMetrics(
        MarketMakerPnlLedger ledger,
        MarketPriceTracker prices,
        IOptions<MarketMakerBotOptions> options)
    {
        _ledger = ledger;
        _prices = prices;
        _markMaxAge = options.Value.Telemetry.MarkMaxAge;
        _meter = new Meter(MeterName, "1.0.0");
        _ordersSubmitted = _meter.CreateCounter<long>("bot.orders.submitted");
        _ordersSubmitFailed = _meter.CreateCounter<long>("bot.orders.submit_failed");
        _fillsReceived = _meter.CreateCounter<long>("bot.fills.received");
        _fillsApplied = _meter.CreateCounter<long>("bot.pnl.fills_applied");
        _fillsUnknownOrder = _meter.CreateCounter<long>("bot.pnl.fills_unknown_order");
        _fillsDuplicate = _meter.CreateCounter<long>("bot.pnl.fills_duplicate");
        _fillsInvalid = _meter.CreateCounter<long>("bot.pnl.fills_invalid");
        _fillsInconsistent = _meter.CreateCounter<long>("bot.pnl.fills_inconsistent");
        _rejects = _meter.CreateCounter<long>("bot.orders.rejected");
        _cancelled = _meter.CreateCounter<long>("bot.orders.cancelled");
        _staleOrdersCancelled = _meter.CreateCounter<long>("bot.orders.stale_cancelled");
        _staleCancelRejected = _meter.CreateCounter<long>("bot.orders.stale_cancel_rejected");
        _staleCancelSubmitFailed = _meter.CreateCounter<long>("bot.orders.stale_cancel_submit_failed");
        _safetyCapHits = _meter.CreateCounter<long>("bot.orders.safety_cap_hit");
        _bookDrivenRequotes = _meter.CreateCounter<long>("bot.orders.book_driven_requote");
        _bookDrivenRequoteSubmitFailed =
            _meter.CreateCounter<long>("bot.orders.book_driven_requote_submit_failed");
        _bookDrivenRequoteCancelRejected =
            _meter.CreateCounter<long>("bot.orders.book_driven_requote_cancel_rejected");

        _meter.CreateObservableGauge("bot.position.quantity", ObservePositions);
        _meter.CreateObservableGauge("bot.position.average_cost", ObserveAverageCosts);
        _meter.CreateObservableGauge("bot.pnl.realized", ObserveRealizedPnl);
        _meter.CreateObservableGauge("bot.pnl.unrealized", ObserveUnrealizedPnl);
    }

    public void RecordOrderSubmitted(string symbol, bool isBuy) =>
        _ordersSubmitted.Add(1, SymbolTag(symbol), SideTag(isBuy));

    public void RecordOrderSubmitFailed(string symbol) =>
        _ordersSubmitFailed.Add(1, SymbolTag(symbol));

    public void RecordFillReceived(string? symbol) =>
        _fillsReceived.Add(1, SymbolTag(symbol));

    public void RecordFillResult(string symbol, FillApplyStatus status)
    {
        var counter = status switch
        {
            FillApplyStatus.Applied => _fillsApplied,
            FillApplyStatus.Duplicate => _fillsDuplicate,
            FillApplyStatus.Invalid => _fillsInvalid,
            FillApplyStatus.Inconsistent => _fillsInconsistent,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        counter.Add(1, SymbolTag(symbol));
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

    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<long>> ObservePositions() =>
        _ledger.SnapshotAll().Select(snapshot =>
            new Measurement<long>(snapshot.Position, SymbolTag(snapshot.Symbol)));

    private IEnumerable<Measurement<double>> ObserveAverageCosts() =>
        _ledger.SnapshotAll().Select(snapshot =>
            new Measurement<double>((double)snapshot.AverageCost, SymbolTag(snapshot.Symbol)));

    private IEnumerable<Measurement<double>> ObserveRealizedPnl() =>
        _ledger.SnapshotAll().Select(snapshot =>
            new Measurement<double>((double)snapshot.RealizedPnl, SymbolTag(snapshot.Symbol)));

    private IEnumerable<Measurement<double>> ObserveUnrealizedPnl()
    {
        foreach (var snapshot in _ledger.SnapshotAll())
        {
            if (_prices.TryGetFreshMark(snapshot.Symbol, _markMaxAge, out var mark))
            {
                yield return new Measurement<double>(
                    (double)snapshot.UnrealizedPnl(mark.Price),
                    SymbolTag(snapshot.Symbol));
            }
        }
    }

    private static KeyValuePair<string, object?> SymbolTag(string? symbol) =>
        new("symbol", string.IsNullOrWhiteSpace(symbol) ? UnknownSymbol : symbol);

    private static KeyValuePair<string, object?> SideTag(bool isBuy) =>
        new("side", isBuy ? "buy" : "sell");
}
