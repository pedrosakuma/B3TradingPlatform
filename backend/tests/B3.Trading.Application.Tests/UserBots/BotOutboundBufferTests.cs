using B3.Trading.Application.UserBots;

namespace B3.Trading.Application.Tests.UserBots;

/// <summary>
/// Unit tests for <see cref="BotOutboundBuffer"/> (sub-issue #172 F):
/// FIFO append, range read, evict, overflow callback semantics, reset.
/// </summary>
public class BotOutboundBufferTests
{
    [Fact]
    public void Append_StoresEntries_InArrivalOrder()
    {
        var buf = new BotOutboundBuffer(Guid.NewGuid(), maxMessages: 100);
        Assert.True(buf.Append(1, new byte[] { 1 }));
        Assert.True(buf.Append(2, new byte[] { 2 }));
        var range = buf.GetRange(1, 2);
        Assert.Equal(2, range.Count);
        Assert.Equal(1ul, range[0].Seq);
        Assert.Equal(2ul, range[1].Seq);
    }

    [Fact]
    public void Append_TakesOwnership_OfPooledFrame_DisposingOnEvict()
    {
        // Successor to the old "DefensivelyCopiesPayload" test. After
        // RFC §5.5 / issue #201, Append no longer copies — the buffer
        // takes ownership of the pooled memory and disposes it on
        // EvictUpTo. Issue #230 moved the rent from MemoryPool to
        // ArrayPool; the lifecycle through a tracking pool is the same.
        using var pool = new TrackingMemoryPool();
        var buf = new BotOutboundBuffer(Guid.NewGuid(), maxMessages: 10);
        var arr = pool.Rent(8);
        arr.AsSpan(0, 3).Clear();
        arr[0] = 1; arr[1] = 2; arr[2] = 3;
        var frame = OutboundFrame.Pooled(arr, 3, pool);

        Assert.True(buf.Append(1, frame));
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(0, pool.DisposeCount);

        var range = buf.GetRange(1, 1);
        Assert.Equal(new byte[] { 1, 2, 3 }, range[0].Bytes.ToArray());

        buf.EvictUpTo(1);
        Assert.Equal(1, pool.DisposeCount);
        Assert.Equal(0, buf.Count);
    }

    [Fact]
    public void EvictUpTo_DropsLowSeqs_Idempotent()
    {
        var buf = new BotOutboundBuffer(Guid.NewGuid(), maxMessages: 10);
        for (var i = 1ul; i <= 5; i++) buf.Append(i, new byte[] { (byte)i });
        buf.EvictUpTo(3);
        Assert.Equal(2, buf.Count);
        buf.EvictUpTo(3); // idempotent
        Assert.Equal(2, buf.Count);
        var range = buf.GetRange(1, 5);
        Assert.Equal(new ulong[] { 4, 5 }, range.Select(r => r.Seq));
    }

    [Fact]
    public void Append_AtCap_FiresOverflow_AndRefusesUntilReset()
    {
        var fired = 0;
        Guid? gotCred = null;
        var credId = Guid.NewGuid();
        var buf = new BotOutboundBuffer(credId, maxMessages: 3, onOverflow: c => { fired++; gotCred = c; });

        Assert.True(buf.Append(1, new byte[] { 1 }));
        Assert.True(buf.Append(2, new byte[] { 2 }));
        Assert.True(buf.Append(3, new byte[] { 3 }));

        Assert.False(buf.Append(4, new byte[] { 4 })); // tripped
        Assert.Equal(1, fired);
        Assert.Equal(credId, gotCred);
        Assert.True(buf.IsOverflowed);
        Assert.Equal(0, buf.Count);

        Assert.False(buf.Append(5, new byte[] { 5 })); // still refusing
        Assert.Equal(1, fired); // not refired

        buf.Reset();
        Assert.False(buf.IsOverflowed);
        Assert.True(buf.Append(6, new byte[] { 6 }));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BotOutboundBuffer(Guid.NewGuid(), 0));
    }
}
