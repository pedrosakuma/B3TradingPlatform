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
    // lookup table used to resolve cancel rejects and cancel acks whose
    // OrigClOrdID was dropped by the gateway.
    private readonly ConcurrentDictionary<ulong, CancelAttempt> _cancelAttempts = new();
    // Expired cancel ids remain recognizable for a bounded period so a late
    // reject cannot be mistaken for a rejected new-order submit.
    private readonly ConcurrentDictionary<ulong, ExpiredCancelAttemptCorrelation>
        _expiredCancelAttempts = new();
    // Tracks which ClOrdId currently owns each (symbol, side) reservation,
    // so a duplicate/racing terminal ER for an order that has already been
    // superseded by a newer reservation on the same side can't free a slot
    // it no longer owns (see Close()).
    private readonly Dictionary<(string Symbol, bool IsBuy), ulong> _activeSideOwners = new();
    private readonly object _sideLock = new();
    private readonly TimeProvider _clock;
    // RFC #703 book-driven quoting: every ER carries the venue's own
    // OrderId (distinct from our ClOrdID namespace). Market-data's
    // order-level book feed (Book/MBO subscribe flag) reports OrderId
    // too, so this is the join key that lets the worker tell "my own
    // resting order just moved the book" apart from "someone else's did"
    // — see MarketDataFeed.BookOrderChanged / MarketMakerWorker's
    // self-order filter. Kept as a bare id set (not merged into _orders)
    // for the same double-counting reason _cancelAttempts is separate.
    private readonly ConcurrentDictionary<ulong, byte> _ownOrderIds = new();

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

    /// <summary>Returns the currently-resting order for (symbol, side), if
    /// any — used by the book-reaction path to compare its resting price
    /// against a freshly-computed target without racing a separate
    /// HasOpenSide-then-TryGet pair.</summary>
    public bool TryGetActiveSideOrder(string symbol, bool isBuy, out TrackedOrder order)
    {
        ulong owner;
        lock (_sideLock)
        {
            if (!_activeSideOwners.TryGetValue((symbol, isBuy), out owner))
            {
                order = default!;
                return false;
            }
        }
        return TryGet(owner, out order);
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
    public void RegisterCancelAttempt(
        ulong cancelClOrdId,
        ulong origClOrdId,
        CancelReason reason = CancelReason.StaleOrder)
    {
        _cancelAttempts[cancelClOrdId] = new CancelAttempt(origClOrdId, reason);
        if (_orders.TryGetValue(origClOrdId, out var order))
            lock (order)
            {
                order.PendingCancelClOrdId = cancelClOrdId;
                order.LastCancelAttemptAtUtc = _clock.GetUtcNow();
            }
    }

    /// <summary>
    /// Atomic check-and-set variant of <see cref="RegisterCancelAttempt"/>:
    /// registers the cancel attempt only if — and only if — the order is
    /// still open, no cancel is already pending for it, and (when
    /// <paramref name="minIntervalSinceLastAttempt"/> is given) at least
    /// that much time has passed since its last cancel attempt. RFC #703's
    /// book-driven reactive requote path (<c>MarketMakerWorker.ReactToBookChangeAsync</c>)
    /// runs concurrently with the pre-existing staleness guard
    /// (<c>CancelStaleOrdersAsync</c>) on a separate loop; without this
    /// atomicity, both could observe <c>PendingCancelClOrdId == null</c>
    /// for the same order and each submit a distinct CancelOrderRequest
    /// for it — two cancels racing the venue for one order, only one of
    /// which the correlation table (last write wins) could ever resolve.
    /// The interval check is folded into the same lock (rather than left
    /// to a separate, pre-lock read in the caller) so a concurrent
    /// register/clear/register sequence on another thread can't leave the
    /// caller's throttle decision stale by the time this actually commits.
    /// The open-check additionally guards against a stale
    /// already-terminal <see cref="TrackedOrder"/> reference (e.g. from a
    /// <c>FindStale</c> snapshot) picking up a fresh cancel attempt after
    /// a concurrent fill/cancel has already closed it via <see cref="Close"/>.
    /// <paramref name="reason"/> is stamped onto the correlation row so a
    /// later reject can be attributed to the correct trigger (see
    /// <see cref="TryResolveCancelAttempt"/>) instead of the staleness
    /// guard and the reactive path being indistinguishable once both
    /// funnel through the same shared submit helper.
    /// Returns <c>false</c> (registering nothing) when any guard fails, so
    /// the caller skips submitting a cancel altogether.
    /// </summary>
    public bool TryRegisterCancelAttempt(ulong cancelClOrdId, ulong origClOrdId,
        TimeSpan? minIntervalSinceLastAttempt = null, CancelReason reason = CancelReason.StaleOrder)
    {
        if (!_orders.TryGetValue(origClOrdId, out var order)) return false;
        lock (order)
        {
            if (!order.IsOpen || order.PendingCancelClOrdId is not null) return false;
            var now = _clock.GetUtcNow();
            if (minIntervalSinceLastAttempt is { } minInterval)
            {
                var lastActivity = order.LastCancelAttemptAtUtc ?? order.SubmittedAtUtc;
                if (now - lastActivity < minInterval) return false;
            }
            _cancelAttempts[cancelClOrdId] = new CancelAttempt(origClOrdId, reason);
            order.PendingCancelClOrdId = cancelClOrdId;
            order.LastCancelAttemptAtUtc = now;
        }
        return true;
    }

    /// <summary>True when <paramref name="clOrdId"/> is a cancel request
    /// the bot itself generated (via <see cref="RegisterCancelAttempt"/>
    /// or <see cref="TryRegisterCancelAttempt"/>), with <paramref
    /// name="origClOrdId"/> set to the order it targeted and <paramref
    /// name="reason"/> set to which trigger raised it — used by
    /// <c>MarketMakerWorker.HandleEventAsync</c>'s OrderRejected case to
    /// attribute the reject to the right metric/log instead of always
    /// reporting it as a stale-order cancel reject now that both
    /// triggers share the same submit path.
    /// </summary>
    public bool TryResolveCancelAttempt(ulong clOrdId, out ulong origClOrdId, out CancelReason reason)
    {
        if (_cancelAttempts.TryGetValue(clOrdId, out var attempt))
        {
            origClOrdId = attempt.OrigClOrdId;
            reason = attempt.Reason;
            return true;
        }
        if (_expiredCancelAttempts.TryGetValue(clOrdId, out var expired))
        {
            origClOrdId = expired.Attempt.OrigClOrdId;
            reason = expired.Attempt.Reason;
            return true;
        }
        origClOrdId = 0;
        reason = default;
        return false;
    }

    /// <summary>Convenience overload for callers that don't need the
    /// trigger tag.</summary>
    public bool TryResolveCancelAttempt(ulong clOrdId, out ulong origClOrdId) =>
        TryResolveCancelAttempt(clOrdId, out origClOrdId, out _);

    public void ForgetCancelAttempt(ulong cancelClOrdId)
    {
        _cancelAttempts.TryRemove(cancelClOrdId, out _);
        _expiredCancelAttempts.TryRemove(cancelClOrdId, out _);
    }

    /// <summary>
    /// Records the venue's own OrderId for a tracked order, learned from
    /// any ER carrying it (OrderAccepted/OrderTrade/OrderModified all do —
    /// see <c>MarketMakerWorker.HandleEventAsync</c>). Idempotent and
    /// deliberately a no-op for an unknown ClOrdID or a zero/absent
    /// OrderId (some ER shapes may not have assigned one yet). This is
    /// the join key <see cref="IsOwnOrder"/> uses to filter book-level
    /// market-data deltas the bot's own resting orders themselves caused
    /// — see RFC #703's book-driven quoting self-awareness requirement.
    /// </summary>
    public void SetOrderId(ulong clOrdId, ulong orderId)
    {
        if (orderId == 0) return;
        if (!_orders.TryGetValue(clOrdId, out var order)) return;
        order.OrderId = orderId;
        _ownOrderIds[orderId] = 0;
    }

    /// <summary>True when <paramref name="orderId"/> (the venue's own
    /// order identifier, as reported on market-data's order-level book
    /// events) belongs to one of the bot's own currently-tracked orders.
    /// Used to filter self-caused book deltas out of the reactive
    /// requote path — see <see cref="SetOrderId"/>.</summary>
    public bool IsOwnOrder(ulong orderId) => _ownOrderIds.ContainsKey(orderId);

    /// <summary>
    /// Clears a previously-registered pending cancel for
    /// <paramref name="origClOrdId"/> WITHOUT closing the order — used
    /// when a cancel is rejected for a reason that doesn't prove the
    /// order is actually gone (see <c>MarketMakerWorker.HandleEventAsync</c>'s
    /// OrderRejected case), so the staleness guard is free to try again
    /// on a later reconcile tick instead of considering one already
    /// outstanding forever. Also drops the now-abandoned <see
    /// cref="_cancelAttempts"/> correlation row for the rejected cancel
    /// request itself (it's no longer in flight, and leaving it behind
    /// would let it accumulate indefinitely for an order that stays open
    /// and keeps having its cancel attempts rejected — a real risk now
    /// that RFC #703's book-driven path can trigger far more retries than
    /// the occasional staleness-guard cancel #705 originally accepted).
    /// </summary>
    public void ClearPendingCancel(ulong origClOrdId)
    {
        if (_orders.TryGetValue(origClOrdId, out var order))
            lock (order)
            {
                if (order.PendingCancelClOrdId is { } pending)
                    _cancelAttempts.TryRemove(pending, out _);
                order.PendingCancelClOrdId = null;
            }
    }

    /// <summary>
    /// Same as <see cref="ClearPendingCancel"/>, but only clears if the
    /// currently-registered pending cancel is still
    /// <paramref name="expectedCancelClOrdId"/> — used when a cancel
    /// SUBMIT itself fails synchronously (no ER will ever arrive to
    /// resolve it), so we don't accidentally clear a DIFFERENT, later
    /// cancel attempt that may already be outstanding for the same order
    /// by the time the failure is handled. Also drops the abandoned
    /// correlation row for the failed cancel — see <see cref="ClearPendingCancel"/>.
    /// </summary>
    public void ClearPendingCancelIfMatches(ulong origClOrdId, ulong expectedCancelClOrdId)
    {
        if (_orders.TryGetValue(origClOrdId, out var order))
            lock (order)
            {
                if (order.PendingCancelClOrdId == expectedCancelClOrdId)
                {
                    order.PendingCancelClOrdId = null;
                    _cancelAttempts.TryRemove(expectedCancelClOrdId, out _);
                }
            }
        _expiredCancelAttempts.TryRemove(expectedCancelClOrdId, out _);
    }

    /// <summary>
    /// Atomically expires open-order cancel attempts whose acknowledgement age
    /// is at least <paramref name="timeout"/>. Only the matching pending marker
    /// and active correlation are cleared; <see cref="TrackedOrder.LastCancelAttemptAtUtc"/>
    /// remains intact so the normal requote throttle still applies. Expired ids
    /// are retained as bounded tombstones for safe classification of late ERs.
    /// </summary>
    public IReadOnlyList<ExpiredPendingCancel> ExpirePendingCancelAttempts(
        TimeSpan timeout,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        List<ExpiredPendingCancel>? expired = null;
        foreach (var order in _orders.Values)
        {
            lock (order)
            {
                if (!order.IsOpen ||
                    order.PendingCancelClOrdId is not { } pending ||
                    order.LastCancelAttemptAtUtc is not { } attemptedAt ||
                    now - attemptedAt < timeout ||
                    !_cancelAttempts.TryGetValue(pending, out var attempt))
                {
                    continue;
                }
                if (attempt.OrigClOrdId != order.ClOrdId)
                    continue;

                _expiredCancelAttempts[pending] =
                    new ExpiredCancelAttemptCorrelation(attempt, now);
                if (!_cancelAttempts.TryRemove(pending, out _))
                {
                    _expiredCancelAttempts.TryRemove(pending, out _);
                    continue;
                }

                order.PendingCancelClOrdId = null;
                (expired ??= new List<ExpiredPendingCancel>()).Add(
                    new ExpiredPendingCancel(
                        order.ClOrdId,
                        pending,
                        order.Symbol,
                        order.IsBuy,
                        attempt.Reason,
                        attemptedAt,
                        now));
            }
        }
        return expired ?? (IReadOnlyList<ExpiredPendingCancel>)Array.Empty<ExpiredPendingCancel>();
    }

    /// <summary>
    /// Records the venue's New-status acceptance of a just-submitted
    /// order. <paramref name="leaves"/> is <c>null</c> when the ER
    /// doesn't carry a LeavesQty value at all — confirmed empirically
    /// against the real venue, whose OrderAccepted (ExecType=New) omits
    /// LeavesQty/CumQty entirely rather than echoing the submitted
    /// quantity. A null value here must NEVER be treated as "zero
    /// remaining"/filled: OrderAccepted's ExecType is always New (a
    /// fill is only ever reported via a subsequent OrderTrade), so this
    /// always keeps the order open — closing it here based on a
    /// null-defaulted-to-zero leaves value was the root cause of an
    /// unbounded duplicate-order bug (the reservation was freed for a
    /// still-genuinely-resting order, so the next ReconcileLoopAsync
    /// tick submitted a duplicate alongside it; see
    /// pedrosakuma/B3EntryPointClient#228). <see cref="TryRegisterSubmit"/>
    /// already seeds Leaves with the submitted quantity, so omitting the
    /// update here when unknown keeps that accurate best-effort value.
    /// </summary>
    public void OnAccepted(ulong clOrdId, long? leaves)
    {
        if (_orders.TryGetValue(clOrdId, out var o))
        {
            if (leaves is { } l) o.Leaves = l;
            o.IsOpen = true;
        }
    }

    /// <summary>
    /// Records a trade (fill) execution report. <paramref name="isFilled"/>
    /// — derived by the caller from the ER's authoritative OrderStatus
    /// field (Filled vs PartiallyFilled), NOT from LeavesQty — is what
    /// decides whether the order closes. <paramref name="leaves"/> is
    /// purely informational (best-effort display/staleness-guard value)
    /// and, like <see cref="OnAccepted"/>, a <c>null</c> value here is
    /// never treated as zero: an absent LeavesQty on a PartiallyFilled
    /// trade must not misclose a still-resting order.
    /// </summary>
    public void OnTrade(ulong clOrdId, bool isFilled, long? leaves)
    {
        if (_orders.TryGetValue(clOrdId, out var o))
        {
            if (leaves is { } l) o.Leaves = l;
            if (isFilled) Close(o);
            else o.IsOpen = true;
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
        lock (o)
        {
            o.IsOpen = false;
            o.PendingCancelClOrdId = null;
        }
        if (o.OrderId is { } orderId) _ownOrderIds.TryRemove(orderId, out _);
        lock (_sideLock)
        {
            var side = (o.Symbol, o.IsBuy);
            if (_activeSideOwners.TryGetValue(side, out var owner) && owner == o.ClOrdId)
                _activeSideOwners.Remove(side);
        }
    }

    /// <summary>
    /// Periodic housekeeping: evicts fully-resolved closed orders (and any
    /// <see cref="_cancelAttempts"/> entries pointing at them) once
    /// they're older than <paramref name="retention"/>. RFC #703's
    /// book-driven reactive requote path makes cancel/resubmit cycles
    /// common rather than rare (unlike the occasional staleness-guard
    /// cancel #705 accepted leaving <see cref="_orders"/>/<see
    /// cref="_cancelAttempts"/> unbounded for), so a long-running process
    /// quoting a volatile book needs this to avoid unbounded growth and
    /// the resulting O(n) slowdown in <see cref="OpenCount"/>/<see
    /// cref="FindStale"/>. Skips any order with a still-outstanding
    /// pending cancel, however old, since its correlation entry is still
    /// needed to resolve the eventual ER.
    /// </summary>
    public void PruneClosed(TimeSpan retention, DateTimeOffset now)
    {
        List<ulong>? toRemove = null;
        foreach (var kv in _orders)
        {
            var o = kv.Value;
            if (!o.IsOpen && o.PendingCancelClOrdId is null && now - o.SubmittedAtUtc > retention)
                (toRemove ??= new List<ulong>()).Add(kv.Key);
        }
        if (toRemove is not null)
            foreach (var id in toRemove)
                _orders.TryRemove(id, out _);

        List<ulong>? staleAttempts = null;
        foreach (var kv in _cancelAttempts)
        {
            if (!_orders.ContainsKey(kv.Value.OrigClOrdId))
                (staleAttempts ??= new List<ulong>()).Add(kv.Key);
        }
        if (staleAttempts is not null)
            foreach (var id in staleAttempts)
                _cancelAttempts.TryRemove(id, out _);

        foreach (var kv in _expiredCancelAttempts)
            if (now - kv.Value.ExpiredAtUtc > retention)
                _expiredCancelAttempts.TryRemove(kv.Key, out _);
    }
}

/// <summary>Correlation row for a cancel REQUEST's own ClOrdId → the
/// original order it targets, tagged with which trigger raised it (see
/// <see cref="OrderTracker.TryResolveCancelAttempt(ulong, out ulong, out CancelReason)"/>).</summary>
internal readonly record struct CancelAttempt(ulong OrigClOrdId, CancelReason Reason);
internal readonly record struct ExpiredCancelAttemptCorrelation(
    CancelAttempt Attempt,
    DateTimeOffset ExpiredAtUtc);

public readonly record struct ExpiredPendingCancel(
    ulong OrigClOrdId,
    ulong CancelClOrdId,
    string Symbol,
    bool IsBuy,
    CancelReason Reason,
    DateTimeOffset AttemptedAtUtc,
    DateTimeOffset ExpiredAtUtc);

/// <summary>
/// Trigger responsible for an explicit cancel. New pricing and feed policies
/// extend this attribution without changing cancel correlation semantics.
/// </summary>
public enum CancelReason
{
    StaleOrder,
    PriceDrift,
    InventoryStrategy,
    VolatilityStrategy,
    FeedUnavailable,
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
    /// <summary>UTC time of the most recent <see cref="OrderTracker.RegisterCancelAttempt"/>/
    /// <see cref="OrderTracker.TryRegisterCancelAttempt"/> for this order,
    /// if any. Used (rather than <see cref="SubmittedAtUtc"/>) to throttle
    /// the book-driven reactive requote path so a synchronously-failed or
    /// rejected cancel — which frees <see cref="PendingCancelClOrdId"/>
    /// immediately — can't be retried on every subsequent book delta.</summary>
    public DateTimeOffset? LastCancelAttemptAtUtc { get; set; }
    /// <summary>The venue's own order identifier, once learned from an ER
    /// — see <see cref="OrderTracker.SetOrderId"/>. Null until the first
    /// ER for this ClOrdID arrives.</summary>
    public ulong? OrderId { get; set; }
}
