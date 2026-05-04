using System.Collections.Concurrent;

namespace B3.Trading.DemoDriver;

/// <summary>
/// In-process registry of orders submitted by demo bots that the
/// <see cref="InjectorWorker"/> may target with synthetic ER injections.
///
/// Per the rubber-duck design review (D1 plan): only orders accepted by the
/// trading-host (i.e., 202 with no terminal Status) are tracked. The injector
/// updates leaves/cum from the response of POST /admin/simulator/er and evicts
/// entries when the order reaches a terminal state (Filled, Cancelled, Rejected).
/// </summary>
internal sealed class DemoOrderRegistry
{
    private readonly ConcurrentDictionary<string, BotOrder> _orders = new(StringComparer.Ordinal);

    public int Count => _orders.Count;

    public int CountFor(string ownerUsername)
    {
        var n = 0;
        foreach (var kv in _orders)
            if (string.Equals(kv.Value.OwnerUsername, ownerUsername, StringComparison.Ordinal))
                n++;
        return n;
    }

    public void Register(BotOrder order) => _orders[order.ClOrdId] = order;

    public bool TryEvict(string clOrdId) => _orders.TryRemove(clOrdId, out _);

    /// <summary>Update leaves/cum after an injection. If leaves==0 the entry is evicted.</summary>
    public void OnInjected(string clOrdId, long leaves, long cum)
    {
        if (leaves <= 0)
        {
            _orders.TryRemove(clOrdId, out _);
            return;
        }
        if (_orders.TryGetValue(clOrdId, out var existing))
        {
            _orders[clOrdId] = existing with { LeavesQuantity = leaves, CumulativeQuantity = cum };
        }
    }

    public BotOrder? PickRandomWorking(Random rng)
    {
        // Snapshot for stability across the random pick — registry can mutate
        // concurrently. ConcurrentDictionary.ToArray is consistent.
        var snapshot = _orders.ToArray();
        if (snapshot.Length == 0) return null;
        var pick = snapshot[rng.Next(snapshot.Length)];
        return pick.Value;
    }
}

internal sealed record BotOrder(
    string ClOrdId,
    string OwnerUsername,
    string Symbol,
    string Side,
    decimal Price,
    long Quantity,
    long LeavesQuantity,
    long CumulativeQuantity);
