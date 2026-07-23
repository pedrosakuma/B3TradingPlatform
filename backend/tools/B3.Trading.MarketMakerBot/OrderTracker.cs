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
/// <see cref="_activeSideOwners"/> is the atomicity guard: the event-driven
/// requote path and the defensive reconcile loop can race to replace
/// the same (symbol, side), so reserving a side is a single
/// check-and-set under <see cref="_sideLock"/> via
/// <see cref="TryRegisterSubmit"/> rather than a separate "is it open"
/// check followed by a separate submit. Tracking the *owning* ClOrdId
/// (rather than a bare presence flag) additionally protects against a
/// duplicate/racing terminal execution report for a superseded order
/// evicting a newer order's reservation for the same side.
/// </summary>
public sealed class OrderTracker
{
    private readonly ConcurrentDictionary<ulong, TrackedOrder> _orders = new();
    // Correlates a cancel REQUEST's own (freshly-generated) ClOrdID back to
    // the original order it targets. Deliberately NOT merged into _orders
    // (an earlier version of this fix aliased the same TrackedOrder under
    // both keys, which double-counted it in OpenCount/InFlightCount/
    // FindStale, all of which iterate _orders.Values) — this is a pure
    // lookup table, consulted only from OrderRejected handling (see
    // MarketMakerWorker.HandleEventAsync), since OrderCancelled already
    // carries the original id directly via OrigClOrdID.
    private readonly ConcurrentDictionary<ulong, ulong> _cancelAttempts = new();
    // Tracks which ClOrdId currently owns each (symbol, side) reservation,
    // so a duplicate/racing terminal ER for an order that has already been
    // superseded by a newer reservation on the same side can't free a slot
    // it no longer owns (see Close()).
    private readonly Dictionary<(string Symbol, bool IsBuy), ulong> _activeSideOwners = new();
    private readonly object _sideLock = new();
    private readonly TimeProvider _clock;

    public OrderTracker(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Exposes the tracker's clock so callers (e.g. the staleness
    /// guard) can compute ages consistently with <see cref="SubmittedAtUtc"/>,
    /// including under test with an injected <see cref="TimeProvider"/>.</summary>
    public DateTimeOffset UtcNow => _clock.GetUtcNow();

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

    /// <summary>Total number of currently-open (resting or just-submitted)
    /// orders across all instruments — the input to the safety cap.</summary>
    public int OpenCount()
    {
        var count = 0;
        foreach (var o in _orders.Values)
            if (o.IsOpen) count++;
        return count;
    }

    /// <summary>Snapshot of currently-open orders whose age (relative to
    /// <paramref name="now"/>) is at least <paramref name="maxAge"/> — the
    /// candidates for the miss-fill/staleness cancel guard. Skips orders
    /// with an already-outstanding cancel attempt (see
    /// <see cref="RegisterCancelAttempt"/>) so a stuck order gets at most
    /// one in-flight cancel at a time rather than a fresh one every
    /// reconcile tick. See <see cref="MarketMakerBotOptions.MaxOrderAge"/>.
    /// </summary>
    public IReadOnlyList<TrackedOrder> FindStale(TimeSpan maxAge, DateTimeOffset now)
    {
        List<TrackedOrder>? stale = null;
        foreach (var o in _orders.Values)
        {
            if (o.IsOpen && o.PendingCancelClOrdId is null && now - o.SubmittedAtUtc >= maxAge)
                (stale ??= new List<TrackedOrder>()).Add(o);
        }
        return stale ?? (IReadOnlyList<TrackedOrder>)Array.Empty<TrackedOrder>();
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
            return _activeSideOwners.ContainsKey((symbol, isBuy));
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
            var side = (symbol, isBuy);
            if (_activeSideOwners.ContainsKey(side))
                return false;
            _activeSideOwners[side] = clOrdId;
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

    /// <summary>
    /// Records that <paramref name="cancelClOrdId"/> (the ClOrdID the bot
    /// generates for an explicit CancelOrderRequest — see
    /// <c>MarketMakerWorker.CancelStaleOrdersAsync</c>) is a cancel attempt
    /// targeting <paramref name="origClOrdId"/>. The venue's ER for a
    /// rejected cancel (<c>OrderRejected</c>, which has no OrigClOrdID
    /// field) carries the CANCEL request's OWN ClOrdID — without this,
    /// such a reject would be indistinguishable from a rejected NEW order
    /// submit, and <c>HandleEventAsync</c> could not safely decide whether
    /// to free the original order's (symbol, side) reservation (freeing it
    /// when the order is, in fact, still resting would let the bot submit
    /// a duplicate order alongside it — the exact failure mode RFC #703
    /// exists to prevent).
    /// </summary>
    public void RegisterCancelAttempt(ulong cancelClOrdId, ulong origClOrdId)
    {
        _cancelAttempts[cancelClOrdId] = origClOrdId;
        if (_orders.TryGetValue(origClOrdId, out var order))
            order.PendingCancelClOrdId = cancelClOrdId;
    }

    /// <summary>True when <paramref name="clOrdId"/> is a cancel request
    /// the bot itself generated (via <see cref="RegisterCancelAttempt"/>),
    /// with <paramref name="origClOrdId"/> set to the order it targeted.
    /// </summary>
    public bool TryResolveCancelAttempt(ulong clOrdId, out ulong origClOrdId) =>
        _cancelAttempts.TryGetValue(clOrdId, out origClOrdId);

    /// <summary>
    /// Clears a previously-registered pending cancel for
    /// <paramref name="origClOrdId"/> WITHOUT closing the order — used
    /// when a cancel is rejected for a reason that doesn't prove the
    /// order is actually gone (see <c>MarketMakerWorker.HandleEventAsync</c>'s
    /// OrderRejected case), so the staleness guard is free to try again
    /// on a later reconcile tick instead of considering one already
    /// outstanding forever.
    /// </summary>
    public void ClearPendingCancel(ulong origClOrdId)
    {
        if (_orders.TryGetValue(origClOrdId, out var order))
            order.PendingCancelClOrdId = null;
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
    /// reservation — but only if this order is still the current owner
    /// of that reservation. A duplicate/racing terminal ER for an order
    /// that has already been superseded by a newer submit on the same
    /// side must not evict the newer order's reservation.</summary>
    private void Close(TrackedOrder o)
    {
        o.IsOpen = false;
        o.PendingCancelClOrdId = null;
        lock (_sideLock)
        {
            var side = (o.Symbol, o.IsBuy);
            if (_activeSideOwners.TryGetValue(side, out var owner) && owner == o.ClOrdId)
                _activeSideOwners.Remove(side);
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
    /// <summary>ClOrdID of an outstanding CancelOrderRequest targeting this
    /// order, if any — see <see cref="OrderTracker.RegisterCancelAttempt"/>.
    /// Null means no cancel is currently in flight for this order.</summary>
    public ulong? PendingCancelClOrdId { get; set; }
}

