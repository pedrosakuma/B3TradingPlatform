using B3.MarketData.WebSocketClient;
using Microsoft.Extensions.Logging;

namespace B3.Trading.MarketMakerBot;

/// <summary>
/// Owns the market-data SDK lifecycle and translates feed events into
/// per-symbol reference readiness. StaticRefPrice keeps the historical
/// best-effort single-connect behavior. PauseAndCancel retries an initial
/// connection failure in the background while strict quote eligibility remains
/// false. Instrument SecurityId/tick/lot identity remains config-driven because
/// the market-data and matching-platform identifier namespaces are independent;
/// this component only supplies reference prices, delisting, and book signals.
/// </summary>
internal sealed class MarketDataFeed : IAsyncDisposable
{
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(30);

    private readonly MarketPriceTracker _tracker;
    private readonly VolatilitySpreadEstimator _volatilitySpread;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;
    private readonly IMarketDataClientFactory _clientFactory;
    private readonly Func<TimeSpan, TimeProvider, CancellationToken, Task> _delayAsync;
    private readonly object _connectionGate = new();
    private IMarketDataClient? _client;
    private CancellationTokenSource? _lifetime;
    private Task? _connectLoop;
    private bool _connectionEligible;
    private bool _started;
    private FeedLossPolicy _feedLossPolicy;
    private TimeSpan _maxReferenceAge = TimeSpan.FromSeconds(30);

    public event Action<string, ulong>? BookOrderChanged;
    public event Action<string>? SymbolAvailabilityChanged;
    public event Action<string>? VolatilitySpreadChanged;
    public event Action? ConnectionEligibilityChanged;

    public MarketDataFeed(
        MarketPriceTracker tracker,
        VolatilitySpreadEstimator volatilitySpread,
        ILogger log,
        TimeProvider? clock = null,
        IMarketDataClientFactory? clientFactory = null,
        Func<TimeSpan, TimeProvider, CancellationToken, Task>? delayAsync = null)
    {
        _tracker = tracker;
        _volatilitySpread = volatilitySpread;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _clientFactory = clientFactory ?? new SdkMarketDataClientFactory();
        _delayAsync = delayAsync ?? ((delay, timeProvider, ct) =>
            Task.Delay(delay, timeProvider, ct));
    }

    public bool IsConnected
    {
        get
        {
            lock (_connectionGate)
                return _connectionEligible;
        }
    }

    public async Task StartAsync(
        MarketDataOptions options,
        IReadOnlyList<InstrumentConfig> instruments,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        lock (_connectionGate)
        {
            if (_started)
                throw new InvalidOperationException("MarketDataFeed has already been started.");
            _started = true;
        }
        _feedLossPolicy = options.FeedLossPolicy;
        _maxReferenceAge = options.MaxReferenceAge;
        if (string.IsNullOrWhiteSpace(options.WsUrl))
        {
            _log.LogInformation("[mm] MarketData:WsUrl not set; quoting off static RefPrice anchors only.");
            return;
        }

        if (options.FeedLossPolicy == FeedLossPolicy.StaticRefPrice)
        {
            try
            {
                await ConnectClientAsync(options, instruments, loggerFactory, strictSubscriptions: false, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "[mm] MarketData connect/subscribe to {WsUrl} failed; continuing with static RefPrice anchors only.",
                    options.WsUrl);
            }
            return;
        }

        lock (_connectionGate)
        {
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _connectLoop = ConnectStrictUntilReadyAsync(
                options,
                instruments,
                loggerFactory,
                _lifetime.Token);
        }
        await Task.CompletedTask;
    }

