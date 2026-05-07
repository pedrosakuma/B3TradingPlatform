using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Slice 1 of #132. Surfaces the advisory venue-staleness overlay
/// described on <see cref="Order.IsStale"/>: an admin/operator can flag
/// a working order as suspected-stale-by-venue (typically after a
/// venue restart that reset the matching book without our trading-host
/// noticing) so the platform stops issuing Cancel/Modify against a
/// phantom while keeping every other accounting concern (positions,
/// cash, risk, algo parents) untouched.
///
/// <para>
/// Mark/Clear go through <see cref="EventDispatcher"/> so the WAL
/// records the decision and post-recovery state matches what the
/// operator saw before the restart. The two paths are idempotent and
/// honour the domain state-machine: only Working / PartiallyFilled
/// orders accept a stale mark, and re-marking an already-stale order
/// is a no-op.
/// </para>
///
/// <para>
/// Auto-clear on terminal ER (<see cref="ExecKind.Filled"/>,
/// <see cref="ExecKind.Canceled"/>, <see cref="ExecKind.Rejected"/>,
/// <see cref="ExecKind.Replaced"/>) is handled in
/// <see cref="ExecutionReportProcessor"/> — the venue actually still
/// knew the order, so the stale mark was a false positive and we lift
/// it as a side-effect of the genuine ER stream.
/// </para>
/// </summary>
public sealed class OrderStalenessService
{
    private readonly EventDispatcher _dispatcher;
    private readonly WorkingOrderBook _orders;

    public OrderStalenessService(EventDispatcher dispatcher, WorkingOrderBook orders)
    {
        _dispatcher = dispatcher;
        _orders = orders;
    }

    public MarkStaleResult MarkStale(string firmId, ulong clOrdId, string reason, DateTimeOffset atUtc, string? actorUserId)
    {
        if (string.IsNullOrWhiteSpace(firmId))
            throw new ArgumentException("firmId required.", nameof(firmId));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason required.", nameof(reason));

        if (!_orders.TryGet(clOrdId, out var order) || order is null)
            return MarkStaleResult.NotFound;
        if (!string.Equals(order.FirmId, firmId, StringComparison.OrdinalIgnoreCase))
            return MarkStaleResult.WrongFirm;
        if (order.IsStale)
            return MarkStaleResult.AlreadyStale;
        if (order.Status is not (OrderStatus.Working or OrderStatus.PartiallyFilled))
            return MarkStaleResult.NotEligible;

        var evt = new OrderStaledEvent
        {
            ClOrdId = clOrdId,
            FirmId = order.FirmId,
            Reason = reason,
            StaledAtUtc = atUtc,
            ActorUserId = actorUserId,
        };
        var marked = false;
        _dispatcher.Dispatch(evt, () =>
        {
            // Re-check inside the dispatcher lock to defend against a
            // racing terminal ER that flipped status after the
            // optimistic check above. ApplyCumulativeFill / MarkCancelled
            // also run under the same lock via EventDispatcher.Dispatch,
            // so this is a sufficient barrier.
            marked = order.MarkStale(reason, atUtc);
        });
        return marked ? MarkStaleResult.Marked : MarkStaleResult.NotEligible;
    }

    /// <summary>
    /// Bulk-mark every Working / PartiallyFilled order for <paramref name="firmId"/>
    /// as stale, one WAL event per order. Used by slice 2 (#132) to react to
    /// venue desync signals (peer-initiated termination, FIXP inbound gap at
    /// reconnect): rather than asking the operator to mark each ghost by hand,
    /// the platform flags the entire firm's working set defensively. Already-
    /// stale orders are skipped (idempotent), so calling this twice in a row
    /// does not write twice.
    ///
    /// <para>
    /// Each order goes through the full <see cref="EventDispatcher.Dispatch"/>
    /// path — same lock as <see cref="MarkStale"/>, same WAL contract — so a
    /// concurrent terminal ER for one of the orders cannot race the bulk-mark
    /// (the ER runs serially under the same lock and triggers
    /// <see cref="ExecutionReportProcessor"/>'s auto-clear branch). The lock
    /// is released between orders so the dispatcher stays available for ER
    /// processing during a long bulk-mark.
    /// </para>
    /// </summary>
    public int MarkAllWorkingByFirm(string firmId, string reason, DateTimeOffset atUtc, string? actorUserId)
    {
        if (string.IsNullOrWhiteSpace(firmId))
            throw new ArgumentException("firmId required.", nameof(firmId));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason required.", nameof(reason));

        var marked = 0;
        foreach (var order in _orders.EnumerateForFirm(firmId, includeTerminal: false))
        {
            if (order.IsStale) continue;
            if (order.Status is not (OrderStatus.Working or OrderStatus.PartiallyFilled)) continue;

            var clOrdId = order.ClOrdId;
            var evt = new OrderStaledEvent
            {
                ClOrdId = clOrdId,
                FirmId = order.FirmId,
                Reason = reason,
                StaledAtUtc = atUtc,
                ActorUserId = actorUserId,
            };
            var didMark = false;
            _dispatcher.Dispatch(evt, () =>
            {
                // Re-check inside the dispatcher lock (same defence as
                // single-order MarkStale): a terminal ER queued behind us
                // may have flipped status / lifted the implicit eligibility
                // since the optimistic enumerate.
                didMark = order.MarkStale(reason, atUtc);
            });
            if (didMark) marked++;
        }
        return marked;
    }

    public ClearStaleResult ClearStale(string firmId, ulong clOrdId, string? actorUserId)
    {
        if (string.IsNullOrWhiteSpace(firmId))
            throw new ArgumentException("firmId required.", nameof(firmId));

        if (!_orders.TryGet(clOrdId, out var order) || order is null)
            return ClearStaleResult.NotFound;
        if (!string.Equals(order.FirmId, firmId, StringComparison.OrdinalIgnoreCase))
            return ClearStaleResult.WrongFirm;
        if (!order.IsStale)
            return ClearStaleResult.NotStale;

        var evt = new OrderStaleClearedEvent
        {
            ClOrdId = clOrdId,
            FirmId = order.FirmId,
            ResolvedBy = "admin",
            ActorUserId = actorUserId,
        };
        var cleared = false;
        _dispatcher.Dispatch(evt, () => cleared = order.ClearStale());
        return cleared ? ClearStaleResult.Cleared : ClearStaleResult.NotStale;
    }
}

public enum MarkStaleResult
{
    Marked,
    AlreadyStale,
    NotFound,
    WrongFirm,
    /// <summary>Order exists but is in PendingNew or a terminal status.</summary>
    NotEligible,
}

public enum ClearStaleResult
{
    Cleared,
    NotStale,
    NotFound,
    WrongFirm,
}
