using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// In-memory registry of working orders, keyed by ClOrdID. v1 is ephemeral;
/// re-derivation from ER replay on reconnect is the planned recovery path
/// (see issue #1 §3 — Position keeper persistence).
/// </summary>
public sealed class WorkingOrderBook
{
    private readonly ConcurrentDictionary<ulong, Order> _orders = new();

    // Secondary index: firmId -> set of ClOrdIDs. Maintained on TryAdd / Restore.
    // Built on top of ConcurrentDictionary so enumeration is lock-free; the inner
    // dictionary's value byte is irrelevant — we only use the keys as a set.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ulong, byte>> _byFirm =
        new(StringComparer.Ordinal);

    // Secondary index: end-client -> set of ClOrdIDs. Used by the
    // slice-7 MaxOpenOrders check so the hot path doesn't scan every
    // historical order in _orders to count what's still open for one
    // owner. Same lock-free shape as _byFirm.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ulong, byte>> _byOwner =
        new(StringComparer.Ordinal);

    public bool TryAdd(Order order)
    {
        if (!_orders.TryAdd(order.ClOrdId, order))
            return false;

        var firmSet = _byFirm.GetOrAdd(order.FirmId, static _ => new ConcurrentDictionary<ulong, byte>());
        firmSet.TryAdd(order.ClOrdId, 0);
        var ownerSet = _byOwner.GetOrAdd(order.Owner.Value, static _ => new ConcurrentDictionary<ulong, byte>());
        ownerSet.TryAdd(order.ClOrdId, 0);
        return true;
    }

    public bool TryGet(ulong clOrdId, out Order? order) => _orders.TryGetValue(clOrdId, out order);

    public IReadOnlyCollection<Order> ForEndClient(EndClientId owner)
    {
        var list = new List<Order>();
        foreach (var kv in _orders)
        {
            if (kv.Value.Owner == owner)
                list.Add(kv.Value);
        }
        return list;
    }

