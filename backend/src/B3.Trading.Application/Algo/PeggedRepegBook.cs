using System.Collections.Concurrent;

namespace B3.Trading.Application;

/// <summary>
/// Pass-1 review (#296) P1-C — Pegged repeg-cycle restart resilience.
///
/// <para>
/// Per-Pegged-parent record of an in-flight repeg cycle: which child
/// the engine cancelled, what target the replacement is being placed
/// at, and when. Populated by:
/// <list type="bullet">
///   <item>engine <see cref="AlgoEngine"/>
///         <c>EvaluatePeggedRepegAsync</c> via the
///         <see cref="Persistence.AlgoPeggedRepegStartedEvent"/>
///         dispatch action (steady-state path), and</item>
///   <item>WAL replay in
///         <see cref="B3.Trading.Infrastructure.Persistence.EventReplayer"/>
///         on the same event (recovery path).</item>
/// </list>
/// Cleared by <see cref="Persistence.AlgoPeggedRepegResolvedEvent"/>
/// once the engine has consumed the cancel-ack and submitted the
/// replacement, and by terminal transitions
/// (<see cref="AlgoEngine.RecordTerminalAsync"/>).
/// </para>
///
/// <para>
/// <b>Why this is not part of the <see cref="Algo"/> aggregate.</b>
/// The pending-repeg record is engine-internal scheduling state, not
/// part of the parent's business identity (mirrors the rationale on
/// <see cref="PovProgressBook"/>). Keeping it in a side book lets the
/// persistence shape evolve independently of
/// <see cref="Persistence.AlgoCreatedEvent"/> /
/// <see cref="Persistence.AlgoSnapshot"/>.
/// </para>
/// </summary>
public sealed class PeggedRepegBook
{
    private readonly ConcurrentDictionary<(string FirmId, ulong AlgoId), PeggedRepegPending> _entries = new();

    public PeggedRepegPending? TryGet(string firmId, ulong algoId) =>
        _entries.TryGetValue((firmId, algoId), out var e) ? e : null;

    /// <summary>
    /// Last-write-wins; a new cycle overwrites any prior pending
    /// entry. Engine guarantees only one cycle is in-flight per
    /// parent (the <c>RepegPending</c> throttle blocks).
    /// </summary>
    public void Set(string firmId, ulong algoId, ulong cancelledChildClOrdId, decimal targetPrice, DateTimeOffset atUtc) =>
        _entries[(firmId, algoId)] = new PeggedRepegPending(cancelledChildClOrdId, targetPrice, atUtc);

    public bool Remove(string firmId, ulong algoId) =>
        _entries.TryRemove((firmId, algoId), out _);

    public IEnumerable<(string FirmId, ulong AlgoId, PeggedRepegPending Pending)> Snapshot()
    {
        foreach (var kv in _entries)
            yield return (kv.Key.FirmId, kv.Key.AlgoId, kv.Value);
    }

    public void Restore(IEnumerable<(string FirmId, ulong AlgoId, PeggedRepegPending Pending)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _entries.Clear();
        foreach (var r in rows)
            _entries[(r.FirmId, r.AlgoId)] = r.Pending;
    }
}

public readonly record struct PeggedRepegPending(
    ulong CancelledChildClOrdId,
    decimal TargetPrice,
    DateTimeOffset AtUtc);
