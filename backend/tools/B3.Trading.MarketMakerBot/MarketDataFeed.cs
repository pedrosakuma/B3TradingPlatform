using B3.MarketData.WebSocketClient;
using Microsoft.Extensions.Logging;

namespace B3.Trading.MarketMakerBot;

/// <summary>
/// Thin wrapper around B3MarketDataPlatform's WebSocket SDK
/// (<see cref="MarketDataClient"/>) that feeds live reference prices
/// (and delisting notices) into <see cref="MarketPriceTracker"/>.
///
/// Deliberately does NOT own instrument identity: <see
/// cref="InstrumentConfig.SecurityId"/>/<see cref="InstrumentConfig.TickSize"/>/<see
/// cref="InstrumentConfig.LotSize"/> stay config-driven because those
/// are matching-platform's wire truth for FIXP order construction, and
/// the market-data feed's own SecurityId numbering is a separate
/// namespace that doesn't necessarily line up with it — conflating the
/// two would be a correctness risk, not a simplification. Market data
/// only sharpens the quote anchor and tells us when to stop quoting a
/// symbol (<see cref="MarketDataClient.SymbolDelisted"/>); it never
/// gates the bot's ability to submit an order in the first place. If
/// <see cref="MarketDataOptions.WsUrl"/> is unset, or the feed fails to
/// connect, the worker just keeps quoting off each instrument's
/// configured <see cref="InstrumentConfig.RefPrice"/> — same
/// degrade-gracefully shape as the trading-host's own market-data gate.
/// </summary>
internal sealed class MarketDataFeed : IAsyncDisposable
{
    private readonly MarketPriceTracker _tracker;
    private readonly ILogger _log;
    private MarketDataClient? _client;

    /// <summary>
    /// Raised for every order-level book delta (add/update/delete) the
    /// venue reports for a subscribed symbol, carrying the venue's own
    /// OrderId. RFC #703 book-driven quoting: <see
    /// cref="MarketMakerWorker"/> subscribes to react to the market
    /// moving instead of waiting solely on its own ER stream, but must
    /// first filter out deltas its OWN resting orders caused (via <see
    /// cref="OrderTracker.IsOwnOrder"/>) — this feed deliberately does
    /// NOT know about the bot's own orders (same "no instrument-identity
    /// smarts" boundary as the rest of this class), so it forwards every
    /// delta and leaves the self-order filter to the subscriber.
    /// </summary>
    public event Action<string, ulong>? BookOrderChanged;

    /// <summary>
    /// Raised after a symbol's quote availability changes in the local price
    /// tracker. The feed exposes only the symbol; pricing/cancel policy remains
    /// the worker's responsibility.
    /// </summary>
    public event Action<string>? SymbolAvailabilityChanged;

    public MarketDataFeed(MarketPriceTracker tracker, ILogger log)
    {
        _tracker = tracker;
        _log = log;
    }

    public bool IsConnected => _client is not null;

