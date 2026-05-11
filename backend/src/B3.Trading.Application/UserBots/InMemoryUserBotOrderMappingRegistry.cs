using System.Collections.Concurrent;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Application.UserBots;

/// <summary>
/// In-memory <see cref="IUserBotOrderMappingRegistry"/> backed by
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. Reads
/// (<see cref="TryGetOrderMapping"/>, <see cref="TryGetByExternal"/>,
/// <see cref="TryGetCancelMapping"/>) are lock-free; writes are coarse-
/// grained per-dictionary and called from the dispatcher's apply lock,
/// so the registry adds no lock contention to the submit path.
///
/// <para>Snapshot capture is called under
/// <c>EventDispatcher.WithSnapshotLock</c>, which excludes concurrent
/// <c>RegisterOrderInternal</c>/<c>RegisterCancelInternal</c> writes —
/// so the captured view is consistent with the WAL seq written into the
/// snapshot envelope.</para>
/// </summary>
public sealed class InMemoryUserBotOrderMappingRegistry : IUserBotOrderMappingRegistry
{
    // Forward map: internalClOrdId → (credentialId, externalClOrdId).
    private readonly ConcurrentDictionary<ulong, OrderMapping> _byInternal = new();

    // Reverse map for FIXP cancel inbound: (credentialId, externalClOrdId) → internalClOrdId.
    // Keyed by a composite struct to avoid string allocation on the hot path.
    private readonly ConcurrentDictionary<ExternalKey, ulong> _byExternal = new();

    // Cancel-side map: cancelInternalClOrdId → (originalInternal, credentialId, externalCancel).
    private readonly ConcurrentDictionary<ulong, CancelMapping> _cancelsByInternal = new();

    public bool TryGetOrderMapping(ulong internalClOrdId, out OrderMapping mapping)
        => _byInternal.TryGetValue(internalClOrdId, out mapping);

    public bool TryGetByExternal(Guid credentialId, ulong externalClOrdId, out ulong internalClOrdId)
        => _byExternal.TryGetValue(new ExternalKey(credentialId, externalClOrdId), out internalClOrdId);

    public bool TryGetCancelMapping(ulong cancelInternalClOrdId, out CancelMapping mapping)
        => _cancelsByInternal.TryGetValue(cancelInternalClOrdId, out mapping);

    public void Reap(ulong internalClOrdId)
    {
        if (_byInternal.TryRemove(internalClOrdId, out var mapping))
            _byExternal.TryRemove(new ExternalKey(mapping.CredentialId, mapping.ExternalClOrdId), out _);
    }

    public void ReapCancel(ulong cancelInternalClOrdId)
        => _cancelsByInternal.TryRemove(cancelInternalClOrdId, out _);

    public void RegisterOrderInternal(ulong internalClOrdId, Guid credentialId, ulong externalClOrdId)
    {
        if (internalClOrdId == 0) throw new ArgumentOutOfRangeException(nameof(internalClOrdId));
        if (credentialId == Guid.Empty) throw new ArgumentException("CredentialId must not be empty.", nameof(credentialId));
        var mapping = new OrderMapping(credentialId, externalClOrdId);
        _byInternal[internalClOrdId] = mapping;
        _byExternal[new ExternalKey(credentialId, externalClOrdId)] = internalClOrdId;
    }

    public void RegisterCancelInternal(
        ulong cancelInternalClOrdId,
        ulong originalInternalClOrdId,
        Guid credentialId,
        ulong externalCancelClOrdId)
    {
        if (cancelInternalClOrdId == 0) throw new ArgumentOutOfRangeException(nameof(cancelInternalClOrdId));
        if (originalInternalClOrdId == 0) throw new ArgumentOutOfRangeException(nameof(originalInternalClOrdId));
        if (credentialId == Guid.Empty) throw new ArgumentException("CredentialId must not be empty.", nameof(credentialId));
        _cancelsByInternal[cancelInternalClOrdId] =
            new CancelMapping(originalInternalClOrdId, credentialId, externalCancelClOrdId);
    }

    public IReadOnlyList<BotOrderMappingSnapshot> SnapshotOrders()
        => _byInternal
            .Select(kv => new BotOrderMappingSnapshot(kv.Key, kv.Value.CredentialId, kv.Value.ExternalClOrdId))
            .OrderBy(s => s.InternalClOrdId)
            .ToList();

    public IReadOnlyList<BotCancelMappingSnapshot> SnapshotCancels()
        => _cancelsByInternal
            .Select(kv => new BotCancelMappingSnapshot(
                kv.Key, kv.Value.OriginalInternalClOrdId, kv.Value.CredentialId, kv.Value.ExternalCancelClOrdId))
            .OrderBy(s => s.CancelInternalClOrdId)
            .ToList();

    /// <inheritdoc />
    public BotOrderMappingRaw[] RawSnapshotOrders()
    {
        var pairs = _byInternal.ToArray();
        if (pairs.Length == 0) return Array.Empty<BotOrderMappingRaw>();
        var raw = new BotOrderMappingRaw[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
            raw[i] = new BotOrderMappingRaw(
                pairs[i].Key, pairs[i].Value.CredentialId, pairs[i].Value.ExternalClOrdId);
        return raw;
    }

    /// <inheritdoc />
    public BotCancelMappingRaw[] RawSnapshotCancels()
    {
        var pairs = _cancelsByInternal.ToArray();
        if (pairs.Length == 0) return Array.Empty<BotCancelMappingRaw>();
        var raw = new BotCancelMappingRaw[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
            raw[i] = new BotCancelMappingRaw(
                pairs[i].Key,
                pairs[i].Value.OriginalInternalClOrdId,
                pairs[i].Value.CredentialId,
                pairs[i].Value.ExternalCancelClOrdId);
        return raw;
    }

    public void Restore(
        IEnumerable<BotOrderMappingSnapshot> orders,
        IEnumerable<BotCancelMappingSnapshot> cancels)
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(cancels);
        _byInternal.Clear();
        _byExternal.Clear();
        _cancelsByInternal.Clear();
        foreach (var s in orders)
            RegisterOrderInternal(s.InternalClOrdId, s.CredentialId, s.ExternalClOrdId);
        foreach (var c in cancels)
            RegisterCancelInternal(
                c.CancelInternalClOrdId, c.OriginalInternalClOrdId,
                c.CredentialId, c.ExternalCancelClOrdId);
    }

    private readonly record struct ExternalKey(Guid CredentialId, ulong ExternalClOrdId);
}
