using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

#pragma warning disable CS0618 // legacy Margin.Initial used to seed capacity in tests

namespace B3.Trading.Application.Tests.Outbound;

public sealed class CancelReplaceOutboundCoordinatorTests
{
    [Fact]
    public async Task Replace_PostFrameFailure_RemainsAmbiguousAndKeepsMargin()
    {
        var gateway = new RecordingGateway(GatewayOutcome.Ambiguous);
        var margin = new RecordingReplaceMarginCoordinator();
        var fixture = CreateFixture(gateway, margin);
        var original = OriginalOrder();
        AddOriginal(fixture, original);
        var mutationId = Approve(fixture, original, 2001);

        var result = await fixture.Coordinator.EnqueueAsync(
            mutationId,
            CancellationToken.None);

        Assert.Equal(CancelReplaceDispatchOutcome.ReconciliationRequired, result.Outcome);
        Assert.True(fixture.Ledger.TryGet(mutationId, out var mutation));
        Assert.Equal(OutboundMutationState.Ambiguous, mutation!.State);
        Assert.Single(mutation.Attempts);
        Assert.NotNull(mutation.Attempts[0].FramePrepared);
        Assert.True(fixture.Replacements.IsOriginalInFlight(original.ClOrdId));
        Assert.True(fixture.Replacements.TryGet(2001, out var intent));
        Assert.Equal(original.ClOrdId, intent!.OriginalClOrdId);
        Assert.Single(margin.Prepared);
        Assert.Empty(margin.Aborted);
    }

    [Fact]
    public async Task ProvenUnsent_RetryUsesFreshId_PreservesTombstones_AndCapsAttempts()
    {
        var gateway = new RecordingGateway(
            GatewayOutcome.ProvenUnsent,
            GatewayOutcome.ProvenUnsent);
        var fixture = CreateFixture(gateway, new RecordingReplaceMarginCoordinator());
        var original = OriginalOrder();
        AddOriginal(fixture, original);
        var mutationId = Approve(fixture, original, 2001);

        var first = await fixture.Coordinator.EnqueueAsync(
            mutationId,
            CancellationToken.None);
        var second = await fixture.Coordinator.RetryProvenUnsentAsync(
            mutationId,
            CancellationToken.None);
        var third = await fixture.Coordinator.RetryProvenUnsentAsync(
            mutationId,
            CancellationToken.None);

        Assert.Equal(CancelReplaceDispatchOutcome.ProvenUnsent, first.Outcome);
        Assert.Equal(CancelReplaceDispatchOutcome.ProvenUnsent, second.Outcome);
        Assert.Equal(CancelReplaceDispatchOutcome.RetryNotAllowed, third.Outcome);
        Assert.True(fixture.Ledger.TryGet(mutationId, out var mutation));
        Assert.Equal(2, mutation!.Attempts.Count);
        Assert.NotEqual(mutation.Attempts[0].ClOrdId, mutation.Attempts[1].ClOrdId);
        Assert.Equal(
            mutation.Attempts.Select(a => a.ClOrdId),
            gateway.ReplaceCommands.Select(c => c.Canonical.ClOrdId));
        var correlations = fixture.Ledger.SnapshotCorrelations();
        Assert.Contains(correlations, c => c.ClOrdId == mutation.Attempts[0].ClOrdId && c.Terminal);
        Assert.Contains(correlations, c => c.ClOrdId == mutation.Attempts[1].ClOrdId && c.Terminal);
    }

