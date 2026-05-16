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

    // In-flight + completed subscribe tasks, keyed by symbol. Acts as both
    // the dedup cache (a successfully-completed task short-circuits future
    // calls) and the concurrent-first-call coalescer (two threads racing
    // EnsureSubscribedAsync(sym) await the same Task). On failure the entry
    // is removed so the next call retries — pass-3 review (#294) P1 fix.
    //
    // Lazy<Task> is the coalescer: ConcurrentDictionary.GetOrAdd does NOT
    // guarantee single factory invocation under contention, but constructing
    // a Lazy is side-effect-free; only the winning Lazy's Value getter runs
    // the SubscribeAsync. ExecutionAndPublication ensures the inner factory
    // runs exactly once per Lazy.
    //
    // We deliberately do NOT unsubscribe on parent terminal / cancel:
    // subscriptions are cheap on the SDK side, and ref-counting across
    // Iceberg/TWAP/VWAP parents sharing a symbol is a footgun we can defer
    // until a concrete use case asks for it. Trade-off: idle symbols
    // accumulate forever in long-running processes; acceptable for v0 given
    // the small active set.
    private readonly ConcurrentDictionary<string, Lazy<Task>> _subscribed =
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
    /// thread; the SDK call happens at most once per (symbol, process) on the
    /// success path. Concurrent first calls for the same symbol are coalesced
    /// into a single in-flight SDK Subscribe via the
    /// <c>ConcurrentDictionary&lt;string, Task&gt;</c> cache. Must be invoked
    /// before a VWAP parent starts ticking — see
    /// <c>AlgoEngine.OnCreatedAsync</c> for the wiring.
    ///
    /// <para>
    /// Pass-3 review (#294) P1: a failed SDK Subscribe is NOT cached, so the
    /// next caller (next <c>AlgoCreatedSignal</c> / reactor re-evaluation)
    /// retries. Exceptions are swallowed (per existing policy) so the
    /// creation flow isn't broken, but logged as warnings so operators see
    /// the retry loop. Without this, a single SDK not-ready / quota / unknown-
    /// symbol error at cold boot would poison the cache and the algo would
    /// never receive trades.
    /// </para>
    /// </summary>
    public async ValueTask EnsureSubscribedAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        var key = symbol.Trim();

        // Concurrent first-call race: GetOrAdd can invoke the value factory
        // multiple times under contention but only one Lazy wins. Lazy
        // (ExecutionAndPublication, the default) then ensures the inner
        // SubscribeAsync runs exactly once. The first caller's CT is the
        // one observed by the SDK call — subsequent awaiters just observe
        // the shared task's outcome.
        var lazy = _subscribed.GetOrAdd(key, k =>
            new Lazy<Task>(() => SubscribeOnceAsync(k, ct)));
        await lazy.Value.ConfigureAwait(false);
    }

    private async Task SubscribeOnceAsync(string key, CancellationToken ct)
    {
        try
        {
            await _subscriber.SubscribeAsync(key, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller is shutting down. Drop the dedup entry so a later
            // (post-shutdown-cancel) caller can retry. Propagate so the
            // awaiter sees the cancel.
            _subscribed.TryRemove(key, out _);
            throw;
        }
        catch (Exception ex)
        {
            // P1 fix: do NOT keep the symbol marked subscribed on failure.
            // The next EnsureSubscribedAsync(key) call (e.g., next
            // AlgoCreatedSignal or reactor re-evaluation) will retry.
            // Swallow the exception so the creation flow stays intact,
            // but log a warning so operators see the retry loop.
            _subscribed.TryRemove(key, out _);
            _logger?.LogWarning(ex,
                "MarketDataVolumePump SubscribeAsync failed for {Symbol}; entry cleared, next EnsureSubscribedAsync call will retry.",
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
