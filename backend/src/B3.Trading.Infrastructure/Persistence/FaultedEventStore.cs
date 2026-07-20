using B3.Trading.Application.Persistence;

namespace B3.Trading.Infrastructure.Persistence;

public sealed class FaultedEventStore : IEventStore, IEventStoreHealth
{
    private readonly Exception _failure;

    public FaultedEventStore(Exception failure)
    {
        _failure = failure;
    }

    public long CurrentSeq => 0;
    public bool IsHealthy => false;
    public Exception? TerminalFault => _failure;

    public long Append(WalEvent evt) => throw CreateFault();

    public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload) =>
        throw CreateFault();

    public ValueTask FlushAsync(CancellationToken ct = default) =>
        ValueTask.FromException(CreateFault());

    public ValueTask FlushThroughAsync(long seq, CancellationToken ct = default) =>
        ValueTask.FromException(CreateFault());

    public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
        long sinceSeqExclusive,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private WalFaultedException CreateFault() =>
        new("The WAL is unavailable because active-host fencing or recovery failed.", _failure);
}

public sealed class FaultedReconciliationMarkerStore : IReconciliationMarkerStore
{
    private readonly Exception _failure;

    public FaultedReconciliationMarkerStore(Exception failure)
    {
        _failure = failure;
    }

    public void Persist(ReconciliationMarker marker) =>
        throw new ReconciliationMarkerPersistException(
            "The reconciliation marker store is unavailable because active-host fencing failed.",
            durablyPublished: false,
            _failure);

    public void Remove(string markerId) =>
        throw new IOException(
            "The reconciliation marker store is unavailable because active-host fencing failed.",
            _failure);

    public IReadOnlyList<ReconciliationMarker> Load() =>
        throw new IOException(
            "The reconciliation marker store is unavailable because active-host fencing failed.",
            _failure);
}
