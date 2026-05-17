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
    /// <summary>
    /// Pass-5 review (#296) P1. FIFO cap on the per-parent set of
    /// recently engine-cancelled child clOrdIds. Bounds the late-ER
    /// dedup memory; the venue is not expected to deliver ERs for
    /// children older than this many repeg cycles back. Tuned at 32
    /// (~32 repegs ≈ several seconds of drift on a 100 ms throttle).
    /// </summary>
    public const int CancelledHistoryCap = 32;

    private readonly ConcurrentDictionary<(string FirmId, ulong AlgoId), PeggedRepegPending> _entries = new();
    private readonly ConcurrentDictionary<(string FirmId, ulong AlgoId), CancelledChildRing> _history = new();

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

    /// <summary>
    /// Pass-5 review (#296) P1. Drop both the pending entry AND the
    /// cancelled-child history for a parent — used on terminal so the
    /// book stays bounded.
    /// </summary>
    public void RemoveAll(string firmId, ulong algoId)
    {
        var key = (firmId, algoId);
        _entries.TryRemove(key, out _);
        _history.TryRemove(key, out _);
    }

    /// <summary>
    /// Pass-5 review (#296) P1. Record that the engine has issued (or
    /// is about to issue) a cancel for <paramref name="childClOrdId"/>
    /// as part of a repeg cycle. Idempotent; the bounded FIFO trims
    /// the oldest entry when the cap is exceeded. Source of truth for
    /// the late-ER dedup branches in <c>AlgoEngine.OnChildErAsync</c>:
    /// a terminal ER whose child id is in this set is "expected
    /// terminal for an engine-initiated cancel" rather than a
    /// venue-cancel surprise — so the parent is NOT flipped to
    /// Suspended/VenueCancelled even when the ER arrives after the
    /// cycle has already been resolved or after a subsequent repeg
    /// has rotated the single-slot active-cycle marker.
    /// </summary>
    public void MarkCancelledChild(string firmId, ulong algoId, ulong childClOrdId)
    {
        var ring = _history.GetOrAdd((firmId, algoId), static _ => new CancelledChildRing(CancelledHistoryCap));
        ring.Add(childClOrdId);
    }

    /// <summary>
    /// Pass-5 review (#296) P1. Returns true when the child id was
    /// recorded by an earlier <see cref="MarkCancelledChild"/> call
    /// (and has not been trimmed out by the FIFO cap or dropped on
    /// parent terminal via <see cref="RemoveAll"/>).
    /// </summary>
    public bool IsCancelledChild(string firmId, ulong algoId, ulong childClOrdId)
    {
        return _history.TryGetValue((firmId, algoId), out var ring) && ring.Contains(childClOrdId);
    }

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

    /// <summary>
    /// Pass-5 review (#296) P1. Snapshot the cancelled-child history
    /// rings so a restart between repeg cycles preserves the late-ER
    /// dedup protection.
    /// </summary>
    public IEnumerable<(string FirmId, ulong AlgoId, IReadOnlyList<ulong> ChildClOrdIds)> SnapshotHistory()
    {
        foreach (var kv in _history)
            yield return (kv.Key.FirmId, kv.Key.AlgoId, kv.Value.Snapshot());
    }

    /// <summary>
    /// Pass-5 review (#296) P1. Restore cancelled-child history rings
    /// from a snapshot. Order within each row is FIFO oldest→newest so
    /// the cap-eviction order survives the round-trip.
    /// </summary>
    public void RestoreHistory(IEnumerable<(string FirmId, ulong AlgoId, IReadOnlyList<ulong> ChildClOrdIds)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _history.Clear();
        foreach (var r in rows)
        {
            var ring = new CancelledChildRing(CancelledHistoryCap);
            foreach (var id in r.ChildClOrdIds) ring.Add(id);
            _history[(r.FirmId, r.AlgoId)] = ring;
        }
    }
}

/// <summary>
/// Pass-5 review (#296) P1. Bounded FIFO set of cancelled-child
/// clOrdIds backing <see cref="PeggedRepegBook.MarkCancelledChild"/>.
/// Thread-safe (engine calls happen on the consumer task, snapshot
/// captures under the dispatcher lock, but a defensive lock keeps
/// readers from observing a torn state during a concurrent Add).
/// </summary>
internal sealed class CancelledChildRing
{
    private readonly int _cap;
    private readonly Queue<ulong> _order;
    private readonly HashSet<ulong> _set;
    private readonly object _lock = new();

    public CancelledChildRing(int cap)
    {
        _cap = cap;
        _order = new Queue<ulong>(cap);
        _set = new HashSet<ulong>(cap);
    }

    public void Add(ulong id)
    {
        lock (_lock)
        {
            if (!_set.Add(id)) return;
            _order.Enqueue(id);
            while (_order.Count > _cap)
            {
                var evicted = _order.Dequeue();
                _set.Remove(evicted);
            }
        }
    }

    public bool Contains(ulong id)
    {
        lock (_lock) return _set.Contains(id);
    }

    public IReadOnlyList<ulong> Snapshot()
    {
        lock (_lock) return _order.ToArray();
    }
}

public readonly record struct PeggedRepegPending(
    ulong CancelledChildClOrdId,
    decimal TargetPrice,
    DateTimeOffset AtUtc);
