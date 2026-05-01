namespace B3.Trading.Application.Persistence;

/// <summary>
/// Serialises append-then-mutate critical sections so snapshots see a
/// consistent view of (event log seq) + (in-memory state). Every call to
/// <see cref="Dispatch"/> takes a single lock that:
/// <list type="number">
///   <item>assigns a WAL seq + queues the event;</item>
///   <item>runs the in-memory mutation while holding the same lock.</item>
/// </list>
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
/// enqueue; disk flush runs on a background task. Contention is
/// negligible at participant volumes.
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
        lock (_lock)
        {
            var seq = _store.Append(evt);
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
