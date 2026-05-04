using B3.Trading.Application;
using Xunit;

namespace B3.Trading.Application.Tests.AlgoEngine;

public class AlgoSignalQueueTests
{
    [Fact]
    public void TryEnqueue_AcceptsUntilCapacity_ThenDrops()
    {
        var q = new AlgoSignalQueue(capacity: 2);
        Assert.True(q.TryEnqueue(new AlgoCreatedSignal { FirmId = "f", AlgoId = 1 }));
        Assert.True(q.TryEnqueue(new AlgoCreatedSignal { FirmId = "f", AlgoId = 2 }));
        // Bounded channel with DropWrite returns false instead of blocking
        // when full — producers must observe the drop and surface it as a
        // metric, not silently retry.
        Assert.False(q.TryEnqueue(new AlgoCreatedSignal { FirmId = "f", AlgoId = 3 }));
    }

    [Fact]
    public async Task Reader_DrainsInFifoOrder()
    {
        var q = new AlgoSignalQueue();
        q.TryEnqueue(new AlgoCreatedSignal { FirmId = "f", AlgoId = 1 });
        q.TryEnqueue(new ChildExecutionObservedSignal { FirmId = "f", AlgoId = 1, ChildClOrdId = 999 });
        q.TryEnqueue(new AlgoCancelRequestedSignal { FirmId = "f", AlgoId = 1 });
        q.Complete();

        var seen = new List<AlgoSignal>();
        await foreach (var s in q.Reader.ReadAllAsync())
            seen.Add(s);

        Assert.Collection(seen,
            s => Assert.IsType<AlgoCreatedSignal>(s),
            s => Assert.IsType<ChildExecutionObservedSignal>(s),
            s => Assert.IsType<AlgoCancelRequestedSignal>(s));
    }

    [Fact]
    public void TryEnqueue_NullArgument_Throws()
    {
        var q = new AlgoSignalQueue();
        Assert.Throws<ArgumentNullException>(() => q.TryEnqueue(null!));
    }
}
