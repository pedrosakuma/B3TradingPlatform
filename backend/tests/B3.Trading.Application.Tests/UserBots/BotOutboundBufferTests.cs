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
    public void Append_DefensivelyCopiesPayload()
    {
        var buf = new BotOutboundBuffer(Guid.NewGuid(), maxMessages: 10);
        var payload = new byte[] { 1, 2, 3 };
        Assert.True(buf.Append(1, payload));
        payload[0] = 99; // mutate caller's array post-Append
        var range = buf.GetRange(1, 1);
        Assert.Equal(1, range[0].Bytes.Span[0]);
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
