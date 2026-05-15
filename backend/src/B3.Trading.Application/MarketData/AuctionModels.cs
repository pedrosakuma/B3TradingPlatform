using B3.Trading.Domain;

namespace B3.Trading.Application.MarketData;

/// <summary>
/// Snapshot of an instrument's current auction "top": the theoretical
/// opening price (ToP) plus the most recent indicative match qty and
/// imbalance side / size. All fields are last-observed; an absent
/// signal is encoded as the zero default for the field's type
/// (callers consume this only when the symbol has been touched at
/// least once).
/// </summary>
public sealed record AuctionTopState(
    string Symbol,
    decimal Top,
    long IndicativeMatchQty,
    long Imbalance,
    OrderSide ImbalanceSide,
    DateTimeOffset At);

/// <summary>Distinguishes the opening cross from the closing cross.</summary>
public enum AuctionPrintKind
{
    Opening,
    Closing,
}

/// <summary>
/// A single auction cross print emitted by matching at the end of an
/// opening or closing call.
/// </summary>
public sealed record AuctionPrint(
    string Symbol,
    AuctionPrintKind Kind,
    decimal Price,
    long Qty,
    DateTimeOffset At);

/// <summary>
/// Phase-transition delta. Emitted by <see cref="AuctionStateStore"/>
/// every time a symbol's phase moves; idempotent updates (same phase
/// observed twice in a row) are suppressed.
/// </summary>
public sealed record PhaseChange(
    string Symbol,
    TradingPhase Phase,
    DateTimeOffset At);

// -----------------------------------------------------------------
// Application-owned shapes for the three UMDF auction frame types.
// Mirror MarketTrade / MarketInfoSnapshot — host adapter translates
// SDK DTOs into these so the application + tests never see the SDK
// types. See SdkMarketDataSubscriber for the adapter.
// -----------------------------------------------------------------

/// <summary>
/// UMDF <c>TheoreticalOpeningPrice_16</c> projection: the matching
/// engine's auction-uncross indicative price + qty for a symbol.
/// </summary>
public readonly record struct MarketTheoreticalOpening(
    string Symbol,
    ulong SecurityId,
    decimal Price,
    long Qty,
    DateTimeOffset ReceivedUtc);

/// <summary>
/// UMDF <c>AuctionImbalance_19</c> projection: net buy / sell
/// imbalance qty at the indicative cross. <see cref="Side"/>
/// indicates which side has the surplus volume.
/// </summary>
public readonly record struct MarketAuctionImbalance(
    string Symbol,
    ulong SecurityId,
    long Quantity,
    OrderSide Side,
    DateTimeOffset ReceivedUtc);

/// <summary>
/// UMDF <c>AuctionPrint</c> projection: the actual cross print at the
/// end of an opening / closing auction.
/// </summary>
public readonly record struct MarketAuctionPrint(
    string Symbol,
    ulong SecurityId,
    AuctionPrintKind Kind,
    decimal Price,
    long Qty,
    DateTimeOffset ReceivedUtc);
