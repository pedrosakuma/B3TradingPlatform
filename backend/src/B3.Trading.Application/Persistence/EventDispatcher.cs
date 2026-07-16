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
/// The lock is process-global (one platform instance per firm pool) and
/// is held for the duration of synchronous, in-memory work only. No I/O
/// happens while it is held — the WAL append is a synchronous channel
/// enqueue; disk flush runs on a background task.
/// </para>
/// </summary>
public sealed class EventDispatcher
{
    private readonly IEventStore _store;
    private readonly object _lock = new();
    private readonly IExecutionFanOutSink[] _erFanOutSinks;

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
        _erFanOutSinks = fanOutSinks is null
            ? System.Array.Empty<IExecutionFanOutSink>()
            : System.Linq.Enumerable.ToArray(fanOutSinks);
    }

    public long CurrentSeq
    {
        get { lock (_lock) return _store.CurrentSeq; }
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
        _store.FlushAsync(cancellationToken);

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
            return new DispatchOutcome(true, seq);
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
            capture(_store.CurrentSeq);
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
}

/// <summary>
/// Result of <see cref="EventDispatcher.DispatchWithPreApply"/>.
/// <c>Applied=false</c> means the pre-apply check returned false and no
/// WAL append was made; <c>Seq</c> is meaningful only when
/// <c>Applied=true</c>.
/// </summary>
public readonly record struct DispatchOutcome(bool Applied, long Seq);