    [Fact]
    public async Task Modify_FreezesFullEffectiveReplaceCommandAtApproval()
    {
        var gateway = new RecordingGateway(GatewayOutcome.Completed);
        var margin = new RecordingReplaceMarginCoordinator();
        var fixture = CreateFixture(gateway, margin);
        var owner = new EndClientId("alice");
        var original = new Order(
            1001,
            owner,
            "PETR4",
            4321,
            OrderSide.Buy,
            OrderType.StopLimit,
            100,
            30.25m,
            "FIRM",
            timeInForce: TimeInForce.GTD,
            stopPrice: 29.75m,
            goodTillDate: DateTimeOffset.Parse("2030-01-02T03:04:05Z"));
        AddOriginal(fixture, original);
        var service = new OrderModifyService(
            fixture.ClOrdIds,
            fixture.Ownership,
            fixture.Orders,
            gateway,
            new NoOpExecutionEventSink(),
            new RiskPipeline(Array.Empty<IRiskCheck>()),
            margin,
            fixture.Replacements,
            fixture.Dispatcher,
            fixture.Drain,
            NullLogger<OrderModifyService>.Instance,
            outboundLedger: fixture.Ledger,
            approvalFactory: fixture.ApprovalFactory,
            outboundCoordinator: fixture.Coordinator);

        var result = await service.ModifyAsync(
            new OrderModifyRequest(owner, original.ClOrdId, 75, NewPrice: null),
            CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.Accepted, result.Kind);
        var sent = Assert.Single(gateway.ReplaceCommands).Canonical;
        Assert.Equal(75, sent.Quantity);
        Assert.Equal(original.Price, sent.Price);
        Assert.Equal(original.TimeInForce.ToString(), sent.TimeInForce);
        Assert.Equal(original.StopPrice, sent.StopPrice);
        Assert.Equal(original.GoodTillDate, sent.GoodTillDate);
        Assert.True(fixture.Ledger.TryGet(result.MutationId, out var mutation));
        var frozen = mutation!.Approval!.CanonicalCommandNonSensitive;
        Assert.Equal(sent, frozen);
    }

    [Fact]
    public async Task ModifyUpsize_WithCoordinator_ReservesMarginExactlyOnce()
    {
        var options = new RiskOptions();
        options.Margin.Enabled = true;
        options.Margin.Initial["alice"] = 10_000m;
        var margin = new ReserveOnSubmitMarginProvider(
            new StaticOptionsMonitor<RiskOptions>(options),
            NullLogger<ReserveOnSubmitMarginProvider>.Instance);
        var gateway = new RecordingGateway(GatewayOutcome.Completed);
        var fixture = CreateFixture(gateway, margin);
        var original = OriginalOrder();
        AddOriginal(fixture, original);
        Assert.True((await margin.TryReserveAsync(
            original.ClOrdId,
            new RiskContext(
                original.Owner,
                original.FirmId,
                original.Symbol,
                original.Side,
                original.Type,
                original.Quantity,
                original.Price),
            CancellationToken.None)).Approved);
        var service = new OrderModifyService(
            fixture.ClOrdIds,
            fixture.Ownership,
            fixture.Orders,
            gateway,
            new NoOpExecutionEventSink(),
            new RiskPipeline(Array.Empty<IRiskCheck>()),
            margin,
            fixture.Replacements,
            fixture.Dispatcher,
            fixture.Drain,
            NullLogger<OrderModifyService>.Instance,
            outboundLedger: fixture.Ledger,
            approvalFactory: fixture.ApprovalFactory,
            outboundCoordinator: fixture.Coordinator);

        var result = await service.ModifyAsync(
            new OrderModifyRequest(
                original.Owner,
                original.ClOrdId,
                NewQuantity: 200,
                NewPrice: original.Price),
            CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.Accepted, result.Kind);
        Assert.Equal(6_000m, margin.ReservedForTesting("alice"));
        Assert.True((await margin.PrepareReplaceAsync(
            original.ClOrdId,
            result.NewClOrdId,
            original.Owner,
            original.FirmId,
            6_000m,
            CancellationToken.None)).Approved);
        Assert.Equal(6_000m, margin.ReservedForTesting("alice"));
    }

