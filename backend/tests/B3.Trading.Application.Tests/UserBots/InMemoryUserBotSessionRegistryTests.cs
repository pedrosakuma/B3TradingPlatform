using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.UserBots;

namespace B3.Trading.Application.Tests.UserBots;

/// <summary>
/// Unit tests for <see cref="InMemoryUserBotSessionRegistry"/>
/// (sub-issue #170, RFC user-bot-fixp-listener-v0 §4.5/§4.8). Covers
/// idempotent allocation, single-active enforcement, the version-bump
/// durability fence, snapshot+replay round-trip, and concurrent-bump
/// safety.
/// </summary>
public class InMemoryUserBotSessionRegistryTests
{
    [Fact]
    public async Task GetOrCreate_AllocatesStableSessionAndEmitsInitEvent()
    {
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var reg = new InMemoryUserBotSessionRegistry(dispatcher, store);
        var credId = Guid.NewGuid();

        var first = await reg.GetOrCreateAsync(credId, default);
        var second = await reg.GetOrCreateAsync(credId, default);

        Assert.Equal(first, second);
        Assert.NotEqual(0u, first.SessionId);
        Assert.Equal(1ul, first.CurrentVer);

        // Idempotent: only one init event ever emitted.
        var inits = store.Recorded.OfType<BotSessionInitializedEvent>().ToList();
        Assert.Single(inits);
        Assert.Equal(credId, inits[0].CredentialId);
        Assert.Equal(first.SessionId, inits[0].SessionId);
        Assert.Equal(1ul, inits[0].InitialVer);
    }

    [Fact]
    public async Task TryClaimActive_RejectsStaleVer_AndSecondConnection()
    {
        var reg = new InMemoryUserBotSessionRegistry();
        var credId = Guid.NewGuid();
        var state = await reg.GetOrCreateAsync(credId, default);

        Assert.False(await reg.TryClaimActiveAsync(credId, state.CurrentVer + 1, "c1", default));
        Assert.True(await reg.TryClaimActiveAsync(credId, state.CurrentVer, "c1", default));
        // Same connection re-claim is idempotent.
        Assert.True(await reg.TryClaimActiveAsync(credId, state.CurrentVer, "c1", default));
        // Different connection while c1 holds the slot is denied.
        Assert.False(await reg.TryClaimActiveAsync(credId, state.CurrentVer, "c2", default));

        await reg.ReleaseAsync(credId, "c1", default);
        Assert.True(await reg.TryClaimActiveAsync(credId, state.CurrentVer, "c2", default));
    }

    [Fact]
    public async Task BumpVersion_AppendsEventThenFlushes_FenceOrdering()
    {
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var reg = new InMemoryUserBotSessionRegistry(dispatcher, store);
        var credId = Guid.NewGuid();
        var state = await reg.GetOrCreateAsync(credId, default);

        await reg.BumpVersionAsync(credId, "single-active-violation", default);

        // RFC §4.8 fence: the FlushAsync call must happen *after* the
        // BotSessionVerAdvancedEvent has been queued. Snapshot the
        // chronological action log and assert the order.
        var actions = store.Actions;
        var bumpIdx = actions.FindIndex(a => a is RecordingEventStore.AppendAction app
            && app.Event is BotSessionVerAdvancedEvent);
        var flushIdx = actions.FindLastIndex(a => a is RecordingEventStore.FlushAction);
        Assert.True(bumpIdx >= 0, "BotSessionVerAdvancedEvent was not appended.");
        Assert.True(flushIdx > bumpIdx, "FlushAsync must follow the WAL append.");

        var afterBump = await reg.GetOrCreateAsync(credId, default);
        Assert.Equal(state.CurrentVer + 1, afterBump.CurrentVer);
    }

