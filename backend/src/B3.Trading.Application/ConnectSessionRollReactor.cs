using System.Collections.Generic;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Application;

/// <summary>
/// Shared session-roll reconciliation policy (#380 / #504). When a firm's
/// venue session version advances past the baseline we last reconciled
/// against, un-acked <see cref="OrderStatus.PendingNew"/> orders are the
/// one class that cannot survive: the venue never acknowledged them, so a
/// session roll guarantees they do not exist under any session version.
/// Working / PartiallyFilled orders are deliberately KEPT — FIXP
/// retransmission resynchronises them during recovery, and a terminal ER
/// (Cancel/Fill) arrives if the venue dropped them.
///
/// <para>
/// This is the single source of truth for that policy. Both the boot-time
/// snapshot-baseline reconcile (<c>SnapshotService.ReconcileFirmSessionVerIds</c>,
/// runs single-threaded before app start) and the runtime post-connect
/// reactor (<see cref="PendingNewReapingConnectRollReactor"/>, runs under the
/// dispatcher lock) call it so their behaviour can never drift apart.
/// </para>
/// </summary>
public static class FirmSessionRollReconciliation
{
    /// <summary>
    /// Cancels every <see cref="OrderStatus.PendingNew"/> order attached to
    /// <paramref name="firmId"/> and returns the number actually transitioned
    /// to <see cref="OrderStatus.Cancelled"/>. The caller owns serialisation:
    /// boot reconcile runs before any event loop starts; the runtime reactor
    /// runs under the <see cref="EventDispatcher"/> lock. Emits the same log
    /// + metrics regardless of caller so dashboards/alerts are uniform.
    /// </summary>
    public static int CancelPendingNewForRolledFirm(
        WorkingOrderBook orders,
        string firmId,
        uint fromVerId,
        uint toVerId,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        ArgumentNullException.ThrowIfNull(logger);

        var cancelled = 0;
        foreach (var order in orders.EnumerateForFirm(firmId, includeTerminal: false))
        {
            if (order.Status == OrderStatus.PendingNew)
            {
                order.MarkCancelled();
                if (order.Status == OrderStatus.Cancelled)
                {
                    cancelled++;
                }
            }
        }

        if (cancelled > 0)
        {
            logger.LogWarning(
                "event=recovery.session-rolled firm={Firm} from={From} to={To} pendingNewCancelled={Cancelled}",
                firmId, fromVerId, toVerId, cancelled);
        }
        else
        {
            logger.LogInformation(
                "event=recovery.session-rolled firm={Firm} from={From} to={To} (FIXP recovery handles sync)",
                firmId, fromVerId, toVerId);
        }

        MetricsRegistry.RecoverySessionRolledFirms.Add(1,
            new KeyValuePair<string, object?>("firm", firmId));
        MetricsRegistry.RecoverySessionRolledOrdersDropped.Add(cancelled,
            new KeyValuePair<string, object?>("firm", firmId));
        return cancelled;
    }

    /// <summary>
    /// Canonical stale-reason string for a confirmed session roll, so the
    /// runtime reactor (connect / reconnect) and any future caller emit an
    /// identical, operator-greppable reason. Format mirrors the sibling
    /// <c>inbound_gap:{from}-{to}</c> / <c>peer_terminated:{code}</c> reasons
    /// produced by <see cref="OrderStaleningVenueReactor"/>.
    /// </summary>
    public static string SessionRolledStaleReason(uint fromVerId, uint toVerId)
        => $"session_rolled:{fromVerId}-{toVerId}";
}

/// <summary>
/// #512 / #380 seam. Called by the FIXP gateway after a CONFIRMED venue
/// session roll — i.e. an Establish-reuse was REJECTED and the SDK
/// renegotiated a fresh session with a BUMPED SessionVerId, so the venue
/// genuinely discarded its per-session state (our working set is gone).
/// Two callers, both high-confidence (reuse-rejected, not a benign blip):
///
/// <list type="number">
///   <item>the initial <c>ConnectAsync</c> cold-resume fallback
///         (<c>B3EntryPointClientGateway.ReconcileConnectSessionRoll</c>);</item>
///   <item>the live reconnect loop on a <c>Renegotiated</c> outcome
///         (<c>B3EntryPointClientGateway.ReconcileReconnectSessionRoll</c>).</item>
/// </list>
///
/// The boot-time #380/#504 reconcile only sees raw verId numbers (it cannot
/// distinguish a reuse-reject from a benign advance), so it stays
/// conservative (reap PendingNew only). This seam carries the richer
/// reuse-rejected signal, so it ALSO flags surviving Working / PartiallyFilled
/// orders stale — they cannot exist under the new session and FIXP
/// retransmission cannot recreate them.
///
/// <para>
/// Mirrors the <see cref="IVenueDisconnectReactor"/> shape: the gateway
/// (Infrastructure) hands the signal to an Application-side implementation
/// without taking a direct reference to <see cref="WorkingOrderBook"/> /
/// <see cref="EventDispatcher"/>.
/// </para>
/// </summary>
public interface IConnectSessionRollReactor
{
    /// <summary>
    /// Reconcile order state for <paramref name="firmId"/> after its venue
    /// session rolled from <paramref name="fromVerId"/> to
    /// <paramref name="toVerId"/> on a confirmed reuse-reject. MUST run before
    /// the firm's event loop and the first snapshot.
    /// </summary>
    void OnSessionRolled(string firmId, uint fromVerId, uint toVerId);
}

