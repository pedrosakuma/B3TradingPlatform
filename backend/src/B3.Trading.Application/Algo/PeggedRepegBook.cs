using System.Collections.Concurrent;
using B3.Trading.Application.Observability;
using Microsoft.Extensions.Logging;

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
    /// Pass-5 review (#296) P1, revised pass-6 (#296) P2. FIFO cap on
    /// the per-parent set of recently engine-cancelled child clOrdIds.
    /// Bounds the late-ER dedup memory.
    ///
    /// <para>
    /// <b>Cap vs venue-tail-latency trade-off.</b> The Pegged scheduler
    /// runs on a 100 ms cadence, so the cap × 100 ms is roughly the
    /// upper bound on how long after engine-issued cancel a venue may
    /// still deliver a terminal ER for the cancelled child and have it
    /// be recognised as "expected terminal" (vs. falling through to
    /// the <c>VenueCancelled</c> branch and suspending the parent).
    /// 32 (the pass-5 value) only covered ~3.2 s of tail; 256 covers
    /// ~25 s which comfortably absorbs the typical venue-side ER tail
    /// while staying bounded in memory (a per-parent
    /// <see cref="HashSet{T}"/> + <see cref="Queue{T}"/> of ulongs).
    /// Overflow beyond this window is observable via
    /// <see cref="MetricsRegistry.AlgoPeggedRepegDedupRingEvicted"/>
    /// (counter) and a one-shot warn log per parent — operators
    /// should treat sustained eviction as a signal the cap needs
    /// bumping further.
    /// </para>
    /// </summary>
    public const int CancelledHistoryCap = 256;

    private readonly ILogger<PeggedRepegBook>? _logger;
    private readonly ConcurrentDictionary<(string FirmId, ulong AlgoId), PeggedRepegPending> _entries = new();
    private readonly ConcurrentDictionary<(string FirmId, ulong AlgoId), CancelledChildRing> _history = new();

    public PeggedRepegBook(ILogger<PeggedRepegBook>? logger = null)
    {
        _logger = logger;
    }

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
        if (ring.Add(childClOrdId))
        {
            // Pass-6 review (#296) P2. Ring eviction is silent w.r.t.
            // the engine: the oldest child id falls out and any
            // subsequent late terminal ER for it will no longer
            // dedup (parent gets suspended via VenueCancelled). Make
            // it observable with a counter, and emit a single warn
            // per parent to surface the situation without spamming
            // logs once we're past the cap.
            MetricsRegistry.AlgoPeggedRepegDedupRingEvicted.Add(1);
            if (!ring.MarkEvictionLogged())
            {
                _logger?.LogWarning(
                    "PeggedRepegBook dedup ring overflow on firm {FirmId} algo {AlgoId}: oldest cancelled-child id evicted from {Cap}-entry FIFO. Late terminal ERs for evicted children will fall through to VenueCancelled; consider raising CancelledHistoryCap if this is sustained.",
                    firmId,
                    algoId,
                    CancelledHistoryCap);
            }
        }
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
    /// Pass-5 review (#296) P1, revised pass-7 (#296) P2. Snapshot the
    /// cancelled-child history rings so a restart between repeg cycles
    /// preserves the late-ER dedup protection. Also captures the
    /// per-ring "one-shot eviction warn already emitted" latch so a
    /// parent that has already warned does NOT warn again post-restart.
    /// </summary>
    public IEnumerable<(string FirmId, ulong AlgoId, IReadOnlyList<ulong> ChildClOrdIds, bool EvictionLogged)> SnapshotHistory()
    {
        foreach (var kv in _history)
        {
            var (ids, logged) = kv.Value.SnapshotWithLatch();
            yield return (kv.Key.FirmId, kv.Key.AlgoId, ids, logged);
        }
    }

    /// <summary>
    /// Pass-5 review (#296) P1, revised pass-7 (#296) P2. Restore
    /// cancelled-child history rings from a snapshot. Order within each
    /// row is FIFO oldest→newest so the cap-eviction order survives the
    /// round-trip. <paramref name="rows"/>' <c>EvictionLogged</c> flag
    /// rehydrates the one-shot warn latch so a parent that had already
    /// warned pre-restart stays silent post-restart.
    /// </summary>
    public void RestoreHistory(IEnumerable<(string FirmId, ulong AlgoId, IReadOnlyList<ulong> ChildClOrdIds, bool EvictionLogged)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _history.Clear();
        foreach (var r in rows)
        {
            var ring = new CancelledChildRing(CancelledHistoryCap);
            foreach (var id in r.ChildClOrdIds) ring.Add(id);
            if (r.EvictionLogged) ring.MarkEvictionLogged();
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
    private bool _evictionLogged;

    public CancelledChildRing(int cap)
    {
        _cap = cap;
        _order = new Queue<ulong>(cap);
        _set = new HashSet<ulong>(cap);
    }

    /// <summary>
    /// Adds <paramref name="id"/> to the FIFO. Returns <c>true</c> iff
    /// the insert caused at least one older entry to be evicted to
    /// honour the cap (so callers can surface eviction via metrics /
    /// logs). Duplicates and no-op inserts return <c>false</c>.
    /// </summary>
    public bool Add(ulong id)
    {
        lock (_lock)
        {
            if (!_set.Add(id)) return false;
            _order.Enqueue(id);
            var evicted = false;
            while (_order.Count > _cap)
            {
                var dropped = _order.Dequeue();
                _set.Remove(dropped);
                evicted = true;
            }
            return evicted;
        }
    }

    /// <summary>
    /// Atomically flips the per-ring "we've already warn-logged about
    /// eviction on this parent" flag. Returns the PREVIOUS value:
    /// <c>false</c> on the first call (caller should log),
    /// <c>true</c> on every subsequent call (caller should suppress).
    /// </summary>
    public bool MarkEvictionLogged()
    {
        lock (_lock)
        {
            var prev = _evictionLogged;
            _evictionLogged = true;
            return prev;
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

    /// <summary>
    /// Pass-7 review (#296) P2. Atomic snapshot of the FIFO contents
    /// AND the per-ring one-shot eviction-warn latch, so a restart
    /// preserves both the dedup memory and the "we've already warned
    /// for this parent" suppression.
    /// </summary>
    public (IReadOnlyList<ulong> Ids, bool EvictionLogged) SnapshotWithLatch()
    {
        lock (_lock) return (_order.ToArray(), _evictionLogged);
    }
}

public readonly record struct PeggedRepegPending(
    ulong CancelledChildClOrdId,
    decimal TargetPrice,
    DateTimeOffset AtUtc);
