using Microsoft.Extensions.Hosting;

namespace B3.Trading.Application.MarketData;

/// <summary>
/// Q3.1 (#281) / pass-1 review #294 P1#1A fix. Bridges live trade
/// prints from <see cref="IMarketDataSubscriber.Trade"/> into the
/// per-symbol <see cref="VolumeCurveEstimator"/> so the VWAP slice
/// scheduler actually sees the venue's intraday volume curve.
///
/// <para>
/// Lives as an <see cref="IHostedService"/> for the same reason as
/// <see cref="MarketDataReferencePrice"/>: DI must construct the pump
/// (and attach its handler to the subscriber) BEFORE the subscriber's
/// connect/subscribe loop starts, otherwise early trade prints in the
/// gap would be silently dropped. <c>StartAsync</c> / <c>StopAsync</c>
/// are no-ops — the work is purely wiring done in the constructor.
/// </para>
///
/// <para>
/// We fan-in every trade for every subscribed symbol; the estimator
/// is sparse per <c>(symbol, day)</c> so untouched symbols cost
/// nothing. <see cref="VolumeCurveEstimator.RecordTrade"/> already
/// guards against non-positive qty / empty symbol, so the handler
/// stays a one-liner.
/// </para>
/// </summary>
public sealed class MarketDataVolumePump : IHostedService
{
    private readonly IMarketDataSubscriber _subscriber;
    private readonly VolumeCurveEstimator _estimator;

    public MarketDataVolumePump(IMarketDataSubscriber subscriber, VolumeCurveEstimator estimator)
    {
        _subscriber = subscriber;
        _estimator = estimator;
        _subscriber.Trade += OnTrade;
    }

    private void OnTrade(MarketTrade t) =>
        _estimator.RecordTrade(t.Symbol, t.Qty, t.ReceivedUtc);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscriber.Trade -= OnTrade;
        return Task.CompletedTask;
    }
}
