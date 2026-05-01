using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// <c>ClOrdID → EndClientId</c> map. Populated on order submit; consulted
/// when an ExecutionReport arrives so the per-end-client domain state can
/// be mutated and the per-end-client subscription stream notified.
///
/// Participant-side mirror of
/// <c>B3MatchingPlatform/docs/B3-ENTRYPOINT-ARCHITECTURE.md §4.7
/// (OrderOwnershipMap)</c>; same shape, opposite direction of fan-out.
/// </summary>
public sealed class OrderOwnershipMap
{
    private readonly ConcurrentDictionary<ulong, EndClientId> _byClOrdId = new();

    public void Register(ulong clOrdId, EndClientId owner)
    {
        if (clOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(clOrdId), "ClOrdID cannot be zero.");
        ArgumentNullException.ThrowIfNull(owner);
        _byClOrdId[clOrdId] = owner;
    }

    public bool TryResolve(ulong clOrdId, out EndClientId? owner) =>
        _byClOrdId.TryGetValue(clOrdId, out owner);

    /// <summary>
    /// Re-key after a successful cancel-replace; the new ClOrdID inherits
    /// the same owner. The original key is kept so any in-flight ER for
    /// it (e.g. Replaced ack) can still resolve.
    /// </summary>
    public void RegisterReplacement(ulong originalClOrdId, ulong newClOrdId)
    {
        if (!_byClOrdId.TryGetValue(originalClOrdId, out var owner))
            throw new InvalidOperationException($"Unknown original ClOrdID '{originalClOrdId}'.");
        _byClOrdId[newClOrdId] = owner;
    }

    public IEnumerable<Persistence.OwnershipMappingSnapshot> Snapshot()
    {
        foreach (var kv in _byClOrdId)
            yield return new Persistence.OwnershipMappingSnapshot(kv.Key, kv.Value.Value);
    }

    public void Restore(IEnumerable<Persistence.OwnershipMappingSnapshot> snaps)
    {
        ArgumentNullException.ThrowIfNull(snaps);
        _byClOrdId.Clear();
        foreach (var s in snaps)
            _byClOrdId[s.ClOrdId] = new EndClientId(s.EndClientId);
    }
}
