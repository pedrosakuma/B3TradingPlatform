using System.Runtime.CompilerServices;
using System.Text.Json;

using BenchmarkDotNet.Attributes;

using B3.Trading.Application.Persistence;
using B3.Trading.Infrastructure.Persistence;

namespace B3.Trading.Benchmarks.Benches;

/// <summary>
/// RFC §7.1 — baseline for the dispatcher critical section that F1
/// targets. Two configurations:
/// <list type="bullet">
///   <item><c>Dispatch</c> against a <see cref="NullEventStore"/> — pure
///   lock + delegate cost, isolated from any serialisation work.</item>
///   <item><c>Dispatch_WithSerialisingStore</c> against a store whose
///   legacy <c>Append(evt)</c> path mirrors the production
///   <see cref="FileEventStore"/>'s source-gen serialise step. This is
///   the configuration the F1 acceptance gate is expressed against —
///   pre-fix the JSON serialisation runs <i>under</i> the dispatcher
///   lock; post-fix the dispatcher hoists it out and the store's
///   <c>Append(evt, payload)</c> overload skips it.</item>
/// </list>
///
/// <para>Acceptance gate (RFC §7.3 / F1): post-fix throughput must be
/// ≥3× baseline and AllocatedBytes must drop by ≥50% on the
/// serialising-store variant. P3's PR records before/after numbers in
/// its body referencing this bench by name.</para>
/// </summary>
[MemoryDiagnoser]
public class EventDispatcher_Dispatch_Bench
{
    private EventDispatcher _dispatcher = null!;
    private EventDispatcher _dispatcherSerialising = null!;
    private WalEvent _evt = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dispatcher = new EventDispatcher(new NullEventStore());
        _dispatcherSerialising = new EventDispatcher(new SerialisingNullEventStore());
        _evt = new SymbolHaltToggledEvent
        {
            Symbol = "PETR4",
            Halted = true,
            ActorUserId = "bench",
        };
    }

    [Benchmark(Baseline = true)]
    public long Dispatch()
        => _dispatcher.Dispatch(_evt, static () => { });

    [Benchmark]
    public long Dispatch_WithSerialisingStore()
        => _dispatcherSerialising.Dispatch(_evt, static () => { });

    /// <summary>
    /// Eight-thread contention bench against the serialising store —
    /// this is the configuration that exposes F1's lock-narrowing win.
    /// Pre-fix the JSON serialise runs <i>under</i> the dispatcher lock
    /// so contention scales with serialise cost; post-fix the lock-held
    /// window is just (seq increment + channel TryWrite + apply()), so
    /// 8 threads can dispatch in parallel limited only by the channel.
    /// </summary>
    [Benchmark]
    [Arguments(8, 1024)]
    public void Dispatch_Concurrent_SerialisingStore(int threads, int dispatchesPerThread)
    {
        var evt = _evt;
        var dispatcher = _dispatcherSerialising;
        var workers = new Thread[threads];
        for (var t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                for (var i = 0; i < dispatchesPerThread; i++)
                    dispatcher.Dispatch(evt, static () => { });
            });
        }
        foreach (var w in workers) w.Start();
        foreach (var w in workers) w.Join();
    }

    /// <summary>
    /// In-bench <see cref="IEventStore"/> double that mirrors the
    /// production <see cref="FileEventStore"/>'s serialise step. The
    /// legacy <c>Append(evt)</c> path runs the source-gen serialise
    /// (modelling the pre-F1 behaviour where this happened under the
    /// dispatcher lock); the F1 <c>Append(evt, payload)</c> overload
    /// trusts the caller and skips it.
    /// </summary>
    private sealed class SerialisingNullEventStore : IEventStore
    {
        private long _seq;
        public long CurrentSeq => Interlocked.Read(ref _seq);

        public long Append(WalEvent evt)
        {
            _ = JsonSerializer.SerializeToUtf8Bytes(evt, WalEventJsonContext.Default.WalEvent);
            return Interlocked.Increment(ref _seq);
        }

        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload) =>
            Interlocked.Increment(ref _seq);

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
