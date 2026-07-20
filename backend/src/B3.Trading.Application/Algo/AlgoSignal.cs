namespace B3.Trading.Application;

/// <summary>
/// Discriminated union of signals fed into the <see cref="AlgoEngine"/>'s
/// reactive consumer. Each signal carries the minimum identity the engine
/// needs to reload state from the books — full domain objects are not
/// captured to avoid pinning stale references when a parent or child is
/// mutated between enqueue and dequeue.
/// </summary>
public abstract record AlgoSignal
{
    /// <summary>Firm scope; required because the <see cref="AlgoBook"/> is keyed by <c>(firmId, algoId)</c>.</summary>
    public required string FirmId { get; init; }
}

/// <summary>
/// A new <see cref="Domain.Algo"/> parent has been persisted by the API
/// (or rehydrated from snapshot/WAL during boot reconciliation) and may
/// require the engine to submit its first child slice.
/// </summary>
public sealed record AlgoCreatedSignal : AlgoSignal
{
    public required ulong AlgoId { get; init; }
}

/// <summary>
/// An operator has called <c>DELETE /algo/{id}</c>. The parent is
/// already in <c>Cancelling</c>; the engine must cancel any live child
/// and then mark the parent terminal.
/// </summary>
public sealed record AlgoCancelRequestedSignal : AlgoSignal
{
    public required ulong AlgoId { get; init; }
}

/// <summary>
/// An execution report was just applied to a child order linked to an
/// algo parent. Enqueued by <c>ExecutionReportProcessor</c> AFTER the
/// dispatcher returns, so the engine consumer never holds the dispatcher
/// lock when reacting (RFC §4.3 thread boundary).
/// </summary>
public sealed record ChildExecutionObservedSignal : AlgoSignal
{
    public required ulong AlgoId { get; init; }
    public required ulong ChildClOrdId { get; init; }
}

/// <summary>
/// Q3.5 (#285). An operator (or, in the future, an in-engine
/// scheduler) requested a cancel-replace of a live child of the
/// algo. The signal carries the requested overrides only — the
/// engine resolves the actual target child by the parent's
/// <c>LiveChildClOrdId</c> when <see cref="TargetChildClOrdId"/>
/// is null, validates terminal/qty invariants on the consumer
/// thread, and dispatches through the durable order-modify service.
/// At least one of <see cref="NewQuantity"/> / <see cref="NewPrice"/>
/// must be set; the engine inherits the omitted side from the
/// live child.
/// </summary>
public sealed record AlgoModifyRequestedSignal : AlgoSignal
{
    public required ulong AlgoId { get; init; }
    public ulong? TargetChildClOrdId { get; init; }
    public long? NewQuantity { get; init; }
    public decimal? NewPrice { get; init; }

    /// <summary>
    /// Human-readable driver — surfaces as a metric tag and as the
    /// <c>reason</c> field on <see cref="Persistence.AlgoChildModifiedEvent"/>.
    /// Use short lowercase identifiers (<c>operator</c>, <c>pegged_repeg</c>).
    /// </summary>
    public required string Reason { get; init; }
}
