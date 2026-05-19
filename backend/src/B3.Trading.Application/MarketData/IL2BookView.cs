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