    private async Task ConnectStrictUntilReadyAsync(
        MarketDataOptions options,
        IReadOnlyList<InstrumentConfig> instruments,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var delay = InitialReconnectDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectClientAsync(options, instruments, loggerFactory, strictSubscriptions: true, ct)
                    .ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "[mm-feed] market-data connect/subscribe failed under PauseAndCancel; quotes remain paused; retrying in {Delay}",
                    delay);
            }

            try
            {
                await _delayAsync(delay, _clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaximumReconnectDelay.Ticks));
        }
    }

    private async Task ConnectClientAsync(
        MarketDataOptions options,
        IReadOnlyList<InstrumentConfig> instruments,
        ILoggerFactory loggerFactory,
        bool strictSubscriptions,
        CancellationToken ct)
    {
        var clientOptions = new MarketDataClientOptions
        {
            Endpoint = new Uri(options.WsUrl!, UriKind.Absolute),
            AutoResubscribeOnReconnect = true,
            BackPressure = BackPressurePolicy.DropOldest,
        };
        var client = _clientFactory.Create(clientOptions, loggerFactory);
        Attach(client);
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            NotifyConnectionState(connected: true);
            foreach (var instrument in instruments)
            {
                try
                {
                    await client.SubscribeAsync(
                        instrument.Symbol,
                        SubscribeFlags.Trades | SubscribeFlags.Info | SubscribeFlags.Book,
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (strictSubscriptions)
                {
                    NotifySubscribeError(instrument.Symbol, "subscribe-failed");
                    _log.LogWarning(ex,
                        "[mm-feed] market-data subscription failed for {Symbol}; that symbol remains paused.",
                        instrument.Symbol);
                }
            }

            lock (_connectionGate)
                _client = client;
            _log.LogInformation(
                "[mm] MarketData connected to {WsUrl}; subscribed to {Count} instrument(s).",
                options.WsUrl,
                instruments.Count);
        }
        catch
        {
            Detach(client);
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort cleanup of a failed attempt */ }
            NotifyConnectionState(connected: false);
            throw;
        }
    }

    private void Attach(IMarketDataClient client)
    {
        client.Trade += OnTrade;
        client.InfoSnapshot += OnInfoSnapshot;
        client.SymbolDelisted += OnSymbolDelisted;
        client.ConnectionStateChanged += OnConnectionStateChanged;
        client.SubscribeError += OnSubscribeError;
        client.OrderAdded += OnOrderAdded;
        client.OrderUpdated += OnOrderUpdated;
        client.OrderDeleted += OnOrderDeleted;
    }

    private void Detach(IMarketDataClient client)
    {
        client.Trade -= OnTrade;
        client.InfoSnapshot -= OnInfoSnapshot;
        client.SymbolDelisted -= OnSymbolDelisted;
        client.ConnectionStateChanged -= OnConnectionStateChanged;
        client.SubscribeError -= OnSubscribeError;
        client.OrderAdded -= OnOrderAdded;
        client.OrderUpdated -= OnOrderUpdated;
        client.OrderDeleted -= OnOrderDeleted;
    }

    private void OnTrade(TradeEvent ev) =>
        NotifyTrade(ev.Symbol, ev.Price, NormalizeSdkUtc(ev.ReceivedUtc));

    private void OnInfoSnapshot(InfoSnapshotEvent ev) =>
        NotifyInfoSnapshot(
            ev.Symbol,
            ev.TradingReferencePrice,
            ev.LastTradePrice,
            NormalizeSdkUtc(ev.ReceivedUtc));

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

    internal void NotifyTrade(string symbol, decimal price, DateTimeOffset? receivedAtUtc = null)
    {
        var before = StrictAvailability(symbol);
        var updated = _tracker.OnTrade(symbol, price, receivedAtUtc ?? _clock.GetUtcNow());
        PublishVolatilityChange(_volatilitySpread.OnTrade(symbol, price));
        PublishStrictAvailabilityChange(symbol, before, updated);
    }

    internal void NotifyInfoSnapshot(
        string symbol,
        decimal? tradingReferencePrice,
        decimal? lastTradePrice,
        DateTimeOffset? receivedAtUtc = null)
    {
        var before = StrictAvailability(symbol);
        var updated = _tracker.OnInfoSnapshot(
            symbol,
            tradingReferencePrice,
            lastTradePrice,
            receivedAtUtc ?? _clock.GetUtcNow());
        PublishStrictAvailabilityChange(symbol, before, updated);
    }

    internal void NotifyConnectionState(bool connected, DateTimeOffset? changedAtUtc = null)
    {
        bool eligibilityChanged;
        lock (_connectionGate)
        {
            eligibilityChanged = _connectionEligible != connected;
            _connectionEligible = connected;
        }

        _tracker.SetConnected(connected, changedAtUtc);
        foreach (var change in _volatilitySpread.SetConnected(connected))
            PublishVolatilityChange(change);
        if (eligibilityChanged)
            ConnectionEligibilityChanged?.Invoke();
    }

    internal void NotifySubscribeError(string symbol, string errorCode)
    {
        var before = StrictAvailability(symbol);
        _tracker.OnSubscriptionError(symbol);
        _log.LogWarning("[mm] MarketData subscribe error for {Symbol}: {Error}", symbol, errorCode);
        PublishStrictAvailabilityChange(symbol, before, updated: true);
    }

    private void OnConnectionStateChanged(ConnectionStateChangedEvent ev)
    {
        _log.LogInformation("[mm] MarketData connection state: {State}", ev.State);
        NotifyConnectionState(
            ev.State == ConnectionState.Connected,
            NormalizeSdkUtc(ev.ChangedUtc));
    }

    private void OnSubscribeError(SubscribeErrorEvent ev) =>
        NotifySubscribeError(ev.Symbol, ev.ErrorCode.ToString());

    private static DateTimeOffset NormalizeSdkUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private ReferenceAvailability? StrictAvailability(string symbol) =>
        _feedLossPolicy == FeedLossPolicy.PauseAndCancel
            ? _tracker.GetAvailability(symbol, _maxReferenceAge)
            : null;

    private void PublishStrictAvailabilityChange(
        string symbol,
        ReferenceAvailability? before,
        bool updated)
    {
        if (!updated || before is null)
            return;
        var after = _tracker.GetAvailability(symbol, _maxReferenceAge);
        if (before.Value.IsEligible != after.IsEligible ||
            before.Value.UnavailableReason != after.UnavailableReason)
        {
            SymbolAvailabilityChanged?.Invoke(symbol);
        }
    }

    private void PublishVolatilityChange(VolatilitySpreadChange? change)
    {
        if (change is not { } value)
            return;
        _log.LogInformation(
            "[mm-volatility] effective spread changed symbol={Symbol} estimateTicks={MoveEstimateTicks} samples={SampleCount} ready={Ready} connected={Connected} previousAdditionalTicks={PreviousAdditionalTicks} additionalTicks={AdditionalTicks}",
            value.Symbol,
            value.Current.MoveEstimateTicks,
            value.Current.SampleCount,
            value.Current.IsReady,
            value.Current.IsConnected,
            value.PreviousAdditionalSpreadTicks,
            value.Current.AdditionalSpreadTicks);
        VolatilitySpreadChanged?.Invoke(value.Symbol);
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? lifetime;
        Task? connectLoop;
        lock (_connectionGate)
        {
            lifetime = _lifetime;
            connectLoop = _connectLoop;
            _lifetime = null;
            _connectLoop = null;
        }

        lifetime?.Cancel();
        if (connectLoop is not null)
        {
            try { await connectLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
        }
        lifetime?.Dispose();

        IMarketDataClient? client;
        lock (_connectionGate)
        {
            client = _client;
            _client = null;
        }
        if (client is null)
            return;
        Detach(client);
        try { await client.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort cleanup on shutdown */ }
        NotifyConnectionState(connected: false);
    }
}

