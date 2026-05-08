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

    /// <summary>
    /// Monotonically advances the per-firm counter to reflect an
    /// <c>AlgoId</c> observed during WAL replay (#160). Mirrors the
    /// fix from <see cref="ClOrdIdPrefixRegistry.AdvanceCounterTo"/>:
    /// without this, after a snapshot+replay roundtrip the next
    /// <see cref="Generate"/> for a firm whose post-snapshot activity
    /// advanced its counter would re-issue an <c>AlgoId</c> already
    /// owned by an algo restored from the WAL.
    ///
    /// <para><b>Replay-only.</b> Single-threaded at startup, before
    /// the host accepts traffic. Not safe to interleave with concurrent
    /// <see cref="Generate"/>.</para>
    ///
    /// <para><b>Validation.</b> Zero is never produced by
    /// <see cref="Generate"/> and is treated as corrupt — emits the
    /// <c>AlgoIdRegistryCorruption</c> metric and is dropped.</para>
    /// </summary>
    public void AdvanceCounterTo(string firmId, ulong observedAlgoId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);

        if (observedAlgoId == 0)
        {
            Observability.MetricsRegistry.AlgoIdRegistryCorruption.Add(1,
                new KeyValuePair<string, object?>("firm", firmId),
                new KeyValuePair<string, object?>("reason", "invalid_observed_algoid"));
            return;
        }

        var target = (long)observedAlgoId;
        var entry = _counters.GetOrAdd(firmId, static _ => new FirmCounter());
        long current;
        do
        {
            current = Interlocked.Read(ref entry.Counter);
            if (current >= target) return;
        } while (Interlocked.CompareExchange(ref entry.Counter, target, current) != current);
    }

    private sealed class FirmCounter
    {
        public long Counter;
    }
}
