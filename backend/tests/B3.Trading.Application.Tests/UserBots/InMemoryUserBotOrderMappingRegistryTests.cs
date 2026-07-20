using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.UserBots;

namespace B3.Trading.Application.Tests.UserBots;

/// <summary>
/// Sub-issue #171 (E). Behavioural unit tests for the bot ClOrdID
/// side-mapping registry. Live routes are derived from order/cancel WAL
/// events; business identity claims, resolutions, and purges have their own
/// durable audit events.
/// These tests cover the in-memory invariants the registry must hold for
/// FIXP cancel-inbound (TryGetByExternal), ER reverse routing
/// (TryGetOrderMapping / TryGetCancelMapping), the snapshot/restore
/// round-trip, and the Reap idempotence rule.
/// </summary>
public class InMemoryUserBotOrderMappingRegistryTests
{
    private static readonly Guid CredA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CredB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset T0 =
        new(2026, 7, 18, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void BusinessIdentity_RemainsDuplicateAfterTerminalReapAndRestart()
    {
        var sut = new InMemoryUserBotOrderMappingRegistry();
        Assert.Equal(
            BotBusinessIdentityClaimResult.Claimed,
            sut.TryClaimBusinessIdentity(CredA, 9, OutboundMutationKind.New, T0));
        sut.RegisterOrderInternal(100, CredA, 9, T0);

        sut.Reap(100);

        Assert.False(sut.TryGetByExternal(CredA, 9, out _));
        Assert.True(sut.ContainsBusinessIdentity(CredA, 9));
        Assert.Equal(
            BotBusinessIdentityClaimResult.Duplicate,
            sut.TryClaimBusinessIdentity(
                CredA,
                9,
                OutboundMutationKind.New,
                T0.AddDays(1)));

        var restored = new InMemoryUserBotOrderMappingRegistry();
        restored.Restore(
            Array.Empty<BotOrderMappingSnapshot>(),
            Array.Empty<BotCancelMappingSnapshot>(),
            sut.SnapshotBusinessIdentities(),
            T0);

        Assert.Equal(
            BotBusinessIdentityClaimResult.Duplicate,
            restored.TryClaimBusinessIdentity(
                CredA,
                9,
                OutboundMutationKind.New,
                T0.AddDays(2)));
    }

    [Fact]
    public void BusinessIdentity_IsScopedByCredential()
    {
        var sut = new InMemoryUserBotOrderMappingRegistry();

        Assert.Equal(
            BotBusinessIdentityClaimResult.Claimed,
            sut.TryClaimBusinessIdentity(CredA, 9, OutboundMutationKind.New, T0));
        Assert.Equal(
            BotBusinessIdentityClaimResult.Claimed,
            sut.TryClaimBusinessIdentity(CredB, 9, OutboundMutationKind.New, T0));

        Assert.Equal(2, sut.SnapshotBusinessIdentities().Count);
    }

    [Fact]
    public void Claim_AppliesAfterAdmissionEvenWhenRequestCancelsDuringCommitWait()
    {
        using var requestCancellation = new CancellationTokenSource();
        var store = new CancelDuringFlushEventStore(requestCancellation);
        var sut = new InMemoryUserBotOrderMappingRegistry(
            new EventDispatcher(store));

        var result = sut.TryClaimBusinessIdentity(
            CredA,
            9,
            OutboundMutationKind.New,
            T0,
            requestCancellation.Token);

        Assert.Equal(BotBusinessIdentityClaimResult.Claimed, result);
        Assert.True(requestCancellation.IsCancellationRequested);
        Assert.True(sut.ContainsBusinessIdentity(CredA, 9));
        Assert.Equal(
            BotBusinessIdentityClaimResult.Duplicate,
            sut.TryClaimBusinessIdentity(
                CredA,
                9,
                OutboundMutationKind.New,
                T0.AddSeconds(1)));
        Assert.Single(store.Events);
    }

    [Fact]
    public void Purge_IsAuditedAndRequiresResolutionRetentionAndNoLiveRouting()
    {
        var store = new RecordingEventStore();
        var sut = new InMemoryUserBotOrderMappingRegistry(
            new EventDispatcher(store));
        Assert.Equal(
            BotBusinessIdentityClaimResult.Claimed,
            sut.TryClaimBusinessIdentity(CredA, 9, OutboundMutationKind.New, T0));

        Assert.Equal(0, sut.PurgeResolvedBusinessIdentities(T0.AddDays(31)));

        sut.MarkBusinessIdentityResolved(CredA, 9, T0.AddHours(1));
        sut.RegisterOrderInternal(100, CredA, 9, T0);
        Assert.Equal(0, sut.PurgeResolvedBusinessIdentities(T0.AddDays(31)));

        sut.Reap(100);
        Assert.Equal(0, sut.PurgeResolvedBusinessIdentities(T0.AddDays(30)));
        Assert.Equal(1, sut.PurgeResolvedBusinessIdentities(T0.AddDays(31)));
        Assert.False(sut.ContainsBusinessIdentity(CredA, 9));

        Assert.Collection(
            store.Events,
            evt => Assert.IsType<BotBusinessIdentityClaimedEvent>(evt),
            evt => Assert.IsType<BotBusinessIdentityResolvedEvent>(evt),
            evt =>
            {
                var purged = Assert.IsType<BotBusinessIdentityTombstonePurgedEvent>(evt);
                Assert.Equal(CredA, purged.CredentialId);
                Assert.Equal(9UL, purged.ExternalClOrdId);
                Assert.Equal(
                    OutboundMutationLedger.DefaultTerminalCorrelationRetention,
                    purged.Retention);
            });
    }

    [Fact]
    public void Purge_DoesNotRemoveTombstoneWhenLinkedMutationEvidenceIsMissing()
    {
        var sut = new InMemoryUserBotOrderMappingRegistry(
            outboundLedger: new OutboundMutationLedger());
        sut.Restore(
            Array.Empty<BotOrderMappingSnapshot>(),
            Array.Empty<BotCancelMappingSnapshot>(),
            [
                new BotBusinessIdentityTombstone
                {
                    CredentialId = CredA,
                    ExternalClOrdId = 9,
                    MutationKind = OutboundMutationKind.New,
                    ClaimedAtUtc = T0,
                    MutationId = OutboundMutationId.New(),
                    ResolvedAtUtc = T0.AddHours(1),
                },
            ],
            T0);

        Assert.Equal(0, sut.PurgeResolvedBusinessIdentities(T0.AddDays(31)));
        Assert.True(sut.ContainsBusinessIdentity(CredA, 9));
    }

    [Fact]
    public void Restore_LegacyLiveMappingsSeedTombstonesAndReportLimitation()
    {
        var sut = new InMemoryUserBotOrderMappingRegistry();

        sut.Restore(
            [new BotOrderMappingSnapshot(100, CredA, 9)],
            Array.Empty<BotCancelMappingSnapshot>(),
            businessIdentities: null,
            legacySnapshotCreatedAtUtc: T0);

        Assert.True(sut.LegacyTerminalHistoryUnavailable);
        var tombstone = Assert.Single(sut.SnapshotBusinessIdentities());
        Assert.Equal(CredA, tombstone.CredentialId);
        Assert.Equal(9UL, tombstone.ExternalClOrdId);
        Assert.Equal(100UL, tombstone.InternalClOrdId);
        Assert.Equal(T0, tombstone.ClaimedAtUtc);
    }

    [Fact]
    public void RegisterOrderInternal_PopulatesBothDirections()
    {
        var sut = new InMemoryUserBotOrderMappingRegistry();

        sut.RegisterOrderInternal(internalClOrdId: 100UL, CredA, externalClOrdId: 9UL);

        Assert.True(sut.TryGetOrderMapping(100UL, out var fwd));
        Assert.Equal(CredA, fwd.CredentialId);
        Assert.Equal(9UL, fwd.ExternalClOrdId);

        Assert.True(sut.TryGetByExternal(CredA, 9UL, out var rev));
        Assert.Equal(100UL, rev);
    }

    [Fact]
    public void TryGetByExternal_DifferentCredential_ReturnsFalse()
    {
        // Cross-credential isolation: the same external ClOrdID under
        // a different credential must NOT resolve.
        var sut = new InMemoryUserBotOrderMappingRegistry();
        sut.RegisterOrderInternal(100UL, CredA, 9UL);

        Assert.False(sut.TryGetByExternal(CredB, 9UL, out _));
    }

    [Fact]
    public void TryGetByExternal_UnknownExternal_ReturnsFalse()
    {
        var sut = new InMemoryUserBotOrderMappingRegistry();
        Assert.False(sut.TryGetByExternal(CredA, 9UL, out _));
    }

    [Fact]
    public void Reap_RemovesBothDirections_AndIsIdempotent()
    {
        var sut = new InMemoryUserBotOrderMappingRegistry();
        sut.RegisterOrderInternal(100UL, CredA, 9UL);

        sut.Reap(100UL);

        Assert.False(sut.TryGetOrderMapping(100UL, out _));
        Assert.False(sut.TryGetByExternal(CredA, 9UL, out _));

        // Idempotent: reaping an unknown id is a silent no-op.
        sut.Reap(100UL);
        sut.Reap(99999UL);
    }

    [Fact]
    public void RegisterCancelInternal_PopulatesCancelMap_OriginalNotAltered()
    {
        var sut = new InMemoryUserBotOrderMappingRegistry();
        sut.RegisterOrderInternal(100UL, CredA, 9UL);
        sut.RegisterCancelInternal(
            cancelInternalClOrdId: 200UL,
            originalInternalClOrdId: 100UL,
            credentialId: CredA,
            externalCancelClOrdId: 77UL);

        Assert.True(sut.TryGetCancelMapping(200UL, out var c));
        Assert.Equal(100UL, c.OriginalInternalClOrdId);
        Assert.Equal(CredA, c.CredentialId);
        Assert.Equal(77UL, c.ExternalCancelClOrdId);

        // Forward order mapping is preserved — F still needs it to route
        // pre-cancel ERs (e.g. partial fills) to the bot.
        Assert.True(sut.TryGetOrderMapping(100UL, out _));
    }

    [Fact]
    public void ReapCancel_OnlyDropsCancelEntry()
    {
        var sut = new InMemoryUserBotOrderMappingRegistry();
        sut.RegisterOrderInternal(100UL, CredA, 9UL);
        sut.RegisterCancelInternal(200UL, 100UL, CredA, 77UL);

        sut.ReapCancel(200UL);

        Assert.False(sut.TryGetCancelMapping(200UL, out _));
        Assert.True(sut.TryGetOrderMapping(100UL, out _));
    }

    [Fact]
    public void RegisterOrderInternal_RejectsZeroOrEmptyArgs()
    {
        var sut = new InMemoryUserBotOrderMappingRegistry();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => sut.RegisterOrderInternal(0UL, CredA, 1UL));
        Assert.Throws<ArgumentException>(
            () => sut.RegisterOrderInternal(1UL, Guid.Empty, 1UL));
    }

