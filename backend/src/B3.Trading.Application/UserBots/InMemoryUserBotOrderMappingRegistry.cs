using System.Collections.Concurrent;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly EventDispatcher? _dispatcher;
    private readonly OutboundMutationLedger? _outboundLedger;
    private readonly ILogger<InMemoryUserBotOrderMappingRegistry> _logger;

    // Forward map: internalClOrdId → (credentialId, externalClOrdId).
    private readonly ConcurrentDictionary<ulong, OrderMapping> _byInternal = new();

    // Reverse map for FIXP cancel inbound: (credentialId, externalClOrdId) → internalClOrdId.
    // Keyed by a composite struct to avoid string allocation on the hot path.
    private readonly ConcurrentDictionary<ExternalKey, ulong> _byExternal = new();

    // Cancel-side map: cancelInternalClOrdId → (originalInternal, credentialId, externalCancel).
    private readonly ConcurrentDictionary<ulong, CancelMapping> _cancelsByInternal = new();
    private readonly ConcurrentDictionary<ExternalKey, BotBusinessIdentityTombstone> _businessIdentities = new();

    public bool LegacyTerminalHistoryUnavailable { get; private set; }

    public InMemoryUserBotOrderMappingRegistry(
        EventDispatcher? dispatcher = null,
        OutboundMutationLedger? outboundLedger = null,
        ILogger<InMemoryUserBotOrderMappingRegistry>? logger = null)
    {
        _dispatcher = dispatcher;
        _outboundLedger = outboundLedger;
        _logger = logger ?? NullLogger<InMemoryUserBotOrderMappingRegistry>.Instance;
    }

    public bool TryGetOrderMapping(ulong internalClOrdId, out OrderMapping mapping)
        => _byInternal.TryGetValue(internalClOrdId, out mapping);

    public bool TryGetByExternal(Guid credentialId, ulong externalClOrdId, out ulong internalClOrdId)
        => _byExternal.TryGetValue(new ExternalKey(credentialId, externalClOrdId), out internalClOrdId);

    public bool ContainsBusinessIdentity(Guid credentialId, ulong externalClOrdId)
        => _businessIdentities.ContainsKey(new ExternalKey(credentialId, externalClOrdId));

    public BotBusinessIdentityClaimResult TryClaimBusinessIdentity(
        Guid credentialId,
        ulong externalClOrdId,
        OutboundMutationKind mutationKind,
        DateTimeOffset claimedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (credentialId == Guid.Empty)
            throw new ArgumentException("CredentialId must not be empty.", nameof(credentialId));
        if (mutationKind is not (OutboundMutationKind.New or OutboundMutationKind.Cancel))
            throw new ArgumentOutOfRangeException(nameof(mutationKind));

        var tombstone = new BotBusinessIdentityTombstone
        {
            CredentialId = credentialId,
            ExternalClOrdId = externalClOrdId,
            MutationKind = mutationKind,
            ClaimedAtUtc = claimedAtUtc,
        };
        var evt = new BotBusinessIdentityClaimedEvent
        {
            CredentialId = credentialId,
            ExternalClOrdId = externalClOrdId,
            MutationKind = mutationKind,
            ClaimedAtUtc = claimedAtUtc,
            TimestampUtc = claimedAtUtc,
        };
        try
        {
            if (_dispatcher is null)
                return _businessIdentities.TryAdd(
                    new ExternalKey(credentialId, externalClOrdId), tombstone)
                    ? BotBusinessIdentityClaimResult.Claimed
                    : BotBusinessIdentityClaimResult.Duplicate;

            var outcome = _dispatcher.DispatchCommittedIf(
                evt,
                () => !ContainsBusinessIdentity(credentialId, externalClOrdId),
                () => Apply(evt),
                cancellationToken);
            return outcome.Applied
                ? BotBusinessIdentityClaimResult.Claimed
                : BotBusinessIdentityClaimResult.Duplicate;
        }
        catch (WalBackpressureException)
        {
            return BotBusinessIdentityClaimResult.WalBackpressure;
        }
        catch (WalFaultedException)
        {
            return BotBusinessIdentityClaimResult.WalFaulted;
        }
    }

    public bool TryGetCancelMapping(ulong cancelInternalClOrdId, out CancelMapping mapping)
        => _cancelsByInternal.TryGetValue(cancelInternalClOrdId, out mapping);

    public void Reap(ulong internalClOrdId)
    {
        if (_byInternal.TryRemove(internalClOrdId, out var mapping))
        {
            _byExternal.TryRemove(new ExternalKey(mapping.CredentialId, mapping.ExternalClOrdId), out _);
            MarkResolved(
                new ExternalKey(mapping.CredentialId, mapping.ExternalClOrdId),
                internalClOrdId,
                DateTimeOffset.UtcNow);
        }
    }

    public void ReapCancel(ulong cancelInternalClOrdId)
    {
        if (_cancelsByInternal.TryRemove(cancelInternalClOrdId, out var mapping))
        {
            MarkResolved(
                new ExternalKey(mapping.CredentialId, mapping.ExternalCancelClOrdId),
                cancelInternalClOrdId,
                DateTimeOffset.UtcNow);
        }
    }

    public void MarkOrderResolved(ulong internalClOrdId, DateTimeOffset resolvedAtUtc)
    {
        if (_byInternal.TryGetValue(internalClOrdId, out var mapping))
        {
            MarkResolved(
                new ExternalKey(mapping.CredentialId, mapping.ExternalClOrdId),
                internalClOrdId,
                resolvedAtUtc);
        }
    }

    public void MarkBusinessIdentityResolved(
        Guid credentialId,
        ulong externalClOrdId,
        DateTimeOffset resolvedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var evt = new BotBusinessIdentityResolvedEvent
        {
            CredentialId = credentialId,
            ExternalClOrdId = externalClOrdId,
            ResolvedAtUtc = resolvedAtUtc,
            TimestampUtc = resolvedAtUtc,
        };
        if (_dispatcher is null)
        {
            Apply(evt);
            return;
        }
        try
        {
            _dispatcher.DispatchCommittedIf(
                evt,
                () => _businessIdentities.TryGetValue(
                        new ExternalKey(credentialId, externalClOrdId),
                        out var current)
                    && current.ResolvedAtUtc is null,
                () => Apply(evt),
                cancellationToken);
        }
        catch (WalBackpressureException ex)
        {
            _logger.LogWarning(
                ex,
                "userbot.business-identity.resolution-not-recorded credentialId={CredentialId} externalClOrdId={ExternalClOrdId}; tombstone remains non-purgeable",
                credentialId,
                externalClOrdId);
        }
        catch (WalFaultedException ex)
        {
            _logger.LogError(
                ex,
                "userbot.business-identity.resolution-not-recorded credentialId={CredentialId} externalClOrdId={ExternalClOrdId}; tombstone remains non-purgeable",
                credentialId,
                externalClOrdId);
        }
    }

    public void RegisterOrderInternal(
        ulong internalClOrdId,
        Guid credentialId,
        ulong externalClOrdId) =>
        RegisterOrderInternal(
            internalClOrdId,
            credentialId,
            externalClOrdId,
            DateTimeOffset.UtcNow,
            mutationId: null);

    public void RegisterOrderInternal(
        ulong internalClOrdId,
        Guid credentialId,
        ulong externalClOrdId,
        DateTimeOffset? recordedAtUtc = null,
        OutboundMutationId? mutationId = null)
    {
        if (internalClOrdId == 0) throw new ArgumentOutOfRangeException(nameof(internalClOrdId));
        if (credentialId == Guid.Empty) throw new ArgumentException("CredentialId must not be empty.", nameof(credentialId));
        var mapping = new OrderMapping(credentialId, externalClOrdId);
        _byInternal[internalClOrdId] = mapping;
        var key = new ExternalKey(credentialId, externalClOrdId);
        _byExternal[key] = internalClOrdId;
        LinkTombstone(
            key,
            OutboundMutationKind.New,
            internalClOrdId,
            mutationId,
            recordedAtUtc ?? DateTimeOffset.UtcNow);
    }

    public void RegisterCancelInternal(
        ulong cancelInternalClOrdId,
        ulong originalInternalClOrdId,
        Guid credentialId,
        ulong externalCancelClOrdId) =>
        RegisterCancelInternal(
            cancelInternalClOrdId,
            originalInternalClOrdId,
            credentialId,
            externalCancelClOrdId,
            DateTimeOffset.UtcNow,
            mutationId: null);

    public void RegisterCancelInternal(
        ulong cancelInternalClOrdId,
        ulong originalInternalClOrdId,
        Guid credentialId,
        ulong externalCancelClOrdId,
        DateTimeOffset? recordedAtUtc = null,
        OutboundMutationId? mutationId = null)
    {
        if (cancelInternalClOrdId == 0) throw new ArgumentOutOfRangeException(nameof(cancelInternalClOrdId));
        if (originalInternalClOrdId == 0) throw new ArgumentOutOfRangeException(nameof(originalInternalClOrdId));
        if (credentialId == Guid.Empty) throw new ArgumentException("CredentialId must not be empty.", nameof(credentialId));
        _cancelsByInternal[cancelInternalClOrdId] =
            new CancelMapping(originalInternalClOrdId, credentialId, externalCancelClOrdId);
        LinkTombstone(
            new ExternalKey(credentialId, externalCancelClOrdId),
            OutboundMutationKind.Cancel,
            cancelInternalClOrdId,
            mutationId,
            recordedAtUtc ?? DateTimeOffset.UtcNow);
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

    public BotBusinessIdentityTombstone[] RawSnapshotBusinessIdentities()
        => _businessIdentities.Values.ToArray();

    public IReadOnlyList<BotBusinessIdentityTombstone> SnapshotBusinessIdentities()
        => _businessIdentities.Values
            .OrderBy(t => t.CredentialId)
            .ThenBy(t => t.ExternalClOrdId)
            .ToArray();

    public int PurgeResolvedBusinessIdentities(
        DateTimeOffset now,
        TimeSpan? retention = null,
        CancellationToken cancellationToken = default)
    {
        var keep = retention ?? OutboundMutationLedger.DefaultTerminalCorrelationRetention;
        if (keep < OutboundMutationLedger.DefaultTerminalCorrelationRetention)
            throw new ArgumentOutOfRangeException(nameof(retention));

        var purged = 0;
        foreach (var tombstone in _businessIdentities.Values
                     .OrderBy(t => t.CredentialId)
                     .ThenBy(t => t.ExternalClOrdId))
        {
            if (!CanPurge(tombstone, now, keep))
                continue;
            var evt = new BotBusinessIdentityTombstonePurgedEvent
            {
                CredentialId = tombstone.CredentialId,
                ExternalClOrdId = tombstone.ExternalClOrdId,
                MutationKind = tombstone.MutationKind,
                InternalClOrdId = tombstone.InternalClOrdId,
                MutationId = tombstone.MutationId,
                ClaimedAtUtc = tombstone.ClaimedAtUtc,
                ResolvedAtUtc = tombstone.ResolvedAtUtc!.Value,
                Retention = keep,
                PurgedAtUtc = now,
                TimestampUtc = now,
            };
            if (_dispatcher is null)
            {
                if (ApplyPurge(evt))
                    purged++;
                continue;
            }
            var outcome = _dispatcher.DispatchCommittedIf(
                evt,
                () => _businessIdentities.TryGetValue(
                        new ExternalKey(evt.CredentialId, evt.ExternalClOrdId),
                        out var current)
                    && CanPurge(current, now, keep),
                () => ApplyPurge(evt),
                cancellationToken);
            if (outcome.Applied)
                purged++;
        }
        return purged;
    }

    public void Restore(
        IEnumerable<BotOrderMappingSnapshot> orders,
        IEnumerable<BotCancelMappingSnapshot> cancels) =>
        Restore(orders, cancels, businessIdentities: null);

    public void Restore(
        IEnumerable<BotOrderMappingSnapshot> orders,
        IEnumerable<BotCancelMappingSnapshot> cancels,
        IEnumerable<BotBusinessIdentityTombstone>? businessIdentities = null,
        DateTimeOffset? legacySnapshotCreatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(cancels);
        _byInternal.Clear();
        _byExternal.Clear();
        _cancelsByInternal.Clear();
        _businessIdentities.Clear();
        LegacyTerminalHistoryUnavailable = businessIdentities is null;
        if (businessIdentities is not null)
        {
            foreach (var tombstone in businessIdentities)
                ApplyTombstone(tombstone);
        }
        else
        {
            MetricsRegistry.UserBotBusinessIdentityMigrationLimitations.Add(1);
            _logger.LogWarning(
                "userbot.business-identity.migration-limitation terminal legacy bot orders without origin history cannot be reconstructed; live mappings will seed tombstones and internal ClOrdID uniqueness remains unchanged");
        }
        var seededAt = legacySnapshotCreatedAtUtc ?? DateTimeOffset.UtcNow;
        foreach (var s in orders)
            RegisterOrderInternal(
                s.InternalClOrdId,
                s.CredentialId,
                s.ExternalClOrdId,
                seededAt);
        foreach (var c in cancels)
            RegisterCancelInternal(
                c.CancelInternalClOrdId, c.OriginalInternalClOrdId,
                c.CredentialId, c.ExternalCancelClOrdId, seededAt);
    }

    public void Apply(BotBusinessIdentityClaimedEvent evt)
    {
        ApplyTombstone(new BotBusinessIdentityTombstone
        {
            CredentialId = evt.CredentialId,
            ExternalClOrdId = evt.ExternalClOrdId,
            MutationKind = evt.MutationKind,
            ClaimedAtUtc = evt.ClaimedAtUtc,
        });
    }

    public void Apply(BotBusinessIdentityResolvedEvent evt)
    {
        var key = new ExternalKey(evt.CredentialId, evt.ExternalClOrdId);
        if (_businessIdentities.TryGetValue(key, out var existing)
            && existing.ResolvedAtUtc is null)
        {
            _businessIdentities[key] = existing with
            {
                ResolvedAtUtc = evt.ResolvedAtUtc,
            };
        }
    }

    public void Apply(BotBusinessIdentityTombstonePurgedEvent evt)
        => ApplyPurge(evt);

    private bool ApplyPurge(BotBusinessIdentityTombstonePurgedEvent evt)
    {
        var key = new ExternalKey(evt.CredentialId, evt.ExternalClOrdId);
        if (!_businessIdentities.TryRemove(key, out var removed))
            return false;
        if (removed.InternalClOrdId is { } internalClOrdId)
        {
            _byExternal.TryRemove(key, out _);
            _byInternal.TryRemove(internalClOrdId, out _);
            _cancelsByInternal.TryRemove(internalClOrdId, out _);
        }
        return true;
    }

    private void LinkTombstone(
        ExternalKey key,
        OutboundMutationKind mutationKind,
        ulong internalClOrdId,
        OutboundMutationId? mutationId,
        DateTimeOffset recordedAtUtc)
    {
        _businessIdentities.AddOrUpdate(
            key,
            _ => new BotBusinessIdentityTombstone
            {
                CredentialId = key.CredentialId,
                ExternalClOrdId = key.ExternalClOrdId,
                MutationKind = mutationKind,
                ClaimedAtUtc = recordedAtUtc,
                InternalClOrdId = internalClOrdId,
                MutationId = mutationId,
            },
            (_, existing) =>
            {
                if (existing.MutationKind != mutationKind
                    || existing.InternalClOrdId is { } linked && linked != internalClOrdId)
                {
                    throw new InvalidOperationException(
                        "Bot business identity is already linked to a different mutation.");
                }
                return existing with
                {
                    InternalClOrdId = internalClOrdId,
                    MutationId = mutationId ?? existing.MutationId,
                };
            });
    }

    private void MarkResolved(ExternalKey key, ulong internalClOrdId, DateTimeOffset resolvedAtUtc)
    {
        if (_businessIdentities.TryGetValue(key, out var existing)
            && existing.InternalClOrdId == internalClOrdId
            && existing.ResolvedAtUtc is null)
        {
            _businessIdentities[key] = existing with { ResolvedAtUtc = resolvedAtUtc };
        }
    }

    private void ApplyTombstone(BotBusinessIdentityTombstone tombstone)
    {
        if (tombstone.CredentialId == Guid.Empty)
            throw new InvalidOperationException("Bot business identity credential is invalid.");
        if (tombstone.MutationKind is not (OutboundMutationKind.New or OutboundMutationKind.Cancel))
            throw new InvalidOperationException("Bot business mutation kind is invalid.");
        _businessIdentities[
            new ExternalKey(tombstone.CredentialId, tombstone.ExternalClOrdId)] = tombstone;
    }

    private bool CanPurge(
        BotBusinessIdentityTombstone tombstone,
        DateTimeOffset now,
        TimeSpan retention)
    {
        if (tombstone.ResolvedAtUtc is not { } resolvedAtUtc
            || resolvedAtUtc > now - retention)
            return false;
        if (_byExternal.ContainsKey(
                new ExternalKey(tombstone.CredentialId, tombstone.ExternalClOrdId))
            || tombstone.InternalClOrdId is { } internalClOrdId
                && _cancelsByInternal.ContainsKey(internalClOrdId))
        {
            return false;
        }
        if (tombstone.MutationId is not { } mutationId)
            return true;
        if (_outboundLedger is null
            || !_outboundLedger.TryGet(mutationId, out var mutation)
            || mutation is null)
            return false;
        return IsResolved(mutation);
    }

    private static bool IsResolved(OutboundMutationSnapshot mutation) =>
        !mutation.RequiresReconciliation
        && mutation.State is OutboundMutationState.VenueAcknowledged
            or OutboundMutationState.OperatorResolved
            or OutboundMutationState.LegacyTerminal;

    private readonly record struct ExternalKey(Guid CredentialId, ulong ExternalClOrdId);
}
