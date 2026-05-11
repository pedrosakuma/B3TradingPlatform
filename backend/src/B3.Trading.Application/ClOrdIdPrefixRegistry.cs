using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Allocates per-end-client ClOrdID prefixes and generates fresh ClOrdIDs.
///
/// <para>
/// <b>Encoding scheme</b> (decided in #7): packed <c>ulong</c>:
/// <c>(prefixIdx &lt;&lt; 40) | counter</c>. <c>prefixIdx</c> is the
/// monotonic per-deployment prefix index (capped at 2^21, defensive
/// bound; matches the previous base-36 4-char width); <c>counter</c>
/// is per-end-client, monotonic, advanced atomically.
/// </para>
///
/// <para>
/// <b>Layout</b>:
/// <list type="bullet">
///   <item>bits 40..60 — <c>prefixIdx</c> (≤ 2^21 ≈ 2M end-clients)</item>
///   <item>bits 0..39 — <c>counter</c> (≤ 2^40 ≈ 1.1T orders/end-client)</item>
///   <item>bit 63 (MSB) — always 0; we stay safely under <c>long.MaxValue</c></item>
/// </list>
/// The first generated value is <c>(0 &lt;&lt; 40) | 1 = 1</c>; zero is
/// never produced (matches EntryPoint <c>ClOrdID</c> non-zero invariant).
/// </para>
///
/// <para>
/// Prefix allocation is process-local and resets on restart; the registry
/// snapshot (Phase 6) restores the watermark on recovery.
/// </para>
/// </summary>
public sealed class ClOrdIdPrefixRegistry
{
    public const int CounterBits = 40;
    public const ulong CounterMask = (1UL << CounterBits) - 1;
    public const long MaxPrefixIndex = 1L << 21;

    private readonly ConcurrentDictionary<EndClientId, EndClientCounter> _counters = new();
    private long _nextPrefix;

    public ulong AllocatePrefix(EndClientId endClient)
    {
        ArgumentNullException.ThrowIfNull(endClient);
        return _counters.GetOrAdd(endClient, CreateCounter).PrefixIdx;
    }

    public ulong Generate(EndClientId endClient)
    {
        ArgumentNullException.ThrowIfNull(endClient);
        var entry = _counters.GetOrAdd(endClient, CreateCounter);
        var seq = (ulong)Interlocked.Increment(ref entry.Counter);
        if (seq > CounterMask)
            throw new InvalidOperationException($"ClOrdID counter overflow for end-client {endClient.Value} (>2^{CounterBits}).");
        return (entry.PrefixIdx << CounterBits) | seq;
    }

    private EndClientCounter CreateCounter(EndClientId _)
    {
        var idx = Interlocked.Increment(ref _nextPrefix) - 1;
        if (idx >= MaxPrefixIndex)
        {
            throw new InvalidOperationException("ClOrdID prefix space exhausted (>2M end-clients). Widen prefix bits.");
        }
        return new EndClientCounter((ulong)idx);
    }

    public Persistence.ClOrdIdRegistrySnapshot Snapshot()
    {
        var snap = new Persistence.ClOrdIdRegistrySnapshot
        {
            NextPrefix = Interlocked.Read(ref _nextPrefix),
        };
        foreach (var kv in _counters)
        {
            snap.Counters.Add(new Persistence.ClOrdIdCounterSnapshot(
                kv.Key.Value, kv.Value.PrefixIdx, Interlocked.Read(ref kv.Value.Counter)));
        }
        return snap;
    }

