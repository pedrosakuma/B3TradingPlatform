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

    public EventDispatcher(IEventStore store) => _store = store;

    public long CurrentSeq
    {
        get { lock (_lock) return _store.CurrentSeq; }
    }

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
}
