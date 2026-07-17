using B3.Trading.Domain;

namespace B3.Trading.Application.Risk;

/// <summary>
/// Slice 2 of #122. Margin coordination for cancel-replace.
///
/// <para>
/// Modify is delta-aware. The naive "release the original reservation
/// then re-reserve under the new ClOrdID" creates a window in which
/// the trader must hold capacity for both legs of the replace
/// simultaneously — a downsize from R1 to R2 (R2 &lt; R1) would
/// momentarily require <c>R1 + R2</c> of cash and could spuriously
/// reject. The coordinator instead reserves only the upsize delta at
/// <see cref="PrepareReplaceAsync"/> time and rebalances at
/// <see cref="CommitReplace"/> time using the venue-confirmed
/// leaves quantity.
/// </para>
///
/// <para>
/// Implementations are expected to be the same singleton as
/// <see cref="IMarginProvider"/> so the underlying reservation ledger
/// is shared.
/// </para>
/// </summary>
public interface IReplaceMarginCoordinator
{
    /// <summary>
    /// Atomically check that the upsize delta (if any) fits in the
    /// owner's available cash and reserve it under
    /// <paramref name="newClOrdId"/>. Returns
    /// <see cref="RiskDecision.Approve"/> when no extra cash is needed
    /// (downsize / same / non-cash side) or when the delta fits;
    /// otherwise <see cref="RiskDecision.Reject"/>.
    /// </summary>
    /// <remarks>
    /// The original reservation under <paramref name="originalClOrdId"/>
    /// is left intact so that, if the venue rejects the replace, the
    /// trader's original order continues to hold its capacity.
    /// </remarks>
    Task<RiskDecision> PrepareReplaceAsync(
        ulong originalClOrdId,
        ulong newClOrdId,
        EndClientId owner,
        decimal newRemainingNotional,
        CancellationToken ct);

    /// <summary>
    /// Firm-aware overload. The default implementation preserves source
    /// compatibility for test doubles; production providers override it so
    /// replace capacity is checked in the original order's firm bucket.
    /// </summary>
    Task<RiskDecision> PrepareReplaceAsync(
        ulong originalClOrdId,
        ulong newClOrdId,
        EndClientId owner,
        string firmId,
        decimal newRemainingNotional,
        CancellationToken ct) =>
        PrepareReplaceAsync(
            originalClOrdId,
            newClOrdId,
            owner,
            newRemainingNotional,
            ct);

    /// <summary>
    /// Called from the ER processor on a successful Replaced ack.
    /// Atomically releases the original reservation and finalizes the
    /// replacement reservation at <paramref name="confirmedRemainingNotional"/>
    /// (computed from <c>intent.NewPrice * ER.LeavesQty</c> by the
    /// caller — using the venue's view of leaves means partial fills
    /// that landed during the Prepare→Commit window self-correct).
    /// </summary>
    void CommitReplace(
        ulong originalClOrdId,
        ulong newClOrdId,
        decimal confirmedRemainingNotional);

    /// <summary>
    /// Called from the ER processor on a replace-reject (Rejected ER
    /// for a ClOrdID present in <see cref="PendingReplacementRegistry"/>)
    /// or from the endpoint when the gateway dispatch throws after
    /// Prepare succeeded. Releases the upsize delta reserved at
    /// Prepare; the original reservation is untouched.
    /// </summary>
    void AbortReplace(ulong newClOrdId);
}
