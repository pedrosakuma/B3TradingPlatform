using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

public sealed class DurableOrderSubmissionServiceTests
{
    [Fact]
    public async Task C01_CrashBeforeRecordedIntentAdmission_RetryReusesUncommittedClOrdId()
    {
        var crashed = CreateFixture(
            Array.Empty<IRiskCheck>(),
            faultInjector: new ThrowingFaultInjector(
                OutboundSubmissionFaultPoint.BeforeRecordedIntentAdmission));

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => crashed.Service.SubmitAsync(Request(), CancellationToken.None));

        Assert.Empty(crashed.Store.Events);
        Assert.Equal(0, crashed.Gateway.CallCount);

        var restarted = CreateFixture(Array.Empty<IRiskCheck>());
        var retry = await restarted.Service.SubmitAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(OrderSubmissionResultKind.Accepted, retry.Kind);
        Assert.Equal(1UL, retry.ClOrdId);
        Assert.Equal(1, restarted.Gateway.CallCount);
    }

    [Fact]
    public async Task C03_CrashAfterIntentCommitBeforeRisk_RestartsFailClosedWithoutPolicyVersion()
    {
        var fixture = CreateFixture(
            Array.Empty<IRiskCheck>(),
            faultInjector: new ThrowingFaultInjector(
                OutboundSubmissionFaultPoint.AfterRecordedIntentCommittedBeforeRisk));

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => fixture.Service.SubmitAsync(Request(), CancellationToken.None));

        var submitted = Assert.IsType<OrderSubmittedEvent>(
            Assert.Single(fixture.Store.Events));
        Assert.Equal(0, fixture.Gateway.CallCount);

        var recovered = RecoverIntentOnly(submitted);
        var mutation = Assert.Single(recovered.SnapshotMutations());
        Assert.Equal(
            OutboundMutationState.RecordedPendingApproval,
            mutation.State);
        Assert.True(mutation.RequiresReconciliation);
        Assert.Equal(1, recovered.ReadinessBlockingCount);
    }

    [Fact]
    public async Task C04_CrashAfterRiskRejectBeforeCommit_ReevaluatesAsPendingApprovalNotPriorReject()
    {
        var fixture = CreateFixture(
            [new RejectingRiskCheck()],
            faultInjector: new ThrowingFaultInjector(
                OutboundSubmissionFaultPoint.AfterRiskRejectedBeforeRejectionCommit));

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => fixture.Service.SubmitAsync(Request(), CancellationToken.None));

        var submitted = Assert.IsType<OrderSubmittedEvent>(
            Assert.Single(fixture.Store.Events));
        Assert.DoesNotContain(
            fixture.Store.Events,
            evt => evt is ExecutionReportReceivedEvent);
        Assert.Equal(0, fixture.Gateway.CallCount);

        var recovered = RecoverIntentOnly(submitted);
        var mutation = Assert.Single(recovered.SnapshotMutations());
        Assert.Equal(
            OutboundMutationState.RecordedPendingApproval,
            mutation.State);
        Assert.Null(mutation.Resolution);
        Assert.True(mutation.RequiresReconciliation);
    }

    [Fact]
    public async Task ApprovedSubmit_CommitsPendingApprovalIntentFrameAndWriteInOrder()
    {
        var fixture = CreateFixture(Array.Empty<IRiskCheck>());

        var result = await fixture.Service.SubmitAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(OrderSubmissionResultKind.Accepted, result.Kind);
        Assert.Collection(
            fixture.Store.Events,
            e => Assert.IsType<OrderSubmittedEvent>(e),
            e => Assert.IsType<OutboundApprovedEvent>(e),
            e => Assert.IsType<OutboundAttemptIntentPreparedEvent>(e),
            e => Assert.IsType<OutboundFramePreparedEvent>(e),
            e => Assert.IsType<OutboundTransportWriteCompletedEvent>(e));
        Assert.Equal(1, fixture.Gateway.CallCount);
    }

    [Fact]
    public async Task RiskReject_IsDurableBeforeApprovalAndNeverEntersGateway()
    {
        var fixture = CreateFixture([new RejectingRiskCheck()]);

        var result = await fixture.Service.SubmitAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(OrderSubmissionResultKind.Rejected, result.Kind);
        Assert.Collection(
            fixture.Store.Events,
            e => Assert.IsType<OrderSubmittedEvent>(e),
            e =>
            {
                var rejection = Assert.IsType<ExecutionReportReceivedEvent>(e);
                Assert.True(rejection.Synthetic);
            });
        Assert.DoesNotContain(fixture.Store.Events, e => e is OutboundApprovedEvent);
        Assert.Equal(0, fixture.Gateway.CallCount);

        var submitted = Assert.IsType<OrderSubmittedEvent>(fixture.Store.Events[0]);
        var rejection = Assert.IsType<ExecutionReportReceivedEvent>(
            fixture.Store.Events[1]);
        var recovered = new OutboundMutationLedger();
        recovered.ImportLegacyNew(submitted);
        var replayResult = recovered.ApplyVenueAcknowledgement(rejection);
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedMatched,
            replayResult.Status);
        var mutationId = Assert.IsType<OutboundMutationId>(submitted.MutationId);
        Assert.True(recovered.TryGet(mutationId, out var mutation));
        Assert.Equal(OutboundMutationState.OperatorResolved, mutation!.State);
        Assert.Equal(
            "OutboundProvenNoWrite",
            mutation.Resolution?.EvidenceKind);
        Assert.False(mutation.RequiresReconciliation);
    }

    [Fact]
    public async Task ProvenUnsent_CommitsDomainTerminalBeforeReleasingMargin()
    {
        var fixture = CreateFixture(
            Array.Empty<IRiskCheck>(),
            gateway: new ProvenUnsentGateway());

        var result = await fixture.Service.SubmitAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(OrderSubmissionResultKind.Rejected, result.Kind);
        Assert.Equal("gateway_proven_unsent", result.Code);
        Assert.Collection(
            fixture.Store.Events,
            e => Assert.IsType<OrderSubmittedEvent>(e),
            e => Assert.IsType<OutboundApprovedEvent>(e),
            e => Assert.IsType<OutboundAttemptIntentPreparedEvent>(e),
            e => Assert.IsType<OutboundProvenUnsentEvent>(e),
            e => Assert.True(Assert.IsType<ExecutionReportReceivedEvent>(e).OutboundProvenNoWrite));
        Assert.True(fixture.Book.TryGet(result.ClOrdId, out var order));
        Assert.Equal(OrderStatus.Rejected, order!.Status);
        Assert.Equal(1, fixture.Margin.ReleaseCount);
        Assert.True(fixture.Ledger.TryGet(result.MutationId, out var mutation));
        Assert.Equal(OutboundMutationState.OperatorResolved, mutation!.State);
        Assert.Equal(0, fixture.Ledger.ReadinessBlockingCount);
    }

    // #768. The modern outbound-dispatch non-success branch previously
    // only incremented a metric; verify it now also logs the business
    // identifiers an operator needs to correlate a lost HTTP response
    // (MutationId/FirmId/ClOrdId) without duplicating them as high-
    // cardinality metric labels.
    [Fact]
    public async Task ProvenUnsent_LogsMutationFirmClOrdIdAndOutcome()
    {
        var logger = new CapturingLogger<OrderSubmissionService>();
        var fixture = CreateFixture(
            Array.Empty<IRiskCheck>(),
            gateway: new ProvenUnsentGateway(),
            logger: logger);

        var result = await fixture.Service.SubmitAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(OrderSubmissionResultKind.Rejected, result.Kind);
        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(result.MutationId.ToString(), entry.Message);
        Assert.Contains("FIRM-A", entry.Message);
        Assert.Contains(result.ClOrdId.ToString(), entry.Message);
    }

    [Fact]
    public async Task AlgoProvenUnsent_TerminalizesPhantomChildWithoutPermittingAutomaticRetry()
    {
        var fixture = CreateFixture(
            Array.Empty<IRiskCheck>(),
            gateway: new ProvenUnsentGateway());
        var origin = new AlgoOutboundOriginIdentity(
            99,
            AlgoOutboundActionKind.NewChild,
            0);
        var request = Request() with
        {
            Source = OrderSubmissionSource.Algo,
            ParentAlgoId = origin.ParentAlgoId,
            AlgoSliceSeq = origin.Sequence,
            AlgoOriginIdentity = origin,
        };

        var result = await fixture.Service.SubmitAsync(request, CancellationToken.None);

        Assert.Equal(OrderSubmissionResultKind.ReconciliationRequired, result.Kind);
        Assert.True(fixture.Book.TryGet(result.ClOrdId, out var child));
        Assert.Equal(OrderStatus.Rejected, child!.Status);
        Assert.Equal(1, fixture.Margin.ReleaseCount);
        Assert.True(fixture.Ledger.TryGet(result.MutationId, out var mutation));
        Assert.Equal(OutboundMutationState.OperatorResolved, mutation!.State);
        Assert.Equal(origin, mutation.AlgoOriginIdentity);
    }

    [Fact]
    public async Task ApprovalAppendFailure_TerminalisesNoWriteBeforeMarginRelease()
    {
        var fixture = CreateFixture(
            Array.Empty<IRiskCheck>(),
            store: new RejectingApprovalStore(rejectTerminal: false));

        var result = await fixture.Service.SubmitAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(OrderSubmissionResultKind.Rejected, result.Kind);
        Assert.Equal("outbound_approval_not_committed", result.Code);
        Assert.Collection(
            fixture.Store.Events,
            e => Assert.IsType<OrderSubmittedEvent>(e),
            e => Assert.True(Assert.IsType<ExecutionReportReceivedEvent>(e).OutboundProvenNoWrite));
        Assert.Equal(1, fixture.Margin.ReleaseCount);
        Assert.True(fixture.Book.TryGet(result.ClOrdId, out var order));
        Assert.Equal(OrderStatus.Rejected, order!.Status);
        Assert.Equal(0, fixture.Gateway.CallCount);
    }

    [Fact]
    public async Task C05_ApprovalAppendedButNotCommitted_RestartsPendingApprovalWithoutGatewayCall()
    {
        var store = new UncommittedApprovalCrashStore();
        var fixture = CreateFixture(Array.Empty<IRiskCheck>(), store: store);

        await Assert.ThrowsAsync<SimulatedApprovalCommitCrashException>(
            () => fixture.Service.SubmitAsync(Request(), CancellationToken.None));

        Assert.Collection(
            store.Events,
            evt => Assert.IsType<OrderSubmittedEvent>(evt),
            evt => Assert.IsType<OutboundApprovedEvent>(evt));
        Assert.Equal(1, store.LastCommittedSeq);
        Assert.Equal(0, fixture.Gateway.CallCount);

        var submitted = Assert.IsType<OrderSubmittedEvent>(
            Assert.Single(store.CommittedEvents));
        Assert.Equal(1UL, submitted.ClOrdId);
        Assert.DoesNotContain(
            store.CommittedEvents,
            evt => evt is OutboundApprovedEvent);

        var recovered = new OutboundMutationLedger();
        foreach (var evt in store.CommittedEvents)
            recovered.ImportLegacyNew(Assert.IsType<OrderSubmittedEvent>(evt));
        var recoveredMutationId = Assert.IsType<OutboundMutationId>(
            submitted.MutationId);
        Assert.True(recovered.TryGet(recoveredMutationId, out var mutation));
        Assert.Equal(
            OutboundMutationState.RecordedPendingApproval,
            mutation!.State);
        Assert.Empty(mutation.Attempts);
        Assert.True(mutation.RequiresReconciliation);
    }

    [Fact]
    public async Task NoWriteTerminalCommitFailure_HoldsMarginAndDrains()
    {
        var fixture = CreateFixture(
            Array.Empty<IRiskCheck>(),
            store: new RejectingApprovalStore(rejectTerminal: true));

        var result = await fixture.Service.SubmitAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(OrderSubmissionResultKind.ReconciliationRequired, result.Kind);
        Assert.Equal(0, fixture.Margin.ReleaseCount);
        Assert.True(fixture.Book.TryGet(result.ClOrdId, out var order));
        Assert.Equal(OrderStatus.PendingNew, order!.Status);
        Assert.True(fixture.Drain.IsDraining);
        Assert.Equal(0, fixture.Gateway.CallCount);
    }

    private static Fixture CreateFixture(
        IEnumerable<IRiskCheck> checks,
        CompletingGateway? gateway = null,
        RecordingStore? store = null,
        IOutboundSubmissionFaultInjector? faultInjector = null,
        ILogger<OrderSubmissionService>? logger = null)
    {
        var protector = new AeadOutboundCommandProtector(
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
                                    "durable-order-submission-service-tests"))),
                    },
                ],
            });
        store ??= new RecordingStore();
        var dispatcher = new EventDispatcher(store);
        var ledger = new OutboundMutationLedger(protector);
        var book = new WorkingOrderBook();
        gateway ??= new CompletingGateway();
        var drain = new RecordingDrain();
        var margin = new RecordingMarginProvider();
        var coordinator = new NewOrderOutboundCoordinator(
            ledger,
            new OutboundProcessEpoch(new ProcessEpochId(
                Guid.Parse("33333333-3333-3333-3333-333333333333"))),
            protector,
            gateway,
            dispatcher,
            book,
            margin,
            drain,
            NullLogger<NewOrderOutboundCoordinator>.Instance);
        var service = new OrderSubmissionService(
            new ClOrdIdPrefixRegistry(),
            new OrderOwnershipMap(),
            book,
            gateway,
            new NullExecutionSink(),
            new RiskPipeline(checks),
            margin,
            new CompositeRiskAccountant(Array.Empty<IRiskAccountant>()),
            dispatcher,
            drain,
            logger ?? NullLogger<OrderSubmissionService>.Instance,
            outboundLedger: ledger,
            approvalFactory: new NewOrderApprovalFactory(protector),
            outboundCoordinator: coordinator,
            faultInjector: faultInjector);
        return new Fixture(service, store, gateway, margin, book, ledger, drain);
    }

    private static OutboundMutationLedger RecoverIntentOnly(
        OrderSubmittedEvent submitted)
    {
        var ledger = new OutboundMutationLedger();
        ledger.ImportLegacyNew(submitted);
        return ledger;
    }

    private static OrderSubmissionRequest Request() =>
        new(
            new EndClientId("alice"),
            "FIRM-A",
            "PETR4",
            4321,
            OrderSide.Buy,
            OrderType.Limit,
            100,
            30m)
        {
            UseDurableOutboundCoordinator = true,
        };

    private sealed record Fixture(
        OrderSubmissionService Service,
        RecordingStore Store,
        CompletingGateway Gateway,
        RecordingMarginProvider Margin,
        WorkingOrderBook Book,
        OutboundMutationLedger Ledger,
        RecordingDrain Drain);

    private sealed class RejectingRiskCheck : IRiskCheck
    {
        public int Order => 0;
        public string Name => "test_reject";
        public RiskDecision Check(RiskContext ctx) =>
            RiskDecision.Reject("test_reject", "rejected by test");
    }

    private sealed class ThrowingFaultInjector(OutboundSubmissionFaultPoint target)
        : IOutboundSubmissionFaultInjector
    {
        public void OnBoundary(OutboundSubmissionFaultPoint point)
        {
            if (point == target)
                throw new SimulatedCrashException(point);
        }
    }

    private sealed class SimulatedCrashException(OutboundSubmissionFaultPoint point)
        : Exception($"Simulated process crash at {point}.");

    private class CompletingGateway : IExchangeGateway
    {
        public int CallCount { get; protected set; }

        public Task SubmitAsync(Order order, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual async Task<ExchangeGatewayReceipt> SubmitWithReceiptAsync(
            OutboundNewOrderCommand command,
            ExchangeGatewayFramePreparedCallback onFramePrepared,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var frame = new ExchangeGatewayFrameIdentity(
                command.FirmId,
                1,
                1,
                1,
                ExchangeGatewayOperation.NewOrder,
                command.Canonical.ClOrdId,
                1,
                new string('a', 64));
            await onFramePrepared(frame, cancellationToken);
            return new ExchangeGatewayReceipt(
                frame,
                ExchangeGatewayAttemptStage.TransportWriteCompleted);
        }

        public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken) =>
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
    }

    private sealed class ProvenUnsentGateway : CompletingGateway
    {
        public override Task<ExchangeGatewayReceipt> SubmitWithReceiptAsync(
            OutboundNewOrderCommand command,
            ExchangeGatewayFramePreparedCallback onFramePrepared,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromException<ExchangeGatewayReceipt>(
                new ExchangeGatewayAttemptException(
                    "typed no-write",
                    ExchangeGatewayFailureDisposition.OutboundProvenUnsent,
                    ExchangeGatewayAttemptStage.SequenceReservedAndEncoded,
                    frame: null));
        }
    }

    private sealed class RecordingMarginProvider : IMarginProvider
    {
        public int ReleaseCount { get; private set; }

        public Task<RiskDecision> TryReserveAsync(
            ulong clOrdId,
            RiskContext ctx,
            CancellationToken ct) =>
            Task.FromResult(RiskDecision.Approve);

        public void ReleaseReservation(ulong clOrdId) => ReleaseCount++;
    }

    private sealed class NullExecutionSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent ev) { }
    }

    private sealed class RecordingDrain : IDrainController
    {
        public bool IsDraining { get; private set; }
        public void BeginDrain(string reason) => IsDraining = true;
    }

    private class RecordingStore : IEventStore
    {
        private long _seq;
        private long _lastCommittedSeq;
        public List<WalEvent> Events { get; } = new();
        public IReadOnlyList<WalEvent> CommittedEvents =>
            Events.Take(checked((int)_lastCommittedSeq)).ToArray();
        public long CurrentSeq => _seq;
        public virtual long LastCommittedSeq => _lastCommittedSeq;

        public long Append(WalEvent evt) => Append(evt, ReadOnlyMemory<byte>.Empty);

        public virtual long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            Events.Add(evt);
            return ++_seq;
        }

        public ValueTask FlushAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public virtual ValueTask FlushThroughAsync(long seq, CancellationToken ct = default)
        {
            _lastCommittedSeq = Math.Max(_lastCommittedSeq, seq);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RejectingApprovalStore(bool rejectTerminal) : RecordingStore
    {
        public override long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            if (evt is OutboundApprovedEvent
                || (rejectTerminal
                    && evt is ExecutionReportReceivedEvent { OutboundProvenNoWrite: true }))
                throw new WalBackpressureException("forced no-write terminal failure");
            return base.Append(evt, preSerialisedPayload);
        }
    }

    private sealed class UncommittedApprovalCrashStore : RecordingStore
    {
        private long? _approvalSeq;

        public override long Append(
            WalEvent evt,
            ReadOnlyMemory<byte> preSerialisedPayload)
        {
            var seq = base.Append(evt, preSerialisedPayload);
            if (evt is OutboundApprovedEvent)
                _approvalSeq = seq;
            return seq;
        }

        public override ValueTask FlushThroughAsync(
            long seq,
            CancellationToken ct = default)
        {
            if (_approvalSeq == seq)
                throw new SimulatedApprovalCommitCrashException();
            return base.FlushThroughAsync(seq, ct);
        }
    }

    private sealed class SimulatedApprovalCommitCrashException()
        : Exception("Simulated crash after approval append and before marker commit.");

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
