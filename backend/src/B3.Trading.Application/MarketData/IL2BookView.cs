namespace B3.Trading.Application.MarketData;

/// <summary>
/// Q3.6 Stage A (#286). Read-only L2 view derived from the L3 / MBO
/// book maintained by <see cref="MboBookStore"/>. Consumers that only
/// need top-of-book (pegging v2, collar v2, depth-ladder FE channel)
/// take a dependency on this seam instead of the full L3 store so the
/// MBO implementation can change without rippling.
/// </summary>
public interface IL2BookView
{
    /// <summary>
    /// Returns the current best bid + best ask + the aggregated qty /
    /// order count on each top level, or <c>null</c> when the store
    /// has not seen any orders for <paramref name="symbol"/> yet (no
    /// snapshot delivered or both sides emptied via
    /// <see cref="MarketBookCleared"/>).
    /// </summary>
    /// <summary>
    /// Q3.6 Stage B (#286). Returns the top-<paramref name="maxLevels"/>
    /// aggregated depth ladder per side (bids descending by price, asks
    /// ascending). Returns <c>null</c> when the store has not seen any
    /// orders for <paramref name="symbol"/> yet, or both sides are
    /// empty. <paramref name="maxLevels"/> must be &gt; 0.
    /// </summary>
    L2Ladder? GetLadder(string symbol, int maxLevels);

    L2TopOfBook? GetTopOfBook(string symbol);
}

/// <summary>
/// Top-of-book aggregate derived from the per-order MBO book. Either
/// side may be a "missing" (price = 0, qty = 0, count = 0) tuple when
/// the corresponding side is empty — callers should check
/// <see cref="L2Side.OrderCount"/> &gt; 0 before consuming the price.
/// </summary>
public readonly record struct L2TopOfBook(
    string Symbol,
    L2Side Bid,
    L2Side Ask,
    DateTimeOffset UpdatedUtc);

/// <summary>Aggregate of one side of one price level.</summary>
public readonly record struct L2Side(
    decimal Price,
    long TotalQty,
    int OrderCount);

/// <summary>
/// Q3.6 Stage B (#286). Top-N depth ladder derived from the L3 / MBO
/// store: per-side list of aggregated price levels, sorted best-to-
/// worst (bids descending, asks ascending). Used by the FE depth
/// view via the <c>book.${symbol}</c> WS channel. Same null/empty
/// semantics as <see cref="L2TopOfBook"/> — sides may be empty when
/// the store has not seen any orders on that side yet.
/// </summary>
public readonly record struct L2Ladder(
    string Symbol,
    IReadOnlyList<L2Side> Bids,
    IReadOnlyList<L2Side> Asks,
    DateTimeOffset UpdatedUtc);
