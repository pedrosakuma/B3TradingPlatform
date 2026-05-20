using System.Collections.Concurrent;

namespace B3.Trading.Application.MarketData;

/// <summary>
/// In-memory <see cref="IL2BookView"/> implementation: maintains a per-
/// symbol L3 (MBO) book and exposes a derived L2 top + ladder. Callers
/// drive it directly through <c>Apply*</c> mutators — there is no
/// adapter to a live wire feed; the production MBO path is owned by
/// <c>SdkBookFeedAdapter</c> (host-side, backed by the SDK 0.4.0
/// <c>IBookFeed</c>) which materializes the same surface from real
/// <c>MarketDataClient</c> events.
///
/// <para>
/// Two roles today:
/// <list type="bullet">
///   <item>The <c>IL2BookView</c> fallback when
///         <c>MarketDataOptions.EnableBook</c> is false (or
///         <c>WsUrl</c> is unset) — registered as a singleton, never
///         mutated, so reads return <c>null</c> / empty exactly like
///         the old store-with-no-pump behavior.</item>
///   <item>The programmable fake used by unit tests that exercise the
///         <c>BookChanged</c> →
///         <see cref="WebSockets.WebSocketBookEventSink"/> /
///         <see cref="MboPegBookPump"/> consumer paths without
///         standing up the SDK book feed.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Thread safety.</b> Each per-symbol state is mutated under its
/// own lock. The outer <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// handles concurrent symbol upserts. Reads (<see cref="GetTopOfBook"/>)
/// take the same per-symbol lock to publish a consistent aggregate.
/// </para>
/// </summary>
public sealed class InMemoryL2BookView : IL2BookView
{
    private readonly ConcurrentDictionary<string, SymbolState> _bySymbol =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Q3.6 Stage B (#286). Raised after every applied frame with the
    /// symbol whose state changed. Listeners pull the derived view they
    /// want (top-of-book or top-N ladder) at their preferred depth so
    /// the store does not pay aggregation cost when nobody is
    /// listening. Fires <b>outside</b> the per-symbol lock so a slow
    /// subscriber cannot stall the apply path; producers must
    /// therefore tolerate stale reads racing newer updates (the
    /// derived snapshot is point-in-time and self-contained).
    /// </summary>
    public event Action<string>? BookChanged;

    public void ApplySnapshot(MarketBookSnapshot snap)
    {
        var state = _bySymbol.GetOrAdd(snap.Symbol, _ => new SymbolState(snap.Symbol));
        lock (state.Gate)
        {
            state.Bids.Clear();
            state.Asks.Clear();
            foreach (var o in snap.Bids)
                state.Bids[o.OrderId] = new OrderEntry(o.Price, o.Qty);
            foreach (var o in snap.Asks)
                state.Asks[o.OrderId] = new OrderEntry(o.Price, o.Qty);
            state.UpdatedUtc = snap.ReceivedUtc;
        }
        BookChanged?.Invoke(snap.Symbol);
    }

    public void ApplyAdded(MarketOrderAdded ev)
    {
        if (ev.Qty <= 0) return;
        var state = _bySymbol.GetOrAdd(ev.Symbol, _ => new SymbolState(ev.Symbol));
        lock (state.Gate)
        {
            SideMap(state, ev.Side)[ev.OrderId] = new OrderEntry(ev.Price, ev.Qty);
            state.UpdatedUtc = ev.ReceivedUtc;
        }
        BookChanged?.Invoke(ev.Symbol);
    }

    public void ApplyUpdated(MarketOrderUpdated ev)
    {
        var state = _bySymbol.GetOrAdd(ev.Symbol, _ => new SymbolState(ev.Symbol));
        lock (state.Gate)
        {
            var map = SideMap(state, ev.Side);
            if (ev.Qty <= 0)
            {
                map.Remove(ev.OrderId);
            }
            else
            {
                map[ev.OrderId] = new OrderEntry(ev.Price, ev.Qty);
            }
            state.UpdatedUtc = ev.ReceivedUtc;
        }
        BookChanged?.Invoke(ev.Symbol);
    }

    public void ApplyDeleted(MarketOrderDeleted ev)
    {
        if (!_bySymbol.TryGetValue(ev.Symbol, out var state)) return;
        lock (state.Gate)
        {
            SideMap(state, ev.Side).Remove(ev.OrderId);
            state.UpdatedUtc = ev.ReceivedUtc;
        }
        BookChanged?.Invoke(ev.Symbol);
    }

    public void ApplyCleared(MarketBookCleared ev)
    {
        if (!_bySymbol.TryGetValue(ev.Symbol, out var state)) return;
        lock (state.Gate)
        {
            switch (ev.ClearSide)
            {
                case MarketBookClearSide.Both:
                    state.Bids.Clear();
                    state.Asks.Clear();
                    break;
                case MarketBookClearSide.Bid:
                    state.Bids.Clear();
                    break;
                case MarketBookClearSide.Ask:
                    state.Asks.Clear();
                    break;
            }
            state.UpdatedUtc = ev.ReceivedUtc;
        }
        BookChanged?.Invoke(ev.Symbol);
    }

    public L2TopOfBook? GetTopOfBook(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        if (!_bySymbol.TryGetValue(symbol.Trim(), out var state)) return null;
        lock (state.Gate) return ComputeTopLocked(state);
    }

    public L2Ladder? GetLadder(string symbol, int maxLevels)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        if (maxLevels <= 0) throw new ArgumentOutOfRangeException(nameof(maxLevels));
        if (!_bySymbol.TryGetValue(symbol.Trim(), out var state)) return null;
        lock (state.Gate)
        {
            if (state.Bids.Count == 0 && state.Asks.Count == 0) return null;
            var bids = LadderSideLocked(state.Bids, ascending: false, maxLevels);
            var asks = LadderSideLocked(state.Asks, ascending: true, maxLevels);
            return new L2Ladder(state.Symbol, bids, asks, state.UpdatedUtc);
        }
    }

