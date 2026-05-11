namespace B3.Trading.Application.Persistence;

/// <summary>
/// Append-only event log abstraction. Two implementations:
/// <list type="bullet">
///   <item><c>FileEventStore</c> — the production store: segmented files,
///   length+CRC framing, sparse index, async write-behind with bounded
///   backpressure.</item>
///   <item><c>NullEventStore</c> — degenerate, in-memory, used when
///   persistence is disabled (integration tests, ephemeral demos).</item>
/// </list>
///
/// <para>
/// Sequence numbers are assigned synchronously on <see cref="Append"/>
/// and are strictly monotonic across the lifetime of a store instance.
/// They start from 1 on a fresh store; on recovery they resume after the
/// highest seq found on disk. <see cref="CurrentSeq"/> reflects the last
/// assigned seq (whether or not it has been flushed).
/// </para>
/// </summary>
public interface IEventStore : IAsyncDisposable
{
    long CurrentSeq { get; }

    /// <summary>
    /// Synchronously assigns a sequence number to <paramref name="evt"/> and
    /// queues it for the background writer. Throws
    /// <see cref="WalBackpressureException"/> when the bounded queue is
    /// full — callers should surface that as a structured "system busy"
    /// rejection rather than block.
    /// </summary>
    long Append(WalEvent evt);

    /// <summary>
    /// RFC §5.1 (F1) fast path: identical to <see cref="Append(WalEvent)"/>
    /// but the caller has already JSON-serialised <paramref name="evt"/>
    /// (e.g. via <c>WalEventJsonContext.Default.WalEvent</c>) and supplies
    /// the bytes in <paramref name="preSerialisedPayload"/>. The store
    /// MUST treat the payload as the canonical on-disk representation —
    /// it is byte-for-byte what the legacy overload would have produced
    /// from the same event under the same options. This lets
    /// <see cref="EventDispatcher"/> hoist the (reflection-replacing,
    /// allocation-heavy) serialisation step out of the dispatcher
    /// critical section while keeping seq assignment + channel enqueue
    /// strictly under the lock so that total WAL ordering (RFC §4.1) is
    /// preserved.
    /// </summary>
    long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload);

    /// <summary>
    /// Awaits durable persistence of every event appended so far.
    /// Used by the snapshot service before recording the snapshot's
    /// reference seq, and at graceful shutdown.
    /// </summary>
    ValueTask FlushAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads events in seq order with <c>seq &gt; sinceSeqExclusive</c>.
    /// Used by recovery to replay past a snapshot, and by the EOD
    /// materialiser to walk a day's segment tree.
    /// </summary>
    IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(long sinceSeqExclusive, CancellationToken ct = default);
}

public sealed class WalBackpressureException : Exception
{
    public WalBackpressureException(string message) : base(message) { }
}
