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
    private readonly ConcurrentDictionary<string, Order> _orders = new();

    public bool TryAdd(Order order) => _orders.TryAdd(order.ClOrdId, order);

    public bool TryGet(string clOrdId, out Order? order) => _orders.TryGetValue(clOrdId, out order);

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
}
