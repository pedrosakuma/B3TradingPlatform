using System.Threading.Channels;
using B3.Trading.Application.UserBots;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// RFC §5.3 / P8 / F3 — per-FIXP-connection outbound writer. Owns a
/// bounded <see cref="Channel{T}"/> of <see cref="OutboundFrame"/> and
/// a single dedicated drain loop (one <see cref="Task"/> per
/// connection, NOT one per outbound message). Replaces the pre-F3
/// <c>Task.Run</c>-per-send fire-and-forget allocation in
/// <c>FixpSessionConnection.IBotSessionOutboundSender.TryEnqueue</c>.
///
/// <para><b>Ordering.</b> The channel is FIFO; the drain loop is
/// single-reader. Any sequence of producer enqueues observed by a
/// single producer is delivered to the socket in that exact order.
/// Cross-producer ordering is "as enqueued", which is what FIXP
/// requires (the per-credential outbound seq is allocated upstream,
/// in the multiplexer's <c>RouteOne</c>, before <c>TryEnqueue</c>).</para>
///
/// <para><b>Backpressure (RFC §5.3.1).</b> The channel uses
/// <see cref="BoundedChannelFullMode.Wait"/> as its declared full
/// mode, but producers go through the non-blocking
/// <see cref="ChannelWriter{T}.TryWrite"/> path so they never await
/// the channel — when the channel is full, <see cref="TryEnqueue"/>
/// returns <c>false</c> and the caller (the multiplexer) leaves the
/// frame in the per-credential <see cref="BotOutboundBuffer"/> for
/// retransmit on the next reconnect. <c>DropOldest</c> is intentionally
/// rejected: the FIXP wire requires per-session sequence continuity
/// and a silent drop would surface only as cancel-rejects with no
/// diagnosable cause (RFC §5.3.1, code-review concern (1)).</para>
///
/// <para><b>Single-disposer rule (RFC §5.5).</b> The drain loop NEVER
/// returns the pooled array backing
/// <see cref="OutboundFrame.PooledArray"/>. The frame remains owned by the per-credential <see cref="BotOutboundBuffer"/> from
/// the moment <c>buffer.Append</c> succeeded upstream; the buffer is
/// the sole disposer (on <c>EvictUpTo</c> / overflow / <c>Reset</c>).
/// This writer only borrows <see cref="OutboundFrame.Bytes"/> for
/// <see cref="System.IO.Stream.WriteAsync(System.ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/>.</para>
///
/// <para><b>Lifetime safety across the awaited write.</b> A bot can
/// only ack a watermark for sequences it has actually received; an
/// unsent (still-queued) frame's seq cannot reach
/// <see cref="BotOutboundBuffer.EvictUpTo"/>. Overflow / version-bump
/// force-closes the connection (and therefore this writer) BEFORE the
/// buffer's <see cref="BotOutboundBuffer.Reset"/> clears pooled
/// owners. Both invariants together are why the drain loop can hold
/// an <see cref="OutboundFrame"/> across an <c>await</c> without a
/// defensive heap copy of <c>frame.Bytes</c>.</para>
///
/// <para><b>Shutdown drain (RFC §5.3.2).</b> <see cref="CompleteAsync"/>
/// signals no further enqueues, then waits up to
/// <c>shutdownTimeout</c> for the drain loop to flush the remaining
/// queued frames. Frames still queued at the deadline are NOT lost
/// from the bot's perspective: they remain owned by the per-credential
/// <see cref="BotOutboundBuffer"/> and are replayed via retransmit on
/// the next reconnect (sub-issue G). The drain loop returns without
/// disposing anything.</para>
/// </summary>
internal sealed class FixpOutboundChannelWriter
{
    /// <summary>
    /// Synchronous socket-write callback invoked by the drain loop.
    /// Returning <c>false</c> ends the drain loop (the writer treats
    /// it as "socket gone"). Throwing is logged and also ends the
    /// drain loop. The callback is responsible for serialising
    /// against any concurrent handshake / order-ack writes the
    /// connection's own request loop is emitting (typically by taking
    /// the same write mutex).
    /// </summary>
    public delegate ValueTask<bool> SocketWriteCallback(
        ReadOnlyMemory<byte> bytes, CancellationToken ct);

    private readonly Channel<OutboundFrame> _channel;
    private readonly SocketWriteCallback _writeAsync;
    private readonly ILogger _logger;
    private readonly string _connectionId;
    private readonly CancellationTokenSource _cts;
    private readonly Task _drainLoop;
    private volatile bool _completed;

