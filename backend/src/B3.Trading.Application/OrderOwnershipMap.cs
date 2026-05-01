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
    private readonly ConcurrentDictionary<string, EndClientId> _byClOrdId = new(StringComparer.Ordinal);

    public void Register(string clOrdId, EndClientId owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clOrdId);
        ArgumentNullException.ThrowIfNull(owner);
        _byClOrdId[clOrdId] = owner;
    }

    public bool TryResolve(string clOrdId, out EndClientId? owner) =>
        _byClOrdId.TryGetValue(clOrdId, out owner);

    /// <summary>
    /// Re-key after a successful cancel-replace; the new ClOrdID inherits
    /// the same owner. The original key is kept so any in-flight ER for
    /// it (e.g. Replaced ack) can still resolve.
    /// </summary>
    public void RegisterReplacement(string originalClOrdId, string newClOrdId)
    {
        if (!_byClOrdId.TryGetValue(originalClOrdId, out var owner))
            throw new InvalidOperationException($"Unknown original ClOrdID '{originalClOrdId}'.");
        _byClOrdId[newClOrdId] = owner;
    }
}
