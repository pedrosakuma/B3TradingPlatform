using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Allocates per-end-client ClOrdID prefixes and generates fresh ClOrdIDs.
///
/// <para>
/// <b>Encoding scheme</b> (decided in #2): <c>{prefix}-{counter:D12}</c>,
/// 17 chars total — comfortably under the 20-char EntryPoint limit.
/// </para>
///
/// <list type="bullet">
///   <item>
///     <c>prefix</c> — 4 chars, base36 (<c>0-9a-z</c>), zero-padded.
///     Allocated by this registry on first use of an
///     <see cref="EndClientId"/>; idempotent for repeat lookups. Capacity:
///     36⁴ = 1,679,616 distinct end-clients per platform deployment, which
///     is plenty for the participant-side scope.
///   </item>
///   <item>
///     <c>counter</c> — 12-digit zero-padded decimal, per-end-client
///     monotonic, advanced atomically with <see cref="Interlocked"/>.
///     Capacity: 10¹² orders per end-client (≈ 30k orders per second for
///     a year), which we will never approach in practice.
///   </item>
/// </list>
///
/// Prefix allocation is process-local and resets on restart. Persistence
/// of the allocation is a Phase 6 concern; until then, restart is
/// equivalent to "new platform" from an EntryPoint correlation
/// standpoint, which is acceptable because Phase 1 is ephemeral by design.
/// </summary>
public sealed class ClOrdIdPrefixRegistry
{
    private const int PrefixWidth = 4;
    private const int CounterWidth = 12;
    private const string Base36 = "0123456789abcdefghijklmnopqrstuvwxyz";

    private readonly ConcurrentDictionary<EndClientId, EndClientCounter> _counters = new();
    private long _nextPrefix;

    public string AllocatePrefix(EndClientId endClient)
    {
        ArgumentNullException.ThrowIfNull(endClient);
        return _counters.GetOrAdd(endClient, CreateCounter).Prefix;
    }

    public string Generate(EndClientId endClient)
    {
        ArgumentNullException.ThrowIfNull(endClient);
        var entry = _counters.GetOrAdd(endClient, CreateCounter);
        var seq = Interlocked.Increment(ref entry.Counter);
        return string.Concat(entry.Prefix, "-", seq.ToString($"D{CounterWidth}"));
    }

    private EndClientCounter CreateCounter(EndClientId _)
    {
        var idx = Interlocked.Increment(ref _nextPrefix) - 1;
        if (idx >= 1L << 21) // 36^4 ≈ 2^20.7, defensive bound just in case
        {
            // Beyond 36^4 — would require a wider prefix; deliberate hard
            // failure rather than silent collision. Refactor to 5 chars
            // when this fires.
            throw new InvalidOperationException("ClOrdID prefix space exhausted (>1.6M end-clients).");
        }
        return new EndClientCounter(EncodeBase36(idx, PrefixWidth));
    }

    private static string EncodeBase36(long value, int width)
    {
        Span<char> buffer = stackalloc char[width];
        for (var i = width - 1; i >= 0; i--)
        {
            buffer[i] = Base36[(int)(value % 36)];
            value /= 36;
        }
        return new string(buffer);
    }

    private sealed class EndClientCounter
    {
        public readonly string Prefix;
        public long Counter;

        public EndClientCounter(string prefix) => Prefix = prefix;
    }
}
