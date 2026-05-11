using BenchmarkDotNet.Attributes;

using B3.Trading.Application.Persistence;
using B3.Trading.Infrastructure.Persistence;

namespace B3.Trading.Benchmarks.Benches;

/// <summary>
/// RFC §7.1 — baseline for the dispatcher critical section that F1
/// targets. Measures pure <see cref="EventDispatcher.Dispatch"/> cost
/// against <see cref="NullEventStore"/> so the number reflects the lock
/// + delegate invocation, isolated from disk I/O.
///
/// <para>Acceptance gate (RFC §7.3 / F1): post-fix throughput must be
/// ≥3× baseline and AllocatedBytes must drop by ≥50%. P3's PR is
/// expected to record before/after numbers in its body referencing this
/// bench by name.</para>
/// </summary>
[MemoryDiagnoser]
public class EventDispatcher_Dispatch_Bench
{
    private EventDispatcher _dispatcher = null!;
    private WalEvent _evt = null!;

    [GlobalSetup]
    public void Setup()
    {
        var store = new NullEventStore();
        _dispatcher = new EventDispatcher(store);
        _evt = new SymbolHaltToggledEvent
        {
            Symbol = "PETR4",
            Halted = true,
            ActorUserId = "bench",
        };
    }

    [Benchmark]
    public long Dispatch()
        => _dispatcher.Dispatch(_evt, static () => { });
}
