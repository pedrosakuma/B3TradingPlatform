using B3.Trading.Application.Persistence;

namespace B3.Trading.Infrastructure.Persistence;

/// <summary>
/// Drop-in <see cref="IEventStore"/> that throws every event away.
/// Wired when <c>Trading:Persistence:Enabled = false</c>; lets the
/// integration tests, ephemeral demos, and the existing
/// <c>WebApplicationFactory</c>-based suite stay file-system free.
/// </summary>
public sealed class NullEventStore : IEventStore, IEventStoreHealth
{
    private long _seq;
    private readonly Guid _generation = Guid.NewGuid();

    public long CurrentSeq => Interlocked.Read(ref _seq);
    public Guid WalGeneration => _generation;
    public long LastAdmittedSeq => CurrentSeq;
    public long LastAppendedSeq => CurrentSeq;
    public long LastLogFsyncedSeq => CurrentSeq;
    public long LastCommittedSeq => CurrentSeq;
    public bool IsHealthy => true;
    public Exception? TerminalFault => null;

    public long Append(WalEvent evt) => Interlocked.Increment(ref _seq);

    public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload) =>
        Interlocked.Increment(ref _seq);

    public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask FlushThroughAsync(long seq, CancellationToken ct = default)
    {
        if (seq < 0 || seq > CurrentSeq)
            throw new ArgumentOutOfRangeException(nameof(seq));
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
        long sinceSeqExclusive,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
