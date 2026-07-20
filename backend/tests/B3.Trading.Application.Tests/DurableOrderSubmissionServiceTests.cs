using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

public sealed class DurableOrderSubmissionServiceTests
{
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
        RecordingStore? store = null)
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
            NullLogger<OrderSubmissionService>.Instance,
            outboundLedger: ledger,
            approvalFactory: new NewOrderApprovalFactory(protector),
            outboundCoordinator: coordinator);
        return new Fixture(service, store, gateway, margin, book, ledger, drain);
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
        public List<WalEvent> Events { get; } = new();
        public long CurrentSeq => _seq;
        public long LastCommittedSeq => _seq;

        public long Append(WalEvent evt) => Append(evt, ReadOnlyMemory<byte>.Empty);

        public virtual long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            Events.Add(evt);
            return ++_seq;
        }

        public ValueTask FlushAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

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
}
