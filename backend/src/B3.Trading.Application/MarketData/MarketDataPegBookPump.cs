using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Application.MarketData;

/// <summary>
/// Q3.3 (#283). Bridges trade prints from <see cref="IMarketDataSubscriber"/>
/// into the per-symbol <see cref="PegBookTopCache"/> so the Pegged algo
/// engine can resolve a live reference price without coupling to the
/// SDK. Sibling of <see cref="MarketDataVolumePump"/> for the volume
/// curve — same lifecycle model: <see cref="IHostedService"/> so DI
/// constructs the pump (and attaches its handler) before the subscriber
/// starts its connect/subscribe loop.
///
/// <para>
/// Also exposes <see cref="EnsureSubscribedAsync"/> so the engine can
/// demand-subscribe to symbols a Pegged parent references but the
/// static <c>Trading:MarketData:Symbols</c> list does not cover —
/// otherwise the SDK never delivers prints, the cache stays empty, and
/// the algo emits zero (re)pegs.
/// </para>
///
/// <para>
/// <b>BBO source.</b> This pump only writes <c>Last</c> (from Trade /
/// InfoSnapshot). The BBO legs in <see cref="BookTop"/> are populated
/// by <see cref="MboPegBookPump"/> off <see cref="MboBookStore"/>
/// when <c>MarketDataOptions.EnableBook</c> is on (Q3.6 Stage C,
/// #286). When MBO is off, BBO stays null and the cache falls back
/// to last-trade per the contract in <see cref="PegBookTopCache"/>.
/// </para>
/// </summary>
public sealed class MarketDataPegBookPump : IHostedService
{
    private readonly IMarketDataSubscriber _subscriber;
    private readonly PegBookTopCache _cache;
    private readonly ILogger<MarketDataPegBookPump>? _logger;

    // Same coalescing strategy as MarketDataVolumePump. See its doc
    // comments for the rationale (Lazy<Task> dedup + concurrent first
    // call + failed-Subscribe cache eviction).
    private readonly ConcurrentDictionary<string, Lazy<Task>> _subscribed =
        new(StringComparer.OrdinalIgnoreCase);

    public MarketDataPegBookPump(
        IMarketDataSubscriber subscriber,
        PegBookTopCache cache,
        ILogger<MarketDataPegBookPump>? logger = null)
    {
        _subscriber = subscriber;
        _cache = cache;
        _logger = logger;
        _subscriber.Trade += OnTrade;
        _subscriber.InfoSnapshot += OnInfoSnapshot;
    }

    private void OnTrade(MarketTrade t) =>
        _cache.UpdateLast(t.Symbol, t.Price, t.ReceivedUtc);

    private void OnInfoSnapshot(MarketInfoSnapshot s)
    {
        var px = s.LastTradePrice ?? s.TradingReferencePrice;
        if (px is not { } p || p <= 0m) return;
        _cache.UpdateLast(s.Symbol, p, s.ReceivedUtc);
    }

    public async ValueTask EnsureSubscribedAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        var key = symbol.Trim();
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
            _subscribed.TryRemove(key, out _);
            throw;
        }
        catch (Exception ex)
        {
            _subscribed.TryRemove(key, out _);
            _logger?.LogWarning(ex,
                "MarketDataPegBookPump SubscribeAsync failed for {Symbol}; entry cleared, next EnsureSubscribedAsync call will retry.",
                key);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscriber.Trade -= OnTrade;
        _subscriber.InfoSnapshot -= OnInfoSnapshot;
        return Task.CompletedTask;
    }
}
