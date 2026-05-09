using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.UserBots;

namespace B3.Trading.Application.Tests.UserBots;

/// <summary>
/// Sub-issue #172 (F). Tests covering the durable seq watermark API:
/// <see cref="IUserBotSessionRegistry.UpdateCheckpointedOutboundSeq"/>.
///
/// Mirrors the no-WAL-spam, monotonic-only, replay-restoring guarantees
/// the multiplexer + checkpointer rely on.
/// </summary>
public class BotSessionCheckpointedSeqTests
{
    [Fact]
    public async Task UpdateCheckpointedOutboundSeq_AppendsEvent_AndAdvancesWatermark()
    {
        var store = new RecordingStore();
        var dispatcher = new EventDispatcher(store);
        var reg = new InMemoryUserBotSessionRegistry(dispatcher, store);
        var credId = Guid.NewGuid();
        await reg.GetOrCreateAsync(credId, default);

        reg.UpdateCheckpointedOutboundSeq(credId, 42);

        var state = await reg.GetOrCreateAsync(credId, default);
        Assert.Equal(42ul, state.LastCheckpointedOutboundSeq);
        var evts = store.Recorded.OfType<BotSessionSeqAdvancedEvent>().ToList();
        Assert.Single(evts);
        Assert.Equal(42ul, evts[0].CheckpointedOutboundSeq);
    }

    [Fact]
    public async Task UpdateCheckpointedOutboundSeq_NonMonotonic_NoOps()
    {
        var store = new RecordingStore();
        var dispatcher = new EventDispatcher(store);
        var reg = new InMemoryUserBotSessionRegistry(dispatcher, store);
        var credId = Guid.NewGuid();
        await reg.GetOrCreateAsync(credId, default);

        reg.UpdateCheckpointedOutboundSeq(credId, 10);
        reg.UpdateCheckpointedOutboundSeq(credId, 5); // backwards
        reg.UpdateCheckpointedOutboundSeq(credId, 10); // equal

        var evts = store.Recorded.OfType<BotSessionSeqAdvancedEvent>().ToList();
        Assert.Single(evts); // only the original 10
        var state = await reg.GetOrCreateAsync(credId, default);
        Assert.Equal(10ul, state.LastCheckpointedOutboundSeq);
    }

    [Fact]
    public async Task UpdateCheckpointedOutboundSeq_UnknownCredential_NoOps()
    {
        var store = new RecordingStore();
        var dispatcher = new EventDispatcher(store);
        var reg = new InMemoryUserBotSessionRegistry(dispatcher, store);

        reg.UpdateCheckpointedOutboundSeq(Guid.NewGuid(), 10);

        Assert.Empty(store.Recorded.OfType<BotSessionSeqAdvancedEvent>());
    }

    private sealed class RecordingStore : IEventStore
    {
        public ConcurrentQueue<WalEvent> Recorded { get; } = new();
        private long _seq;
        public long CurrentSeq => Interlocked.Read(ref _seq);

        public long Append(WalEvent evt)
        {
            Recorded.Enqueue(evt);
            return Interlocked.Increment(ref _seq);
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