    [Fact]
    public void Snapshot_RoundTripsViaRestore()
    {
        var seed = new InMemoryUserBotOrderMappingRegistry();
        seed.RegisterOrderInternal(100UL, CredA, 9UL);
        seed.RegisterOrderInternal(101UL, CredB, 10UL);
        seed.RegisterCancelInternal(200UL, 100UL, CredA, 77UL);

        var orders = seed.SnapshotOrders();
        var cancels = seed.SnapshotCancels();

        var restored = new InMemoryUserBotOrderMappingRegistry();
        restored.Restore(orders, cancels);

        Assert.True(restored.TryGetByExternal(CredA, 9UL, out var rev));
        Assert.Equal(100UL, rev);
        Assert.True(restored.TryGetByExternal(CredB, 10UL, out rev));
        Assert.Equal(101UL, rev);
        Assert.True(restored.TryGetCancelMapping(200UL, out var cm));
        Assert.Equal(77UL, cm.ExternalCancelClOrdId);
    }

    [Fact]
    public void Restore_ClearsExistingState()
    {
        // The snapshot is the authoritative truth at startup; any stale
        // in-memory state from a previous Restore must be wiped first.
        var sut = new InMemoryUserBotOrderMappingRegistry();
        sut.RegisterOrderInternal(999UL, CredA, 999UL);
        sut.RegisterCancelInternal(998UL, 999UL, CredA, 998UL);

        sut.Restore(
            new[] { new BotOrderMappingSnapshot(100UL, CredB, 10UL) },
            Array.Empty<BotCancelMappingSnapshot>());

        Assert.False(sut.TryGetOrderMapping(999UL, out _));
        Assert.False(sut.TryGetCancelMapping(998UL, out _));
        Assert.True(sut.TryGetOrderMapping(100UL, out _));
    }

