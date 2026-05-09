namespace B3.Trading.Application.UserBots;

/// <summary>
/// Sub-issue #172 (F). Per-credential allocator for the outbound
/// FIXP application-message sequence number. Seeded from
/// <c>BotSessionState.LastCheckpointedOutboundSeq</c> on first use; a
/// concurrent <see cref="Allocate"/> burst from the ER hot path is
/// safe via <see cref="Interlocked.Increment(ref long)"/>.
///
/// <para>The allocator is in-memory only; persistence is handled by
/// the periodic <c>BotSessionSeqAdvancedEvent</c> checkpointer
/// (RFC §4.8) — sub-issue G is the consumer that needs the durable
/// lower bound.</para>
/// </summary>
public sealed class BotOutboundSeqAllocator
{
    // We use long internally because Interlocked.Increment lacks a ulong
    // overload; the wire format is uint64 so callers cast to ulong on
    // the read path. The signed range gives us 2^63 sequence numbers per
    // credential, which exceeds the FIXP spec's effective lifetime by
    // many orders of magnitude.
    private long _next;

    /// <summary>Constructs an allocator seeded so the first <see cref="Allocate"/> returns <paramref name="seedSeq"/> + 1.</summary>
    public BotOutboundSeqAllocator(ulong seedSeq = 0)
    {
        if (seedSeq > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(seedSeq),
                "Seed seq exceeds Int64 range; the in-memory allocator does not support this lifetime.");
        _next = (long)seedSeq;
    }

    /// <summary>
    /// Returns the next monotonically increasing seq number. Concurrent
    /// callers each get a unique value — the FIXP outbound stream order
    /// is whatever order the callers' subsequent send observes.
    /// </summary>
    public ulong Allocate() => (ulong)Interlocked.Increment(ref _next);

    /// <summary>Current high-watermark (most recently allocated value, or seed if none).</summary>
    public ulong Current => (ulong)Interlocked.Read(ref _next);
}
