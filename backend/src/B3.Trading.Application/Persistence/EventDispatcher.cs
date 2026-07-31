using System.Text.Json;

namespace B3.Trading.Application.Persistence;

/// <summary>
/// Serialises append-then-mutate critical sections so snapshots see a
/// consistent view of (event log seq) + (in-memory state).
///
/// <para>
/// <b>Lock scope (RFC §5.1, F1).</b> JSON serialisation of the WAL
/// payload happens <i>outside</i> the dispatcher lock via the
/// source-generated <see cref="WalEventJsonContext"/>. The lock then
/// covers exactly:
/// <list type="number">
///   <item>seq assignment + bounded-channel enqueue (inside
///   <c>IEventStore.Append(evt, payload)</c>);</item>
///   <item>the in-memory <c>apply()</c> mutation.</item>
/// </list>
/// Total WAL ordering (RFC §4.1) is preserved by design: no event can
/// observe another's <c>apply()</c> side effects without also having a
/// strictly greater seq, because both still happen under one lock per
/// dispatch. The pre-serialised payload is just bytes — it has no seq
/// and no observable effect until <c>Append</c> hands it to the channel.
/// </para>
///
/// <para>
/// <see cref="WithSnapshotLock"/> is for the snapshot service: it reads
/// <c>CurrentSeq</c> + captures state inside the same lock, so the
/// snapshot's <c>seq</c> is guaranteed to bracket only events whose
/// in-memory mutations are already reflected in the captured state. On
/// recovery, replaying events with <c>seq &gt; snapshot.seq</c> is then
/// safe — there is no double-apply risk.
/// </para>
///
/// <para>
/// The lock is process-global (one platform instance per firm pool).
/// Ordinary dispatch holds it only for synchronous in-memory work. The
/// inbound-evidence <see cref="DispatchCommitted(WalEvent, Action, CancellationToken)"/>
/// path deliberately also holds it across the marker-commit wait so a
/// snapshot cannot overtake venue evidence.
/// </para>
/// </summary>
public sealed class EventDispatcher
{
    private readonly IEventStore _store;
    private readonly object _lock = new();
    private readonly IExecutionFanOutSink[] _erFanOutSinks;
    private readonly HashSet<long> _completedAppliedSeqs = new();
    private long _lastAppliedSeq;

    public EventDispatcher(IEventStore store)
        : this(store, fanOutSinks: null)
    {
    }

    /// <summary>
    /// Constructor used by DI: the host registers per-sink fan-out
    /// targets (WS hub channel sink, bot router) and they are
    /// snapshotted into a flat array at construction time so the
    /// dispatch hot path is a tight indexed loop with no enumerator
    /// allocation. Tests that don't care about fan-out use the
    /// single-arg overload.
    /// </summary>
    public EventDispatcher(IEventStore store, System.Collections.Generic.IEnumerable<IExecutionFanOutSink>? fanOutSinks)
    {
        _store = store;
        _lastAppliedSeq = store.LastCommittedSeq;
        _erFanOutSinks = fanOutSinks is null
            ? System.Array.Empty<IExecutionFanOutSink>()
            : System.Linq.Enumerable.ToArray(fanOutSinks);
    }

    public long CurrentSeq
    {
        get { lock (_lock) return _store.CurrentSeq; }
    }

    public long LastAppliedSeq
    {
        get { lock (_lock) return _lastAppliedSeq; }
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
        _store.FlushAsync(cancellationToken);

    /// <summary>
    /// Awaits proof that <paramref name="seq"/> and every earlier WAL record
    /// are marker-committed. Intended as the commit-before-side-effect fence
    /// for ordered Class O coordinators.
    /// </summary>
    public ValueTask FlushThroughAsync(long seq, CancellationToken cancellationToken = default) =>
        _store.FlushThroughAsync(seq, cancellationToken);

    /// <summary>
    /// Persists <paramref name="evt"/> then runs <paramref name="apply"/>
    /// under the same lock. Throws (and skips the mutation) if the WAL
    /// rejects the append — the caller is expected to surface a
    /// structured failure to its own client.
    /// </summary>
    public long Dispatch(WalEvent evt, Action apply)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(apply);
        // Pre-serialise OUTSIDE the lock (RFC §5.1). Reflection-free via
        // the source-gen context. The bytes carry no seq and have no
        // observable effect until Append enqueues them under the lock.
        var payload = JsonSerializer.SerializeToUtf8Bytes(evt, WalEventJsonContext.Default.WalEvent);
        lock (_lock)
        {
            var seq = _store.Append(evt, payload);
            apply();
            AdvanceApplied(seq);
            return seq;
        }
    }

