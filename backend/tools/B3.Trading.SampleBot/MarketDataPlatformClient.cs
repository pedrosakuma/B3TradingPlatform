using B3.MarketData.WebSocketClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.SampleBot;

internal interface ISampleBotMarketDataObserver
{
    Task OnConnectedAsync(bool isReconnect, CancellationToken cancellationToken);

    Task OnDisconnectedAsync(Exception? error, CancellationToken cancellationToken);

    Task OnQuoteAsync(MarketDataQuote quote, CancellationToken cancellationToken);
}

internal interface ISampleBotMarketDataClient
{
    Task RunAsync(ISampleBotMarketDataObserver observer, string symbol, CancellationToken cancellationToken);
}

internal sealed class MarketDataPlatformClient : ISampleBotMarketDataClient
{
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(30);

    private readonly SampleBotOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MarketDataPlatformClient> _logger;
    private readonly IMarketDataClientFactory _clientFactory;
    private readonly Func<TimeSpan, TimeProvider, CancellationToken, Task> _delayAsync;

    public MarketDataPlatformClient(
        IOptions<SampleBotOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        ILogger<MarketDataPlatformClient> logger,
        IMarketDataClientFactory? clientFactory = null,
        Func<TimeSpan, TimeProvider, CancellationToken, Task>? delayAsync = null)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _clientFactory = clientFactory ?? new SdkMarketDataClientFactory();
        _delayAsync = delayAsync ?? ((delay, timeProvider, ct) => Task.Delay(delay, timeProvider, ct));
    }

    public async Task RunAsync(ISampleBotMarketDataObserver observer, string symbol, CancellationToken cancellationToken)
    {
        var reconnectDelay = InitialReconnectDelay;
        var isReconnect = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var client = _clientFactory.Create(new MarketDataClientOptions
            {
                Endpoint = new Uri(_options.MarketData.WsUrl!, UriKind.Absolute),
                AutoResubscribeOnReconnect = true,
                BackPressure = BackPressurePolicy.DropOldest,
            }, _loggerFactory);

            var bridge = new MarketDataObserverBridge(observer, _logger);
            bridge.Attach(client);

            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                await bridge.NotifyConnectedAsync(isReconnect, cancellationToken).ConfigureAwait(false);
                await client.SubscribeAsync(
                    symbol,
                    SubscribeFlags.Info | SubscribeFlags.Trades | SubscribeFlags.Book,
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Connected to market-data endpoint {WsUrl} for symbol {Symbol}.",
                    _options.MarketData.WsUrl,
                    symbol);
                reconnectDelay = InitialReconnectDelay;
                throw await bridge.WaitForTerminalFailureAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await bridge.NotifyDisconnectedAsync(ex, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(ex, "Market-data connection ended; retrying.");
            }
            finally
            {
                bridge.Detach(client);
            }

            isReconnect = true;
            await _delayAsync(reconnectDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            reconnectDelay = TimeSpan.FromTicks(Math.Min(reconnectDelay.Ticks * 2, MaximumReconnectDelay.Ticks));
        }
    }

    private sealed class MarketDataObserverBridge
    {
        private readonly ISampleBotMarketDataObserver _observer;
        private readonly ILogger _logger;
        private readonly TaskCompletionSource<Exception> _terminalFailure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _connected;

        public MarketDataObserverBridge(ISampleBotMarketDataObserver observer, ILogger logger)
        {
            _observer = observer;
            _logger = logger;
        }

        public void Attach(IMarketDataClient client)
        {
            client.Trade += OnTrade;
            client.InfoSnapshot += OnInfoSnapshot;
            client.BookSnapshot += OnBookSnapshot;
            client.OrderAdded += OnOrderAdded;
            client.OrderUpdated += OnOrderUpdated;
            client.ConnectionStateChanged += OnConnectionStateChanged;
            client.SubscribeError += OnSubscribeError;
            client.SymbolDelisted += OnSymbolDelisted;
        }

        public void Detach(IMarketDataClient client)
        {
            client.Trade -= OnTrade;
            client.InfoSnapshot -= OnInfoSnapshot;
            client.BookSnapshot -= OnBookSnapshot;
            client.OrderAdded -= OnOrderAdded;
            client.OrderUpdated -= OnOrderUpdated;
            client.ConnectionStateChanged -= OnConnectionStateChanged;
            client.SubscribeError -= OnSubscribeError;
            client.SymbolDelisted -= OnSymbolDelisted;
        }

        public Task NotifyConnectedAsync(bool isReconnect, CancellationToken cancellationToken)
        {
            _connected = true;
            return _observer.OnConnectedAsync(isReconnect, cancellationToken);
        }

        public Task NotifyDisconnectedAsync(Exception? error, CancellationToken cancellationToken)
        {
            if (!_connected && error is null)
                return Task.CompletedTask;

            _connected = false;
            return _observer.OnDisconnectedAsync(error, cancellationToken);
        }

        public Task<Exception> WaitForTerminalFailureAsync(CancellationToken cancellationToken) =>
            _terminalFailure.Task.WaitAsync(cancellationToken);

        private void OnTrade(TradeEvent ev) =>
            _observer.OnQuoteAsync(new MarketDataQuote(
                ev.Symbol,
                ev.SecurityId,
                ReferencePriceSource.LastTradePrice,
                ev.Price,
                new DateTimeOffset(DateTime.SpecifyKind(ev.ReceivedUtc, DateTimeKind.Utc))), CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        private void OnOrderAdded(OrderAddedEvent ev) => OnBookOrder(
            ev.Symbol,
            ev.SecurityId,
            ev.Price,
            ev.ReceivedUtc);

        private void OnBookSnapshot(BookSnapshotEvent ev)
        {
            var price = ev.Asks
                .Where(order => order.Price > 0m)
                .Select(order => order.Price)
                .DefaultIfEmpty()
                .Min();
            if (price <= 0m)
            {
                price = ev.Bids
                    .Where(order => order.Price > 0m)
                    .Select(order => order.Price)
                    .DefaultIfEmpty()
                    .Max();
            }

            OnBookOrder(ev.Symbol, ev.SecurityId, price, ev.ReceivedUtc);
        }

        private void OnOrderUpdated(OrderUpdatedEvent ev) => OnBookOrder(
            ev.Symbol,
            ev.SecurityId,
            ev.Price,
            ev.ReceivedUtc);

        private void OnBookOrder(string symbol, ulong securityId, decimal price, DateTime receivedUtc)
        {
            if (price <= 0m)
                return;

            _observer.OnQuoteAsync(new MarketDataQuote(
                symbol,
                securityId,
                ReferencePriceSource.BookOrder,
                price,
                new DateTimeOffset(DateTime.SpecifyKind(receivedUtc, DateTimeKind.Utc))), CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        }

        private void OnInfoSnapshot(InfoSnapshotEvent ev)
        {
            var receivedAt = new DateTimeOffset(DateTime.SpecifyKind(ev.ReceivedUtc, DateTimeKind.Utc));
            if (ev.TradingReferencePrice is { } reference && reference > 0m)
            {
                _observer.OnQuoteAsync(new MarketDataQuote(
                    ev.Symbol,
                    ev.SecurityId,
                    ReferencePriceSource.TradingReferencePrice,
                    reference,
                    receivedAt), CancellationToken.None).GetAwaiter().GetResult();
                return;
            }

            if (ev.LastTradePrice is { } lastTrade && lastTrade > 0m)
            {
                _observer.OnQuoteAsync(new MarketDataQuote(
                    ev.Symbol,
                    ev.SecurityId,
                    ReferencePriceSource.LastTradePrice,
                    lastTrade,
                    receivedAt), CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        private void OnConnectionStateChanged(ConnectionStateChangedEvent ev)
        {
            _logger.LogInformation("Market-data connection state: {State}", ev.State);
            if (ev.State == ConnectionState.Connected)
            {
                _connected = true;
                _observer.OnConnectedAsync(isReconnect: true, CancellationToken.None).GetAwaiter().GetResult();
                return;
            }

            if (_connected && ev.State is ConnectionState.Disconnected or ConnectionState.Faulted or ConnectionState.Reconnecting)
            {
                _connected = false;
                _observer.OnDisconnectedAsync(null, CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        private void OnSubscribeError(SubscribeErrorEvent ev)
        {
            _terminalFailure.TrySetResult(
                new InvalidOperationException($"Market-data subscription failed for {ev.Symbol}: {ev.ErrorCode}."));
        }

        private void OnSymbolDelisted(SymbolDelistedEvent ev)
        {
            _terminalFailure.TrySetResult(
                new InvalidOperationException($"Market-data reports symbol {ev.Symbol} delisted."));
        }
    }
}

internal sealed record MarketDataQuote(
    string Symbol,
    ulong SecurityId,
    ReferencePriceSource Source,
    decimal Price,
    DateTimeOffset ReceivedAtUtc);

internal enum ReferencePriceSource
{
    TradingReferencePrice,
    LastTradePrice,
    BookOrder,
}

internal interface IMarketDataClient : IAsyncDisposable
{
    event Action<TradeEvent>? Trade;
    event Action<InfoSnapshotEvent>? InfoSnapshot;
    event Action<BookSnapshotEvent>? BookSnapshot;
    event Action<OrderAddedEvent>? OrderAdded;
    event Action<OrderUpdatedEvent>? OrderUpdated;
    event Action<SymbolDelistedEvent>? SymbolDelisted;
    event Action<ConnectionStateChangedEvent>? ConnectionStateChanged;
    event Action<SubscribeErrorEvent>? SubscribeError;

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
        new SdkMarketDataClient(new MarketDataClient(options, loggerFactory.CreateLogger<MarketDataClient>()));
}

internal sealed class SdkMarketDataClient : IMarketDataClient
{
    private readonly MarketDataClient _inner;

    public SdkMarketDataClient(MarketDataClient inner)
    {
        _inner = inner;
    }

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

    public event Action<BookSnapshotEvent>? BookSnapshot
    {
        add => _inner.BookSnapshot += value;
        remove => _inner.BookSnapshot -= value;
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

    public Task ConnectAsync(CancellationToken ct) => _inner.ConnectAsync(ct);

    public ValueTask SubscribeAsync(string symbol, SubscribeFlags flags, CancellationToken ct) =>
        _inner.SubscribeAsync(symbol, flags, ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