    [Theory]
    [InlineData(OutboundMutationKind.Cancel)]
    [InlineData(OutboundMutationKind.Replace)]
    public void ProvenUnsent_ReleasesOriginalForFreshMutation(OutboundMutationKind kind)
    {
        var fixture = CreateFixture(
            new RecordingGateway(GatewayOutcome.Completed),
            new RecordingReplaceMarginCoordinator());
        var original = OriginalOrder();
        AddOriginal(fixture, original);
        var firstMutationId = OutboundMutationId.New();
        var firstClOrdId = 2001UL;
        var frozen = kind == OutboundMutationKind.Cancel
            ? fixture.ApprovalFactory.CreateCancel(
                firstMutationId,
                original,
                firstClOrdId,
                DateTimeOffset.UtcNow)
            : fixture.ApprovalFactory.CreateReplace(
                firstMutationId,
                original,
                firstClOrdId,
                120,
                31.50m,
                TimeInForce.Day,
                null,
                null,
                3_780m,
                DateTimeOffset.UtcNow);
        ApplyApproval(fixture, firstMutationId, kind, original, firstClOrdId, frozen);
        var attemptId = OutboundAttemptId.New();
        fixture.Ledger.Apply(new OutboundAttemptIntentPreparedEvent
        {
            MutationId = firstMutationId,
            AttemptId = attemptId,
            AttemptNo = 1,
            ClOrdId = firstClOrdId,
            ProcessEpochId = ProcessEpochId.New(),
            IntentPreparedAtUtc = DateTimeOffset.UtcNow,
            TimestampUtc = DateTimeOffset.UtcNow,
        });
        fixture.Ledger.Apply(new OutboundProvenUnsentEvent
        {
            MutationId = firstMutationId,
            AttemptId = attemptId,
            Evidence = OutboundProvenUnsentEvidence.TypedPreFrameFailure,
            TimestampUtc = DateTimeOffset.UtcNow,
        });

        Assert.False(fixture.Ledger.TryGetActiveForOriginal(
            original.FirmId,
            original.ClOrdId,
            out _));
        var restored = new OutboundMutationLedger(fixture.Protector);
        restored.Restore(
            fixture.Ledger.SnapshotMutations(),
            fixture.Ledger.SnapshotCorrelations());
        Assert.False(restored.TryGetActiveForOriginal(
            original.FirmId,
            original.ClOrdId,
            out _));

        var secondMutationId = OutboundMutationId.New();
        var secondClOrdId = 2002UL;
        var secondFrozen = kind == OutboundMutationKind.Cancel
            ? fixture.ApprovalFactory.CreateCancel(
                secondMutationId,
                original,
                secondClOrdId,
                DateTimeOffset.UtcNow)
            : fixture.ApprovalFactory.CreateReplace(
                secondMutationId,
                original,
                secondClOrdId,
                120,
                31.50m,
                TimeInForce.Day,
                null,
                null,
                3_780m,
                DateTimeOffset.UtcNow);
        ApplyApproval(
            fixture,
            secondMutationId,
            kind,
            original,
            secondClOrdId,
            secondFrozen);

        Assert.True(fixture.Ledger.TryGetActiveForOriginal(
            original.FirmId,
            original.ClOrdId,
            out var active));
        Assert.Equal(secondMutationId, active!.MutationId);
    }

    [Theory]
    [InlineData(OutboundMutationKind.Cancel)]
    [InlineData(OutboundMutationKind.Replace)]
    public void DeadEpochIntentWithoutFrame_ReleasesOriginalForFreshMutation(
        OutboundMutationKind kind)
    {
        var fixture = CreateFixture(
            new RecordingGateway(GatewayOutcome.Completed),
            new RecordingReplaceMarginCoordinator());
        var original = OriginalOrder();
        AddOriginal(fixture, original);
        var firstMutationId = OutboundMutationId.New();
        var firstClOrdId = 2001UL;
        var frozen = CreateFrozenApproval(
            fixture,
            kind,
            firstMutationId,
            original,
            firstClOrdId);
        ApplyApproval(fixture, firstMutationId, kind, original, firstClOrdId, frozen);
        fixture.Ledger.Apply(new OutboundAttemptIntentPreparedEvent
        {
            MutationId = firstMutationId,
            AttemptId = OutboundAttemptId.New(),
            AttemptNo = 1,
            ClOrdId = firstClOrdId,
            ProcessEpochId = ProcessEpochId.New(),
            IntentPreparedAtUtc = DateTimeOffset.UtcNow,
            TimestampUtc = DateTimeOffset.UtcNow,
        });

        var recovered = new OutboundMutationLedger(fixture.Protector);
        recovered.Restore(
            fixture.Ledger.SnapshotMutations(),
            fixture.Ledger.SnapshotCorrelations());
        Assert.True(recovered.TryGetActiveForOriginal(
            original.FirmId,
            original.ClOrdId,
            out _));

        Assert.Equal(
            1,
            recovered.ClassifyRecoveredAttempts(
                ProcessEpochId.New(),
                DateTimeOffset.UtcNow));
        Assert.True(recovered.TryGet(firstMutationId, out var provenUnsent));
        Assert.Equal(OutboundMutationState.ProvenUnsent, provenUnsent!.State);
        Assert.False(recovered.TryGetActiveForOriginal(
            original.FirmId,
            original.ClOrdId,
            out _));

        var secondMutationId = OutboundMutationId.New();
        var secondClOrdId = 2002UL;
        var secondFrozen = CreateFrozenApproval(
            fixture,
            kind,
            secondMutationId,
            original,
            secondClOrdId);
        recovered.Apply(CreateApprovalEvent(
            secondMutationId,
            kind,
            original,
            secondClOrdId,
            secondFrozen));

        Assert.True(recovered.TryGetActiveForOriginal(
            original.FirmId,
            original.ClOrdId,
            out var active));
        Assert.Equal(secondMutationId, active!.MutationId);
    }

