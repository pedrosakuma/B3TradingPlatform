using B3.Trading.Application.UserBots;

namespace B3.Trading.Application.Tests.UserBots;

/// <summary>
/// Unit tests for <see cref="BotOutboundSeqAllocator"/> (sub-issue #172 F).
/// </summary>
public class BotOutboundSeqAllocatorTests
{
    [Fact]
    public void Allocate_StartsAtSeedPlusOne()
    {
        var a = new BotOutboundSeqAllocator(seedSeq: 7);
        Assert.Equal(8ul, a.Allocate());
        Assert.Equal(9ul, a.Allocate());
    }

    [Fact]
    public void Current_TracksLastAllocated()
    {
        var a = new BotOutboundSeqAllocator();
        Assert.Equal(0ul, a.Current);
        a.Allocate();
        a.Allocate();
        Assert.Equal(2ul, a.Current);
    }

    [Fact]
    public void Allocate_IsThreadSafe_AcrossManyConcurrentCallers()
    {
        var a = new BotOutboundSeqAllocator();
        const int threads = 16;
        const int perThread = 5_000;
        var bag = new System.Collections.Concurrent.ConcurrentBag<ulong>();
        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++) bag.Add(a.Allocate());
        });
        var distinct = bag.Distinct().Count();
        Assert.Equal(threads * perThread, distinct);
        Assert.Equal((ulong)(threads * perThread), a.Current);
    }
}
