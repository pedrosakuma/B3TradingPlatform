using System.Collections.Concurrent;

namespace B3.Trading.Application;

/// <summary>
/// Allocates monotonic <c>AlgoId</c>s scoped per firm. Mirrors the
/// firm-isolation model used everywhere else (snapshots, kill-switch,
/// FIXP sessions): two firms can independently issue <c>AlgoId = 1</c>
/// without collision because every API/WS payload carries the firm
/// context derived from the caller's auth claim.
///
/// <para>
/// Per-firm rather than per-end-client (unlike <see cref="ClOrdIdPrefixRegistry"/>):
/// algo creation is rare compared to order submission — a single
/// per-firm atomic counter has trivial contention at algo rates, so the
/// extra prefix-per-end-client trick is unnecessary in v0. Promotion to
/// per-end-client follows the same pattern if benchmarks ever justify it.
/// </para>
/// </summary>
public sealed class AlgoIdRegistry
{
    private readonly ConcurrentDictionary<string, FirmCounter> _counters =
        new(StringComparer.Ordinal);

    public ulong Generate(string firmId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        var entry = _counters.GetOrAdd(firmId, static _ => new FirmCounter());
        var seq = (ulong)Interlocked.Increment(ref entry.Counter);
        return seq;
    }

    public Persistence.AlgoIdRegistrySnapshot Snapshot()
    {
        var snap = new Persistence.AlgoIdRegistrySnapshot();
        foreach (var kv in _counters)
        {
            snap.Counters.Add(new Persistence.AlgoIdCounterSnapshot(
                kv.Key, Interlocked.Read(ref kv.Value.Counter)));
        }
        return snap;
    }

    public void Restore(Persistence.AlgoIdRegistrySnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(snap);
        _counters.Clear();
        foreach (var c in snap.Counters)
        {
            _counters[c.FirmId] = new FirmCounter { Counter = c.Counter };
        }
    }

    private sealed class FirmCounter
    {
        public long Counter;
    }
}
