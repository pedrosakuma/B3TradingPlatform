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
    private readonly ConcurrentDictionary<ulong, ulong> _cancelToOrig = new();

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

    /// <summary>
    /// Records the cancel/replace-side <paramref name="newClOrdId"/> as a
    /// pointer back to <paramref name="originalClOrdId"/>. Lets
    /// <see cref="ExecutionReportProcessor"/> resolve cancel/replace
    /// acknowledgements when the upstream gateway omits the
    /// <c>OrigClOrdID</c> on the ER (defensive against
    /// <see href="https://github.com/pedrosakuma/B3EntryPointClient/issues/154">SDK</see>
    /// not echoing it back). Also registers the new ClOrdID against the
    /// same owner so per-end-client ER fan-out continues to work.
    /// </summary>
    public void RegisterCancelLink(ulong newClOrdId, ulong originalClOrdId)
    {
        if (newClOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(newClOrdId));
        if (originalClOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(originalClOrdId));
        _cancelToOrig[newClOrdId] = originalClOrdId;
        if (_byClOrdId.TryGetValue(originalClOrdId, out var owner))
            _byClOrdId[newClOrdId] = owner;
    }

    /// <summary>
    /// Slice 1 of #122. Records a cancel-replace request: registers the
    /// owner of <paramref name="newClOrdId"/> (same as original) AND the
    /// reverse <paramref name="newClOrdId"/> → <paramref name="originalClOrdId"/>
    /// fallback link used by <see cref="ExecutionReportProcessor"/> when
    /// the venue omits <c>OrigClOrdID</c> on the Replaced ER.
    ///
    /// <para>
    /// Functionally equivalent to calling <see cref="RegisterCancelLink"/>
    /// today, but exposed under a replace-specific name so callers can
    /// signal intent and so future divergence (e.g. in-flight tracking
    /// keyed by original) has an obvious extension point. Throws when
    /// the original is unknown — a replace request against an
    /// unregistered ClOrdID is always a programmer error.
    /// </para>
    /// </summary>
    public void RegisterReplaceLink(ulong originalClOrdId, ulong newClOrdId)
    {
        if (originalClOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(originalClOrdId));
        if (newClOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(newClOrdId));
        if (!_byClOrdId.TryGetValue(originalClOrdId, out var owner))
            throw new InvalidOperationException($"Unknown original ClOrdID '{originalClOrdId}'.");
        _byClOrdId[newClOrdId] = owner;
        _cancelToOrig[newClOrdId] = originalClOrdId;
    }

    /// <summary>
    /// Looks up the original ClOrdID a cancel/replace request was issued
    /// against. Returns <c>false</c> when no such link was registered.
    /// </summary>
    public bool TryResolveOrig(ulong newClOrdId, out ulong originalClOrdId) =>
        _cancelToOrig.TryGetValue(newClOrdId, out originalClOrdId);

    public IEnumerable<Persistence.OwnershipMappingSnapshot> Snapshot()
    {
        foreach (var kv in _byClOrdId)
            yield return new Persistence.OwnershipMappingSnapshot(kv.Key, kv.Value.Value);
    }

    /// <summary>
    /// Phase-1 (lock-side) capture for the two-phase snapshot pipeline
    /// (RFC §5.8 / P6). The underlying entries are immutable
    /// <c>(ulong, EndClientId)</c> pairs, so a single
    /// <c>ConcurrentDictionary.ToArray()</c> snapshot already gives us
    /// stable raw data; we merely shape it into the lighter
    /// <see cref="Persistence.OwnershipRaw"/> tuple here so the projection
    /// step does not need to touch <see cref="EndClientId"/> again.
    /// </summary>
    public Persistence.OwnershipRaw[] RawSnapshot()
    {
        var pairs = _byClOrdId.ToArray();
        if (pairs.Length == 0) return Array.Empty<Persistence.OwnershipRaw>();
        var raw = new Persistence.OwnershipRaw[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
            raw[i] = new Persistence.OwnershipRaw(pairs[i].Key, pairs[i].Value.Value);
        return raw;
    }

    public void Restore(IEnumerable<Persistence.OwnershipMappingSnapshot> snaps)
    {
        ArgumentNullException.ThrowIfNull(snaps);
        _byClOrdId.Clear();
        foreach (var s in snaps)
            _byClOrdId[s.ClOrdId] = new EndClientId(s.EndClientId);
    }
}
