using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Fan-out abstraction for <see cref="Algo"/> aggregate updates. Mirrors
/// <see cref="IExecutionEventSink"/>: the application layer never knows
/// who is listening (WebSocket fan-out, audit recorder, future
/// engine-to-engine replication). Implementations resolve the live
/// aggregate from <see cref="AlgoBook"/> at publish time so the wire
/// snapshot is always current — callers do not need to assemble a DTO.
/// </summary>
public interface IAlgoEventSink
{
    /// <summary>
    /// Publishes the current state of algo <paramref name="algoId"/> on
    /// the firm-scoped feed for <paramref name="owner"/>. Silently no-op
    /// if the algo is not in the book (either replay-time before
    /// registration or already pruned).
    /// </summary>
    void PublishAlgoSnapshot(EndClientId owner, string firmId, ulong algoId);
}

public sealed class NoOpAlgoEventSink : IAlgoEventSink
{
    public void PublishAlgoSnapshot(EndClientId owner, string firmId, ulong algoId) { }
}
