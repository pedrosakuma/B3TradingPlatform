namespace B3.Trading.Application.Risk;

/// <summary>
/// Pluggable margin / collateral check.
///
/// <para>
/// <b>Contract:</b> implementations may be stateful — the contract for
/// <see cref="TryReserveAsync"/> is "atomically check available
/// collateral and reserve the order's notional if the check passes".
/// Reservations are released via <see cref="OnExecution"/> as
/// ExecutionReports flow back from the exchange (partial fill releases
/// proportionally; terminal status releases the rest). The
/// <see cref="NoOpMarginProvider"/> default implementation makes both
/// methods harmless no-ops so deployments can opt out by simply not
/// registering a real provider.
/// </para>
///
/// <para>
/// The model assumed by v2 (see <c>docs/rfcs/pre-trade-risk-v2.md</c>
/// §3.1) is reserve-on-submit / release-on-fill, mirroring a crypto
/// spot exchange. Derivatives-style margin (initial/maintenance,
/// greeks) and T+N cash settlement are deliberately out-of-scope and
/// require a different provider behind this interface.
/// </para>
/// </summary>
public interface IMarginProvider
{
    /// <summary>
    /// Atomically check available collateral for the order and, if
    /// sufficient, reserve the order's notional under
    /// <paramref name="clOrdId"/>. Returns
    /// <see cref="RiskDecision.Approve"/> on success or a
    /// <see cref="RiskDecision.Reject"/> with a human-readable reason
    /// otherwise. The reservation is released by <see cref="OnExecution"/>
    /// when matching ERs arrive.
    /// </summary>
    Task<RiskDecision> TryReserveAsync(ulong clOrdId, RiskContext ctx, CancellationToken ct);

    /// <summary>
    /// Release reserved collateral as ERs arrive. Default: no-op.
    /// Implementations that hold reservations override this to release
    /// the unfilled portion on fills/cancels/rejects. <paramref name="lastQty"/>
    /// is the per-ER fill quantity (0 for non-fill ERs).
    /// </summary>
    void OnExecution(ulong clOrdId, ExecKind kind, long lastQty) { }

    /// <summary>
    /// Synchronous release used by the order-submit path when the
    /// downstream gateway throws after a successful reservation.
    /// Default: no-op.
    /// </summary>
    void ReleaseReservation(ulong clOrdId) { }
}

public sealed class NoOpMarginProvider : IMarginProvider
{
    public Task<RiskDecision> TryReserveAsync(ulong clOrdId, RiskContext ctx, CancellationToken ct) =>
        Task.FromResult(RiskDecision.Approve);
}
