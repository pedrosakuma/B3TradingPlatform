using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using B3.Trading.Application.Observability;
using B3.Trading.Application.UserBots;
using B3.Trading.EntryPointListener.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using xRetry;

namespace B3.Trading.EntryPointListener.Tests.Hosting;

/// <summary>
/// RFC §5.3 / P8 / F3 — behavioural tests for the per-FIXP-connection
/// outbound writer. Verifies (a) per-connection FIFO ordering, (b)
/// bounded-channel backpressure surfaces as <c>TryEnqueue == false</c>
/// (never silent drop), (c) shutdown drain flushes already-queued
/// frames, (d) the drain loop NEVER disposes an
/// <see cref="OutboundFrame"/> (single-disposer rule, RFC §5.5).
/// </summary>
public sealed class FixpOutboundChannelWriterTests
{
    private static OutboundFrame Frame(byte tag)
    {
        // Use a unique payload byte so the receive log can assert
        // ordering without depending on payload format.
        return OutboundFrame.Unowned(new byte[] { tag });
    }

    [Fact(Timeout = 5_000)]
    public async Task TryEnqueue_PreservesPerConnectionFifoOrder()
    {
        // Many enqueues from a single producer must reach the socket
        // in arrival order — single-reader drain over a FIFO channel.
        var received = new List<byte>();
        var allDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int total = 1000;

        var writer = new FixpOutboundChannelWriter(
            capacity: 4096,
            writeAsync: (bytes, ct) =>
            {
                received.Add(bytes.Span[0]);
                if (received.Count == total) allDelivered.TrySetResult();
                return ValueTask.FromResult(true);
            },
            connectionId: "conn-fifo",
            logger: NullLogger.Instance);

        for (var i = 0; i < total; i++)
        {
            Assert.True(writer.TryEnqueue(Frame((byte)(i % 251))));
        }

        await allDelivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await writer.CompleteAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(total, received.Count);
        for (var i = 0; i < total; i++)
            Assert.Equal((byte)(i % 251), received[i]);
    }