    [Fact]
    public async Task ConcurrentBumps_AreSerialisedAndStrictlyMonotonic()
    {
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var reg = new InMemoryUserBotSessionRegistry(dispatcher, store);
        var credId = Guid.NewGuid();
        await reg.GetOrCreateAsync(credId, default);

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => reg.BumpVersionAsync(credId, "test", default)))
            .ToArray();
        await Task.WhenAll(tasks);

        var advances = store.Recorded.OfType<BotSessionVerAdvancedEvent>().ToList();
        Assert.Equal(32, advances.Count);

        // Each advance must be (oldVer, oldVer+1) and the chain must
        // cover [1..33] without gaps regardless of dispatch order.
        var sortedByNew = advances.OrderBy(e => e.NewVer).ToList();
        for (var i = 0; i < sortedByNew.Count; i++)
        {
            Assert.Equal((ulong)(i + 1), sortedByNew[i].OldVer);
            Assert.Equal((ulong)(i + 2), sortedByNew[i].NewVer);
        }

        var final = await reg.GetOrCreateAsync(credId, default);
        Assert.Equal(33ul, final.CurrentVer);
    }

    [Fact]
    public async Task SnapshotAndRestore_RoundTripPreservesState()
    {
        var src = new InMemoryUserBotSessionRegistry();
        var credA = Guid.NewGuid();
        var credB = Guid.NewGuid();
        var stateA = await src.GetOrCreateAsync(credA, default);
        var stateB = await src.GetOrCreateAsync(credB, default);
        await src.BumpVersionAsync(credA, "test", default);

        var snap = src.Snapshot();

        var dst = new InMemoryUserBotSessionRegistry();
        dst.Restore(snap);

        var restoredA = await dst.GetOrCreateAsync(credA, default);
        var restoredB = await dst.GetOrCreateAsync(credB, default);
        Assert.Equal(stateA.SessionId, restoredA.SessionId);
        Assert.Equal(stateB.SessionId, restoredB.SessionId);
        Assert.Equal(stateA.CurrentVer + 1, restoredA.CurrentVer);
        Assert.Equal(stateB.CurrentVer, restoredB.CurrentVer);
    }

    [Fact]
    public async Task Replay_ReconstructsStateFromInitAndAdvanceEvents()
    {
        var reg = new InMemoryUserBotSessionRegistry();
        var credId = Guid.NewGuid();

        // Simulate event-store replay (no dispatcher in the loop).
        var state = new BotSessionState(credId, SessionId: 12345, CurrentVer: 1, LastCheckpointedOutboundSeq: 0);
        reg.ApplyInitialized(state);
        reg.ApplyVerAdvanced(credId, 2);
        reg.ApplyVerAdvanced(credId, 3);

        var got = await reg.GetOrCreateAsync(credId, default);
        Assert.Equal(12345u, got.SessionId);
        Assert.Equal(3ul, got.CurrentVer);
    }

    [Fact]
    public async Task BumpVersion_OnUnknownCredential_Throws()
    {
        var reg = new InMemoryUserBotSessionRegistry();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reg.BumpVersionAsync(Guid.NewGuid(), "test", default));
    }

    /// <summary>
    /// In-memory <see cref="IEventStore"/> double that records every
    /// <c>Append</c>/<c>FlushAsync</c> call in chronological order so
    /// tests can assert RFC §4.8 ordering invariants.
    /// </summary>
    private sealed class RecordingEventStore : IEventStore
    {
        public abstract record StoreAction;
        public sealed record AppendAction(long Seq, WalEvent Event) : StoreAction;
        public sealed record FlushAction : StoreAction;

        private readonly object _gate = new();
        public List<StoreAction> Actions { get; } = new();
        public ConcurrentQueue<WalEvent> Recorded { get; } = new();
        private long _seq;
        public long CurrentSeq => Interlocked.Read(ref _seq);

        public long Append(WalEvent evt)
        {
            var s = Interlocked.Increment(ref _seq);
            Recorded.Enqueue(evt);
            lock (_gate) Actions.Add(new AppendAction(s, evt));
            return s;
        }

        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload) => Append(evt);

        public ValueTask FlushAsync(CancellationToken ct = default)
        {
            lock (_gate) Actions.Add(new FlushAction());
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
