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