    public FixpOutboundChannelWriter(
        int capacity,
        SocketWriteCallback writeAsync,
        string connectionId,
        ILogger? logger = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        ArgumentNullException.ThrowIfNull(writeAsync);

        _writeAsync = writeAsync;
        _logger = logger ?? NullLogger.Instance;
        _connectionId = connectionId;
        _channel = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(capacity)
        {
            // FullMode = Wait is the *declared* policy; producers always
            // go through TryWrite so they never actually wait. See the
            // type-level §5.3.1 note.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        _cts = new CancellationTokenSource();
        // Start the dedicated drain loop. One Task per connection,
        // not per outbound message — that is the entire point of P8.
        _drainLoop = Task.Run(() => DrainAsync(_cts.Token));
    }

    /// <summary>
    /// Number of frames currently queued. Test-only — the production
    /// hot path never reads this.
    /// </summary>
    internal int QueuedCount => _channel.Reader.CanCount ? _channel.Reader.Count : 0;

    /// <summary>
    /// Awaitable that completes when the drain loop exits. Test-only.
    /// </summary>
    internal Task DrainCompletion => _drainLoop;

    /// <summary>
    /// Non-blocking enqueue. Returns <c>false</c> when (a) the writer
    /// has been completed (connection closed) or (b) the bounded
    /// channel is full (slow-consumer backpressure, RFC §5.3.1). The
    /// caller MUST NOT dispose <paramref name="frame"/> in either
    /// branch — single-disposer rule.
    /// </summary>
    public bool TryEnqueue(OutboundFrame frame)
    {
        if (_completed) return false;
        // TryWrite is non-blocking and returns false when the bounded
        // channel is full — exactly the surface §5.3.1 wants. We also
        // get a false return after Complete(), which we treat the same.
        return _channel.Writer.TryWrite(frame);
    }

    /// <summary>
    /// Marks the writer complete (no further enqueues accepted) and
    /// waits up to <paramref name="shutdownTimeout"/> for the drain
    /// loop to flush remaining queued frames. Idempotent.
    /// </summary>
    public async Task CompleteAsync(TimeSpan shutdownTimeout)
    {
        if (_completed)
        {
            // Still wait for the loop to finish — callers expect the
            // returned Task to complete with the drain loop, not to
            // race with a still-running flush.
            await WaitForDrainAsync(shutdownTimeout).ConfigureAwait(false);
            return;
        }
        _completed = true;
        // Complete() lets the foreach in DrainAsync exit cleanly once
        // the channel is empty. We intentionally do NOT cancel _cts
        // here — that would abort an in-flight flush mid-message and
        // leave the bot observing a partially-written frame.
        _channel.Writer.TryComplete();
        await WaitForDrainAsync(shutdownTimeout).ConfigureAwait(false);
    }

    private async Task WaitForDrainAsync(TimeSpan timeout)
    {
        try
        {
            await _drainLoop.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The drain loop is still running (peer is stuck or the
            // socket-write is pathologically slow). Cancel it so the
            // current WriteAsync observes the cancellation and the
            // loop returns; remaining queued frames stay owned by the
            // per-credential BotOutboundBuffer and ride retransmit on
            // the next reconnect (RFC §5.3.2).
            try { _cts.Cancel(); } catch { /* ignore */ }
            // Bounded grace period after cancel — never await the
            // loop unbounded, otherwise a callback that ignores
            // cancellation would still hang shutdown despite the
            // configured timeout.
            try
            {
                await _drainLoop.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Drain still hung — log and abandon. Connection
                // cleanup proceeds; the (orphaned) drain task will
                // exit when its WriteAsync eventually unblocks.
                _logger.LogWarning(
                    "fixp.outbound.drain.shutdown.abandoned connectionId={ConnectionId} timeoutMs={TimeoutMs}",
                    _connectionId, (int)timeout.TotalMilliseconds);
                return;
            }
            catch
            {
                // swallow shutdown noise
            }
            _logger.LogWarning(
                "fixp.outbound.drain.shutdown.timeout connectionId={ConnectionId} timeoutMs={TimeoutMs}",
                _connectionId, (int)timeout.TotalMilliseconds);
        }
        catch
        {
            // The drain loop ended with an exception; CompleteAsync
            // does not propagate — connection close is best-effort.
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            // foreach over ReadAllAsync naturally completes when the
            // writer is Complete()'d AND the channel is empty —
            // exactly the §5.3.2 shutdown drain semantics.
            await foreach (var frame in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                bool keepGoing;
                try
                {
                    keepGoing = await _writeAsync(frame.Bytes, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Shutdown timeout cancelled us mid-write. The
                    // frame is NOT disposed here; the buffer still
                    // owns it (single-disposer rule) and retransmit
                    // will pick it up on the next reconnect.
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "fixp.outbound.write.error connectionId={ConnectionId}",
                        _connectionId);
                    // Socket failed; end the drain loop. Same lifetime
                    // story as above — the buffer is the sole disposer.
                    return;
                }
                if (!keepGoing) return;
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "fixp.outbound.drain.unexpected connectionId={ConnectionId}",
                _connectionId);
        }
    }
}