    /// <summary>
    /// Counts an end-client's non-terminal orders (PendingNew /
    /// Working / PartiallyFilled). Indexed via <see cref="_byOwner"/>
    /// so the cost is O(orders for owner) rather than O(total orders)
    /// — the v2 risk pipeline calls this on every submit.
    /// </summary>
    /// <remarks>
    /// The current order being submitted is already in the book by
    /// the time the risk pipeline runs (the persistence dispatcher
    /// adds it before evaluation), so callers comparing to a cap
    /// should use strict <c>&gt;</c>, not <c>&gt;=</c>.
    /// <para>
    /// Slice 4 of #132. Stale orders (admin mark-stale or auto-detect
    /// on FIXP venue desync) are skipped: a ghost that the venue does
    /// not know about must not block the trader's max-open-orders
    /// budget, otherwise a venue restart would silently freeze new
    /// trading until every stale order is cancelled.
    /// </para>
    /// </remarks>
    public int CountOpenForOwner(EndClientId owner)
    {
        if (!_byOwner.TryGetValue(owner.Value, out var set)) return 0;
        var count = 0;
        foreach (var clOrdId in set.Keys)
        {
            if (!_orders.TryGetValue(clOrdId, out var order)) continue;
            if (IsTerminal(order.Status)) continue;
            if (order.IsStale) continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Sums <see cref="Order.LeavesQuantity"/> across the end-client's
    /// non-terminal Sell orders for a single symbol. Used by the
    /// pre-trade naked-short gate to compute available sellable
    /// inventory pessimistically (assume every open Sell still
    /// executes; do not net against open Buys, which may be
    /// cancelled). Index-driven via <see cref="_byOwner"/> so cost
    /// is O(orders for owner), not O(total orders) — same shape as
    /// <see cref="CountOpenForOwner"/>.
    /// </summary>
    /// <remarks>
    /// As with <see cref="CountOpenForOwner"/>, the order being
    /// submitted is **already** in the book by the time the risk
    /// pipeline runs (the persistence dispatcher adds it before
    /// evaluation). Callers must subtract their own incoming
    /// quantity from the returned sum to avoid double-counting it
    /// against itself.
    /// <para>
    /// Slice 4 of #132. Stale orders are excluded from the sum: the
    /// venue is unlikely to ever execute them, so locking inventory
    /// against ghost Sell leaves would prevent a trader from selling
    /// the shares they actually hold after a venue desync. The
    /// pre-trade naked-short gate (<see cref="Risk.Checks.NoNakedShortCheck"/>)
    /// applies the matching skip in its replace projection branch.
    /// </para>
    /// </remarks>
    public long SumOpenSellLeavesForSymbol(EndClientId owner, string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (!_byOwner.TryGetValue(owner.Value, out var set)) return 0;
        long total = 0;
        foreach (var clOrdId in set.Keys)
        {
            if (!_orders.TryGetValue(clOrdId, out var order)) continue;
            if (order.Side != OrderSide.Sell) continue;
            if (!string.Equals(order.Symbol, symbol, StringComparison.Ordinal)) continue;
            if (IsTerminal(order.Status)) continue;
            if (order.IsStale) continue;
            total += order.LeavesQuantity;
        }
        return total;
    }

    /// <summary>
    /// Snapshots the orders associated with <paramref name="firmId"/>. By default
    /// only non-terminal orders are returned (PendingNew / Working / PartiallyFilled),
    /// which matches the FIXP "outstanding orders" semantics used to reconcile
    /// against <c>SessionSnapshot.OutstandingOrders</c> after warm restart or
    /// gap-recovery reconnect.
    /// </summary>
    /// <remarks>
    /// Snapshot semantics: callers receive a stable list captured at call time;
    /// concurrent <see cref="TryAdd"/> or status mutations after the call do not
    /// affect the returned collection. Index-driven, so cost is O(orders for firm)
    /// rather than O(total orders).
    /// </remarks>
    public IReadOnlyCollection<Order> EnumerateForFirm(string firmId, bool includeTerminal = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);

        if (!_byFirm.TryGetValue(firmId, out var firmSet))
            return Array.Empty<Order>();

        var list = new List<Order>(firmSet.Count);
        foreach (var clOrdId in firmSet.Keys)
        {
            if (!_orders.TryGetValue(clOrdId, out var order))
                continue;
            if (!includeTerminal && IsTerminal(order.Status))
                continue;
            list.Add(order);
        }
        return list;
    }

    private static bool IsTerminal(OrderStatus s) =>
        s is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Replaced;

    /// <summary>
    /// Returns every order whose <see cref="Order.ParentAlgoId"/> matches
    /// <paramref name="parentAlgoId"/> within the given firm scope. Used by
    /// the algo engine at boot reconciliation to rebuild per-parent runtime
    /// state (live child + cumulative-fill baseline) without rescanning the
    /// full order book on every signal. Index-driven (firm secondary index)
    /// so cost is O(orders for firm), not O(total orders).
    /// </summary>
    public IReadOnlyCollection<Order> EnumerateChildrenOf(string firmId, ulong parentAlgoId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        if (parentAlgoId == 0)
            throw new ArgumentOutOfRangeException(nameof(parentAlgoId));
        if (!_byFirm.TryGetValue(firmId, out var firmSet))
            return Array.Empty<Order>();
        var list = new List<Order>();
        foreach (var clOrdId in firmSet.Keys)
        {
            if (!_orders.TryGetValue(clOrdId, out var order)) continue;
            if (order.ParentAlgoId != parentAlgoId) continue;
            list.Add(order);
        }
        return list;
    }

    /// <summary>
    /// Captures the current set of working orders for snapshotting.
    /// Terminal-state orders (Filled/Cancelled/Rejected) are still
    /// included so that replay-without-snapshot and replay-from-snapshot
    /// produce the same in-memory state, even for very recently terminated
    /// orders the operator might still want visibility on.
    /// </summary>
    public IEnumerable<Persistence.OrderSnapshot> Snapshot()
    {
        foreach (var kv in _orders)
        {
            var o = kv.Value;
            yield return new Persistence.OrderSnapshot(
                o.ClOrdId, o.Owner.Value, o.Symbol, o.SecurityId,
                o.Side.ToString(), o.Type.ToString(),
                o.Quantity, o.Price, o.LeavesQuantity, o.CumulativeQuantity,
                o.Status.ToString(), o.FirmId, o.ParentAlgoId, o.AlgoSliceSeq)
            {
                IsStale = o.IsStale,
                StaleReason = o.StaleReason,
                StaledAtUtc = o.StaledAtUtc,
            };
        }
    }

    public void Restore(IEnumerable<Persistence.OrderSnapshot> snaps)
    {
        ArgumentNullException.ThrowIfNull(snaps);
        _orders.Clear();
        _byFirm.Clear();
        _byOwner.Clear();
        foreach (var s in snaps)
        {
            var owner = new EndClientId(s.EndClientId);
            var side = Enum.Parse<OrderSide>(s.Side);
            var type = Enum.Parse<OrderType>(s.Type);
            var status = Enum.Parse<OrderStatus>(s.Status);
            _orders[s.ClOrdId] = Order.Hydrate(s.ClOrdId, owner, s.Symbol, s.SecurityId, side, type,
                s.Quantity, s.Price, s.LeavesQuantity, s.CumulativeQuantity, status, s.FirmId,
                s.ParentAlgoId, s.AlgoSliceSeq,
                isStale: s.IsStale, staleReason: s.StaleReason, staledAtUtc: s.StaledAtUtc);
            var firmSet = _byFirm.GetOrAdd(s.FirmId, static _ => new ConcurrentDictionary<ulong, byte>());
            firmSet.TryAdd(s.ClOrdId, 0);
            var ownerSet = _byOwner.GetOrAdd(s.EndClientId, static _ => new ConcurrentDictionary<ulong, byte>());
            ownerSet.TryAdd(s.ClOrdId, 0);
        }
    }
}
