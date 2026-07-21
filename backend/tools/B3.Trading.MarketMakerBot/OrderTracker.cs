using System.Collections.Concurrent;

namespace B3.Trading.MarketMakerBot;

/// <summary>
/// In-memory ClOrdID-keyed view of bot-submitted orders. The bot needs
/// to (a) recognise fills/cancels/rejects from the ER stream so it can
/// immediately re-quote the affected side, and (b) let the defensive
/// reconciliation loop discover a (symbol, side) that currently has no
/// resting order — <see cref="OrderTracker"/> is the single source of
/// truth for both. State lives in process; no persistence beyond what
/// the SDK's session state store gives us for SessionVerId.
///
/// <see cref="_activeSides"/> is the atomicity guard: the event-driven
/// requote path and the defensive reconcile loop can race to replace
/// the same (symbol, side), so reserving a side is a single
/// check-and-set under <see cref="_sideLock"/> via
/// <see cref="TryRegisterSubmit"/> rather than a separate "is it open"
/// check followed by a separate submit.
/// </summary>
public sealed class OrderTracker
{
    private readonly ConcurrentDictionary<ulong, TrackedOrder> _orders = new();
    private readonly HashSet<(string Symbol, bool IsBuy)> _activeSides = new();
    private readonly object _sideLock = new();
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

    /// <summary>True when this (symbol, side) currently has a resting
    /// order or a just-submitted-but-not-yet-acknowledged one. The
    /// market maker keeps at most one resting order per side per
    /// instrument, so this doubles as "is this side currently quoted /
    /// spoken for".</summary>
    public bool HasOpenSide(string symbol, bool isBuy)
    {
        lock (_sideLock)
        {
            return _activeSides.Contains((symbol, isBuy));
        }
    }

    /// <summary>
    /// Atomically reserves (symbol, side) and registers the order if — and
    /// only if — that side wasn't already spoken for. Returns <c>false</c>
    /// without registering anything when the side is already active, so
    /// the event-driven requote path and the reconcile safety net can
    /// never both submit a replacement for the same side.
    /// </summary>
    public bool TryRegisterSubmit(ulong clOrdId, string symbol, decimal price, long quantity, bool isBuy)
    {
        lock (_sideLock)
        {
            if (!_activeSides.Add((symbol, isBuy)))
                return false;
        }

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
        return true;
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
            if (leaves > 0) o.IsOpen = true;
            else Close(o);
        }
    }

    public void OnTrade(ulong clOrdId, long leaves)
    {
        if (_orders.TryGetValue(clOrdId, out var o))
        {
            o.Leaves = leaves;
            if (leaves > 0) o.IsOpen = true;
            else Close(o);
        }
    }

    public void OnTerminal(ulong clOrdId)
    {
        if (_orders.TryGetValue(clOrdId, out var o))
        {
            o.Leaves = 0;
            Close(o);
        }
    }

    /// <summary>Marks the order closed and frees its (symbol, side)
    /// reservation so a future requote/reconcile pass may take it.</summary>
    private void Close(TrackedOrder o)
    {
        o.IsOpen = false;
        lock (_sideLock)
        {
            _activeSides.Remove((o.Symbol, o.IsBuy));
        }
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

