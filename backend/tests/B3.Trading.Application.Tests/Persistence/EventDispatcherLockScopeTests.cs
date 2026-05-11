using System.Runtime.CompilerServices;
using System.Text.Json;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// Pins the RFC §5.1 (F1) contract for <see cref="EventDispatcher.Dispatch"/>:
/// JSON serialisation of the WAL payload happens <i>outside</i> the
/// dispatcher lock, but seq assignment + <c>apply()</c> still execute
/// strictly under one lock so total WAL ordering (RFC §4.1) is preserved.
/// </summary>
public class EventDispatcherLockScopeTests
{
    [Fact]
    public void Dispatch_ForwardsPreSerialisedPayload_ByteIdenticalToContextSerialisation()
    {
        var store = new RecordingStore();
        var dispatcher = new EventDispatcher(store);
        var evt = new SymbolHaltToggledEvent
        {
            Symbol = "PETR4",
            Halted = true,
            ActorUserId = "alice",
        };

        var seq = dispatcher.Dispatch(evt, () => { });

        Assert.Equal(1, seq);
        var (capturedEvt, capturedPayload) = Assert.Single(store.Appended);
        Assert.Same(evt, capturedEvt);
        var expected = JsonSerializer.SerializeToUtf8Bytes(evt, WalEventJsonContext.Default.WalEvent);
        Assert.Equal(expected, capturedPayload);
    }

    [Fact]
    public void Dispatch_DoesNotInvokeLegacyAppendOverload()
    {
        // F1 must route exclusively through the (evt, payload) overload —
        // the legacy Append(evt) path serialises under the lock and would
        // re-introduce the cost we just removed.
        var store = new RecordingStore();
        var dispatcher = new EventDispatcher(store);

        dispatcher.Dispatch(new SymbolHaltToggledEvent { Symbol = "X", Halted = false, ActorUserId = "a" }, () => { });

        Assert.Equal(0, store.LegacyAppendCalls);
        Assert.Single(store.Appended);
    }

    [Fact]
    public void Dispatch_TotalOrdering_AppendAndApplyInterleaveUnderLock()
    {
        // §4.1: under N concurrent Dispatch calls, the apply() callbacks
        // observe each other in the same order as the WAL seq. We assert
        // this by recording the seq each apply() observes and checking
        // they form the natural order 1..N. The legacy lock guaranteed
        // this trivially; the narrowed lock (F1) must keep the property
        // because Append(payload) + apply() still share one lock.
        var store = new RecordingStore();
        var dispatcher = new EventDispatcher(store);
        var appliedSeqs = new List<long>();
        var gate = new object();
        long lastSeqApplied = 0;

        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                dispatcher.Dispatch(new SymbolHaltToggledEvent
                {
                    Symbol = "X",
                    Halted = (i & 1) == 0,
                    ActorUserId = "t",
                }, () =>
                {
                    var seqNow = store.LastSeq;
                    lock (gate)
                    {
                        Assert.True(seqNow > lastSeqApplied,
                            $"apply() observed seq {seqNow} after seq {lastSeqApplied}; lock narrowing broke §4.1.");
                        lastSeqApplied = seqNow;
                        appliedSeqs.Add(seqNow);
                    }
                });
            }
        })).ToArray();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.Equal(8 * 200, appliedSeqs.Count);
        Assert.Equal(Enumerable.Range(1, 8 * 200).Select(i => (long)i), appliedSeqs);
    }

    private sealed class RecordingStore : IEventStore
    {
        public List<(WalEvent Evt, byte[] Payload)> Appended { get; } = new();
        public int LegacyAppendCalls;
        private long _seq;
        public long CurrentSeq => Interlocked.Read(ref _seq);
        public long LastSeq => Interlocked.Read(ref _seq);

        public long Append(WalEvent evt)
        {
            Interlocked.Increment(ref LegacyAppendCalls);
            return Interlocked.Increment(ref _seq);
        }

        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            Appended.Add((evt, preSerialisedPayload.ToArray()));
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
