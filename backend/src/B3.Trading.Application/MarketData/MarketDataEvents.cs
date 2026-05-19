namespace B3.Trading.Application.MarketData;

/// <summary>
/// Application-owned market-data event records. Decouple risk logic
/// from the upstream <c>B3.MarketData.WebSocketClient</c> SDK shapes:
/// <c>SdkMarketDataSubscriber</c> in the host translates the SDK
/// events into these on the way in, so anything below this layer
/// (tests, alternate transports) only needs to deal with our own
/// types.
/// </summary>
public readonly record struct MarketTrade(
    string Symbol,
    ulong SecurityId,
    decimal Price,
    long Qty,
    DateTimeOffset ReceivedUtc);

/// <summary>
/// Snapshot view of an instrument's reference data (open / close /
/// last trade / trading reference). Only the fields we currently use
/// are surfaced; if a future risk check needs e.g. <c>VwapPrice</c>,
/// add it here and propagate.
/// </summary>
public readonly record struct MarketInfoSnapshot(
    string Symbol,
    ulong SecurityId,
    decimal? LastTradePrice,
    decimal? TradingReferencePrice,
    DateTimeOffset ReceivedUtc);

/// <summary>
/// Coarse connection state of the underlying market-data feed.
/// Mirrors <c>B3.MarketData.WebSocketClient.ConnectionState</c> 1:1
/// but kept app-owned so tests don't reference the SDK enum.
/// </summary>
public enum MarketDataConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Faulted,
}

// ── L3 / MBO book events (Q3.6 Stage A, #286) ───────────────────────

/// <summary>App-owned mirror of the SDK's <c>BookSide</c> enum.
/// Same wire bytes (0 = Bid, 1 = Ask). Kept here so consumers below
/// the host adapter don't reference the SDK.</summary>
public enum MarketBookSide : byte
{
    Bid = 0,
    Ask = 1,
}

/// <summary>App-owned mirror of the SDK's <c>BookClearSide</c> enum
/// (0 = Both, 1 = Bid, 2 = Ask).</summary>
public enum MarketBookClearSide : byte
{
    Both = 0,
    Bid = 1,
    Ask = 2,
}

/// <summary>One MBO order inside a <see cref="MarketBookSnapshot"/>.
/// Price is already scaled by the SDK.</summary>
public readonly record struct MarketBookOrder(
    ulong OrderId,
    decimal Price,
    long Qty);

/// <summary>
/// L3 / order-by-order snapshot. Consumers MUST clear any prior per-
/// symbol L3 state on receipt and then rebuild from <see cref="Bids"/>
/// / <see cref="Asks"/> (which may be empty if the server sends the
/// snapshot as an empty marker followed by <see cref="MarketOrderAdded"/>
/// frames in the same packet). <see cref="RptSeq"/> matches the
/// per-symbol sequence the next incremental will carry.
/// </summary>
public sealed class MarketBookSnapshot
{
    public required string Symbol { get; init; }
    public required ulong SecurityId { get; init; }
    public required uint RptSeq { get; init; }
    public IReadOnlyList<MarketBookOrder> Bids { get; init; } = Array.Empty<MarketBookOrder>();
    public IReadOnlyList<MarketBookOrder> Asks { get; init; } = Array.Empty<MarketBookOrder>();
    public required DateTimeOffset ReceivedUtc { get; init; }
}

/// <summary>Per-order Add. Price is already scaled by the SDK.</summary>
public readonly record struct MarketOrderAdded(
    string Symbol,
    ulong SecurityId,
    ulong OrderId,
    MarketBookSide Side,
    decimal Price,
    long Qty,
    DateTimeOffset ReceivedUtc);

/// <summary>Per-order Update (qty / price change). Consumers SHOULD
/// upsert by <see cref="OrderId"/>.</summary>
public readonly record struct MarketOrderUpdated(
    string Symbol,
    ulong SecurityId,
    ulong OrderId,
    MarketBookSide Side,
    decimal Price,
    long Qty,
    DateTimeOffset ReceivedUtc);

/// <summary>Per-order Delete. Consumers MUST drop
/// <see cref="OrderId"/> on <see cref="Side"/>.</summary>
public readonly record struct MarketOrderDeleted(
    string Symbol,
    ulong SecurityId,
    ulong OrderId,
    MarketBookSide Side,
    DateTimeOffset ReceivedUtc);

/// <summary>Mass-delete of one or both sides. A follow-up snapshot is
/// NOT guaranteed; consumers MUST drop every order on the affected
/// side(s).</summary>
public readonly record struct MarketBookCleared(
    string Symbol,
    ulong SecurityId,
    MarketBookClearSide ClearSide,
    DateTimeOffset ReceivedUtc);