    /// <summary>
    /// Phase-1 (lock-side) capture for the two-phase snapshot pipeline
    /// (RFC §5.8 / P6). Same monotonic <see cref="Interlocked.Read"/>
    /// reads as <see cref="Snapshot"/>; defers the
    /// <see cref="Persistence.ClOrdIdRegistrySnapshot"/> DTO + inner
    /// <c>List&lt;T&gt;</c> allocation to the projection step.
    /// </summary>
    public Persistence.ClOrdIdRegistryRaw RawSnapshot()
    {
        var pairs = _counters.ToArray();
        if (pairs.Length == 0)
            return new Persistence.ClOrdIdRegistryRaw(Interlocked.Read(ref _nextPrefix), Array.Empty<Persistence.ClOrdIdCounterRaw>());
        var raw = new Persistence.ClOrdIdCounterRaw[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
        {
            raw[i] = new Persistence.ClOrdIdCounterRaw(
                pairs[i].Key.Value, pairs[i].Value.PrefixIdx,
                Interlocked.Read(ref pairs[i].Value.Counter));
        }
        return new Persistence.ClOrdIdRegistryRaw(Interlocked.Read(ref _nextPrefix), raw);
    }

    public void Restore(Persistence.ClOrdIdRegistrySnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(snap);
        _counters.Clear();
        Interlocked.Exchange(ref _nextPrefix, snap.NextPrefix);
        foreach (var c in snap.Counters)
        {
            var entry = new EndClientCounter(c.PrefixIdx) { Counter = c.Counter };
            _counters[new EndClientId(c.EndClientId)] = entry;
        }
    }

    /// <summary>
    /// Monotonically advances the per-end-client counter and the global
    /// prefix watermark to reflect a ClOrdID observed during WAL replay
    /// (#157). Closes the snapshot/WAL-replay regression that #156's
    /// defensive guard turned into a 409 — without this, after recovery
    /// the next <see cref="Generate"/> for an end-client whose post-snapshot
    /// activity advanced its counter would re-allocate IDs already in
    /// the book.
    ///
    /// <para><b>Replay-only.</b> Single-threaded at startup, before the
    /// host accepts traffic. The CAS loops are correct under contention
    /// but the API is not designed to coexist with concurrent
    /// <see cref="Generate"/> on the same end-client (a live request
    /// could win <c>GetOrAdd</c> with a freshly allocated prefix that
    /// disagrees with the persisted prefix, leaving the entry split).</para>
    ///
    /// <para><b>Validation.</b> A structurally invalid <paramref name="observedClOrdId"/>
    /// (zero counter — never produced by <see cref="Generate"/>; or
    /// prefix outside <see cref="MaxPrefixIndex"/>) emits the
    /// <c>ClOrdIdRegistryCorruption</c> metric and is dropped. We do
    /// not touch state from data we cannot trust.</para>
    ///
    /// <para><b>Prefix mismatch.</b> If <paramref name="endClient"/> is
    /// already registered with a different <c>prefixIdx</c>, the existing
    /// entry is preserved (overwriting would invalidate every ID already
    /// generated under the live prefix). The corruption metric fires.
    /// Critically, the observed prefix is still globally reserved via
    /// <c>_nextPrefix</c> so a future end-client allocation does not
    /// reuse it and produce fresh collisions.</para>
    /// </summary>
    public void AdvanceCounterTo(EndClientId endClient, ulong observedClOrdId)
    {
        ArgumentNullException.ThrowIfNull(endClient);

        var prefixIdx = observedClOrdId >> CounterBits;
        var counter = (long)(observedClOrdId & CounterMask);

        if (counter == 0 || prefixIdx >= (ulong)MaxPrefixIndex)
        {
            Observability.MetricsRegistry.ClOrdIdRegistryCorruption.Add(1,
                new KeyValuePair<string, object?>("end_client", endClient.Value),
                new KeyValuePair<string, object?>("reason", "invalid_observed_clordid"));
            return;
        }

        // Always reserve the observed prefix globally — even if the
        // per-end-client mismatch path below bails early, we cannot
        // leave this prefix slot available for a future fresh allocation.
        AdvanceNextPrefixPast((long)prefixIdx);

        var entry = _counters.GetOrAdd(endClient, _ => new EndClientCounter(prefixIdx));
        if (entry.PrefixIdx != prefixIdx)
        {
            Observability.MetricsRegistry.ClOrdIdRegistryCorruption.Add(1,
                new KeyValuePair<string, object?>("end_client", endClient.Value),
                new KeyValuePair<string, object?>("reason", "prefix_mismatch"));
            return;
        }

        long current;
        do
        {
            current = Interlocked.Read(ref entry.Counter);
            if (current >= counter) return;
        } while (Interlocked.CompareExchange(ref entry.Counter, counter, current) != current);
    }

    private void AdvanceNextPrefixPast(long observedPrefix)
    {
        long current;
        var target = observedPrefix + 1;
        do
        {
            current = Interlocked.Read(ref _nextPrefix);
            if (current >= target) return;
        } while (Interlocked.CompareExchange(ref _nextPrefix, target, current) != current);
    }

    private sealed class EndClientCounter
    {
        public readonly ulong PrefixIdx;
        public long Counter;

        public EndClientCounter(ulong prefixIdx) => PrefixIdx = prefixIdx;
    }
}