    /// <summary>
    /// Atomically re-validates a live-state predicate, appends the event, and
    /// applies its mutation under the dispatcher lock. A false predicate
    /// performs neither append nor apply.
    /// </summary>
    public DispatchOutcome DispatchIf(WalEvent evt, Func<bool> condition, Action apply)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(apply);
        var payload = JsonSerializer.SerializeToUtf8Bytes(evt, WalEventJsonContext.Default.WalEvent);
        lock (_lock)
        {
            if (!condition())
                return new DispatchOutcome(false, 0);
            var seq = _store.Append(evt, payload);
            apply();
            AdvanceApplied(seq);
            return new DispatchOutcome(true, seq);
        }
    }

    /// <summary>
    /// Commit-fenced counterpart to <see cref="DispatchIf"/> for outbound
    /// attempt transitions that must remain conditional without entering the
    /// gateway before the WAL marker is durable.
    /// </summary>
    public DispatchOutcome DispatchCommittedIf(
        WalEvent evt,
        Func<bool> condition,
        Action apply,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(apply);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            evt,
            WalEventJsonContext.Default.WalEvent);
        lock (_lock)
        {
            if (!condition())
                return new DispatchOutcome(false, 0);
            var seq = _store.Append(evt, payload);
            _store.FlushThroughAsync(seq, cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            apply();
            AdvanceApplied(seq);
            return new DispatchOutcome(true, seq);
        }
    }

    /// <summary>
    /// RFC §5.2 (F2). Outcome-capture overload. <paramref name="applyAndCapture"/>
    /// runs under the dispatcher lock — same as the legacy <see cref="Dispatch(WalEvent, Action)"/> —
    /// but instead of synchronously fanning out to subscribers it adds
    /// the resulting <see cref="ExecutionEvent"/>(s) to the supplied
    /// <see cref="ExecutionFanOut"/> writer. The dispatcher then
    /// enqueues each captured event onto every registered
    /// <see cref="IExecutionFanOutSink"/> WHILE STILL HOLDING THE LOCK
    /// so per-sink drain order matches WAL seq order by construction
    /// (RFC §4.1, §5.2).
    ///
    /// <para>
    /// Each sink's <see cref="IExecutionFanOutSink.Enqueue"/> is required
    /// to be non-blocking (typically a <c>Channel.TryWrite</c>); the
    /// expensive publish work — subscriber dictionary walks, DTO
    /// allocation, framing — runs on the sink's drain thread, OUTSIDE
    /// this lock. That is the entire point of F2.
    /// </para>
    ///
    /// <para>
    /// Total order (RFC §4.1) is preserved by design: <c>Append</c> +
    /// <c>applyAndCapture</c> + per-sink TryWrites all happen under the
    /// same lock per dispatch, so a thread holding seq N+1 cannot write
    /// to any sink before the thread holding seq N has done so.
    /// Snapshot consistency (RFC §4.3) is preserved because the
    /// dispatcher lock is the same one
    /// <see cref="WithSnapshotLock"/> takes — fan-out is a read-only
    /// projection of state already mutated under the lock.
    /// </para>
    /// </summary>
    public long Dispatch(WalEvent evt, Action<ExecutionFanOut> applyAndCapture)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(applyAndCapture);
        var payload = JsonSerializer.SerializeToUtf8Bytes(evt, WalEventJsonContext.Default.WalEvent);
        var fanOut = ExecutionFanOut.Rent();
        long seq;
        try
        {
            lock (_lock)
            {
                seq = _store.Append(evt, payload);
                applyAndCapture(fanOut);
                AdvanceApplied(seq);

                // Per-sink channel writes UNDER the lock. Empty fast-paths
                // are common (e.g. a successful but ER-less WAL event).
                var sinks = _erFanOutSinks;
                var captured = fanOut.Count;
                if (sinks.Length > 0 && captured > 0)
                {
                    for (var i = 0; i < captured; i++)
                    {
                        var entry = fanOut[i];
                        for (var s = 0; s < sinks.Length; s++)
                        {
                            var sink = sinks[s];
                            if ((entry.Targets & sink.Target) != 0)
                                sink.Enqueue(seq, entry.Event);
                        }
                    }
                }
            }
            return seq;
        }
        finally
        {
            fanOut.Return();
        }
    }

    /// <summary>
    /// Class V/L inbound-evidence boundary: append, marker-commit the complete
    /// WAL prefix, then apply ledger/domain state in that order. The dispatcher
    /// lock remains held across the durability wait so no later event or
    /// snapshot can observe a projection beyond the committed evidence.
    /// </summary>
    public long DispatchCommitted(
        WalEvent evt,
        Action<ExecutionFanOut> applyAndCapture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(applyAndCapture);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            evt,
            WalEventJsonContext.Default.WalEvent);
        var fanOut = ExecutionFanOut.Rent();
        try
        {
            lock (_lock)
            {
                var seq = _store.Append(evt, payload);
                _store.FlushThroughAsync(seq, cancellationToken)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                applyAndCapture(fanOut);
                AdvanceApplied(seq);

                var sinks = _erFanOutSinks;
                for (var i = 0; i < fanOut.Count; i++)
                {
                    var entry = fanOut[i];
                    for (var s = 0; s < sinks.Length; s++)
                    {
                        var sink = sinks[s];
                        if ((entry.Targets & sink.Target) != 0)
                            sink.Enqueue(seq, entry.Event);
                    }
                }
                return seq;
            }
        }
        finally
        {
            fanOut.Return();
        }
    }

    public long DispatchCommitted(
        WalEvent evt,
        Action apply,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return DispatchCommitted(evt, _ => apply(), cancellationToken);
    }

    /// <summary>
    /// Q2.2 (#269) P1 fix. Atomic "validate + debit + append" primitive
    /// used by callers whose in-memory mutation MUST be visible to the
    /// snapshot service iff the WAL append also lands. Concretely: the
    /// cash withdrawal path must not let a snapshot interleave between
    /// the keeper debit and the WAL append, otherwise a snapshot would
    /// persist a reduced balance with no matching event in the WAL —
    /// permanent cash loss on restore.
    ///
    /// <para>
    /// Semantics, all under the dispatcher lock (the same lock
    /// <see cref="WithSnapshotLock"/> takes):
    /// <list type="number">
    ///   <item>invoke <paramref name="preApply"/>; if it returns
    ///   <c>false</c>, no append, no rollback — the caller surfaces
    ///   a structured business-rule failure (e.g. 422);</item>
    ///   <item>append <paramref name="evt"/> to the WAL;</item>
    ///   <item>if append throws, run <paramref name="rollback"/> WHILE
    ///   STILL HOLDING THE LOCK so a snapshot cannot observe the
    ///   transient debited-but-no-event state, then rethrow.</item>
    /// </list>
    /// JSON serialisation runs OUTSIDE the lock — same F1 narrowing as
    /// the regular <see cref="Dispatch(WalEvent, Action)"/> overloads.
    /// </para>
    /// </summary>
    public DispatchOutcome DispatchWithPreApply(WalEvent evt, Func<bool> preApply, Action rollback)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(preApply);
        ArgumentNullException.ThrowIfNull(rollback);
        var payload = JsonSerializer.SerializeToUtf8Bytes(evt, WalEventJsonContext.Default.WalEvent);
        lock (_lock)
        {
            if (!preApply())
                return new DispatchOutcome(false, 0);
            long seq;
            try
            {
                seq = _store.Append(evt, payload);
            }
            catch
            {
                rollback();
                throw;
            }
            AdvanceApplied(seq);
            return new DispatchOutcome(true, seq);
        }
    }

    /// <summary>
    /// #671/#753 (RFC PR 3, code-review addendum #3). Event-FACTORY
    /// variant of <see cref="DispatchWithPreApply(WalEvent, Func{bool}, Action)"/>
    /// for callers whose event payload cannot be safely resolved
    /// before the dispatcher lock is acquired — e.g. whole-account
    /// reset, whose absolute position payload must be derived from
    /// live keeper state (which symbols are currently non-flat) at the
    /// SAME linearization point as the guard re-check. Resolving that
    /// payload from a snapshot captured before this method is called
    /// would leave a TOCTOU window: a fill/fee/adjustment landing
    /// between the caller's pre-check and lock acquisition could
    /// mutate or introduce a symbol that never makes it into the
    /// persisted event, silently escaping the reset.
    ///
    /// <para>
    /// <paramref name="resolveAndPreApply"/> runs FIRST, under the
    /// lock: it MUST re-validate the guard predicate against live
    /// state and, if not blocked, resolve the event to persist —
    /// reading whatever live keeper state it needs at that exact
    /// instant — and return it together with the in-memory mutation
    /// (<c>Apply</c>) to run immediately after a successful append.
    /// Returning a <c>null</c> event means "blocked" (or "nothing to
    /// resolve") — same contract as a <c>false</c> return from the
    /// classic <c>preApply</c> overload — and skips both append and
    /// apply.
    /// </para>
    ///
    /// <para>
    /// <b>Contract: <paramref name="resolveAndPreApply"/> itself MUST
    /// be free of in-memory-projection side effects.</b> Unlike the
    /// classic overload's <c>preApply</c> — which mutates state BEFORE
    /// <c>Append</c>, and therefore needs a pre-append rollback on an
    /// Append failure — this factory only RESOLVES the event and
    /// returns a separate <c>Apply</c> delegate; all in-memory mutation
    /// is deferred until AFTER a successful, durable <c>Append</c>.
    /// Consequently an Append failure here (e.g. WAL backpressure)
    /// needs — and gets — NO rollback at all: nothing was mutated, so
    /// the exception simply propagates with every in-memory projection
    /// left byte-for-byte, logically unchanged (no flat rows
    /// materialised, no change-notification side effects, no
    /// keeper/ledger writes of any kind).
    /// </para>
    ///
    /// <para>
    /// <b>Code-review final finding.</b> <paramref name="rollbackOnApplyFailure"/>
    /// (optional) instead guards the ONE step that runs after the
    /// durable Append: if <c>apply()</c> itself throws — the event IS
    /// already persisted, but the in-memory mutation only partially
    /// completed — <paramref name="rollbackOnApplyFailure"/> is invoked
    /// as a best-effort restore of the pre-apply state before the
    /// exception is rethrown (the applied-sequence tracker is
    /// deliberately NOT advanced in that case, since the live
    /// projection does not yet fully reflect the persisted event; a
    /// future cold-start/snapshot+tail replay will still reconstruct
    /// the authoritative post-event state deterministically from the
    /// durable record). Under the individual keeper APIs this feature
    /// uses (simple absolute-overwrite dictionary sets with no I/O),
    /// <c>apply()</c> throwing is not expected in practice — this is a
    /// defense-in-depth safety net, not the primary correctness
    /// mechanism (that is Append succeeding or failing atomically).
    /// </para>
    ///
    /// <para>
    /// Unlike the pre-serialised RFC §5.1 (F1) fast path the other
    /// overloads use, JSON serialisation of the resolved event
    /// necessarily happens INSIDE the lock here, because the event's
    /// content is not knowable before the lock is acquired. Callers
    /// with a statically-known event should keep using the classic
    /// <see cref="DispatchWithPreApply(WalEvent, Func{bool}, Action)"/>
    /// overload — this one trades a small amount of in-lock
    /// serialisation cost for closing the resolve-time TOCTOU window,
    /// and is intended for low-frequency admin operations, not the hot
    /// order-submit path.
    /// </para>
    /// </summary>
    public DispatchOutcome DispatchWithPreApply<TEvent>(
        Func<(TEvent? Event, Action Apply)> resolveAndPreApply,
        Action? rollbackOnApplyFailure = null)
        where TEvent : WalEvent
    {
        ArgumentNullException.ThrowIfNull(resolveAndPreApply);
        lock (_lock)
        {
            var (evt, apply) = resolveAndPreApply();
            if (evt is null)
                return new DispatchOutcome(false, 0);
            ArgumentNullException.ThrowIfNull(apply);
            var payload = JsonSerializer.SerializeToUtf8Bytes<WalEvent>(evt, WalEventJsonContext.Default.WalEvent);

            // No in-memory projection has been touched yet — by the
            // contract documented above, resolveAndPreApply must be
            // read-only. An Append failure (e.g. WalBackpressureException)
            // therefore propagates directly with nothing to roll back;
            // there is no pre-append rollback path in this overload.
            var seq = _store.Append(evt, payload);

            try
            {
                apply();
            }
            catch
            {
                // apply() ran AFTER a successful, durable Append: the
                // event is persisted, but the in-memory mutation only
                // partially completed. Best-effort restore, then
                // rethrow — AdvanceApplied deliberately does not run.
                rollbackOnApplyFailure?.Invoke();
                throw;
            }

            AdvanceApplied(seq);
            return new DispatchOutcome(true, seq);
        }
    }

    /// <summary>
    /// Captures a raw snapshot and its WAL lineage at the highest contiguous
    /// sequence whose in-memory projection has completed. The callback runs
    /// under the dispatcher lock; projection and durability waits must run
    /// after this method returns.
    /// </summary>
    public T CaptureSnapshot<T>(Func<SnapshotCaptureContext, T> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        lock (_lock)
        {
            EnsureSnapshotCoverage();
            return capture(new SnapshotCaptureContext(
                _lastAppliedSeq,
                _store.WalGeneration));
        }
    }

    /// <summary>
    /// Captures a consistent <c>(seq, state)</c> view by running
    /// <paramref name="capture"/> under the dispatcher lock.
    /// </summary>
    public void WithSnapshotLock(Action<long> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        lock (_lock)
        {
            EnsureSnapshotCoverage();
            capture(_lastAppliedSeq);
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> under the dispatcher lock without an
    /// accompanying WAL append. Reserved for consistent read/capture work
    /// and recovery coordination; durable live mutations must use
    /// <see cref="Dispatch(WalEvent, Action)"/>.
    /// </summary>
    public void RunExclusive(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        lock (_lock)
        {
            body();
        }
    }

    private void AdvanceApplied(long seq)
    {
        if (seq <= _lastAppliedSeq || !_completedAppliedSeqs.Add(seq))
        {
            throw new InvalidOperationException(
                $"Dispatcher received duplicate or regressive applied sequence {seq} after {_lastAppliedSeq}.");
        }
        while (_lastAppliedSeq < long.MaxValue
               && _completedAppliedSeqs.Remove(_lastAppliedSeq + 1))
        {
            _lastAppliedSeq++;
        }
    }

    private void EnsureSnapshotCoverage()
    {
        var admitted = _store.LastAdmittedSeq;
        if (admitted != _lastAppliedSeq)
        {
            throw new InvalidOperationException(
                $"Snapshot capture refused because WAL admitted sequence {admitted} is not fully covered by the contiguous applied prefix {_lastAppliedSeq}.");
        }
    }
}

/// <summary>
/// Result of <see cref="EventDispatcher.DispatchWithPreApply"/>.
/// <c>Applied=false</c> means the pre-apply check returned false and no
/// WAL append was made; <c>Seq</c> is meaningful only when
/// <c>Applied=true</c>.
/// </summary>
public readonly record struct DispatchOutcome(bool Applied, long Seq);

public readonly record struct SnapshotCaptureContext(
    long AppliedSeq,
    Guid WalGeneration);
