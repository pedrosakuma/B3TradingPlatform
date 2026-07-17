using System.Collections.Concurrent;

namespace B3.Trading.Application;

/// <summary>
/// Tracks one durable in-flight cancel per original order. A transient zero
/// marker claims the original before a new ClOrdID is allocated; only
/// WAL-backed intents are included in snapshots and replay.
/// </summary>
public sealed class PendingCancelRegistry
{
    private readonly ConcurrentDictionary<ulong, ulong> _byOriginalClOrdId = new();
    private readonly ConcurrentDictionary<ulong, ulong> _byCancelClOrdId = new();

    public PendingCancelClaim Claim(ulong originalClOrdId)
    {
        if (originalClOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(originalClOrdId));

        var spinner = new SpinWait();
        while (true)
        {
            if (_byOriginalClOrdId.TryAdd(originalClOrdId, 0))
                return PendingCancelClaim.Acquired;

            if (_byOriginalClOrdId.TryGetValue(originalClOrdId, out var existing)
                && existing != 0)
            {
                return PendingCancelClaim.Existing(existing);
            }

            spinner.SpinOnce();
        }
    }

    public bool CompleteClaim(ulong originalClOrdId, ulong cancelClOrdId)
    {
        if (cancelClOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(cancelClOrdId));
        if (!_byCancelClOrdId.TryAdd(cancelClOrdId, originalClOrdId))
            return false;
        if (_byOriginalClOrdId.TryUpdate(originalClOrdId, cancelClOrdId, 0))
            return true;
        _byCancelClOrdId.TryRemove(cancelClOrdId, out _);
        return false;
    }

    public bool ReleaseClaim(ulong originalClOrdId) =>
        ((ICollection<KeyValuePair<ulong, ulong>>)_byOriginalClOrdId)
            .Remove(new KeyValuePair<ulong, ulong>(originalClOrdId, 0));

    public bool TryAdd(ulong originalClOrdId, ulong cancelClOrdId)
    {
        if (originalClOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(originalClOrdId));
        if (cancelClOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(cancelClOrdId));
        if (!_byOriginalClOrdId.TryAdd(originalClOrdId, cancelClOrdId))
            return false;
        if (_byCancelClOrdId.TryAdd(cancelClOrdId, originalClOrdId))
            return true;
        _byOriginalClOrdId.TryRemove(originalClOrdId, out _);
        return false;
    }

    public bool TryGetByCancel(ulong cancelClOrdId, out ulong originalClOrdId) =>
        _byCancelClOrdId.TryGetValue(cancelClOrdId, out originalClOrdId);

    public bool TryConsumeByCancel(ulong cancelClOrdId, out ulong originalClOrdId)
    {
        if (_byCancelClOrdId.TryRemove(cancelClOrdId, out originalClOrdId))
        {
            ((ICollection<KeyValuePair<ulong, ulong>>)_byOriginalClOrdId)
                .Remove(new KeyValuePair<ulong, ulong>(originalClOrdId, cancelClOrdId));
            return true;
        }
        return false;
    }

    public bool TryConsumeByOriginal(ulong originalClOrdId, out ulong cancelClOrdId)
    {
        if (_byOriginalClOrdId.TryGetValue(originalClOrdId, out cancelClOrdId)
            && cancelClOrdId != 0
            && ((ICollection<KeyValuePair<ulong, ulong>>)_byOriginalClOrdId)
                .Remove(new KeyValuePair<ulong, ulong>(originalClOrdId, cancelClOrdId)))
        {
            _byCancelClOrdId.TryRemove(cancelClOrdId, out _);
            return true;
        }
        cancelClOrdId = 0;
        return false;
    }

    public IReadOnlyList<PendingCancelSnapshotEntry> Snapshot() =>
        _byCancelClOrdId
            .Select(static pair => new PendingCancelSnapshotEntry(pair.Value, pair.Key))
            .ToArray();

    public void Restore(IEnumerable<PendingCancelSnapshotEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _byOriginalClOrdId.Clear();
        _byCancelClOrdId.Clear();
        foreach (var entry in entries)
            TryAdd(entry.OriginalClOrdId, entry.CancelClOrdId);
    }

    internal int CountForTesting => _byCancelClOrdId.Count;
}

public readonly record struct PendingCancelClaim(bool IsAcquired, ulong ExistingCancelClOrdId)
{
    public static PendingCancelClaim Acquired { get; } = new(true, 0);
    public static PendingCancelClaim Existing(ulong cancelClOrdId) => new(false, cancelClOrdId);
}

public readonly record struct PendingCancelSnapshotEntry(ulong OriginalClOrdId, ulong CancelClOrdId);
