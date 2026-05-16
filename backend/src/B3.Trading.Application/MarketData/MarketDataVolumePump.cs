using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
///
/// <para>
/// Pass-2 review (#294) P1: also exposes <see cref="EnsureSubscribedAsync"/>
/// so the algo engine can demand-subscribe to symbols that VWAP parents
/// reference but the static <c>Trading:MarketData:Symbols</c> list does
/// not cover. Without dynamic subscribe the SDK never delivers trade
/// prints for those symbols, the estimator stays empty, and with
/// <see cref="VwapParameters.ParticipationCap"/> set the slice scheduler
/// emits zero-qty slices until window expiry → silent no-op algo.
/// </para>
/// </summary>
public sealed class MarketDataVolumePump : IHostedService
{
    private readonly IMarketDataSubscriber _subscriber;
    private readonly VolumeCurveEstimator _estimator;
    private readonly ILogger<MarketDataVolumePump>? _logger;

    // Subscribed-symbol set, kept idempotent so repeated EnsureSubscribed
    // calls (multiple VWAP parents on the same symbol, reactor re-evaluations)
    // map to exactly one SDK Subscribe per process lifetime. We deliberately
    // do NOT unsubscribe on parent terminal / cancel: subscriptions are
    // cheap on the SDK side, and ref-counting across Iceberg/TWAP/VWAP
    // parents sharing a symbol is a footgun we can defer until a concrete
    // use case asks for it. Trade-off: idle symbols accumulate forever in
    // long-running processes; acceptable for v0 given the small active set.
    private readonly ConcurrentDictionary<string, byte> _subscribed =
        new(StringComparer.OrdinalIgnoreCase);

    public MarketDataVolumePump(
        IMarketDataSubscriber subscriber,
        VolumeCurveEstimator estimator,
        ILogger<MarketDataVolumePump>? logger = null)
    {
        _subscriber = subscriber;
        _estimator = estimator;
        _logger = logger;
        _subscriber.Trade += OnTrade;
    }

    private void OnTrade(MarketTrade t) =>
        _estimator.RecordTrade(t.Symbol, t.Qty, t.ReceivedUtc);

    /// <summary>
    /// Idempotently subscribes the underlying <see cref="IMarketDataSubscriber"/>
    /// to trade prints for <paramref name="symbol"/>. Safe to call from any
    /// thread; the SDK call happens at most once per (symbol, process).
    /// Must be invoked before a VWAP parent starts ticking — see
    /// <c>AlgoEngine.OnCreatedAsync</c> for the wiring.
    /// </summary>
    public async ValueTask EnsureSubscribedAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        var key = symbol.Trim();
        if (!_subscribed.TryAdd(key, 0)) return;

        try
        {
            await _subscriber.SubscribeAsync(key, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller is shutting down; let it propagate but allow a later
            // retry by clearing the dedup marker.
            _subscribed.TryRemove(key, out _);
            throw;
        }
        catch (Exception ex)
        {
            // Keep the symbol marked subscribed so we don't hammer the SDK
            // on every algo creation: the SDK's auto-resubscribe-on-reconnect
            // will retry. Surface the failure so ops sees what happened.
            _logger?.LogWarning(ex,
                "MarketDataVolumePump SubscribeAsync failed for {Symbol}; SDK auto-resubscribe will retry on next reconnect.",
                key);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscriber.Trade -= OnTrade;
        return Task.CompletedTask;
    }
}