/// <summary>
/// Default <see cref="IConnectSessionRollReactor"/>. On a confirmed session
/// roll it does two things, in order:
///
/// <list type="number">
///   <item>reaps un-acked <see cref="OrderStatus.PendingNew"/> orders for the
///         rolled firm under the <see cref="EventDispatcher"/> lock (in-memory
///         <c>MarkCancelled</c>, durable via the next snapshot — the #504
///         model);</item>
///   <item>flags surviving <see cref="OrderStatus.Working"/> /
///         <see cref="OrderStatus.PartiallyFilled"/> orders STALE via
///         <see cref="OrderStalenessService.MarkAllWorkingByFirm"/>. Unlike the
///         boot reconcile (which cannot tell a reuse-reject from a benign verId
///         advance and therefore keeps Working orders), this seam only fires on
///         a reuse-reject, so the venue truly lost the order and FIXP
///         retransmission cannot resurrect it. Staling is non-destructive
///         (operator-clearable; auto-clears on a terminal ER) and WAL-durable
///         per order (<c>OrderStaledEvent</c> via <c>Dispatch</c>). Recovery is
///         ASYMMETRIC with Phase 1: PendingNew reaping is re-runnable from the
///         restart boot reconcile, but the boot reconcile is conservative (#504,
///         PendingNew only) and will NOT re-stale Working/PartiallyFilled. So if
///         this phase fails mid-bulk (e.g. a WAL append error) the un-marked
///         tail is stranded; the reactor emits
///         <see cref="MetricsRegistry.SessionRollStaleReconcileFailed"/> +
///         a critical log and rethrows so an operator reconciles the survivors
///         via the admin mark-stale endpoint.</item>
/// </list>
///
/// <para>
/// The two phases are deliberately NOT nested: <see cref="OrderStalenessService.MarkAllWorkingByFirm"/>
/// takes the dispatcher lock per order, so running it inside the PendingNew
/// reap's <see cref="EventDispatcher.RunExclusive"/> would hold the lock across
/// every order's payload serialisation + WAL append. PendingNew (now Cancelled)
/// and Working/PartiallyFilled are disjoint sets, so no atomicity is needed
/// between them.
/// </para>
/// </summary>
public sealed class PendingNewReapingConnectRollReactor : IConnectSessionRollReactor
{
    private readonly WorkingOrderBook _orders;
    private readonly EventDispatcher _dispatcher;
    private readonly OrderStalenessService? _staleness;
    private readonly TimeProvider _clock;
    private readonly ILogger<PendingNewReapingConnectRollReactor> _logger;

    public PendingNewReapingConnectRollReactor(
        WorkingOrderBook orders,
        EventDispatcher dispatcher,
        ILogger<PendingNewReapingConnectRollReactor> logger,
        OrderStalenessService? staleness = null,
        TimeProvider? clock = null)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _staleness = staleness;
        _clock = clock ?? TimeProvider.System;
    }

    public void OnSessionRolled(string firmId, uint fromVerId, uint toVerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);

        // Phase 1: reap un-acked PendingNew under the dispatcher lock.
        _dispatcher.RunExclusive(() =>
            FirmSessionRollReconciliation.CancelPendingNewForRolledFirm(
                _orders, firmId, fromVerId, toVerId, _logger));

        // Phase 2: flag surviving Working / PartiallyFilled stale. NOT nested
        // inside RunExclusive (see class remarks).
        if (_staleness is null)
        {
            // Real trading mode always wires the staleness service; a null
            // here means a reduced composition (tests / mock exchange). Warn
            // so a misconfigured production deployment that silently drops
            // back to "keep Working ghosts" (#380) is visible in logs.
            _logger.LogWarning(
                "event=recovery.session-rolled firm={Firm} from={From} to={To} stale=skipped reason=no_staleness_service",
                firmId, fromVerId, toVerId);
            return;
        }

        var reason = FirmSessionRollReconciliation.SessionRolledStaleReason(fromVerId, toVerId);
        int marked;
        try
        {
            marked = _staleness.MarkAllWorkingByFirm(firmId, reason, _clock.GetUtcNow(), actorUserId: null);
        }
        catch (Exception ex)
        {
            // The staling phase is recovered ONLY by its own per-order WAL
            // durability (OrderStaledEvent re-applied on replay): the restart
            // boot reconcile is deliberately conservative (#504, PendingNew
            // only) and will NOT re-stale surviving Working/PartiallyFilled
            // orders. So a failure here (e.g. a WAL append error mid-bulk) can
            // strand the un-marked tail. Emit a CRITICAL, operator-actionable
            // signal — survivors for this firm must be reconciled by hand via
            // the admin mark-stale endpoint — then rethrow so the gateway keeps
            // SessionVerId at the old baseline (preserving the PendingNew boot
            // backstop) and logs the reconcile failure.
            MetricsRegistry.SessionRollStaleReconcileFailed.Add(1,
                new KeyValuePair<string, object?>("firm", firmId));
            _logger.LogCritical(ex,
                "event=recovery.session-rolled.stale-failed firm={Firm} from={From} to={To} action=operator_must_mark_stale "
                + "(confirmed session roll: surviving Working/PartiallyFilled orders may be venue ghosts that were NOT flagged stale; "
                + "the boot reconcile will not re-stale them — reconcile via the admin mark-stale endpoint).",
                firmId, fromVerId, toVerId);
            throw;
        }

        if (marked > 0)
        {
            MetricsRegistry.OrdersAutoStaledByVenueDesync.Add(marked,
                new KeyValuePair<string, object?>("firm", firmId),
                new KeyValuePair<string, object?>("reason", "session_rolled"));
        }
    }
}
