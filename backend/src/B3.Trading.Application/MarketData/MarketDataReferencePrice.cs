using System.Collections.Concurrent;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Risk;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.MarketData;

/// <summary>
/// Live <see cref="IReferencePrice"/> backed by the B3MarketDataPlatform
/// WebSocket feed via <see cref="IMarketDataSubscriber"/>.
///
/// <para>
/// Maintains a per-symbol cache of last-observed price + timestamp.
/// On <see cref="TryGet"/> miss (symbol never traded, never seeded by
/// snapshot) or staleness (older than <see cref="MarketDataOptions.MaxStaleness"/>),
/// delegates to <paramref name="fallback"/> — typically
/// <see cref="ConfigReferencePrice"/>. Same fail-open semantics as
/// before: no number → collar approves.
/// </para>
///
/// <para>
/// Implements <see cref="IHostedService"/> so DI is forced to construct
/// it (and attach event handlers to the subscriber) BEFORE the host
/// starts the subscriber's connect/subscribe loop. Without that, early
/// trade prints arriving in the gap between subscriber-start and first
/// <c>IReferencePrice</c> resolution would be silently dropped.
/// </para>
/// </summary>
public sealed class MarketDataReferencePrice : IReferencePrice, IHostedService
{
    private readonly IMarketDataSubscriber _subscriber;
    private readonly IReferencePrice _fallback;
    private readonly TimeProvider _clock;
    private readonly ILogger<MarketDataReferencePrice> _logger;
    private readonly MarketDataOptions _options;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public MarketDataReferencePrice(
        IMarketDataSubscriber subscriber,
        IReferencePrice fallback,
        IOptions<MarketDataOptions> options,
        TimeProvider clock,
        ILogger<MarketDataReferencePrice> logger)
    {
        _subscriber = subscriber;
        _fallback = fallback;
        _clock = clock;
        _logger = logger;
        _options = options.Value;

        _subscriber.Trade += OnTrade;
        _subscriber.InfoSnapshot += OnInfoSnapshot;
        _subscriber.ConnectionStateChanged += OnConnectionStateChanged;
        _subscriber.SubscribeError += OnSubscribeError;
    }

    /// <summary>Exposed for tests / ops introspection. Snapshot, not live.</summary>
    public IReadOnlyDictionary<string, (decimal Price, DateTimeOffset UpdatedUtc)> Snapshot() =>
        _cache.ToDictionary(
            kv => kv.Key,
            kv => (kv.Value.Price, kv.Value.UpdatedUtc),
            StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string symbol, out decimal price)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            price = 0m;
            return false;
        }

        if (_cache.TryGetValue(symbol, out var entry) && entry.Price > 0m)
        {
            // Negative or zero MaxStaleness means "trust cache forever".
            if (_options.MaxStaleness <= TimeSpan.Zero ||
                _clock.GetUtcNow() - entry.UpdatedUtc <= _options.MaxStaleness)
            {
                price = entry.Price;
                return true;
            }
        }

        return _fallback.TryGet(symbol, out price);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Symbols.Length == 0)
        {
            // The gate is on (WsUrl set) but the subscription list is
            // empty — the feed will connect and idle forever, which is
            // almost certainly a misconfig. Loud log so it shows up in
            // every operator's first deploy.
            _logger.LogWarning(
                "MarketData feed enabled but Trading:MarketData:Symbols is empty; nothing will be subscribed and the price collar will fall back to the static reference-price dictionary.");
        }

        try
        {
            await _subscriber.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Don't abort host startup if the MD endpoint is down — the
            // SDK's reconnect loop will keep trying. Collar stays in
            // fallback mode meanwhile.
            _logger.LogWarning(ex,
                "MarketData ConnectAsync failed at startup; SDK reconnect will keep trying.");
        }

        foreach (var raw in _options.Symbols)
        {
            var symbol = (raw ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(symbol))
                continue;

            try
            {
                await _subscriber.SubscribeAsync(symbol, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("MarketData subscribed: {Symbol}", symbol);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MarketData subscribe failed for {Symbol}; will retry on next reconnect via auto-resubscribe.", symbol);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscriber.Trade -= OnTrade;
        _subscriber.InfoSnapshot -= OnInfoSnapshot;
        _subscriber.ConnectionStateChanged -= OnConnectionStateChanged;
        _subscriber.SubscribeError -= OnSubscribeError;
        return Task.CompletedTask;
    }

    // -------- event handlers (hot path; keep tight) --------

    private void OnTrade(MarketTrade t)
    {
        if (string.IsNullOrWhiteSpace(t.Symbol) || t.Price <= 0m)
            return;
        Update(t.Symbol, t.Price, t.ReceivedUtc);
    }

    private void OnInfoSnapshot(MarketInfoSnapshot s)
    {
        if (string.IsNullOrWhiteSpace(s.Symbol))
            return;
        // Prefer LastTradePrice (closest to a "current" mark). Fall back
        // to TradingReferencePrice (the venue-supplied collar anchor)
        // only if no trade has been observed in this snapshot.
        var seed = s.LastTradePrice ?? s.TradingReferencePrice;
        if (seed is not { } px || px <= 0m)
            return;
        Update(s.Symbol, px, s.ReceivedUtc);
    }

    private void Update(string symbol, decimal price, DateTimeOffset receivedUtc)
    {
        var key = symbol.Trim();
        _cache[key] = new CacheEntry(price, receivedUtc);
    }

    private void OnConnectionStateChanged(MarketDataConnectionState state)
    {
        _logger.LogInformation("MarketData connection state: {State}", state);
        // The connected gauge is published as an observable in the
        // host's metrics registration so ops sees the binary "is the
        // feed up" without us emitting a sample on every transition.
    }

    private void OnSubscribeError(MarketSubscribeError err)
    {
        MetricsRegistry.MarketDataSubscribeErrors.Add(1,
            new KeyValuePair<string, object?>("symbol", err.Symbol),
            new KeyValuePair<string, object?>("reason", err.Reason));
        _logger.LogWarning(
            "MarketData subscribe error for {Symbol}: {Reason}", err.Symbol, err.Reason);
    }

    private readonly record struct CacheEntry(decimal Price, DateTimeOffset UpdatedUtc);
}