internal interface IMarketDataClient : IAsyncDisposable
{
    event Action<TradeEvent>? Trade;
    event Action<InfoSnapshotEvent>? InfoSnapshot;
    event Action<SymbolDelistedEvent>? SymbolDelisted;
    event Action<ConnectionStateChangedEvent>? ConnectionStateChanged;
    event Action<SubscribeErrorEvent>? SubscribeError;
    event Action<OrderAddedEvent>? OrderAdded;
    event Action<OrderUpdatedEvent>? OrderUpdated;
    event Action<OrderDeletedEvent>? OrderDeleted;

    Task ConnectAsync(CancellationToken ct);
    ValueTask SubscribeAsync(string symbol, SubscribeFlags flags, CancellationToken ct);
}

internal interface IMarketDataClientFactory
{
    IMarketDataClient Create(MarketDataClientOptions options, ILoggerFactory loggerFactory);
}

internal sealed class SdkMarketDataClientFactory : IMarketDataClientFactory
{
    public IMarketDataClient Create(MarketDataClientOptions options, ILoggerFactory loggerFactory) =>
        new SdkMarketDataClient(
            new MarketDataClient(options, loggerFactory.CreateLogger<MarketDataClient>()));
}

internal sealed class SdkMarketDataClient : IMarketDataClient
{
    private readonly MarketDataClient _inner;

    public SdkMarketDataClient(MarketDataClient inner) => _inner = inner;

    public event Action<TradeEvent>? Trade
    {
        add => _inner.Trade += value;
        remove => _inner.Trade -= value;
    }
    public event Action<InfoSnapshotEvent>? InfoSnapshot
    {
        add => _inner.InfoSnapshot += value;
        remove => _inner.InfoSnapshot -= value;
    }
    public event Action<SymbolDelistedEvent>? SymbolDelisted
    {
        add => _inner.SymbolDelisted += value;
        remove => _inner.SymbolDelisted -= value;
    }
    public event Action<ConnectionStateChangedEvent>? ConnectionStateChanged
    {
        add => _inner.ConnectionStateChanged += value;
        remove => _inner.ConnectionStateChanged -= value;
    }
    public event Action<SubscribeErrorEvent>? SubscribeError
    {
        add => _inner.SubscribeError += value;
        remove => _inner.SubscribeError -= value;
    }
    public event Action<OrderAddedEvent>? OrderAdded
    {
        add => _inner.OrderAdded += value;
        remove => _inner.OrderAdded -= value;
    }
    public event Action<OrderUpdatedEvent>? OrderUpdated
    {
        add => _inner.OrderUpdated += value;
        remove => _inner.OrderUpdated -= value;
    }
    public event Action<OrderDeletedEvent>? OrderDeleted
    {
        add => _inner.OrderDeleted += value;
        remove => _inner.OrderDeleted -= value;
    }

    public Task ConnectAsync(CancellationToken ct) => _inner.ConnectAsync(ct);
    public ValueTask SubscribeAsync(string symbol, SubscribeFlags flags, CancellationToken ct) =>
        _inner.SubscribeAsync(symbol, flags, ct);
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
