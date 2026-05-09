using B3.Trading.Application.Persistence;

namespace B3.Trading.Application.UserBots;

/// <summary>
/// Sub-issue #171 (E). Side-mapping between the platform's internal
/// <c>ulong</c> ClOrdID (allocated by <see cref="ClOrdIdPrefixRegistry"/>)
/// and the bot-visible <c>(CredentialId, ExternalClOrdId)</c> identity
/// for FIXP-origin orders. Lookups are on the hot ER processing path
/// (<see cref="TryGetOrderMapping"/> is synchronous) so implementations
/// must avoid async I/O on the read side.
///
/// <para>State is rebuilt at startup by the snapshot+replay machinery —
/// snapshot capture/restore covers live mappings, and post-snapshot
/// <see cref="OrderSubmittedEvent"/> /
/// <see cref="OrderCancelRequestedEvent"/> events further mutate the
/// registry via the existing replay path. The registry never originates
/// a WAL append on its own; the order/cancel events are the source of
/// truth and the registry is just a queryable cache rebuilt from them.</para>
/// </summary>
public interface IUserBotOrderMappingRegistry
{
    /// <summary>
    /// Synchronous lookup used by sub-issue F (#172) to reverse-route an
    /// already-normalised <see cref="ExecutionEvent"/> ClOrdID back to
    /// the originating bot session. Returns <c>false</c> when the
    /// internal id is not bot-origin (REST/WS orders) — F treats that
    /// as "not for me" and skips.
    /// </summary>
    bool TryGetOrderMapping(ulong internalClOrdId, out OrderMapping mapping);

    /// <summary>
    /// Synchronous reverse lookup used by the FIXP listener on inbound
    /// <c>OrderCancelRequest</c> to resolve the bot's
    /// <c>(credentialId, externalOrigClOrdId)</c> back to the platform's
    /// internal original ClOrdID. Returns <c>false</c> when no live
    /// mapping exists OR when the credential does not own the order
    /// (cross-user isolation guard, RFC §4.6).
    /// </summary>
    bool TryGetByExternal(Guid credentialId, ulong externalClOrdId, out ulong internalClOrdId);

    /// <summary>
    /// Synchronous lookup for sub-issue F to recover the bot's external
    /// cancel ClOrdID when an ER references a cancel-side internal
    /// ClOrdID. Returns <c>false</c> when the cancel-side id is not
    /// bot-origin.
    /// </summary>
    bool TryGetCancelMapping(ulong cancelInternalClOrdId, out CancelMapping mapping);

    /// <summary>
    /// Drops a forward order mapping. Sub-issue F (or a future janitor)
    /// calls this on terminal ER kinds (Filled, Cancelled, Rejected).
    /// Idempotent — reaping an unknown id is a no-op.
    /// </summary>
    void Reap(ulong internalClOrdId);

    /// <summary>
    /// Drops a cancel-side mapping. Sub-issue F calls this once the
    /// cancel-ack ER is forwarded.
    /// </summary>
    void ReapCancel(ulong cancelInternalClOrdId);

    /// <summary>
    /// In-memory mutation invoked from the
    /// <see cref="EventDispatcher"/> apply callback for an
    /// <see cref="OrderSubmittedEvent"/> with a non-null
    /// <see cref="OrderSubmittedEvent.BotMapping"/>. Synchronous —
    /// runs under the dispatcher lock alongside the WAL append.
    /// </summary>
    void RegisterOrderInternal(ulong internalClOrdId, Guid credentialId, ulong externalClOrdId);

    /// <summary>
    /// In-memory mutation invoked from the
    /// <see cref="EventDispatcher"/> apply callback for an
    /// <see cref="OrderCancelRequestedEvent"/> with a non-null
    /// <see cref="OrderCancelRequestedEvent.BotMapping"/>. Synchronous —
    /// runs under the dispatcher lock alongside the WAL append.
    /// </summary>
    void RegisterCancelInternal(
        ulong cancelInternalClOrdId,
        ulong originalInternalClOrdId,
        Guid credentialId,
        ulong externalCancelClOrdId);

    /// <summary>Snapshot capture — called under <c>WithSnapshotLock</c>.</summary>
    IReadOnlyList<BotOrderMappingSnapshot> SnapshotOrders();

    /// <summary>Snapshot capture — called under <c>WithSnapshotLock</c>.</summary>
    IReadOnlyList<BotCancelMappingSnapshot> SnapshotCancels();

    /// <summary>Snapshot restore — single-threaded at startup.</summary>
    void Restore(
        IEnumerable<BotOrderMappingSnapshot> orders,
        IEnumerable<BotCancelMappingSnapshot> cancels);
}

/// <summary>
/// Forward-routing tuple returned by
/// <see cref="IUserBotOrderMappingRegistry.TryGetOrderMapping"/>.
/// </summary>
public readonly record struct OrderMapping(Guid CredentialId, ulong ExternalClOrdId);

/// <summary>
/// Cancel-side routing tuple. <see cref="OriginalInternalClOrdId"/> is
/// kept on the cancel record so F can correlate a cancel-ack ER (whose
/// raw <c>ClOrdId</c> is the cancel-side id) with the original order's
/// bot-visible identity in a single lookup.
/// </summary>
public readonly record struct CancelMapping(
    ulong OriginalInternalClOrdId,
    Guid CredentialId,
    ulong ExternalCancelClOrdId);
