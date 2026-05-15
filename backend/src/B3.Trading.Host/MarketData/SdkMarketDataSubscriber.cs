using B3.MarketData.WebSocketClient;
using B3.Trading.Application.MarketData;
using Microsoft.Extensions.Logging;
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
///   <item><see cref="SubscribeAsync(string, CancellationToken)"/> always asks for
///         <c>Trades | Info</c> so a fresh subscription seeds the cache
///         immediately from the snapshot frame.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class SdkMarketDataSubscriber : IMarketDataSubscriber
{
    private readonly MarketDataClient _client;
    private readonly ILogger<SdkMarketDataSubscriber> _logger;

    public event Action<AppTrade>? Trade;
    public event Action<AppInfoSnapshot>? InfoSnapshot;
    public event Action<AppConnState>? ConnectionStateChanged;
    public event Action<MarketSubscribeError>? SubscribeError;

    // SDK gap: B3.MarketData.WebSocketClient 0.1.0 does not surface
    // dedicated auction events. We declare the events on the seam so
    // AuctionStateStore + WS channels are wired end-to-end; they
    // simply never fire under the live SDK today. Tests inject a fake
    // subscriber that raises them. Tracking: B3MatchingPlatform#321/#322.
#pragma warning disable CS0067 // event never used — see SDK-gap note above.
    public event Action<B3.Trading.Application.MarketData.MarketTheoreticalOpening>? TheoreticalOpening;
    public event Action<B3.Trading.Application.MarketData.MarketAuctionImbalance>? AuctionImbalance;
    public event Action<B3.Trading.Application.MarketData.MarketAuctionPrint>? AuctionPrint;
#pragma warning restore CS0067

    public SdkMarketDataSubscriber(MarketDataClient client, ILogger<SdkMarketDataSubscriber> logger)
    {
        _client = client;
        _logger = logger;

        _client.Trade += OnSdkTrade;
        _client.InfoSnapshot += OnSdkInfo;
        _client.ConnectionStateChanged += OnSdkConn;
        _client.SubscribeError += OnSdkSubErr;
    }

    public AppConnState State => Translate(_client.State);

    public long DroppedEventCount => _client.DroppedEventCount;

    public Task ConnectAsync(CancellationToken ct = default) => _client.ConnectAsync(ct);

    public async ValueTask SubscribeAsync(string symbol, CancellationToken ct = default)
    {
        await _client.SubscribeAsync(symbol, SubscribeFlags.Trades | SubscribeFlags.Info, ct)
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
        Trade?.Invoke(new AppTrade(
            Symbol: ev.Symbol,
            SecurityId: ev.SecurityId,
            Price: ev.Price,
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
