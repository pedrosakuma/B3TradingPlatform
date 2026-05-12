using System.Buffers;
using B3.Trading.Application.UserBots;

namespace B3.Trading.Application.Tests.UserBots;

/// <summary>
/// RFC §5.5 / issue #201 (P7 / F5). Pinned tests for the single-
/// disposer invariant of <see cref="BotOutboundBuffer"/> against
/// <see cref="OutboundFrame"/>: every pooled owner the buffer accepts
/// is disposed exactly once, only by the buffer, and only on the
/// documented terminal events (EvictUpTo / overflow / reset / refused
/// append). Encoder / drain / retransmit paths must NOT dispose.
/// </summary>
public class BotOutboundBufferOwnershipTests
{
    private static OutboundFrame Pooled(TrackingMemoryPool pool, int length, byte tag)
    {
        var arr = pool.Rent(length);
        arr.AsSpan(0, length).Clear();
        arr[0] = tag;
        return OutboundFrame.Pooled(arr, length, pool);
    }

    [Fact]
    public void EvictUpTo_DisposesPooledOwner_Once()
    {
        using var pool = new TrackingMemoryPool();
        var buf = new BotOutboundBuffer(Guid.NewGuid(), maxMessages: 10);
        for (var i = 1; i <= 5; i++) Assert.True(buf.Append((ulong)i, Pooled(pool, 16, (byte)i)));

        Assert.Equal(5, pool.RentCount);
        Assert.Equal(0, pool.DisposeCount);

        buf.EvictUpTo(3);
        Assert.Equal(3, pool.DisposeCount);
        Assert.Equal(2, pool.OutstandingCount);

        // Idempotent re-eviction at the same watermark must not
        // re-dispose anything.
        buf.EvictUpTo(3);
        Assert.Equal(3, pool.DisposeCount);

        buf.EvictUpTo(10);
        Assert.Equal(5, pool.DisposeCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public void Encoder_HandsOff_NeverDisposes()
    {
        // Encoder hands off to Append; once Append accepts, only the
        // buffer disposes — *never* the caller. Post issue #230 the
        // frame is a readonly struct (no finalizer, no heap), so the
        // pre-#230 "drop reference + GC.Collect" rehearsal no longer
        // applies. The substantive invariant remains: dispose is
        // observed only after the buffer evicts.
        using var pool = new TrackingMemoryPool();
        var buf = new BotOutboundBuffer(Guid.NewGuid(), maxMessages: 4);

        Assert.True(buf.Append(1, Pooled(pool, 32, tag: 0xAB)));
        Assert.Equal(0, pool.DisposeCount);

        // Force a GC to confirm that even with the struct's
        // copy-by-value flying around the stack, no finalizer-based
        // path disposes the owner — only the buffer's eviction does.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(0, pool.DisposeCount);

        buf.EvictUpTo(1);
        Assert.Equal(1, pool.DisposeCount);
    }

    [Fact]
    public void Append_RefusedOnOverflow_Disposes_Incoming_AndAllBuffered()
    {
        using var pool = new TrackingMemoryPool();
        var buf = new BotOutboundBuffer(Guid.NewGuid(), maxMessages: 2);
        Assert.True(buf.Append(1, Pooled(pool, 16, 1)));
        Assert.True(buf.Append(2, Pooled(pool, 16, 2)));

        // Cap trip: the rejected frame AND the buffered ones all get
        // disposed exactly once.
        Assert.False(buf.Append(3, Pooled(pool, 16, 3)));
        Assert.Equal(3, pool.RentCount);
        Assert.Equal(3, pool.DisposeCount);
        Assert.Equal(0, pool.OutstandingCount);

        // Once overflowed, subsequent Appends still dispose the rejected
        // frame on the way out — caller never has to.
        Assert.False(buf.Append(4, Pooled(pool, 16, 4)));
        Assert.Equal(4, pool.RentCount);
        Assert.Equal(4, pool.DisposeCount);
    }

    [Fact]
    public void Reset_DisposesAllOutstanding()
    {
        using var pool = new TrackingMemoryPool();
        var buf = new BotOutboundBuffer(Guid.NewGuid(), maxMessages: 8);
        for (var i = 1; i <= 3; i++) Assert.True(buf.Append((ulong)i, Pooled(pool, 16, (byte)i)));

        buf.Reset();
        Assert.Equal(3, pool.DisposeCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public void GetRange_BorrowsOnly_DoesNotDispose()
    {
        using var pool = new TrackingMemoryPool();
        var buf = new BotOutboundBuffer(Guid.NewGuid(), maxMessages: 8);
        for (var i = 1; i <= 5; i++) Assert.True(buf.Append((ulong)i, Pooled(pool, 16, (byte)i)));

        // Retransmit walk reads but does not own.
        var range = buf.GetRange(2, 4);
        Assert.Equal(3, range.Count);
        Assert.Equal(0, pool.DisposeCount);

        // Bytes still readable AFTER the walk because the buffer is
        // still the owner.
        Assert.Equal(2, range[0].Bytes.Span[0]);
        Assert.Equal(0, pool.DisposeCount);

        buf.EvictUpTo(5);
        Assert.Equal(5, pool.DisposeCount);
    }

    [Fact]
    public async Task Concurrent_RetransmitWalk_AndEvict_DoesNotDoubleDispose()
    {
        // Race model: many threads simultaneously enumerate GetRange
        // (retransmit) while another thread evicts. Both paths take
        // the buffer's internal _gate lock; the invariant is that the
        // tracking pool never observes a double-dispose, regardless
        // of interleaving. Failing here would surface as an
        // InvalidOperationException from TrackingMemoryPool.
        using var pool = new TrackingMemoryPool();
        var buf = new BotOutboundBuffer(Guid.NewGuid(), maxMessages: 10_000);
        const int N = 1_000;
        for (var i = 1; i <= N; i++) Assert.True(buf.Append((ulong)i, Pooled(pool, 16, (byte)(i & 0xFF))));

        var tasks = new List<Task>();
        for (var t = 0; t < 4; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (var k = 0; k < 200; k++) _ = buf.GetRange(1, (ulong)N);
            }));
        }
        tasks.Add(Task.Run(() =>
        {
            for (ulong w = 50; w <= (ulong)N; w += 50) buf.EvictUpTo(w);
        }));
        await Task.WhenAll(tasks);

        buf.EvictUpTo((ulong)N);
        Assert.Equal(N, pool.RentCount);
        Assert.Equal(N, pool.DisposeCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public void Unowned_Frame_Append_Evict_NoPoolInteraction()
    {
        using var pool = new TrackingMemoryPool();
        var buf = new BotOutboundBuffer(Guid.NewGuid(), maxMessages: 4);
        Assert.True(buf.Append(1, OutboundFrame.Unowned(new byte[] { 1, 2, 3 })));
        buf.EvictUpTo(1);
        Assert.Equal(0, pool.RentCount);
        Assert.Equal(0, pool.DisposeCount);
    }

    [Fact]
    public void GetRange_Returns_Snapshot_That_Survives_Eviction()
    {
        // The retransmit replay path awaits across socket writes; if
        // GetRange aliased pooled memory, an Eviction (or Reset /
        // Overflow) firing mid-replay would dispose the underlying
        // owner and the in-flight WriteAsync would see returned-to-pool
        // bytes. The buffer takes a snapshot under _gate to guarantee
        // the caller sees stable bytes for as long as it needs them
        // (RFC §5.5 transitional design pre-F3).
        using var pool = new TrackingMemoryPool();
        var buf = new BotOutboundBuffer(Guid.NewGuid(), maxMessages: 8);
        Assert.True(buf.Append(1, Pooled(pool, 16, 0xAA)));
        Assert.True(buf.Append(2, Pooled(pool, 16, 0xBB)));

        var range = buf.GetRange(1, 2);
        Assert.Equal(2, range.Count);

        // Evict everything; pooled owners are disposed exactly once.
        buf.EvictUpTo(2);
        Assert.Equal(2, pool.DisposeCount);

        // Snapshot bytes are still readable after the underlying pool
        // memory has been returned. With the previous aliasing
        // implementation this could observe garbage / next tenant.
        Assert.Equal(0xAA, range[0].Bytes.Span[0]);
        Assert.Equal(0xBB, range[1].Bytes.Span[0]);
    }
}
