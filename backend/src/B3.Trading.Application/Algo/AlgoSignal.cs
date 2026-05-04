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
