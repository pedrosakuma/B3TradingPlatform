using B3.Trading.Application;

namespace B3.Trading.Api.WebSockets;

/// <summary>
/// Q4.6 (#306). Tee a single <see cref="IExecutionEventSink.Publish"/>
/// call to multiple downstream sinks. Used to forward synthetic
/// publishes (<see cref="Application.OrderStalenessService"/> /
/// EntryPoint WAL-backpressure fallback) to BOTH the per-user WS hub
/// (<see cref="WebSocketExecutionEventSink"/>) and the compliance
/// drop-copy fan-out
/// (<see cref="DropCopy.DropCopyExecutionEventSink"/>) so the
/// drop-copy feed's "all traffic" guarantee covers events that did
/// NOT travel through the dispatcher's fan-out target machinery.
/// Each leg's <see cref="IExecutionEventSink.Publish"/> is a
/// non-blocking channel write; failure on one leg does not affect the
/// other.
/// </summary>
public sealed class CompositeExecutionEventSink : IExecutionEventSink
{
    private readonly IExecutionEventSink[] _sinks;

    public CompositeExecutionEventSink(params IExecutionEventSink[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks;
    }

    public void Publish(ExecutionEvent ev)
    {
        for (var i = 0; i < _sinks.Length; i++)
        {
            try { _sinks[i].Publish(ev); }
            catch { /* per-sink failure is isolated; sinks bound + report their own metrics */ }
        }
    }
}
