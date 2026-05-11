using B3.Trading.Application.Persistence;

namespace B3.Trading.Infrastructure.Persistence;

/// <summary>
/// Drop-in <see cref="IEventStore"/> that throws every event away.
/// Wired when <c>Trading:Persistence:Enabled = false</c>; lets the
/// integration tests, ephemeral demos, and the existing
/// <c>WebApplicationFactory</c>-based suite stay file-system free.
/// </summary>
public sealed class NullEventStore : IEventStore
{
    private long _seq;

    public long CurrentSeq => Interlocked.Read(ref _seq);

    public long Append(WalEvent evt) => Interlocked.Increment(ref _seq);

    public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload) =>
        Interlocked.Increment(ref _seq);

    public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
        long sinceSeqExclusive,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