    public async Task StartAsync(MarketDataOptions options, IReadOnlyList<InstrumentConfig> instruments,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.WsUrl))
        {
            _log.LogInformation("[mm] MarketData:WsUrl not set; quoting off static RefPrice anchors only.");
            return;
        }

        var clientOptions = new MarketDataClientOptions
        {
            Endpoint = new Uri(options.WsUrl),
            AutoResubscribeOnReconnect = true,
            BackPressure = BackPressurePolicy.DropOldest,
        };

        var client = new MarketDataClient(clientOptions, loggerFactory.CreateLogger<MarketDataClient>());
        client.Trade += OnTrade;
        client.InfoSnapshot += OnInfoSnapshot;
        client.SymbolDelisted += OnSymbolDelisted;
        client.ConnectionStateChanged += OnConnectionStateChanged;
        client.SubscribeError += OnSubscribeError;
        client.OrderAdded += OnOrderAdded;
        client.OrderUpdated += OnOrderUpdated;
        client.OrderDeleted += OnOrderDeleted;

        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            foreach (var instr in instruments)
            {
                // Book (MBO) is required alongside Trades|Info: it's the
                // only flag that carries order-level deltas with the
                // venue's own OrderId, which BookOrderChanged's
                // self-order filter depends on (see RFC #703).
                await client.SubscribeAsync(instr.Symbol, SubscribeFlags.Trades | SubscribeFlags.Info | SubscribeFlags.Book, ct)
                    .ConfigureAwait(false);
            }
            _client = client;
            _tracker.SetConnected(true);
            _log.LogInformation("[mm] MarketData connected to {WsUrl}; subscribed to {Count} instrument(s).",
                options.WsUrl, instruments.Count);
        }
        catch (Exception ex)
        {
            // Covers OperationCanceledException too — on shutdown mid-
            // connect/subscribe we still must unhook handlers and
            // dispose the partially-initialized client before
            // propagating, otherwise it leaks (never assigned to
            // _client, so DisposeAsync above would be a no-op).
            client.Trade -= OnTrade;
            client.InfoSnapshot -= OnInfoSnapshot;
            client.SymbolDelisted -= OnSymbolDelisted;
            client.ConnectionStateChanged -= OnConnectionStateChanged;
            client.SubscribeError -= OnSubscribeError;
            client.OrderAdded -= OnOrderAdded;
            client.OrderUpdated -= OnOrderUpdated;
            client.OrderDeleted -= OnOrderDeleted;
            await client.DisposeAsync().ConfigureAwait(false);
            if (ex is OperationCanceledException) throw;
            _log.LogWarning(ex,
                "[mm] MarketData connect/subscribe to {WsUrl} failed; continuing with static RefPrice anchors only.",
                options.WsUrl);
        }
    }

    private void OnTrade(TradeEvent ev) => _tracker.OnTrade(ev.Symbol, ev.Price);

    private void OnInfoSnapshot(InfoSnapshotEvent ev) =>
        _tracker.OnInfoSnapshot(ev.Symbol, ev.TradingReferencePrice, ev.LastTradePrice);

    private void OnOrderAdded(OrderAddedEvent ev) => BookOrderChanged?.Invoke(ev.Symbol, ev.OrderId);

    private void OnOrderUpdated(OrderUpdatedEvent ev) => BookOrderChanged?.Invoke(ev.Symbol, ev.OrderId);

    private void OnOrderDeleted(OrderDeletedEvent ev) => BookOrderChanged?.Invoke(ev.Symbol, ev.OrderId);

    private void OnSymbolDelisted(SymbolDelistedEvent ev) => NotifySymbolDelisted(ev.Symbol);

    internal void NotifySymbolDelisted(string symbol)
    {
        _log.LogWarning("[mm] MarketData reports {Symbol} delisted; pausing quotes for it.", symbol);
        _tracker.OnSymbolDelisted(symbol);
        SymbolAvailabilityChanged?.Invoke(symbol);
    }

    private void OnConnectionStateChanged(ConnectionStateChangedEvent ev)
    {
        _log.LogInformation("[mm] MarketData connection state: {State}", ev.State);
        // Only trust cached prices while genuinely Connected — a stale
        // last-known price served through a Reconnecting/Faulted gap
        // could anchor quotes far from the real market. See
        // MarketPriceTracker.TryGetReferencePrice.
        _tracker.SetConnected(ev.State == ConnectionState.Connected);
    }

    private void OnSubscribeError(SubscribeErrorEvent ev) =>
        _log.LogWarning("[mm] MarketData subscribe error for {Symbol}: {Error}", ev.Symbol, ev.ErrorCode);

    public async ValueTask DisposeAsync()
    {
        if (_client is null) return;
        _client.Trade -= OnTrade;
        _client.InfoSnapshot -= OnInfoSnapshot;
        _client.SymbolDelisted -= OnSymbolDelisted;
        _client.ConnectionStateChanged -= OnConnectionStateChanged;
        _client.SubscribeError -= OnSubscribeError;
        _client.OrderAdded -= OnOrderAdded;
        _client.OrderUpdated -= OnOrderUpdated;
        _client.OrderDeleted -= OnOrderDeleted;
        try { await _client.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort cleanup on shutdown */ }
    }
}