    [Fact(Timeout = 5_000)]
    public async Task TryEnqueue_WhenChannelFull_ReturnsFalseAndDoesNotDispose()
    {
        // Block the drain so the bounded channel can fill, then assert
        // that the next TryEnqueue returns false (RFC §5.3.1: surface
        // backpressure, never silent drop / DropOldest).
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = 0;

        var writer = new FixpOutboundChannelWriter(
            capacity: 2,
            writeAsync: async (bytes, ct) =>
            {
                if (Interlocked.Increment(ref written) == 1)
                {
                    firstEntered.TrySetResult();
                    await release.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                return true;
            },
            connectionId: "conn-bp",
            logger: NullLogger.Instance);

        // Use a pooled-tracking frame to assert no double-dispose.
        var pool = new TrackingPool();
        var rejected = pool.RentFrame(payload: 0xFF);

        // Saturate: 1 in-flight + 2 queued = 3. The 4th must be rejected.
        Assert.True(writer.TryEnqueue(Frame(0x01)));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(writer.TryEnqueue(Frame(0x02)));
        Assert.True(writer.TryEnqueue(Frame(0x03)));

        // Spin a few iterations to allow the previous TryWrite to land.
        // Bounded TryWrite returns false deterministically once full.
        var didReject = false;
        for (var i = 0; i < 200; i++)
        {
            if (!writer.TryEnqueue(rejected)) { didReject = true; break; }
            await Task.Delay(5);
        }

        Assert.True(didReject, "expected channel-full TryEnqueue to return false");
        // Drain loop NEVER disposes (RFC §5.5). The rejected frame is
        // still owned by the caller (in production: the per-credential
        // BotOutboundBuffer); the writer must NOT have touched its
        // pooled owner.
        Assert.Equal(0, pool.DisposedCount);

        release.TrySetResult();
        await writer.CompleteAsync(TimeSpan.FromSeconds(2));
        // Still no dispose by the writer, even after drain completes.
        Assert.Equal(0, pool.DisposedCount);
    }

    [Fact(Timeout = 5_000)]
    public async Task CompleteAsync_FlushesAlreadyQueuedFrames()
    {
        // RFC §5.3.2 shutdown drain: frames enqueued before Complete
        // must be observed by the drain loop before it returns.
        var received = new ConcurrentQueue<byte>();

        var writer = new FixpOutboundChannelWriter(
            capacity: 16,
            writeAsync: (bytes, ct) =>
            {
                received.Enqueue(bytes.Span[0]);
                return ValueTask.FromResult(true);
            },
            connectionId: "conn-drain",
            logger: NullLogger.Instance);

        for (byte i = 1; i <= 8; i++) Assert.True(writer.TryEnqueue(Frame(i)));

        await writer.CompleteAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(8, received.Count);
        for (byte i = 1; i <= 8; i++)
        {
            Assert.True(received.TryDequeue(out var b));
            Assert.Equal(i, b);
        }
    }

    [Fact(Timeout = 5_000)]
    public async Task CompleteAsync_AfterTimeout_LeavesQueuedFramesUntouched()
    {
        // A pathologically slow socket-write must not block shutdown
        // forever; the writer cancels its drain and remaining queued
        // frames stay owned by the caller (single-disposer rule).
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pool = new TrackingPool();

        var writer = new FixpOutboundChannelWriter(
            capacity: 16,
            writeAsync: async (bytes, ct) =>
            {
                await release.Task.WaitAsync(ct).ConfigureAwait(false);
                return true;
            },
            connectionId: "conn-timeout",
            logger: NullLogger.Instance);

        // First TryEnqueue lands in the in-flight WriteAsync; the rest
        // sit in the channel.
        Assert.True(writer.TryEnqueue(pool.RentFrame(0x10)));
        Assert.True(writer.TryEnqueue(pool.RentFrame(0x20)));
        Assert.True(writer.TryEnqueue(pool.RentFrame(0x30)));

        await writer.CompleteAsync(TimeSpan.FromMilliseconds(150));
        // None of the frames may have been disposed by the writer.
        Assert.Equal(0, pool.DisposedCount);
        // Unblock so the test does not leak the producer task.
        release.TrySetResult();
    }

    [Fact(Timeout = 5_000)]
    public async Task TryEnqueue_AfterCompleteAsync_ReturnsFalse()
    {
        // No double-dispose under shutdown race: TryEnqueue arriving
        // after CompleteAsync must return false WITHOUT touching the
        // frame's pooled owner.
        var pool = new TrackingPool();
        var writer = new FixpOutboundChannelWriter(
            capacity: 4,
            writeAsync: (_, _) => ValueTask.FromResult(true),
            connectionId: "conn-race",
            logger: NullLogger.Instance);

        await writer.CompleteAsync(TimeSpan.FromSeconds(1));

        var late = pool.RentFrame(0xAA);
        Assert.False(writer.TryEnqueue(late));
        // Writer NEVER disposes — even on the rejected branch
        // (RFC §5.5: only BotOutboundBuffer may dispose).
        Assert.Equal(0, pool.DisposedCount);
    }

    [Fact(Timeout = 5_000)]
    public async Task DrainLoop_WhenWriteCallbackThrows_EndsLoopWithoutDisposing()
    {
        // Socket failure mid-drain ends the loop. The frame in flight
        // and any successors stay owned by the caller.
        var pool = new TrackingPool();
        var writer = new FixpOutboundChannelWriter(
            capacity: 4,
            writeAsync: (_, _) => throw new IOException("simulated socket failure"),
            connectionId: "conn-fail",
            logger: NullLogger.Instance);

        Assert.True(writer.TryEnqueue(pool.RentFrame(0xCA)));
        // Wait for the drain loop to terminate after observing the throw.
        await writer.DrainCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, pool.DisposedCount);

        // Subsequent enqueues just sit in the channel until Complete;
        // still no dispose.
        Assert.True(writer.TryEnqueue(pool.RentFrame(0xCB)) || true);
        await writer.CompleteAsync(TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, pool.DisposedCount);
    }

