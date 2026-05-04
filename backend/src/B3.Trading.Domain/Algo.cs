namespace B3.Trading.Domain;

public enum AlgoType
{
    Iceberg,
    Twap,
}

/// <summary>
/// Lifecycle of an <see cref="Algo"/> parent. The state machine is enforced
/// by <c>Algo</c>'s mutators; transitions outside the allowed graph throw.
/// See RFC docs/rfcs/algo-orders-v0.md §4.4.
/// </summary>
public enum AlgoStatus
{
    PendingNew,
    Working,
    Cancelling,
    Cancelled,
    Suspended,
    Expired,
    Completed,
}

/// <summary>
/// Durable companion to terminal/suspended states. Persisting the reason
/// matters because the same <see cref="AlgoStatus.Cancelled"/> outcome
/// means very different things to a UI/operator depending on whether the
/// parent had partially filled. See RFC §4.1.
/// </summary>
public enum AlgoTerminalReason
{
    None = 0,
    UserCancelled,
    RiskRejected,
    GatewayUnavailable,
    VenueCancelled,
    TwapWindowExpired,
    RetriesExhausted,
    Drained,
}

/// <summary>
/// Per-type parameters carried by an <see cref="Algo"/>. Sealed-class-per-type
/// keeps the discriminated shape explicit on the wire and at runtime; the
/// abstract base lets consumers pattern-match without losing type safety.
/// </summary>
public abstract record AlgoParameters;

public sealed record IcebergParameters(long DisplayQuantity, decimal? LimitPrice) : AlgoParameters;

public sealed record TwapParameters(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int SliceCount,
    OrderType ChildOrderType,
    decimal? ChildPrice) : AlgoParameters;

/// <summary>
/// Parent algo aggregate, sibling to <see cref="Order"/>. v0 stores only
/// what the engine needs for state-machine decisions; child progress is
/// derived from the order/ER stream during replay (RFC §4.5). The aggregate
/// is mutated only by the algo engine under the per-parent lock described
/// in RFC §4.3 — concurrent mutation from multiple threads is undefined.
/// </summary>
public sealed class Algo
{
    public Algo(
        ulong algoId,
        EndClientId owner,
        string firmId,
        string symbol,
        ulong securityId,
        OrderSide side,
        AlgoType type,
        long totalQuantity,
        AlgoParameters parameters,
        DateTimeOffset createdAtUtc)
    {
        if (algoId == 0)
            throw new ArgumentOutOfRangeException(nameof(algoId), "AlgoId cannot be zero (reserved as null sentinel).");
        if (string.IsNullOrWhiteSpace(firmId))
            throw new ArgumentException("FirmId required.", nameof(firmId));
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol required.", nameof(symbol));
        if (totalQuantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalQuantity));
        ArgumentNullException.ThrowIfNull(parameters);
        ValidateParametersMatchType(type, parameters);

