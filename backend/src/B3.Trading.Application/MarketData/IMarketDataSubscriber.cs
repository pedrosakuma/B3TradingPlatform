namespace B3.Trading.Application.MarketData;

/// <summary>
/// Application-side seam over the market-data SDK so risk logic can be
/// unit-tested without touching the network. The host wires
/// <c>SdkMarketDataSubscriber</c> (wraps <c>MarketDataClient</c>); tests
/// wire a fake that synchronously raises events on demand.
///
/// <para>
/// Implementations MUST raise <see cref="Trade"/> / <see cref="InfoSnapshot"/>
/// with already-validated payloads (positive prices, non-empty symbol).
/// Consumers can still defensively skip junk, but the seam exists to
/// keep that policy out of risk-side code paths.
/// </para>
/// </summary>
public interface IMarketDataSubscriber : IAsyncDisposable
{
    MarketDataConnectionState State { get; }

    /// <summary>Last-observed dropped-event count from the underlying
    /// bounded back-pressure channel. Surfaced as an observable gauge
    /// for ops alerting.</summary>
    long DroppedEventCount { get; }

    event Action<MarketTrade>? Trade;
    event Action<MarketInfoSnapshot>? InfoSnapshot;
    event Action<MarketDataConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Raised when the upstream rejects a Subscribe (unknown symbol,
    /// not ready, …). Symbol-mapping mismatches between this host and
    /// the MD platform surface here.
    /// </summary>
    event Action<MarketSubscribeError>? SubscribeError;

    // -------------------------------------------------------------
    // Auction frames (Q1.5 / #257). These project the three UMDF
    // auction frame types — TheoreticalOpeningPrice_16,
    // AuctionImbalance_19 and AuctionPrint — into application-owned
    // records. They feed AuctionStateStore which in turn drives the
    // public phases.* / auction.* WS channels.
    //
    // Note (SDK gap): B3.MarketData.WebSocketClient 0.1.0 does NOT
    // surface dedicated auction events today; the wire protocol does
    // carry the fields (FieldTheoreticalOpeningPrice=7,
    // FieldTheoreticalOpeningSize=8, FieldAuctionImbalanceSize=9 in
    // InfoSnapshot) but the SDK reader skips them. The adapter
    // (SdkMarketDataSubscriber) leaves these events unraised until
    // the SDK is bumped; tests use an in-process fake to drive them.
    // Tracking: B3MatchingPlatform#321 / #322.
    // -------------------------------------------------------------

    event Action<MarketTheoreticalOpening>? TheoreticalOpening;
    event Action<MarketAuctionImbalance>? AuctionImbalance;
    event Action<MarketAuctionPrint>? AuctionPrint;

    // -------------------------------------------------------------
    // Per-symbol trading-status delta (#370 Stage A). The adapter
    // remembers the last observed status per symbol (raw SBE
    // SecurityTradingStatus code carried in InfoSnapshot.TradingStatus)
    // and raises this event only when it actually changes. Consumers:
    // VenueHaltSubscriber bridges PAUSE/FORBIDDEN ↔ SymbolHaltService.
    // Stage B will replace the delta-detection with a typed SDK event
    // once B3.MarketData.WebSocketClient exposes one.
    // -------------------------------------------------------------

    event Action<MarketTradingStatusChange>? TradingStatusChanged;

    // L3 / MBO frames are not raised on this seam. The live wire path
    // funnels them through SDK 0.4.0's IBookFeed → SdkBookFeedAdapter →
    // IL2BookView; tests that need to drive an L3 book directly use
    // InMemoryL2BookView's Apply* mutators instead.

    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Subscribes to trade prints + info snapshots for <paramref name="symbol"/>.
    /// Idempotent on the SDK side.
    /// </summary>
    ValueTask SubscribeAsync(string symbol, CancellationToken ct = default);
}

public readonly record struct MarketSubscribeError(string Symbol, string Reason);
