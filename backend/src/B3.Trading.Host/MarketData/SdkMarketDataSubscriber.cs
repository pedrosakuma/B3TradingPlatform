using B3.MarketData.WebSocketClient;
using B3.Trading.Application.MarketData;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AppConnState = B3.Trading.Application.MarketData.MarketDataConnectionState;
using AppTrade = B3.Trading.Application.MarketData.MarketTrade;
using AppInfoSnapshot = B3.Trading.Application.MarketData.MarketInfoSnapshot;
using SdkConnState = B3.MarketData.WebSocketClient.ConnectionState;
using SdkAuctionCondition = B3.MarketData.WebSocketClient.AuctionImbalanceCondition;

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
    private readonly AuctionProjector _auctionProjector;

    public event Action<AppTrade>? Trade;
    public event Action<AppInfoSnapshot>? InfoSnapshot;
    public event Action<AppConnState>? ConnectionStateChanged;
    public event Action<MarketSubscribeError>? SubscribeError;

    // Auction events: surfaced by SDK 0.4.0 as cumulative fields on
    // InfoSnapshotEvent (TheoreticalOpening / AuctionImbalance) and as a
    // TradeFlags bit on TradeEvent (AuctionPrint). The host adapter funnels
    // those into AuctionProjector, which collapses cumulative snapshots into
    // delta events and decides opening vs closing kind from the last
    // TradingStatus observed for the symbol. Upstream B3MarketDataPlatform#40.
    public event Action<MarketTheoreticalOpening>? TheoreticalOpening;
    public event Action<MarketAuctionImbalance>? AuctionImbalance;
    public event Action<MarketAuctionPrint>? AuctionPrint;

    // ── #370 Stage A — venue trading-status delta detection ─────────
    // SDK 0.4.0 carries TradingStatus as a nullable long inside
    // InfoSnapshotEvent. The application seam wants a discrete event,
    // not a snapshot stream, so we keep the last observed value per
    // symbol here and only raise when it changes. Single-threaded
    // access from the SDK callback path; no lock needed.
    public event Action<MarketTradingStatusChange>? TradingStatusChanged;
    private readonly Dictionary<string, long> _lastTradingStatus =
        new(StringComparer.OrdinalIgnoreCase);

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

        _auctionProjector = new AuctionProjector();
        _auctionProjector.TheoreticalOpening += ev => TheoreticalOpening?.Invoke(ev);
        _auctionProjector.AuctionImbalance += ev => AuctionImbalance?.Invoke(ev);
        _auctionProjector.AuctionPrint += ev => AuctionPrint?.Invoke(ev);

        _client.Trade += OnSdkTrade;
        _client.InfoSnapshot += OnSdkInfo;
        _client.ConnectionStateChanged += OnSdkConn;
        _client.SubscribeError += OnSdkSubErr;

        // Note: when _bookEnabled is true the host registers SDK 0.4.0's
        // IBookFeed which subscribes to MarketDataClient.Book* events
        // internally and maintains the materialised book that
        // SdkBookFeedAdapter projects into IL2BookView. We only need to
        // include SubscribeFlags.Book so the server actually streams the
        // MBO frames to that BookFeed.
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
        return _client.DisposeAsync();
    }

    private void OnSdkTrade(TradeEvent ev)
    {
        var receivedUtc = new DateTimeOffset(DateTime.SpecifyKind(ev.ReceivedUtc, DateTimeKind.Utc));
        Trade?.Invoke(new AppTrade(
            Symbol: ev.Symbol,
            SecurityId: ev.SecurityId,
            Price: ev.Price,
            Qty: ev.Qty,
            ReceivedUtc: receivedUtc));

        if ((ev.Flags & TradeFlags.AuctionPrint) != 0)
        {
            _auctionProjector.OnAuctionTrade(ev.Symbol, ev.SecurityId, ev.Price, ev.Qty, receivedUtc);
        }
    }

    private void OnSdkInfo(InfoSnapshotEvent ev)
    {
        var receivedUtc = new DateTimeOffset(DateTime.SpecifyKind(ev.ReceivedUtc, DateTimeKind.Utc));
        InfoSnapshot?.Invoke(new AppInfoSnapshot(
            Symbol: ev.Symbol,
            SecurityId: ev.SecurityId,
            LastTradePrice: ev.LastTradePrice,
            TradingReferencePrice: ev.TradingReferencePrice,
            ReceivedUtc: receivedUtc));

        _auctionProjector.OnInfoSnapshot(
            symbol: ev.Symbol,
            securityId: ev.SecurityId,
            theoreticalOpeningPrice: ev.TheoreticalOpeningPrice,
            theoreticalOpeningSize: ev.TheoreticalOpeningSize,
            imbalanceSize: ev.AuctionImbalanceSize,
            imbalanceSide: TranslateAuctionSide(ev.AuctionImbalanceCondition),
            tradingStatus: ev.TradingStatus,
            receivedUtc: receivedUtc);

        // #370 Stage A: surface a TradingStatus delta only when the
        // SDK actually carries the field and the value changed. The
        // wire encodes status as long? (SBE SecurityTradingStatus
        // codes) — see SecurityTradingStatusCodes for the values
        // VenueHaltSubscriber interprets.
        if (ev.TradingStatus is { } status)
        {
            long? previous = null;
            var changed = true;
            if (_lastTradingStatus.TryGetValue(ev.Symbol, out var prev))
            {
                previous = prev;
                changed = prev != status;
            }
            if (changed)
            {
                _lastTradingStatus[ev.Symbol] = status;
                TradingStatusChanged?.Invoke(new MarketTradingStatusChange(
                    Symbol: ev.Symbol,
                    SecurityId: ev.SecurityId,
                    PreviousStatus: previous,
                    NewStatus: status,
                    ReceivedUtc: receivedUtc));
            }
        }
    }

    private static OrderSide? TranslateAuctionSide(SdkAuctionCondition? c) => c switch
    {
        SdkAuctionCondition.MoreBuyers => OrderSide.Buy,
        SdkAuctionCondition.MoreSellers => OrderSide.Sell,
        // Balanced / Unknown / null → no pending side → no imbalance delta.
        _ => null,
    };

    private void OnSdkConn(ConnectionStateChangedEvent ev) =>
        ConnectionStateChanged?.Invoke(Translate(ev.State));

    private void OnSdkSubErr(SubscribeErrorEvent ev) =>
        SubscribeError?.Invoke(new MarketSubscribeError(ev.Symbol, ev.ErrorCode.ToString()));

    // ── Q3.6 Stage A (#286) MBO translation ─────────────────────────
    // Removed: MBO frames are now consumed directly by SDK 0.4.0's
    // IBookFeed (registered in MarketDataRegistration when EnableBook)
    // and surfaced via SdkBookFeedAdapter → IL2BookView. No translation
    // hop on this seam any more.

    private static DateTimeOffset AsOffset(DateTime dt) =>
        new(DateTime.SpecifyKind(dt, DateTimeKind.Utc));

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
