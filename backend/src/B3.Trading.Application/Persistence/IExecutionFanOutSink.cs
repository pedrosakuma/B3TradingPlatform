namespace B3.Trading.Application.Persistence;

/// <summary>
/// RFC §5.2 (F2). Per-sink fan-out target for <see cref="ExecutionEvent"/>
/// produced by an <see cref="EventDispatcher.Dispatch(WalEvent, System.Action{ExecutionFanOut})"/>
/// call.
///
/// <para>
/// The dispatcher invokes <see cref="Enqueue"/> on every registered sink
/// while still holding the dispatcher lock so that the relative order of
/// events on each sink's channel matches WAL append order
/// (<see cref="Target"/>-filtered). Sinks must implement
/// <see cref="Enqueue"/> as a non-blocking operation — typically a
/// <c>Channel&lt;T&gt;.Writer.TryWrite</c> — and perform the actual
/// publish work on a background drain thread, OUTSIDE the dispatcher
/// lock (RFC §5.2 ordering note).
/// </para>
///
/// <para>
/// Per-sink overflow policy is the implementation's responsibility and
/// is documented in RFC §6.3 (e.g. WS hub: bounded + DropOldest;
/// bot router: unbounded with transitive bound via per-credential
/// outbound buffers; algo signals: bounded + DropOldest + metric).
/// </para>
/// </summary>
public interface IExecutionFanOutSink
{
    /// <summary>
    /// Which fan-out target this sink represents. The dispatcher uses
    /// this to decide whether each captured event should be enqueued
    /// onto the sink (e.g. a synthetic "replace-rejected" event is only
    /// meaningful for the bot router because no <c>Order</c> exists in
    /// the book for the cancel/replace ClOrdID).
    /// </summary>
    ExecutionFanOutTargets Target { get; }

    /// <summary>
    /// Called UNDER the dispatcher lock. MUST be non-blocking. The
    /// captured <paramref name="seq"/> is the WAL seq assigned to the
    /// originating event; implementations may carry it through to the
    /// drain side for diagnostics or sequence-gap detection.
    /// </summary>
    void Enqueue(long seq, ExecutionEvent ev);
}

/// <summary>
/// Bitmask of fan-out targets. Used both by sinks (to declare which
/// target they implement) and by callers of
/// <see cref="ExecutionFanOut.Add(ExecutionEvent, ExecutionFanOutTargets)"/>
/// (to constrain which sinks an event reaches).
/// </summary>
[System.Flags]
public enum ExecutionFanOutTargets : byte
{
    None = 0,
    /// <summary>End-client WebSocket hub (executions.me / orders.me / positions.me).</summary>
    WsHub = 1,
    /// <summary>FIXP outbound multiplexer to the originating bot session.</summary>
    BotRouter = 2,
    /// <summary>Default for an ER captured during apply — routes to every sink.</summary>
    All = WsHub | BotRouter,
}
