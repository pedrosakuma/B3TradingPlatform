using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application.MarketData;

/// <summary>
/// Q3.3 (#283). Per-symbol cache of the book-top fields a Pegged algo
/// needs (best bid, best ask, last trade). Owned by the application
/// layer so tests can inject prices directly without a real MD feed.
///
/// <para>
/// <b>BBO source (Q3.6 Stage C, #286).</b> Best-bid / best-ask are
/// populated by <see cref="MboPegBookPump"/> off the in-host
/// <see cref="MboBookStore"/> when <c>MarketDataOptions.EnableBook</c>
/// is on — the SDK still does not raise standalone BBO frames, but
/// the MBO feed already carries the per-order book we aggregate. When
/// MBO is disabled, the BBO legs stay null and <see cref="BookTop"/>
/// transparently falls back to last-trade for all three pegRefs
/// (legacy v1 behavior). Last-trade itself is fed by
/// <see cref="MarketDataPegBookPump"/> off <c>Trade</c> /
/// <c>InfoSnapshot</c> events as before.
/// </para>
/// </summary>
public sealed class PegBookTopCache
{
    private readonly ConcurrentDictionary<string, BookTop> _book =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Snapshot, never <c>null</c> entries — returns <c>null</c> when
    /// the symbol has never been seeded. Caller should treat an empty
    /// snapshot as "no live reference yet".
    /// </summary>
    public BookTop? TryGet(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        return _book.TryGetValue(symbol.Trim(), out var t) ? t : null;
    }

    /// <summary>
    /// Updates the last-trade leg. Idempotent; safe to call from any
    /// thread.
    /// </summary>
    public void UpdateLast(string symbol, decimal price, DateTimeOffset receivedUtc)
    {
        if (string.IsNullOrWhiteSpace(symbol) || price <= 0m) return;
        var key = symbol.Trim();
        _book.AddOrUpdate(
            key,
            _ => new BookTop(null, null, price, receivedUtc),
            (_, existing) => new BookTop(existing.BestBid, existing.BestAsk, price, receivedUtc));
    }

    /// <summary>
    /// Updates the BBO legs. Called by future BBO-aware adapters or by
    /// tests that need to drive mid/best directly. Either leg may be
    /// <c>null</c> to leave it untouched.
    /// </summary>
    public void UpdateBookTop(string symbol, decimal? bestBid, decimal? bestAsk, DateTimeOffset receivedUtc)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        var key = symbol.Trim();
        _book.AddOrUpdate(
            key,
            _ => new BookTop(bestBid, bestAsk, null, receivedUtc),
            (_, existing) => new BookTop(
                bestBid ?? existing.BestBid,
                bestAsk ?? existing.BestAsk,
                existing.Last,
                receivedUtc));
    }
}

/// <summary>
/// Immutable per-symbol snapshot of the book top. <see cref="Mid"/>
/// falls back to <see cref="Last"/> when BBO legs are missing;
/// <see cref="BestForSide"/> falls back to the same-side leg or
/// <see cref="Last"/> when absent. See <see cref="PegBookTopCache"/>
/// for the SDK-gap note.
/// </summary>
public readonly record struct BookTop(
    decimal? BestBid,
    decimal? BestAsk,
    decimal? Last,
    DateTimeOffset UpdatedUtc)
{
    public decimal? Mid =>
        BestBid is { } b && BestAsk is { } a ? (b + a) / 2m : Last;

    public decimal? BestForSide(OrderSide side) =>
        side == OrderSide.Buy ? (BestBid ?? Last) : (BestAsk ?? Last);

    /// <summary>
    /// Resolves the reference price for the requested <paramref name="kind"/>.
    /// Returns <c>null</c> when no live data is available — caller should
    /// no-op and wait for the next tick.
    /// </summary>
    public decimal? RefPrice(PegRef kind, OrderSide side) => kind switch
    {
        PegRef.Mid => Mid,
        PegRef.Best => BestForSide(side),
        PegRef.Last => Last,
        _ => null,
    };
}
