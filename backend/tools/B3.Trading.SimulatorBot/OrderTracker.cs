using System.Collections.Concurrent;

namespace B3.Trading.SimulatorBot;

/// <summary>
/// In-memory ClOrdID-keyed view of bot-submitted orders. The bot needs
/// to (a) cap in-flight per symbol, (b) recognise cancels/fills from
/// the ER stream, and (c) discover candidates for auto-cancel —
/// <see cref="OrderTracker"/> is the single source of truth for all
/// three. State lives in process; no persistence beyond what the SDK's
/// session state store gives us for SessionVerId.
/// </summary>
public sealed class OrderTracker
{
    private readonly ConcurrentDictionary<ulong, TrackedOrder> _orders = new();
    private readonly TimeProvider _clock;

    public OrderTracker(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    public int InFlightCount(string symbol)
    {
        var count = 0;
        foreach (var o in _orders.Values)
        {
            if (o.IsOpen && string.Equals(o.Symbol, symbol, StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    public void RegisterSubmit(ulong clOrdId, string symbol, decimal price, long quantity, bool isBuy)
    {
        var now = _clock.GetUtcNow();
        _orders[clOrdId] = new TrackedOrder
        {
            ClOrdId = clOrdId,
            Symbol = symbol,
            Price = price,
            Quantity = quantity,
            IsBuy = isBuy,
            SubmittedAtUtc = now,
            // Treat as open until we learn otherwise. A fill or reject ER
            // closes it; an explicit cancel ER closes it.
            IsOpen = true,
            Leaves = quantity,
        };
    }

    /// <summary>Returns true when this order is currently tracked as the
    /// bot's own (so the caller should react). False for stale/unknown
    /// IDs (e.g. ER for an order from a previous run).</summary>
    public bool TryGet(ulong clOrdId, out TrackedOrder order)
    {
        if (_orders.TryGetValue(clOrdId, out var found))
        {
            order = found;
            return true;
        }
        order = default!;
        return false;
    }

    public void OnAccepted(ulong clOrdId, long leaves)
    {
        if (_orders.TryGetValue(clOrdId, out var o))
        {
            o.Leaves = leaves;
            o.IsOpen = leaves > 0;
        }
    }

    public void OnTrade(ulong clOrdId, long leaves)
    {
        if (_orders.TryGetValue(clOrdId, out var o))
        {
            o.Leaves = leaves;
            o.IsOpen = leaves > 0;
        }
    }

    public void OnTerminal(ulong clOrdId)
    {
        if (_orders.TryGetValue(clOrdId, out var o))
        {
            o.Leaves = 0;
            o.IsOpen = false;
        }
    }

    /// <summary>Snapshot the open orders older than <paramref name="threshold"/>
    /// at the current clock — candidates for auto-cancel.</summary>
    public IReadOnlyList<TrackedOrder> SnapshotStaleOpen(TimeSpan threshold)
    {
        var cutoff = _clock.GetUtcNow() - threshold;
        var result = new List<TrackedOrder>();
        foreach (var o in _orders.Values)
        {
            if (o.IsOpen && o.SubmittedAtUtc <= cutoff)
                result.Add(o);
        }
        return result;
    }
}

public sealed class TrackedOrder
{
    public ulong ClOrdId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public long Quantity { get; set; }
    public long Leaves { get; set; }
    public bool IsBuy { get; set; }
    public DateTimeOffset SubmittedAtUtc { get; set; }
    public bool IsOpen { get; set; }
}
