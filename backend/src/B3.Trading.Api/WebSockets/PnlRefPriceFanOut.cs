using System.Collections.Concurrent;
using System.Threading.Channels;
using B3.Trading.Application;
using B3.Trading.Application.MarketData;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Api.WebSockets;

/// <summary>
/// Pass-1 review (#278) P1#3. Bridges
/// <see cref="MarketDataReferencePrice.PriceChanged"/> to the
/// <c>pnl.me</c> WS channel: when a symbol's mark moves, every owner
/// holding an open position in that symbol with active subscribers
/// receives a fresh <see cref="PnlTodayDto"/> delta so unrealized
/// P&amp;L tracks the latest refprice without waiting for the next fill.
///
/// <para>
/// <b>Throttling.</b> L1 ticks can be very frequent (tens to hundreds
/// per second per active symbol). To avoid flooding subscribers and
/// the WS hub channel, fan-out is coalesced PER SYMBOL: the most
/// recent refprice tick for a symbol enqueues a publish job, and a
/// single drain task processes the queue with a minimum gap of
/// <see cref="ThrottleGap"/> between successive publishes for the
/// same symbol. Multiple ticks arriving inside the gap collapse into
/// one published delta carrying the latest mark; only the most recent
/// price ever reaches subscribers, which is the right semantic for a
/// projection of "current mark to position".
/// </para>
///
/// <para>
/// <b>Lock ordering.</b> The fan-out is invoked from the drain task
/// (NOT from <see cref="MarketDataReferencePrice"/>'s tick handler)
/// so the price-cache write path stays tight. The drain only takes
/// read locks (PositionKeeper enumeration + PnlKeeper dictionary
/// reads + SubscriptionManager publish under its own per-owner lock)
/// — never the EventDispatcher lock and never the PnlKeeper per-key
/// fill lock — so it cannot deadlock with the live ER path.
/// </para>
/// </summary>
public sealed class PnlRefPriceFanOut : IHostedService, IAsyncDisposable
{
    /// <summary>
    /// Minimum gap between successive <c>pnl.me</c> publishes for the
    /// same symbol. 200 ms gives ≤ 5 publishes/sec/symbol — enough
    /// for a smooth UI without saturating the WS channel under busy
    /// L1 markets. Tunable if real workloads show different needs.
    /// </summary>
    public static readonly TimeSpan ThrottleGap = TimeSpan.FromMilliseconds(200);

    private readonly MarketDataReferencePrice _refPrice;
    private readonly SubscriptionManager _subs;
    private readonly PnlKeeper _pnl;
    private readonly PositionKeeper _positions;
    private readonly IReferencePrice _refPriceLookup;
    private readonly ILogger<PnlRefPriceFanOut>? _logger;
    private readonly TimeProvider _clock;

    // Per-symbol last-publish tick. We use the throttle gap to coalesce
    // bursts; the dictionary grows with the symbol cardinality, which
    // is bounded by the configured subscription set in production.
    private readonly ConcurrentDictionary<string, long> _lastPublishedTicks =
        new(StringComparer.Ordinal);

    // Per-symbol "publish pending" flag so a single drain entry exists
    // per symbol no matter how many ticks coalesce into it.
    private readonly ConcurrentDictionary<string, byte> _pending =
        new(StringComparer.Ordinal);

    // Per-symbol "deferred republish in flight" flag — at most one
    // Task.Delay continuation is queued per symbol so a hot tick stream
    // can't accumulate background timers.
    private readonly ConcurrentDictionary<string, byte> _deferred =
        new(StringComparer.Ordinal);

    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cts = new();
    private Task? _drainTask;
    private int _stopped;

    public PnlRefPriceFanOut(
        MarketDataReferencePrice refPrice,
        SubscriptionManager subs,
        PnlKeeper pnl,
        PositionKeeper positions,
        IReferencePrice refPriceLookup,
        TimeProvider clock,
        ILogger<PnlRefPriceFanOut>? logger = null)
    {
        _refPrice = refPrice;
        _subs = subs;
        _pnl = pnl;
        _positions = positions;
        _refPriceLookup = refPriceLookup;
        _clock = clock;
        _logger = logger;
        _refPrice.PriceChanged += OnPriceChanged;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _drainTask = Task.Run(DrainAsync);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _refPrice.PriceChanged -= OnPriceChanged;
        _cts.Cancel();
        _channel.Writer.TryComplete();
        if (_drainTask is not null)
        {
            try { await _drainTask.ConfigureAwait(false); } catch { /* best-effort */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _cts.Dispose();
    }

    private void OnPriceChanged(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return;
        // Coalesce: at most one queued entry per symbol.
        if (_pending.TryAdd(symbol, 0))
            _channel.Writer.TryWrite(symbol);
    }

    private async Task DrainAsync()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var symbol))
                {
                    _pending.TryRemove(symbol, out _);
                    try { PublishForSymbol(symbol); }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex,
                            "pnl refprice fan-out failed for symbol={Symbol}", symbol);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private void PublishForSymbol(string symbol)
    {
        // Throttle: skip if we published this symbol within ThrottleGap.
        // To avoid losing the most recent tick when a burst lands fully
        // inside the gap, schedule a single deferred republish per
        // symbol — coalesced via _deferred so a hot stream cannot
        // accumulate background timers.
        var nowTicks = _clock.GetUtcNow().UtcTicks;
        if (_lastPublishedTicks.TryGetValue(symbol, out var last))
        {
            var elapsed = nowTicks - last;
            if (elapsed < ThrottleGap.Ticks)
            {
                MetricsRegistry.PnlRefPriceThrottled.Add(1);
                ScheduleDeferredRepublish(symbol, ThrottleGap.Ticks - elapsed);
                return;
            }
        }

        var holders = _positions.ForSymbolWithFirm(symbol);
        if (holders.Count == 0) return;

        // PR #316 P1. Snapshot is firm-scoped; ForSymbolWithFirm yields
        // one row per (firmId, position) so a JWT sub registered in
        // two firms gets one publish per firm with the matching
        // bucket's basis + realized totals — and the firm-aware
        // Publish overload filters delivery to the WS sessions that
        // actually authenticated under that firm.
        var seenOwnerFirms = new HashSet<(string FirmId, EndClientId Owner)>();
        var publishCount = 0;
        foreach (var (firmId, p) in holders)
        {
            var key = (firmId, p.Owner);
            if (!seenOwnerFirms.Add(key)) continue;
            if (_subs.CountFor(p.Owner) == 0) continue;
            var snap = PnlProjection.Build(p.Owner, firmId, _pnl, _positions, _refPriceLookup);
            _subs.Publish(p.Owner, firmId, Channels.PnlMe, snap);
            publishCount++;
        }

        if (publishCount > 0)
        {
            _lastPublishedTicks[symbol] = nowTicks;
            MetricsRegistry.PnlRefPricePublishes.Add(1);
        }
    }

    private void ScheduleDeferredRepublish(string symbol, long remainingTicks)
    {
        if (!_deferred.TryAdd(symbol, 0)) return;
        var delay = TimeSpan.FromTicks(Math.Max(remainingTicks, TimeSpan.TicksPerMillisecond));
        _ = Task.Delay(delay, _cts.Token).ContinueWith(_ =>
        {
            _deferred.TryRemove(symbol, out byte _);
            if (_pending.TryAdd(symbol, 0))
                _channel.Writer.TryWrite(symbol);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }
}
