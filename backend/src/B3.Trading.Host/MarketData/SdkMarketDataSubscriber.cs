using B3.MarketData.WebSocketClient;
using B3.Trading.Application.MarketData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AppConnState = B3.Trading.Application.MarketData.MarketDataConnectionState;
using AppTrade = B3.Trading.Application.MarketData.MarketTrade;
using AppInfoSnapshot = B3.Trading.Application.MarketData.MarketInfoSnapshot;
using SdkConnState = B3.MarketData.WebSocketClient.ConnectionState;

namespace B3.Trading.Host.MarketData;

/// <summary>
/// Adapter from <c>B3.MarketData.WebSocketClient.MarketDataClient</c>
/// (the SDK) to the application-side <see cref="IMarketDataSubscriber"/>
/// abstraction. Lives in the host because it carries the SDK package
/// dependency we deliberately keep out of B3.Trading.Application.
///
/// <para>
/// Translation rules:
/// <list type="bullet">
///   <item>SDK <c>TradeEvent</c> → <see cref="AppTrade"/> 1:1, price
///         already scaled by the SDK (1e-4).</item>
///   <item>SDK <c>InfoSnapshotEvent</c> → <see cref="AppInfoSnapshot"/>
///         keeping only the two prices the collar consumes today.</item>
///   <item>SDK MBO events (<c>BookSnapshot</c>, <c>OrderAdded</c>,
///         <c>OrderUpdated</c>, <c>OrderDeleted</c>,
///         <c>BookCleared</c>) → app-owned <c>Market*</c> records
///         (Q3.6 Stage A, #286). Hooked only when
///         <see cref="MarketDataOptions.EnableBook"/> is true; when
///         off the SDK still surfaces them but we deliberately don't
///         subscribe to <c>SubscribeFlags.Book</c> so the server
///         never streams them.</item>
///   <item><see cref="SubscribeAsync(string, CancellationToken)"/> asks for
///         <c>Trades | Info</c> by default and adds <c>Book</c> when
///         <see cref="MarketDataOptions.EnableBook"/> is true.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class SdkMarketDataSubscriber : IMarketDataSubscriber
{
    private readonly MarketDataClient _client;
    private readonly ILogger<SdkMarketDataSubscriber> _logger;
    private readonly SubscribeFlags _subscribeFlags;
    private readonly bool _bookEnabled;

    public event Action<AppTrade>? Trade;
    public event Action<AppInfoSnapshot>? InfoSnapshot;
    public event Action<AppConnState>? ConnectionStateChanged;
    public event Action<MarketSubscribeError>? SubscribeError;

    // SDK gap: B3.MarketData.WebSocketClient 0.2.0 still does not surface
    // dedicated auction events. We declare the events on the seam so
    // AuctionStateStore + WS channels are wired end-to-end; they
    // simply never fire under the live SDK today. Tests inject a fake
    // subscriber that raises them. Tracking: B3MarketDataPlatform#40.
#pragma warning disable CS0067 // event never used — see SDK-gap note above.
    public event Action<B3.Trading.Application.MarketData.MarketTheoreticalOpening>? TheoreticalOpening;
    public event Action<B3.Trading.Application.MarketData.MarketAuctionImbalance>? AuctionImbalance;
    public event Action<B3.Trading.Application.MarketData.MarketAuctionPrint>? AuctionPrint;
#pragma warning restore CS0067

    public event Action<MarketBookSnapshot>? BookSnapshot;
    public event Action<MarketOrderAdded>? OrderAdded;
    public event Action<MarketOrderUpdated>? OrderUpdated;
    public event Action<MarketOrderDeleted>? OrderDeleted;
    public event Action<MarketBookCleared>? BookCleared;

    public SdkMarketDataSubscriber(
        MarketDataClient client,
        ILogger<SdkMarketDataSubscriber> logger,
        IOptions<MarketDataOptions> options)
    {
        _client = client;
        _logger = logger;
        _bookEnabled = options.Value.EnableBook;
        _subscribeFlags = _bookEnabled
            ? SubscribeFlags.Trades | SubscribeFlags.Info | SubscribeFlags.Book
            : SubscribeFlags.Trades | SubscribeFlags.Info;

        _client.Trade += OnSdkTrade;
        _client.InfoSnapshot += OnSdkInfo;
        _client.ConnectionStateChanged += OnSdkConn;
        _client.SubscribeError += OnSdkSubErr;

        if (_bookEnabled)
        {
            _client.BookSnapshot += OnSdkBookSnapshot;
            _client.OrderAdded += OnSdkOrderAdded;
            _client.OrderUpdated += OnSdkOrderUpdated;
            _client.OrderDeleted += OnSdkOrderDeleted;
            _client.BookCleared += OnSdkBookCleared;
        }
    }

    public AppConnState State => Translate(_client.State);

    public long DroppedEventCount => _client.DroppedEventCount;

    public Task ConnectAsync(CancellationToken ct = default) => _client.ConnectAsync(ct);

    public async ValueTask SubscribeAsync(string symbol, CancellationToken ct = default)
    {
        await _client.SubscribeAsync(symbol, _subscribeFlags, ct)
            .ConfigureAwait(false);

        if (_client.TryGetSecurityId(symbol, out var securityId))
        {
            // Surfaces the symbol → SecurityId binding so any silent
            // mismatch with the matching engine's IDs shows up in logs
            // before it shows up as a wrong-symbol fill.
            _logger.LogInformation(
                "MarketData symbol mapping: {Symbol} → SecurityId={SecurityId}", symbol, securityId);
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Trade -= OnSdkTrade;
        _client.InfoSnapshot -= OnSdkInfo;
        _client.ConnectionStateChanged -= OnSdkConn;
        _client.SubscribeError -= OnSdkSubErr;
        if (_bookEnabled)
        {
            _client.BookSnapshot -= OnSdkBookSnapshot;
            _client.OrderAdded -= OnSdkOrderAdded;
            _client.OrderUpdated -= OnSdkOrderUpdated;
            _client.OrderDeleted -= OnSdkOrderDeleted;
            _client.BookCleared -= OnSdkBookCleared;
        }
        return _client.DisposeAsync();
    }

    private void OnSdkTrade(TradeEvent ev)
    {
        Trade?.Invoke(new AppTrade(
            Symbol: ev.Symbol,
            SecurityId: ev.SecurityId,
            Price: ev.Price,
            Qty: ev.Qty,
            ReceivedUtc: new DateTimeOffset(DateTime.SpecifyKind(ev.ReceivedUtc, DateTimeKind.Utc))));
    }

    private void OnSdkInfo(InfoSnapshotEvent ev)
    {
        InfoSnapshot?.Invoke(new AppInfoSnapshot(
            Symbol: ev.Symbol,
            SecurityId: ev.SecurityId,
            LastTradePrice: ev.LastTradePrice,
            TradingReferencePrice: ev.TradingReferencePrice,
            ReceivedUtc: new DateTimeOffset(DateTime.SpecifyKind(ev.ReceivedUtc, DateTimeKind.Utc))));
    }

    private void OnSdkConn(ConnectionStateChangedEvent ev) =>
        ConnectionStateChanged?.Invoke(Translate(ev.State));

    private void OnSdkSubErr(SubscribeErrorEvent ev) =>
        SubscribeError?.Invoke(new MarketSubscribeError(ev.Symbol, ev.ErrorCode.ToString()));

    // ── Q3.6 Stage A (#286) MBO translation ─────────────────────────

    private void OnSdkBookSnapshot(BookSnapshotEvent ev)
    {
        var bids = ev.Bids.Count == 0
            ? (IReadOnlyList<MarketBookOrder>)Array.Empty<MarketBookOrder>()
            : ev.Bids.Select(o => new MarketBookOrder(o.OrderId, o.Price, o.Qty)).ToArray();
        var asks = ev.Asks.Count == 0
            ? (IReadOnlyList<MarketBookOrder>)Array.Empty<MarketBookOrder>()
            : ev.Asks.Select(o => new MarketBookOrder(o.OrderId, o.Price, o.Qty)).ToArray();
        BookSnapshot?.Invoke(new MarketBookSnapshot
        {
            Symbol = ev.Symbol,
            SecurityId = ev.SecurityId,
            RptSeq = ev.RptSeq,
            Bids = bids,
            Asks = asks,
            ReceivedUtc = AsOffset(ev.ReceivedUtc),
        });
    }

    private void OnSdkOrderAdded(OrderAddedEvent ev) =>
        OrderAdded?.Invoke(new MarketOrderAdded(
            ev.Symbol, ev.SecurityId, ev.OrderId, TranslateSide(ev.Side),
            ev.Price, ev.Qty, AsOffset(ev.ReceivedUtc)));

    private void OnSdkOrderUpdated(OrderUpdatedEvent ev) =>
        OrderUpdated?.Invoke(new MarketOrderUpdated(
            ev.Symbol, ev.SecurityId, ev.OrderId, TranslateSide(ev.Side),
            ev.Price, ev.Qty, AsOffset(ev.ReceivedUtc)));

    private void OnSdkOrderDeleted(OrderDeletedEvent ev) =>
        OrderDeleted?.Invoke(new MarketOrderDeleted(
            ev.Symbol, ev.SecurityId, ev.OrderId, TranslateSide(ev.Side),
            AsOffset(ev.ReceivedUtc)));

    private void OnSdkBookCleared(BookClearedEvent ev) =>
        BookCleared?.Invoke(new MarketBookCleared(
            ev.Symbol, ev.SecurityId, TranslateClearSide(ev.ClearSide),
            AsOffset(ev.ReceivedUtc)));

    private static DateTimeOffset AsOffset(DateTime dt) =>
        new(DateTime.SpecifyKind(dt, DateTimeKind.Utc));

    private static MarketBookSide TranslateSide(BookSide s) => s switch
    {
        BookSide.Bid => MarketBookSide.Bid,
        BookSide.Ask => MarketBookSide.Ask,
        _ => MarketBookSide.Bid,
    };

    private static MarketBookClearSide TranslateClearSide(BookClearSide s) => s switch
    {
        BookClearSide.Both => MarketBookClearSide.Both,
        BookClearSide.Bid => MarketBookClearSide.Bid,
        BookClearSide.Ask => MarketBookClearSide.Ask,
        _ => MarketBookClearSide.Both,
    };

    private static AppConnState Translate(SdkConnState s) => s switch
    {
        SdkConnState.Disconnected => AppConnState.Disconnected,
        SdkConnState.Connecting => AppConnState.Connecting,
        SdkConnState.Connected => AppConnState.Connected,
        SdkConnState.Reconnecting => AppConnState.Reconnecting,
        SdkConnState.Faulted => AppConnState.Faulted,
        _ => AppConnState.Disconnected,
    };
}
