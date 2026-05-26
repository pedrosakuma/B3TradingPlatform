using B3.MarketData.WebSocketClient;
using B3.Trading.Application;
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
    private readonly bool _securityDefinitionEnabled;
    private readonly SecurityDefinitionRegistry? _securityDefinitionRegistry;
    private readonly bool _priceBandEnabled;
    private readonly PriceBandRegistry? _priceBandRegistry;
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
        IOptions<MarketDataOptions> options,
        SecurityDefinitionRegistry? securityDefinitionRegistry = null,
        PriceBandRegistry? priceBandRegistry = null)
    {
        _client = client;
        _logger = logger;
        _bookEnabled = options.Value.EnableBook;
        _securityDefinitionEnabled = options.Value.EnableSecurityDefinition
            && securityDefinitionRegistry is not null;
        _securityDefinitionRegistry = _securityDefinitionEnabled
            ? securityDefinitionRegistry
            : null;
        _priceBandEnabled = options.Value.EnablePriceBand
            && priceBandRegistry is not null;
        _priceBandRegistry = _priceBandEnabled
            ? priceBandRegistry
            : null;

        // OPT-D (#486). SubscribeFlags.SecurityDefinition (0x20) ships
        // in SDK 0.5.0 / pedrosakuma/B3MarketDataPlatform#55 — bootstrap
        // + delta of tick / lot / contractMultiplier / option metadata
        // per symbol, projected by OnSdkSecurityDefinition into the
        // registry SymbolDirectory.TryGetSpec consults first.
        // OPT-E (#487). SubscribeFlags.PriceBand (0x40) ships in
        // SDK 0.6.0 / pedrosakuma/B3MarketDataPlatform#56 — bootstrap
        // + delta of the venue's authoritative dynamic price band per
        // symbol, projected by OnSdkPriceBand into the registry
        // PriceBandCheck consults.
        var flags = SubscribeFlags.Trades | SubscribeFlags.Info;
        if (_bookEnabled) flags |= SubscribeFlags.Book;
        if (_securityDefinitionEnabled) flags |= SubscribeFlags.SecurityDefinition;
        if (_priceBandEnabled) flags |= SubscribeFlags.PriceBand;
        _subscribeFlags = flags;

        _auctionProjector = new AuctionProjector();
        _auctionProjector.TheoreticalOpening += ev => TheoreticalOpening?.Invoke(ev);
        _auctionProjector.AuctionImbalance += ev => AuctionImbalance?.Invoke(ev);
        _auctionProjector.AuctionPrint += ev => AuctionPrint?.Invoke(ev);

        _client.Trade += OnSdkTrade;
        _client.InfoSnapshot += OnSdkInfo;
        _client.ConnectionStateChanged += OnSdkConn;
        _client.SubscribeError += OnSdkSubErr;
        if (_securityDefinitionEnabled)
        {
            _client.SecurityDefinition += OnSdkSecurityDefinition;
        }
        if (_priceBandEnabled)
        {
            _client.PriceBand += OnSdkPriceBand;
        }

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
        if (_securityDefinitionEnabled)
        {
            _client.SecurityDefinition -= OnSdkSecurityDefinition;
        }
        if (_priceBandEnabled)
        {
            _client.PriceBand -= OnSdkPriceBand;
        }
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

    private void OnSdkSecurityDefinition(SecurityDefinitionEvent ev)
    {
        if (_securityDefinitionRegistry is null) return;
        if (string.IsNullOrWhiteSpace(ev.Symbol)) return;

        // tick: already a scaled decimal per SDK 0.5.0 contract
        // (Fixed8 / 1e8 unwrapped by the client).
        var tick = ev.MinPriceIncrement is { } t && t > 0m ? t : (decimal?)null;
        // lot: shares (equity) or contracts (option), 1-based long.
        var lot = ev.MinTradeVolume is { } l && l > 0 ? l : (long?)null;

        // OPT-D translator lives in Application
        // (SecurityDefinitionRegistry.TryProject) so it can be unit-
        // tested without taking the SDK as a test-project dep. The
        // host adapter passes primitives only.
        var option = SecurityDefinitionRegistry.TryProject(
            contractMultiplier: ev.ContractMultiplier,
            maturityDate: ev.MaturityDate,
            putOrCall: ev.PutOrCall,
            exerciseStyle: ev.ExerciseStyle,
            strikePrice: ev.StrikePrice,
            priceDivisor: ev.PriceDivisor,
            underlyingAsset: ev.Asset);
        var securityType = option is null ? SecurityType.Equity : SecurityType.Option;

        if (tick is null && lot is null && option is null) return;

        var spec = new InstrumentSpec(tick, lot, TickLadder: null, securityType, option);
        _securityDefinitionRegistry.Upsert(ev.Symbol, spec, ev.SecurityId);

        _logger.LogDebug(
            "SecurityDefinition upsert: Symbol={Symbol} SecurityId={SecurityId} Tick={Tick} Lot={Lot} SecurityType={SecurityType} ContractMultiplier={Multiplier}",
            ev.Symbol, ev.SecurityId, tick, lot, securityType,
            option is { } o ? o.ContractMultiplier : 1m);
    }

    private void OnSdkPriceBand(PriceBandEvent ev)
    {
        if (_priceBandRegistry is null) return;
        if (string.IsNullOrWhiteSpace(ev.Symbol)) return;

        // OPT-E translator lives in Application (PriceBandRegistry.TryProject)
        // so it can be unit-tested without taking the SDK as a test-project
        // dep. The host adapter passes primitives only — PriceLimitType
        // discriminates absolute (PRICE_UNIT) vs derived (TICKS/PERCENTAGE);
        // only PRICE_UNIT is projected today, the rest fail-open into
        // PriceBandBypassedNoBand (see XML doc on TryProject).
        if (!PriceBandRegistry.TryProject(
                lowerBand: ev.LowerBand,
                upperBand: ev.UpperBand,
                priceLimitType: ev.PriceLimitType,
                out var lower,
                out var upper))
        {
            return;
        }

        // AsOfTimestamp is a SBE wall-clock long? (SBE epoch nanos when
        // present). When the venue omits it — or it's malformed — fall
        // back to the SDK's ReceivedUtc so the age-staleness signal stays
        // honest (under-counts age in the fallback case, which is the
        // safer bias: the band gets at most a few ms of age credit it
        // didn't strictly earn rather than appearing artificially fresh).
        var asOfUtc = TryParseAsOf(ev.AsOfTimestamp)
            ?? new DateTimeOffset(DateTime.SpecifyKind(ev.ReceivedUtc, DateTimeKind.Utc));

        _priceBandRegistry.Upsert(ev.Symbol, lower, upper, asOfUtc);

        _logger.LogDebug(
            "PriceBand upsert: Symbol={Symbol} SecurityId={SecurityId} Lower={Lower} Upper={Upper} PriceLimitType={Type} AsOf={AsOf}",
            ev.Symbol, ev.SecurityId, lower, upper, ev.PriceLimitType, asOfUtc);
    }

    private static DateTimeOffset? TryParseAsOf(long? sbeNanos)
    {
        // SBE wall-clock epoch in nanoseconds per B3 UMDF convention.
        // Guard the conversion so a 0 / negative / future-dated frame
        // can't poison the staleness gauge with a bogus timestamp.
        if (sbeNanos is not { } n || n <= 0) return null;
        try
        {
            var ticks = n / 100L; // 1 tick = 100 ns
            return new DateTimeOffset(DateTime.UnixEpoch.AddTicks(ticks), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

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
