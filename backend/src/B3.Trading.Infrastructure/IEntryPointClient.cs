namespace B3.Trading.Infrastructure;

/// <summary>
/// Internal seam between the wire layer and our ER router. Production
/// uses <see cref="B3EntryPointClientGateway"/> wrapping the upstream
/// <c>B3.EntryPoint.Client.EntryPointClient</c>. Tests use
/// <see cref="MockEntryPointClient"/> to drive ERs without TCP.
///
/// Records here mirror the upstream package's shape (ulong ClOrdId,
/// ulong SecurityId, FIX-style enums) so translation at the boundary
/// is a pure field-mapping exercise.
/// </summary>
public interface IEntryPointClient
{
    Task SubmitNewOrderAsync(NewOrderSingle request, CancellationToken cancellationToken);
    Task SubmitCancelAsync(OrderCancelRequest request, CancellationToken cancellationToken);
    Task SubmitCancelReplaceAsync(OrderCancelReplaceRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Raised whenever the exchange pushes an ExecutionReport (new, fill,
    /// cancel ack, reject, etc.) to the platform. Single-threaded by
    /// implementation contract; consumers do not have to lock.
    /// </summary>
    event Action<ExecutionReportEnvelope>? ExecutionReportReceived;

    /// <summary>
    /// #432 — raised whenever the exchange pushes a <c>BusinessReject</c>
    /// (structural rejection: malformed message, unknown SecurityID,
    /// outside trading hours, etc.). These have no ClOrdID anchor so
    /// they cannot piggy-back on the ER pipeline, but the platform must
    /// still surface them: a venue BusinessReject means an order request
    /// never produced an ExecutionReport, and the operator/trader needs
    /// the audit trail to reconcile. Single-threaded by implementation
    /// contract.
    /// </summary>
    event Action<BusinessRejectEnvelope>? BusinessRejectReceived;
}

public enum EpSide
{
    Buy,
    Sell,
}

public enum EpOrderType
{
    Limit,
    Market,
}

public enum EpExecType
{
    New,
    PartialFill,
    Fill,
    Canceled,
    Replaced,
    Rejected,
}

public sealed record NewOrderSingle(
    ulong ClOrdId,
    ulong SecurityId,
    string Symbol,
    EpSide Side,
    EpOrderType Type,
    long Quantity,
    decimal? Price,
    string FirmId,
    /// <summary>Q3.4 (#284). Native iceberg / reserve display qty (FIX MaxFloor).
    /// Null means full disclosure (no reserve). Set by
    /// <see cref="EntryPointClientGateway"/> from the domain order's
    /// <c>DisplayQty</c>. The real SDK path (<c>B3EntryPointClientGateway</c>)
    /// already wires MaxFloor on the upstream <c>NewOrderRequest</c>; this
    /// field plumbs the same intent through the mock seam so tests can
    /// assert wire mapping without standing up the real client.</summary>
    long? MaxFloor = null);

public sealed record OrderCancelRequest(
    ulong ClOrdId,
    ulong OrigClOrdId,
    ulong SecurityId,
    EpSide Side,
    string FirmId);

public sealed record OrderCancelReplaceRequest(
    ulong OriginalClOrdId,
    ulong NewClOrdId,
    ulong SecurityId,
    EpSide Side,
    long NewQuantity,
    decimal? NewPrice,
    string FirmId,
    /// <summary>Q3.4 (#284). Native iceberg / reserve display qty (FIX MaxFloor)
    /// inherited from the original order, clamped to <c>NewQuantity</c> when the
    /// replace shrinks the order below the original visible portion. Null when
    /// the original was a full-disclosure order.</summary>
    long? MaxFloor = null);

/// <summary>
/// ExecutionReport surfaced by the wire to the platform. <see cref="OrigClOrdId"/>
/// is non-zero only on cancel-ack and modify-ack — the processor uses it to
/// look up the original order whose state must mutate (the cancel/modify
/// request itself was issued under a brand-new ClOrdID).
/// </summary>
public sealed record ExecutionReportEnvelope(
    ulong ClOrdId,
    EpExecType ExecType,
    long LeavesQuantity,
    long CumulativeQuantity,
    long LastQuantity,
    decimal LastPrice,
    string? RejectReason,
    ulong OrigClOrdId = 0,
    /// <summary>
    /// PR #317 P1. Source firm the ER arrived on — populated by the
    /// inbound wire path (<see cref="B3EntryPointClientGateway"/>'s
    /// translation + <see cref="MockEntryPointClient"/> / simulator).
    /// Carried end-to-end through the WAL
    /// (<c>ExecutionReportReceivedEvent.FirmId</c>) and verified by
    /// <c>ExecutionReportProcessor.Apply</c> against the resolved
    /// order's <c>FirmId</c>: a mismatch is rejected (no state
    /// mutation) + counted on <c>trading.er.firm_mismatch_total</c>.
    /// Nullable so legacy WAL events written before #317 replay
    /// unchanged — a null envelope FirmId skips the cross-firm check.
    /// </summary>
    string? FirmId = null);

/// <summary>
/// #432. Wire-layer envelope for a venue <c>BusinessReject</c> — a
/// structural rejection of an inbound message that never produced an
/// ExecutionReport (e.g. malformed payload, unknown <c>SecurityID</c>,
/// outside trading hours). No ClOrdID anchor (the reject references the
/// session seqnum, not a business id), so it travels on its own event
/// channel rather than as a degraded
/// <see cref="ExecutionReportEnvelope"/>.
/// </summary>
/// <param name="FirmId">Source firm the reject arrived on. Always
/// populated by the gateway/mock; nullable only to stay symmetric with
/// <see cref="ExecutionReportEnvelope"/>.</param>
/// <param name="RefSeqNum">FIX <c>RefSeqNum</c> — session seqnum of the
/// rejected inbound message. Anchors the reject to a request when the
/// caller correlates by seqnum (see RFC outbound seqnum reservation).</param>
/// <param name="RejectReason">FIX <c>BusinessRejectReason</c> code as
/// emitted by the venue.</param>
/// <param name="Text">Human-readable detail string from the venue —
/// preserved verbatim for operator audit / log scraping.</param>
/// <param name="SeqNum">Venue session inbound seqnum of the reject
/// message itself.</param>
/// <param name="SendingTime">Venue <c>SendingTime</c> from the reject
/// envelope.</param>
public sealed record BusinessRejectEnvelope(
    string? FirmId,
    ulong RefSeqNum,
    int RejectReason,
    string? Text,
    ulong SeqNum,
    DateTimeOffset SendingTime);
