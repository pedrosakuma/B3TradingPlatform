namespace B3.Trading.Application.Persistence;

/// <summary>
/// RFC §5.2 (F2). Mutable per-dispatch buffer of <see cref="ExecutionEvent"/>
/// values captured by an <c>Apply</c> callback while the dispatcher
/// lock is held. The dispatcher walks the buffer once <c>Apply</c>
/// returns and writes each entry to every registered
/// <see cref="IExecutionFanOutSink"/> whose
/// <see cref="IExecutionFanOutSink.Target"/> intersects the entry's
/// <see cref="ExecutionFanOutTargets"/> mask — all still under the
/// lock so subscribers observe events in WAL-append order.
///
/// <para>
/// Instances are pooled per-thread to keep the hot dispatch path
/// allocation-free in steady state. A dispatch typically captures 0–2
/// events (single ER, or replace-accepted with original + new), so the
/// initial backing array of 4 covers the common case without growth.
/// </para>
///
/// <para>
/// Not thread-safe. The instance handed to an <c>Apply</c> callback is
/// owned by that callback for the duration of the call and is returned
/// to the pool by the dispatcher in a <c>finally</c> block.
/// </para>
/// </summary>
public sealed class ExecutionFanOut
{
    [System.ThreadStatic]
    private static ExecutionFanOut? _cached;

    private Entry[] _buf = new Entry[4];
    private int _count;

    /// <summary>Number of events captured so far.</summary>
    public int Count => _count;

    internal Entry this[int i] => _buf[i];

    /// <summary>
    /// Records <paramref name="ev"/> for fan-out to every sink whose
    /// <see cref="IExecutionFanOutSink.Target"/> is set in
    /// <paramref name="targets"/>. The default (<c>All</c>) is the
    /// usual ER path; pass <see cref="ExecutionFanOutTargets.BotRouter"/>
    /// for a synthetic event meaningful only to the bot session (e.g.
    /// replace-rejected, where no in-book order exists for the
    /// replace-side ClOrdID).
    /// </summary>
    public void Add(ExecutionEvent ev, ExecutionFanOutTargets targets = ExecutionFanOutTargets.All)
    {
        if (targets == ExecutionFanOutTargets.None) return;
        if (_count == _buf.Length) System.Array.Resize(ref _buf, _buf.Length * 2);
        _buf[_count++] = new Entry(ev, targets);
    }

    internal static ExecutionFanOut Rent()
    {
        var f = _cached;
        if (f is not null) { _cached = null; return f; }
        return new ExecutionFanOut();
    }

    internal void Return()
    {
        if (_count > 0) System.Array.Clear(_buf, 0, _count);
        _count = 0;
        _cached ??= this;
    }

    internal readonly record struct Entry(ExecutionEvent Event, ExecutionFanOutTargets Targets);
}
