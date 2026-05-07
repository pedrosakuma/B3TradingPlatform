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
