using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application.MarketData;

/// <summary>
/// Pure projection of upstream market-data auction fields into the
/// application-side <see cref="MarketTheoreticalOpening"/>,
/// <see cref="MarketAuctionImbalance"/> and <see cref="MarketAuctionPrint"/>
/// deltas. Stateful in the small: it remembers the last value emitted per
/// symbol so cumulative snapshots from the SDK (re-published with every
/// frame) collapse into change events only.
///
/// <para>Lives in <c>B3.Trading.Application</c> on purpose — the host
/// adapter (<c>SdkMarketDataSubscriber</c>) translates SDK enums into
/// primitives + <see cref="OrderSide"/> and hands them here, so the
/// projector stays free of any SDK package reference and is
/// directly unit-testable.</para>
///
/// <para><b>Trading-status codes</b>: <see cref="ReservedTradingStatus"/>
/// (21) and <see cref="FinalClosingCallTradingStatus"/> (101) come from the
/// B3 UMDF SBE schema 2.2.0 (<c>SecurityTradingStatus</c>). They drive
/// <see cref="MarketAuctionPrint.Kind"/>: an auction cross emitted while
/// the venue reports <c>FINAL_CLOSING_CALL</c> is a closing print; everything
/// else (opening / reopening) is an opening print.</para>
/// </summary>
public sealed class AuctionProjector
{
    /// <summary>B3 SBE <c>SecurityTradingStatus</c> code for pre-open / reserved.</summary>
    public const long ReservedTradingStatus = 21;

    /// <summary>B3 SBE <c>SecurityTradingStatus</c> code for the final closing call.</summary>
    public const long FinalClosingCallTradingStatus = 101;

    private readonly ConcurrentDictionary<string, SymbolState> _bySymbol = new(StringComparer.Ordinal);

    /// <summary>Fires when the theoretical-opening (price, qty) pair changes for a symbol.</summary>
    public event Action<MarketTheoreticalOpening>? TheoreticalOpening;

    /// <summary>Fires when the auction imbalance (qty + pending side) changes for a symbol.</summary>
    public event Action<MarketAuctionImbalance>? AuctionImbalance;

    /// <summary>Fires for every trade flagged as an auction print.</summary>
    public event Action<MarketAuctionPrint>? AuctionPrint;

    /// <summary>
    /// Ingest one info snapshot. <paramref name="theoreticalOpeningPrice"/> and
    /// <paramref name="theoreticalOpeningSize"/> are paired — both must be present
    /// for a theoretical-opening delta to fire. <paramref name="imbalanceSide"/>
    /// is <c>null</c> when the venue reports a balanced book or has not yet
    /// published an imbalance; in that case no imbalance delta fires.
    /// <paramref name="tradingStatus"/> is remembered so a subsequent auction
    /// print can be classified as opening vs closing.
    /// </summary>
    public void OnInfoSnapshot(
        string symbol,
        ulong securityId,
        decimal? theoreticalOpeningPrice,
        long? theoreticalOpeningSize,
        long? imbalanceSize,
        OrderSide? imbalanceSide,
        long? tradingStatus,
        DateTimeOffset receivedUtc)
    {
        if (string.IsNullOrEmpty(symbol)) return;
        var state = _bySymbol.GetOrAdd(symbol, _ => new SymbolState());

        MarketTheoreticalOpening? topDelta = null;
        MarketAuctionImbalance? imbDelta = null;

        lock (state.Sync)
        {
            if (tradingStatus.HasValue)
            {
                state.LastTradingStatus = tradingStatus.Value;
            }

            if (theoreticalOpeningPrice.HasValue && theoreticalOpeningSize.HasValue)
            {
                var price = theoreticalOpeningPrice.Value;
                var qty = theoreticalOpeningSize.Value;
                if (!state.HasTop || state.LastTopPrice != price || state.LastTopQty != qty)
                {
                    state.HasTop = true;
                    state.LastTopPrice = price;
                    state.LastTopQty = qty;
                    topDelta = new MarketTheoreticalOpening(symbol, securityId, price, qty, receivedUtc);
                }
            }

            if (imbalanceSize.HasValue && imbalanceSide.HasValue)
            {
                var qty = imbalanceSize.Value;
                var side = imbalanceSide.Value;
                if (!state.HasImbalance || state.LastImbalanceQty != qty || state.LastImbalanceSide != side)
                {
                    state.HasImbalance = true;
                    state.LastImbalanceQty = qty;
                    state.LastImbalanceSide = side;
                    imbDelta = new MarketAuctionImbalance(symbol, securityId, qty, side, receivedUtc);
                }
            }
        }

        if (topDelta is { } t) TheoreticalOpening?.Invoke(t);
        if (imbDelta is { } i) AuctionImbalance?.Invoke(i);
    }

    /// <summary>
    /// Ingest one trade that the SDK has flagged as an auction print
    /// (<c>TradeFlags.AuctionPrint</c>). The print's
    /// <see cref="AuctionPrintKind"/> is decided from the last
    /// <c>TradingStatus</c> observed on the same symbol via
    /// <see cref="OnInfoSnapshot"/>: <c>FINAL_CLOSING_CALL</c> ⇒ closing,
    /// anything else (including never-observed) ⇒ opening.
    /// </summary>
    public void OnAuctionTrade(
        string symbol,
        ulong securityId,
        decimal price,
        long qty,
        DateTimeOffset receivedUtc)
    {
        if (string.IsNullOrEmpty(symbol)) return;
        var state = _bySymbol.GetOrAdd(symbol, _ => new SymbolState());

        AuctionPrintKind kind;
        bool clearTop;
        lock (state.Sync)
        {
            kind = state.LastTradingStatus == FinalClosingCallTradingStatus
                ? AuctionPrintKind.Closing
                : AuctionPrintKind.Opening;

            // The cross has happened: any retained TheoreticalOpening / imbalance
            // for this symbol is now stale. Drop the memo so the next snapshot
            // (or a re-auction) re-publishes from scratch instead of being
            // suppressed as a duplicate.
            clearTop = state.HasTop || state.HasImbalance;
            state.HasTop = false;
            state.HasImbalance = false;
            state.LastTopPrice = default;
            state.LastTopQty = default;
            state.LastImbalanceQty = default;
            state.LastImbalanceSide = default;
        }

        _ = clearTop; // documented behavior; nothing else to do under the lock.
        AuctionPrint?.Invoke(new MarketAuctionPrint(symbol, securityId, kind, price, qty, receivedUtc));
    }

    private sealed class SymbolState
    {
        public readonly object Sync = new();
        public long? LastTradingStatus;
        public bool HasTop;
        public decimal LastTopPrice;
        public long LastTopQty;
        public bool HasImbalance;
        public long LastImbalanceQty;
        public OrderSide LastImbalanceSide;
    }
}