    [Fact]
    public async Task ConcurrentRegistrations_AreThreadSafe()
    {
        // Submit pipeline runs apply callbacks under the dispatcher lock,
        // but reads (TryGetOrderMapping for ER routing) are unsynchronised.
        // Verify no torn writes / lost updates under contention.
        var sut = new InMemoryUserBotOrderMappingRegistry();
        const int n = 5_000;

        var tasks = Enumerable.Range(0, n).Select(i => Task.Run(() =>
            sut.RegisterOrderInternal((ulong)(i + 1), CredA, (ulong)(i + 1)))).ToArray();
        await Task.WhenAll(tasks);

        for (int i = 1; i <= n; i++)
        {
            Assert.True(sut.TryGetOrderMapping((ulong)i, out var m));
            Assert.Equal((ulong)i, m.ExternalClOrdId);
        }
    }

    private sealed class RecordingEventStore : IEventStore
    {
        private long _seq;

        public List<WalEvent> Events { get; } = [];
        public long CurrentSeq => _seq;

        public long Append(WalEvent evt) => Append(evt, ReadOnlyMemory<byte>.Empty);

        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            Events.Add(evt);
            return ++_seq;
        }

        public ValueTask FlushAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancelDuringFlushEventStore : IEventStore
    {
        private readonly CancellationTokenSource _requestCancellation;
        private long _seq;

        public CancelDuringFlushEventStore(
            CancellationTokenSource requestCancellation)
        {
            _requestCancellation = requestCancellation;
        }

        public List<WalEvent> Events { get; } = [];
        public long CurrentSeq => _seq;

        public long Append(WalEvent evt) => Append(evt, ReadOnlyMemory<byte>.Empty);

        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            Events.Add(evt);
            return ++_seq;
        }

        public ValueTask FlushThroughAsync(
            long seq,
            CancellationToken ct = default)
        {
            _requestCancellation.Cancel();
            return ct.IsCancellationRequested
                ? ValueTask.FromCanceled(ct)
                : ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
