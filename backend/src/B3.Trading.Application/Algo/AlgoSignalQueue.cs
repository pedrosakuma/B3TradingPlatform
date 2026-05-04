using System.Threading.Channels;

namespace B3.Trading.Application;

/// <summary>
/// Producer side of the algo engine's signal channel. The producer never
/// blocks on a full queue: backpressure surfaces as a returned <c>false</c>
/// so the caller can record metrics + log without back-pressuring the
/// dispatcher thread.
/// </summary>
public interface IAlgoSignalQueue
{
    bool TryEnqueue(AlgoSignal signal);
}

/// <summary>
/// Bounded MPSC channel feeding the single <see cref="AlgoEngine"/>
/// consumer task (RFC §4.3 v0). Capacity is generous because algo flow is
/// orders-of-magnitude lower than ER flow; a full queue indicates a
/// pathological loop and should fail loud, not block producers.
/// </summary>
public sealed class AlgoSignalQueue : IAlgoSignalQueue
{
    private readonly Channel<AlgoSignal> _channel;

    public const int DefaultCapacity = 4096;

    public AlgoSignalQueue(int capacity = DefaultCapacity)
    {
        _channel = Channel.CreateBounded<AlgoSignal>(new BoundedChannelOptions(capacity)
        {
            // Wait + TryWrite: when full, TryWrite returns false rather than
            // overwriting an in-flight signal. Producers (the dispatcher
            // thread, the ER processor) MUST observe the drop and surface
            // it as a metric — silently overwriting would lose engine
            // intent (e.g. a cancel-requested behind a flood of child ERs).
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public bool TryEnqueue(AlgoSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return _channel.Writer.TryWrite(signal);
    }

    /// <summary>
    /// Async stream consumed by the engine's hosted-service loop. Completes
    /// when <see cref="Complete"/> is called (host shutdown).
    /// </summary>
    public ChannelReader<AlgoSignal> Reader => _channel.Reader;

    public void Complete() => _channel.Writer.TryComplete();
}
