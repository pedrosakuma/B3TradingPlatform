using System.Runtime.CompilerServices;
using System.Text.Json;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// Pins the RFC §5.2 (F2) contract for
/// <see cref="EventDispatcher.Dispatch(WalEvent, Action{ExecutionFanOut})"/>:
/// the apply callback runs under the dispatcher lock and records its
/// outcome onto an <see cref="ExecutionFanOut"/> writer; the dispatcher
/// then TryWrites every captured event onto each registered
/// <see cref="IExecutionFanOutSink"/> WHILE STILL HOLDING THE LOCK so
/// per-sink observation order matches WAL append order even under
/// concurrent dispatch (RFC §4.1).
/// </summary>
public class EventDispatcherFanOutTests
{
    [Fact]
    public void Dispatch_FanOut_EnqueuesCapturedEventsToEverySink_UnderTheLock()
    {
        var store = new RecordingStore();
        var ws = new RecordingSink(ExecutionFanOutTargets.WsHub);
        var bot = new RecordingSink(ExecutionFanOutTargets.BotRouter);
        var dispatcher = new EventDispatcher(store, new IExecutionFanOutSink[] { ws, bot });
        var ev = NewEvent(1);

        var seq = dispatcher.Dispatch(NewWalEvent(), fanOut => fanOut.Add(ev));

        Assert.Equal(1, seq);
        var (wsSeq, wsEv) = Assert.Single(ws.Items);
        Assert.Equal(1, wsSeq);
        Assert.Equal(ev, wsEv);
        var (botSeq, botEv) = Assert.Single(bot.Items);
        Assert.Equal(1, botSeq);
        Assert.Equal(ev, botEv);
    }

    [Fact]
    public void Dispatch_FanOut_RespectsTargetMask_BotOnlyEventSkipsWsHub()
    {
        // Synthetic replace-rejected ER is meaningful only to the bot
        // router (no in-book Order exists for the replace-side ClOrdID).
        var store = new RecordingStore();
        var ws = new RecordingSink(ExecutionFanOutTargets.WsHub);
        var bot = new RecordingSink(ExecutionFanOutTargets.BotRouter);
        var dispatcher = new EventDispatcher(store, new IExecutionFanOutSink[] { ws, bot });
        var ev = NewEvent(42);

        dispatcher.Dispatch(NewWalEvent(), fanOut => fanOut.Add(ev, ExecutionFanOutTargets.BotRouter));

        Assert.Empty(ws.Items);
        Assert.Single(bot.Items);
    }