    private static L2TopOfBook? ComputeTopLocked(SymbolState state)
    {
        var bid = TopOfSide(state.Bids, ascending: false);
        var ask = TopOfSide(state.Asks, ascending: true);
        if (bid.OrderCount == 0 && ask.OrderCount == 0) return null;
        return new L2TopOfBook(state.Symbol, bid, ask, state.UpdatedUtc);
    }

    // Aggregates the per-order side map into top-N price levels, sorted
    // best-to-worst. Caller MUST hold the per-symbol gate.
    private static IReadOnlyList<L2Side> LadderSideLocked(
        Dictionary<ulong, OrderEntry> side, bool ascending, int maxLevels)
    {
        if (side.Count == 0) return Array.Empty<L2Side>();
        // Aggregate by price first (qty + count). Capacity hint is a
        // mild over-estimate to avoid resizes for the common case.
        var byPrice = new Dictionary<decimal, (long Qty, int Count)>(side.Count);
        foreach (var e in side.Values)
        {
            if (byPrice.TryGetValue(e.Price, out var agg))
                byPrice[e.Price] = (agg.Qty + e.Qty, agg.Count + 1);
            else
                byPrice[e.Price] = (e.Qty, 1);
        }
        // Sort then trim to maxLevels. Ascending = ask side (best = lowest),
        // descending = bid side (best = highest).
        var ordered = ascending
            ? byPrice.OrderBy(kv => kv.Key)
            : byPrice.OrderByDescending(kv => kv.Key);
        var result = new List<L2Side>(Math.Min(maxLevels, byPrice.Count));
        foreach (var kv in ordered)
        {
            if (result.Count >= maxLevels) break;
            result.Add(new L2Side(kv.Key, kv.Value.Qty, kv.Value.Count));
        }
        return result;
    }

    /// <summary>Test/diagnostic hook: per-side live order count.</summary>
    public (int Bids, int Asks) GetOrderCounts(string symbol)
    {
        if (!_bySymbol.TryGetValue(symbol, out var state)) return (0, 0);
        lock (state.Gate) return (state.Bids.Count, state.Asks.Count);
    }

    private static L2Side TopOfSide(Dictionary<ulong, OrderEntry> side, bool ascending)
    {
        if (side.Count == 0) return new L2Side(0m, 0, 0);
        decimal best = ascending ? decimal.MaxValue : decimal.MinValue;
        foreach (var e in side.Values)
        {
            if (ascending ? e.Price < best : e.Price > best)
                best = e.Price;
        }
        long qty = 0;
        int count = 0;
        foreach (var e in side.Values)
        {
            if (e.Price == best)
            {
                qty += e.Qty;
                count++;
            }
        }
        return new L2Side(best, qty, count);
    }

    private static Dictionary<ulong, OrderEntry> SideMap(SymbolState s, MarketBookSide side) =>
        side == MarketBookSide.Bid ? s.Bids : s.Asks;

    private readonly record struct OrderEntry(decimal Price, long Qty);

    private sealed class SymbolState
    {
        public SymbolState(string symbol) { Symbol = symbol; }
        public string Symbol { get; }
        public Dictionary<ulong, OrderEntry> Bids { get; } = new();
        public Dictionary<ulong, OrderEntry> Asks { get; } = new();
        public DateTimeOffset UpdatedUtc { get; set; }
        public object Gate { get; } = new();
    }
}