        AlgoId = algoId;
        Owner = owner;
        FirmId = firmId;
        Symbol = symbol;
        SecurityId = securityId;
        Side = side;
        Type = type;
        TotalQuantity = totalQuantity;
        Parameters = parameters;
        CreatedAtUtc = createdAtUtc;
        Status = AlgoStatus.PendingNew;
        TerminalReason = AlgoTerminalReason.None;
    }

    public ulong AlgoId { get; }
    public EndClientId Owner { get; }
    public string FirmId { get; }
    public string Symbol { get; }
    public ulong SecurityId { get; }
    public OrderSide Side { get; }
    public AlgoType Type { get; }
    public long TotalQuantity { get; }
    public AlgoParameters Parameters { get; }
    public DateTimeOffset CreatedAtUtc { get; }

    public long FilledQuantity { get; private set; }
    public AlgoStatus Status { get; private set; }
    public AlgoTerminalReason TerminalReason { get; private set; }
    public DateTimeOffset? TerminalAtUtc { get; private set; }

    public long RemainingQuantity => Math.Max(0, TotalQuantity - FilledQuantity);

    public bool IsTerminal => Status is AlgoStatus.Cancelled or AlgoStatus.Completed
        or AlgoStatus.Expired or AlgoStatus.Suspended;

    /// <summary>
    /// Engine-driven transition out of <see cref="AlgoStatus.PendingNew"/>
    /// once the first child has been accepted by the submit pipeline. No-op
    /// if the algo has already advanced (idempotent under replay).
    /// </summary>
    public void MarkWorking()
    {
        if (Status == AlgoStatus.PendingNew)
            Status = AlgoStatus.Working;
    }

    /// <summary>
    /// Records a fill against the parent. Idempotent only at the slice
    /// level (slice 6 onwards); v0 callers must apply each fill exactly
    /// once. Overfill is permitted (mirrors <see cref="Order"/>) so replay
    /// of a venue-misbehaving stream stays total.
    /// </summary>
    public void RecordFill(long fillQty)
    {
        if (fillQty <= 0)
            throw new ArgumentOutOfRangeException(nameof(fillQty));
        FilledQuantity += fillQty;
    }

    /// <summary>
    /// Operator cancel request. Allowed from any non-terminal state; the
    /// engine arms outstanding child cancels and the parent moves to
    /// <see cref="AlgoStatus.Cancelling"/>. From <see cref="AlgoStatus.Cancelling"/>
    /// the call is a no-op (the operator can spam DELETE without harm).
    /// </summary>
    public void RequestCancel()
    {
        if (IsTerminal || Status == AlgoStatus.Cancelling)
            return;
        Status = AlgoStatus.Cancelling;
    }

    /// <summary>
    /// Records a terminal outcome. Caller is responsible for ensuring the
    /// reason matches the status (e.g. <see cref="AlgoTerminalReason.TwapWindowExpired"/>
    /// only with <see cref="AlgoStatus.Expired"/>); the aggregate validates
    /// the status is terminal but trusts the supplied reason. Idempotent
    /// when called with the same status (replay-safe).
    /// </summary>
    public void RecordTerminal(AlgoStatus status, AlgoTerminalReason reason, DateTimeOffset atUtc)
    {
        if (status is not (AlgoStatus.Cancelled or AlgoStatus.Completed
            or AlgoStatus.Expired or AlgoStatus.Suspended))
        {
            throw new ArgumentOutOfRangeException(nameof(status), $"Not a terminal status: {status}");
        }
        if (IsTerminal && Status == status)
            return;
        if (IsTerminal && Status != status)
            throw new InvalidOperationException($"Algo {AlgoId} already terminal in {Status}; cannot move to {status}.");

        Status = status;
        TerminalReason = reason;
        TerminalAtUtc = atUtc;
    }

    /// <summary>
    /// Engine-only hook used at boot reconciliation: fills are NOT journaled
    /// for algo parents (RFC §4.5 — derived from the child stream), so when
    /// the platform restarts from WAL alone the parent comes back with
    /// <see cref="FilledQuantity"/> = 0 even though the child orders carry
    /// the real cumulative quantity. The engine sums child cums and calls
    /// this method once during reconciliation so subsequent reactor passes
    /// see the true remaining quantity. Refuses to move the value backwards
    /// (snapshot-restored parents already carry the higher truth).
    /// </summary>
    public void RehydrateProgress(long filledQuantity)
    {
        if (filledQuantity < 0)
            throw new ArgumentOutOfRangeException(nameof(filledQuantity));
        if (filledQuantity > FilledQuantity)
            FilledQuantity = filledQuantity;
    }

    /// <summary>
    /// Reconstructs an algo from snapshot data. Bypasses the state-machine
    /// invariants because the snapshot was, by construction, produced from
    /// a sequence of valid mutations. Mirrors <see cref="Order.Hydrate"/>.
    /// </summary>
    internal static Algo Hydrate(
        ulong algoId, EndClientId owner, string firmId, string symbol, ulong securityId,
        OrderSide side, AlgoType type, long totalQuantity, AlgoParameters parameters,
        DateTimeOffset createdAtUtc, long filledQuantity, AlgoStatus status,
        AlgoTerminalReason terminalReason, DateTimeOffset? terminalAtUtc)
    {
        var a = new Algo(algoId, owner, firmId, symbol, securityId, side, type,
            totalQuantity, parameters, createdAtUtc);
        a.FilledQuantity = filledQuantity;
        a.Status = status;
        a.TerminalReason = terminalReason;
        a.TerminalAtUtc = terminalAtUtc;
        return a;
    }

    private static void ValidateParametersMatchType(AlgoType type, AlgoParameters parameters)
    {
        var ok = type switch
        {
            AlgoType.Iceberg => parameters is IcebergParameters,
            AlgoType.Twap => parameters is TwapParameters,
            _ => false,
        };
        if (!ok)
            throw new ArgumentException($"Parameters {parameters.GetType().Name} do not match algo type {type}.", nameof(parameters));
    }
}
