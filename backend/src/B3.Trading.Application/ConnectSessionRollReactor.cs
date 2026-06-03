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
}

/// <summary>
/// #512 seam. Called by the FIXP gateway immediately after the initial
/// <c>ConnectAsync</c> when the SDK fell back from Establish-reuse to a
/// freshly negotiated session with a BUMPED SessionVerId (a recoverable
/// reuse reject — the venue genuinely lost the session). The boot-time
/// #380/#504 reconcile already ran against the OLD verId before app start,
/// so it cannot observe this deferred bump; without this seam the un-acked
/// PendingNew "ghosts" would linger for the whole process lifetime.
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
    /// <paramref name="toVerId"/> during the initial connect. MUST run before
    /// the firm's event loop and the first snapshot.
    /// </summary>
    void OnSessionRolledAtConnect(string firmId, uint fromVerId, uint toVerId);
}

/// <summary>
/// Default <see cref="IConnectSessionRollReactor"/>: reaps un-acked
/// PendingNew orders for the rolled firm under the <see cref="EventDispatcher"/>
/// lock so the mutation is serialised against concurrently-connecting firms'
/// event loops and the snapshot service. Durability follows the #504 model:
/// the cancellation is captured by the next snapshot (taken under the same
/// lock); a crash before that snapshot is backstopped by the boot-time
/// baseline reconcile on the next restart, which re-detects the advance
/// (the snapshot baseline still records the pre-bump verId) and re-reaps.
/// </summary>
public sealed class PendingNewReapingConnectRollReactor : IConnectSessionRollReactor
{
    private readonly WorkingOrderBook _orders;
    private readonly EventDispatcher _dispatcher;
    private readonly ILogger<PendingNewReapingConnectRollReactor> _logger;

    public PendingNewReapingConnectRollReactor(
        WorkingOrderBook orders,
        EventDispatcher dispatcher,
        ILogger<PendingNewReapingConnectRollReactor> logger)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void OnSessionRolledAtConnect(string firmId, uint fromVerId, uint toVerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        _dispatcher.RunExclusive(() =>
            FirmSessionRollReconciliation.CancelPendingNewForRolledFirm(
                _orders, firmId, fromVerId, toVerId, _logger));
    }
}
