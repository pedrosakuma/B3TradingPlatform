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

    public bool TryAdd(Order order) => _orders.TryAdd(order.ClOrdId, order);

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
                o.Status.ToString(), o.FirmId);
        }
    }

    public void Restore(IEnumerable<Persistence.OrderSnapshot> snaps)
    {
        ArgumentNullException.ThrowIfNull(snaps);
        _orders.Clear();
        foreach (var s in snaps)
        {
            var owner = new EndClientId(s.EndClientId);
            var side = Enum.Parse<OrderSide>(s.Side);
            var type = Enum.Parse<OrderType>(s.Type);
            var status = Enum.Parse<OrderStatus>(s.Status);
            _orders[s.ClOrdId] = Order.Hydrate(s.ClOrdId, owner, s.Symbol, s.SecurityId, side, type,
                s.Quantity, s.Price, s.LeavesQuantity, s.CumulativeQuantity, status, s.FirmId);
        }
    }
}
