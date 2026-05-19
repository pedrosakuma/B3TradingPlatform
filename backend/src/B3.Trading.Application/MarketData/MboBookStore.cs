using System.Collections.Concurrent;

namespace B3.Trading.Application.MarketData;

/// <summary>
/// Q3.6 Stage A (#286). In-host L3 (MBO) book store + derived L2 view.
/// Applies the per-symbol stream of
/// <see cref="MarketBookSnapshot"/> + <see cref="MarketOrderAdded"/> /
/// <see cref="MarketOrderUpdated"/> / <see cref="MarketOrderDeleted"/> /
/// <see cref="MarketBookCleared"/> frames the
/// <see cref="IMarketDataSubscriber"/> raises (when
/// <c>MarketDataOptions.EnableBook</c> is on) and exposes the aggregate
/// top-of-book via <see cref="IL2BookView"/>.
///
/// <para>
/// <b>Design choice (MBO-only on the wire).</b> The server also exposes
/// an L2 / MBP stream, but we deliberately subscribe to MBO only and
/// derive L2 internally — one feed, one source of truth, no L2/L3
/// divergence bugs. See <c>docs/rfcs/</c> design notes; tracking
/// issue #286.
/// </para>
///
/// <para>
/// <b>Thread safety.</b> Each per-symbol state is mutated under its
/// own lock. The outer <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// handles concurrent symbol upserts. Reads (<see cref="GetTopOfBook"/>)
/// take the same per-symbol lock to publish a consistent aggregate.
/// The SDK delivers per-symbol events on a single dispatch thread so
/// contention is limited to readers vs. that thread.
/// </para>
/// </summary>
public sealed class MboBookStore : IL2BookView
{
    private readonly ConcurrentDictionary<string, SymbolState> _bySymbol =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Q3.6 Stage B (#286). Raised after every applied frame, with the
    /// derived top-of-book at that instant — <c>null</c> when the frame
    /// emptied both sides. The event fires <b>outside</b> the per-symbol
    /// lock so a slow subscriber cannot stall the apply path; producers
    /// must therefore tolerate stale reads racing newer updates (the
    /// derived snapshot is point-in-time and self-contained, so this is
    /// safe). Used by <c>WebSocketBookEventSink</c> to fan out to
    /// <c>book.${symbol}</c> subscribers.
    /// </summary>
    public event Action<L2TopOfBook?>? TopChanged;

    public void ApplySnapshot(MarketBookSnapshot snap)
    {
        var state = _bySymbol.GetOrAdd(snap.Symbol, _ => new SymbolState(snap.Symbol));
        L2TopOfBook? top;
        lock (state.Gate)
        {
            state.Bids.Clear();
            state.Asks.Clear();
            foreach (var o in snap.Bids)
                state.Bids[o.OrderId] = new OrderEntry(o.Price, o.Qty);
            foreach (var o in snap.Asks)
                state.Asks[o.OrderId] = new OrderEntry(o.Price, o.Qty);
            state.UpdatedUtc = snap.ReceivedUtc;
            top = ComputeTopLocked(state);
        }
        TopChanged?.Invoke(top);
    }

    public void ApplyAdded(MarketOrderAdded ev)
    {
        if (ev.Qty <= 0) return;
        var state = _bySymbol.GetOrAdd(ev.Symbol, _ => new SymbolState(ev.Symbol));
        L2TopOfBook? top;
        lock (state.Gate)
        {
            SideMap(state, ev.Side)[ev.OrderId] = new OrderEntry(ev.Price, ev.Qty);
            state.UpdatedUtc = ev.ReceivedUtc;
            top = ComputeTopLocked(state);
        }
        TopChanged?.Invoke(top);
    }

    public void ApplyUpdated(MarketOrderUpdated ev)
    {
        var state = _bySymbol.GetOrAdd(ev.Symbol, _ => new SymbolState(ev.Symbol));
        L2TopOfBook? top;
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
            top = ComputeTopLocked(state);
        }
        TopChanged?.Invoke(top);
    }

    public void ApplyDeleted(MarketOrderDeleted ev)
    {
        if (!_bySymbol.TryGetValue(ev.Symbol, out var state)) return;
        L2TopOfBook? top;
        lock (state.Gate)
        {
            SideMap(state, ev.Side).Remove(ev.OrderId);
            state.UpdatedUtc = ev.ReceivedUtc;
            top = ComputeTopLocked(state);
        }
        TopChanged?.Invoke(top);
    }

    public void ApplyCleared(MarketBookCleared ev)
    {
        if (!_bySymbol.TryGetValue(ev.Symbol, out var state)) return;
        L2TopOfBook? top;
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
            top = ComputeTopLocked(state);
        }
        TopChanged?.Invoke(top);
    }

    public L2TopOfBook? GetTopOfBook(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        if (!_bySymbol.TryGetValue(symbol.Trim(), out var state)) return null;
        lock (state.Gate) return ComputeTopLocked(state);
    }

    private static L2TopOfBook? ComputeTopLocked(SymbolState state)
    {
        var bid = TopOfSide(state.Bids, ascending: false);
        var ask = TopOfSide(state.Asks, ascending: true);
        if (bid.OrderCount == 0 && ask.OrderCount == 0) return null;
        return new L2TopOfBook(state.Symbol, bid, ask, state.UpdatedUtc);
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