    [Fact]
    public void Dispatch_FanOut_PerSinkOrderMatchesWalSeq_UnderConcurrentDispatch()
    {
        // §4.1 ordering invariant under stress: 8 threads × 200 dispatches
        // each, two sinks. Per-sink observed seq sequence must be exactly
        // 1..1600 in order. A naive lift-and-shift ("release the lock then
        // call Publish") would surface here as a non-monotonic seq on at
        // least one sink — see RFC §5.2 ordering note.
        const int threads = 8;
        const int perThread = 200;
        var store = new RecordingStore();
        var ws = new RecordingSink(ExecutionFanOutTargets.WsHub);
        var bot = new RecordingSink(ExecutionFanOutTargets.BotRouter);
        var dispatcher = new EventDispatcher(store, new IExecutionFanOutSink[] { ws, bot });

        var workers = new Thread[threads];
        for (var t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    dispatcher.Dispatch(NewWalEvent(), fanOut =>
                    {
                        fanOut.Add(NewEvent((ulong)store.LastSeq + 1));
                    });
                }
            });
        }
        foreach (var w in workers) w.Start();
        foreach (var w in workers) w.Join();

        AssertMonotonic(ws.Items, threads * perThread);
        AssertMonotonic(bot.Items, threads * perThread);
    }

    [Fact]
    public void Dispatch_FanOut_NonBlockingEnqueueRunsUnderLock_NoSinkBackpressure()
    {
        // Sink that observes whether Enqueue is called while another
        // dispatcher operation is concurrently in flight. Under correct
        // implementation, no two enqueues from different dispatch threads
        // overlap because they share the dispatcher lock.
        var store = new RecordingStore();
        var concurrencyDetector = new ConcurrencyDetectingSink();
        var dispatcher = new EventDispatcher(store, new IExecutionFanOutSink[] { concurrencyDetector });

        const int threads = 8;
        const int perThread = 200;
        var workers = new Thread[threads];
        for (var t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    dispatcher.Dispatch(NewWalEvent(), fanOut => fanOut.Add(NewEvent(1)));
                }
            });
        }
        foreach (var w in workers) w.Start();
        foreach (var w in workers) w.Join();

        Assert.Equal(0, concurrencyDetector.OverlapCount);
        Assert.Equal(threads * perThread, concurrencyDetector.Calls);
    }

    [Fact]
    public void Dispatch_FanOut_NoSinksRegistered_StillSerialisesAndAppends()
    {
        // Test contexts that don't wire any fan-out sinks must still get
        // a working dispatcher: append + apply happen, the captured
        // events are simply discarded.
        var store = new RecordingStore();
        var dispatcher = new EventDispatcher(store);
        var captured = 0;

        var seq = dispatcher.Dispatch(NewWalEvent(), fanOut =>
        {
            fanOut.Add(NewEvent(1));
            captured++;
        });

        Assert.Equal(1, seq);
        Assert.Equal(1, captured);
        Assert.Single(store.Appended);
    }

    [Fact]
    public void Dispatch_FanOut_EmptyApply_IsLockOnlyAndSinksNotInvoked()
    {
        var store = new RecordingStore();
        var ws = new RecordingSink(ExecutionFanOutTargets.WsHub);
        var dispatcher = new EventDispatcher(store, new IExecutionFanOutSink[] { ws });

        dispatcher.Dispatch(NewWalEvent(), fanOut => { /* nothing captured */ });

        Assert.Single(store.Appended);
        Assert.Empty(ws.Items);
    }

    private static void AssertMonotonic(IReadOnlyList<(long Seq, ExecutionEvent Ev)> items, int expectedCount)
    {
        Assert.Equal(expectedCount, items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            Assert.Equal(i + 1, items[i].Seq);
        }
    }

    private static WalEvent NewWalEvent() => new SymbolHaltToggledEvent
    {
        Symbol = "PETR4",
        Halted = false,
        ActorUserId = "t",
    };

    private static ExecutionEvent NewEvent(ulong clOrdId) => new(
        Owner: new EndClientId("alice"),
        ClOrdId: clOrdId,
        Symbol: "PETR4",
        Side: OrderSide.Buy,
        Status: OrderStatus.Working,
        Kind: ExecKind.New,
        LeavesQuantity: 100,
        CumulativeQuantity: 0,
        LastQuantity: 0,
        LastPrice: 0m,
        RejectReason: null,
        TimestampUtc: DateTimeOffset.UtcNow);

    private sealed class RecordingSink : IExecutionFanOutSink
    {
        private readonly List<(long Seq, ExecutionEvent Ev)> _items = new();
        public RecordingSink(ExecutionFanOutTargets target) => Target = target;
        public ExecutionFanOutTargets Target { get; }
        public IReadOnlyList<(long Seq, ExecutionEvent Ev)> Items => _items;
        public void Enqueue(long seq, ExecutionEvent ev)
        {
            // Single-writer semantics from the dispatcher's perspective
            // (only one thread is inside the dispatch lock at a time);
            // the lock guarantees we can append without our own lock.
            _items.Add((seq, ev));
        }
    }

    private sealed class ConcurrencyDetectingSink : IExecutionFanOutSink
    {
        private int _inside;
        public int Calls;
        public int OverlapCount;
        public ExecutionFanOutTargets Target => ExecutionFanOutTargets.All;
        public void Enqueue(long seq, ExecutionEvent ev)
        {
            if (Interlocked.Increment(ref _inside) > 1)
                Interlocked.Increment(ref OverlapCount);
            // Tiny spin so any racing dispatch thread has a chance to
            // collide; under the correct (lock-held) implementation the
            // spin is wasted but no overlap is possible.
            for (var i = 0; i < 8; i++) Thread.SpinWait(64);
            Interlocked.Increment(ref Calls);
            Interlocked.Decrement(ref _inside);
        }
    }

    private sealed class RecordingStore : IEventStore
    {
        public List<(WalEvent Evt, byte[] Payload)> Appended { get; } = new();
        private long _seq;
        public long CurrentSeq => Interlocked.Read(ref _seq);
        public long LastSeq => Interlocked.Read(ref _seq);
        public long Append(WalEvent evt) => Interlocked.Increment(ref _seq);
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