    [Fact]
    public async Task Recovery_AmbiguousReplace_RestoresMarginAndCorrelationWithoutResend()
    {
        var gateway = new RecordingGateway(GatewayOutcome.Ambiguous);
        var fixture = CreateFixture(gateway, new RecordingReplaceMarginCoordinator());
        var original = OriginalOrder();
        AddOriginal(fixture, original);
        var mutationId = Approve(fixture, original, 2001);
        await fixture.Coordinator.EnqueueAsync(mutationId, CancellationToken.None);

        var recoveredGateway = new RecordingGateway();
        var recoveredMargin = new RecordingReplaceMarginCoordinator();
        var recoveredReplacements = new PendingReplacementRegistry();
        var recoveredOrders = new WorkingOrderBook();
        Assert.True(recoveredOrders.TryAdd(original));
        var recoveredOwnership = new OrderOwnershipMap();
        recoveredOwnership.Register(original.ClOrdId, original.Owner);
        var recovered = new CancelReplaceOutboundCoordinator(
            fixture.Ledger,
            new OutboundProcessEpoch(),
            fixture.Protector,
            recoveredGateway,
            fixture.Dispatcher,
            recoveredOrders,
            new ClOrdIdPrefixRegistry(),
            recoveredOwnership,
            new PendingCancelRegistry(),
            recoveredReplacements,
            recoveredMargin,
            new RecordingDrainController(),
            NullLogger<CancelReplaceOutboundCoordinator>.Instance);

        await recovered.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!recoveredReplacements.IsOriginalInFlight(original.ClOrdId)
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(recoveredReplacements.IsOriginalInFlight(original.ClOrdId));
        Assert.True(recoveredReplacements.IsAmbiguous(2001));
        Assert.Single(recoveredMargin.Prepared);
        Assert.Empty(recoveredGateway.ReplaceCommands);
        await recovered.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void RequestedEventBeforeApproval_KeepsMutationIdentityAndUpgradesLegacyUnknown()
    {
        var fixture = CreateFixture(
            new RecordingGateway(GatewayOutcome.Completed),
            new RecordingReplaceMarginCoordinator());
        var original = OriginalOrder();
        AddOriginal(fixture, original);
        var mutationId = OutboundMutationId.New();
        fixture.Ledger.ImportLegacyReplace(new OrderReplaceRequestedEvent
        {
            MutationId = mutationId,
            OriginalClOrdId = original.ClOrdId,
            NewClOrdId = 2001,
            EndClientId = original.Owner.Value,
            FirmId = original.FirmId,
            Symbol = original.Symbol,
            SecurityId = original.SecurityId,
            Side = original.Side.ToString(),
            Type = original.Type.ToString(),
            NewQuantity = 120,
            NewPrice = 31.50m,
            TimestampUtc = DateTimeOffset.UtcNow,
        });

        Assert.True(fixture.Ledger.TryGet(mutationId, out var legacy));
        Assert.Equal(OutboundMutationState.LegacyUnknownReplace, legacy!.State);

        Approve(fixture, original, 2001, mutationId);

        Assert.True(fixture.Ledger.TryGet(mutationId, out var approved));
        Assert.Equal(OutboundMutationState.ApprovedToSend, approved!.State);
    }

    private static Fixture CreateFixture(
        RecordingGateway gateway,
        IReplaceMarginCoordinator margin)
    {
        var protector = CreateProtector();
        var ledger = new OutboundMutationLedger(protector);
        var store = new RecordingCommittedStore();
        var dispatcher = new EventDispatcher(store);
        var cancels = new PendingCancelRegistry();
        var replacements = new PendingReplacementRegistry();
        var drain = new RecordingDrainController();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var orders = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var approvalFactory = new CancelReplaceApprovalFactory(protector);
        var coordinator = new CancelReplaceOutboundCoordinator(
            ledger,
            new OutboundProcessEpoch(),
            protector,
            gateway,
            dispatcher,
            orders,
            clOrdIds,
            ownership,
            cancels,
            replacements,
            margin,
            drain,
            NullLogger<CancelReplaceOutboundCoordinator>.Instance);
        return new(
            coordinator,
            approvalFactory,
            ledger,
            dispatcher,
            cancels,
            replacements,
            drain,
            clOrdIds,
            orders,
            ownership,
            margin,
            protector);
    }

    private static OutboundMutationId Approve(
        Fixture fixture,
        Order original,
        ulong newClOrdId,
        OutboundMutationId? requestedMutationId = null)
    {
        var mutationId = requestedMutationId ?? OutboundMutationId.New();
        var frozen = fixture.ApprovalFactory.CreateReplace(
            mutationId,
            original,
            newClOrdId,
            120,
            31.50m,
            TimeInForce.Day,
            null,
            null,
            3780m,
            DateTimeOffset.UtcNow);
        ApplyApproval(
            fixture,
            mutationId,
            OutboundMutationKind.Replace,
            original,
            newClOrdId,
            frozen);
        fixture.Margin.PrepareReplaceAsync(
            original.ClOrdId,
            newClOrdId,
            original.Owner,
            3780m,
            CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(fixture.Replacements.TryAdd(new OrderReplacementIntent(
            original.ClOrdId,
            newClOrdId,
            original.Owner,
            original.Symbol,
            original.SecurityId,
            original.Side,
            original.Type,
            120,
            31.50m,
            original.FirmId,
            original.ParentAlgoId,
            original.AlgoSliceSeq,
            TimeInForce.Day)));
        return mutationId;
    }

    private static void ApplyApproval(
        Fixture fixture,
        OutboundMutationId mutationId,
        OutboundMutationKind kind,
        Order original,
        ulong clOrdId,
        (string EndClientRef, OutboundApprovalSnapshot Approval) frozen)
    {
        var approved = CreateApprovalEvent(
            mutationId,
            kind,
            original,
            clOrdId,
            frozen);
        fixture.Dispatcher.DispatchCommitted(
            approved,
            () => fixture.Ledger.Apply(approved),
            CancellationToken.None);
    }

    private static OutboundApprovedEvent CreateApprovalEvent(
        OutboundMutationId mutationId,
        OutboundMutationKind kind,
        Order original,
        ulong clOrdId,
        (string EndClientRef, OutboundApprovalSnapshot Approval) frozen) =>
        new()
        {
            MutationId = mutationId,
            MutationKind = kind,
            FirmId = original.FirmId,
            EndClientRef = frozen.EndClientRef,
            Origin = OutboundMutationOrigin.Rest,
            PrimaryClOrdId = clOrdId,
            OriginalClOrdId = original.ClOrdId,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            Approval = frozen.Approval,
            TimestampUtc = DateTimeOffset.UtcNow,
        };

    private static (string EndClientRef, OutboundApprovalSnapshot Approval)
        CreateFrozenApproval(
            Fixture fixture,
            OutboundMutationKind kind,
            OutboundMutationId mutationId,
            Order original,
            ulong clOrdId) =>
        kind == OutboundMutationKind.Cancel
            ? fixture.ApprovalFactory.CreateCancel(
                mutationId,
                original,
                clOrdId,
                DateTimeOffset.UtcNow)
            : fixture.ApprovalFactory.CreateReplace(
                mutationId,
                original,
                clOrdId,
                120,
                31.50m,
                TimeInForce.Day,
                null,
                null,
                3_780m,
                DateTimeOffset.UtcNow);

    private static Order OriginalOrder() =>
        new(
            1001,
            new EndClientId("alice"),
            "PETR4",
            4321,
            OrderSide.Buy,
            OrderType.Limit,
            100,
            30m,
            "FIRM");

    private static void AddOriginal(Fixture fixture, Order original)
    {
        Assert.True(fixture.Orders.TryAdd(original));
        fixture.Ownership.Register(original.ClOrdId, original.Owner);
    }

    private static AeadOutboundCommandProtector CreateProtector() =>
        new(
            new OutboundCommandProtectionOptions
            {
                ActiveKeyId = "test",
                ActiveKeyVersion = 1,
                Keys =
                [
                    new OutboundCommandProtectionKeyOptions
                    {
                        KeyId = "test",
                        Version = 1,
                        KeyBase64 = Convert.ToBase64String(
                            System.Security.Cryptography.SHA256.HashData(
                                System.Text.Encoding.UTF8.GetBytes(
                                    "cancel-replace-outbound-coordinator-tests"))),
                    },
                ],
            });

    private sealed record Fixture(
        CancelReplaceOutboundCoordinator Coordinator,
        CancelReplaceApprovalFactory ApprovalFactory,
        OutboundMutationLedger Ledger,
        EventDispatcher Dispatcher,
        PendingCancelRegistry Cancels,
        PendingReplacementRegistry Replacements,
        RecordingDrainController Drain,
        ClOrdIdPrefixRegistry ClOrdIds,
        WorkingOrderBook Orders,
        OrderOwnershipMap Ownership,
        IReplaceMarginCoordinator Margin,
        AeadOutboundCommandProtector Protector);

    private enum GatewayOutcome
    {
        Completed,
        ProvenUnsent,
        Ambiguous,
    }

    private sealed class RecordingGateway(params GatewayOutcome[] outcomes) : IExchangeGateway
    {
        private readonly Queue<GatewayOutcome> _outcomes = new(outcomes);

        public List<OutboundReplaceCommand> ReplaceCommands { get; } = [];

        public Task SubmitAsync(Order order, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CancelAsync(
            Order order,
            ulong newClOrdId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CancelReplaceAsync(
            Order original,
            ulong newClOrdId,
            long newQuantity,
            decimal? newPrice,
            TimeInForce? requestedTimeInForce,
            decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<ExchangeGatewayReceipt> CancelReplaceWithReceiptAsync(
            OutboundReplaceCommand command,
            ExchangeGatewayFramePreparedCallback onFramePrepared,
            CancellationToken cancellationToken)
        {
            ReplaceCommands.Add(command);
            var outcome = _outcomes.Dequeue();
            if (outcome == GatewayOutcome.ProvenUnsent)
            {
                throw new ExchangeGatewayAttemptException(
                    "not written",
                    ExchangeGatewayFailureDisposition.OutboundProvenUnsent,
                    ExchangeGatewayAttemptStage.NotStarted,
                    frame: null);
            }
            var frame = new ExchangeGatewayFrameIdentity(
                command.FirmId,
                10,
                20,
                30,
                ExchangeGatewayOperation.Replace,
                command.Canonical.ClOrdId,
                64,
                new string('a', 64));
            await onFramePrepared(frame, cancellationToken);
            if (outcome == GatewayOutcome.Ambiguous)
                throw new IOException("connection lost after frame preparation");
            return new ExchangeGatewayReceipt(
                frame,
                ExchangeGatewayAttemptStage.TransportWriteCompleted);
        }
    }

    private sealed class RecordingReplaceMarginCoordinator : IReplaceMarginCoordinator
    {
        public List<(ulong Original, ulong Replacement, decimal Notional)> Prepared { get; } = [];
        public List<ulong> Aborted { get; } = [];

        public Task<RiskDecision> PrepareReplaceAsync(
            ulong originalClOrdId,
            ulong newClOrdId,
            EndClientId owner,
            decimal newRemainingNotional,
            CancellationToken ct)
        {
            Prepared.Add((originalClOrdId, newClOrdId, newRemainingNotional));
            return Task.FromResult(RiskDecision.Approve);
        }

        public void CommitReplace(
            ulong originalClOrdId,
            ulong newClOrdId,
            decimal confirmedRemainingNotional)
        {
        }

        public void AbortReplace(ulong newClOrdId) => Aborted.Add(newClOrdId);
    }

    private sealed class RecordingDrainController : IDrainController
    {
        public bool IsDraining { get; private set; }
        public void BeginDrain(string reason) => IsDraining = true;
    }

    private sealed class RecordingCommittedStore : IEventStore
    {
        private long _seq;
        public long CurrentSeq => _seq;
        public long LastCommittedSeq => _seq;
        public long Append(WalEvent evt) => Append(evt, ReadOnlyMemory<byte>.Empty);
        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload) => ++_seq;
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask FlushThroughAsync(long seq, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