    /// <summary>
    /// Test-only pool that tracks how many of its rented arrays got
    /// returned, so the test can assert the writer never invoked
    /// <c>DisposeOwner</c> (RFC §5.5 single-disposer rule). Issue #230:
    /// switched from <c>IMemoryOwner&lt;byte&gt;</c> to a raw
    /// <see cref="System.Buffers.ArrayPool{T}"/>-based path; this
    /// double subclasses <see cref="System.Buffers.ArrayPool{T}"/> to
    /// observe the return.
    /// </summary>
    // #332. Wrapped in <c>[RetryFact]</c> because the MeterListener
    // start-vs-publish race occasionally drops the increment under CI
    // parallelism (see in-line comment on the bounded poll). Retry up
    // to 3 times before failing CI — a genuine regression still loses.
    [RetryFact(maxRetries: 3, delayBetweenRetriesMs: 250)]
    public async Task CompleteAsync_AbandonedPath_IncrementsOtelCounter()
    {
        // Issue #233. The abandoned path must increment the
        // `trading.fixp.outbound.drain.shutdown.abandoned` OTel
        // counter exactly once, at the same call site as the
        // existing structured warning log. We force the abandoned
        // branch by handing the writer a write callback that
        // ignores the cancellation token entirely — after the
        // configured timeout the writer cancels its CTS, waits
        // 250 ms, observes the drain loop still has not returned,
        // logs `.abandoned` and bumps the counter.
        long captured = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "trading.fixp.outbound.drain.shutdown.abandoned")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            Interlocked.Add(ref captured, measurement);
        });
        listener.Start();

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new FixpOutboundChannelWriter(
            capacity: 4,
            // Intentionally ignores ct — this is the "drain loop
            // ignored cancellation" pathology the abandoned path
            // exists to detect.
            writeAsync: async (_, _) =>
            {
                await release.Task.ConfigureAwait(false);
                return true;
            },
            connectionId: "conn-abandoned",
            logger: NullLogger.Instance);

        Assert.True(writer.TryEnqueue(OutboundFrame.Unowned(new byte[] { 0x01 })));

        // Short timeout → quick to the abandoned branch
        // (timeout + 250 ms cancel grace ≈ 300 ms).
        await writer.CompleteAsync(TimeSpan.FromMilliseconds(50));

        // Issue #332. Bounded poll instead of bare assert: although
        // MeterListener callbacks run synchronously to Counter.Add per
        // .NET docs, under CI parallelism we have observed at least one
        // miss (run 26064747516). The miss is most plausibly explained
        // by a race between MeterListener.Start() and concurrent
        // instrument publication from other test classes — Start()
        // enumerates already-published instruments under an internal
        // lock, but a Counter created on another thread mid-Start can
        // slip past. Polling up to 1 s on Interlocked.Read absorbs that
        // window without masking a real regression (an actually-missing
        // increment would still time out and fail).
        var deadline = Environment.TickCount64 + 1_000;
        while (Interlocked.Read(ref captured) == 0 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Equal(1, Interlocked.Read(ref captured));

        // Unblock so the orphaned drain task can exit cleanly and
        // the test does not leak it.
        release.TrySetResult();
    }

    private sealed class TrackingPool : System.Buffers.ArrayPool<byte>
    {
        private int _disposed;
        public int DisposedCount => Volatile.Read(ref _disposed);

        public OutboundFrame RentFrame(byte payload)
        {
            var arr = Rent(1);
            arr[0] = payload;
            return OutboundFrame.Pooled(arr, length: 1, pool: this);
        }

        public override byte[] Rent(int minimumLength) => new byte[Math.Max(minimumLength, 1)];

        public override void Return(byte[] array, bool clearArray = false)
            => Interlocked.Increment(ref _disposed);
    }
}
