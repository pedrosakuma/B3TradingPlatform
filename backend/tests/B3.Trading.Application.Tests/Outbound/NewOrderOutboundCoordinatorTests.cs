using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Outbound;

public sealed class NewOrderOutboundCoordinatorTests
{
    [Fact]
    public async Task ApprovedMutation_CommitsIntentFrameAndWriteBeforeReturning()
    {
        var fixture = CreateFixture(new CompletingGateway());

        var result = await fixture.Coordinator.EnqueueAsync(
            fixture.MutationId,
            CancellationToken.None);

        Assert.Equal(NewOrderDispatchOutcome.TransportWriteCompleted, result.Outcome);
        Assert.True(fixture.Ledger.TryGet(fixture.MutationId, out var mutation));
        Assert.Equal(OutboundMutationState.TransportWriteCompleted, mutation!.State);
        Assert.Collection(
            fixture.Store.Events.Skip(1),
            e => Assert.IsType<OutboundAttemptIntentPreparedEvent>(e),
            e => Assert.IsType<OutboundFramePreparedEvent>(e),
            e => Assert.IsType<OutboundTransportWriteCompletedEvent>(e));
    }

    [Fact]
    public async Task ExceptionAfterFrame_IsAmbiguousAndDoesNotReleaseMargin()
    {
        var margin = new RecordingMarginProvider();
        var fixture = CreateFixture(new ThrowAfterFrameGateway(), margin);

        var result = await fixture.Coordinator.EnqueueAsync(
            fixture.MutationId,
            CancellationToken.None);

        Assert.Equal(NewOrderDispatchOutcome.ReconciliationRequired, result.Outcome);
        Assert.True(fixture.Ledger.TryGet(fixture.MutationId, out var mutation));
        Assert.Equal(OutboundMutationState.Ambiguous, mutation!.State);
        Assert.True(mutation.RequiresReconciliation);
        Assert.Equal(0, margin.ReleaseCount);
        Assert.True(fixture.Drain.IsDraining);
        Assert.Equal(OrderStatus.PendingNew, fixture.Order.Status);
    }

    [Fact]
    public async Task TypedPreFrameFailure_IsProvenUnsentAndReleasesMargin()
    {
        var margin = new RecordingMarginProvider();
        var fixture = CreateFixture(new ProvenUnsentGateway(), margin);

        var result = await fixture.Coordinator.EnqueueAsync(
            fixture.MutationId,
            CancellationToken.None);

        Assert.Equal(NewOrderDispatchOutcome.ProvenUnsent, result.Outcome);
        Assert.True(fixture.Ledger.TryGet(fixture.MutationId, out var mutation));
        Assert.Equal(OutboundMutationState.ProvenUnsent, mutation!.State);
        Assert.Equal(1, margin.ReleaseCount);
        Assert.False(fixture.Drain.IsDraining);
    }

    [Fact]
    public async Task RecoveryStart_EntersApprovedMutationExactlyOnce()
    {
        var gateway = new CompletingGateway();
        var fixture = CreateFixture(gateway);

        await fixture.Coordinator.StartAsync(CancellationToken.None);
        await fixture.Coordinator.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (gateway.CallCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(1, gateway.CallCount);
        Assert.True(fixture.Ledger.TryGet(fixture.MutationId, out var mutation));
        Assert.Equal(OutboundMutationState.TransportWriteCompleted, mutation!.State);
    }

    [Fact]
    public async Task FramePersistenceFailure_PreventsWriteAndRequiresReconciliation()
    {
        var gateway = new CompletingGateway();
        var margin = new RecordingMarginProvider();
        var fixture = CreateFixture(gateway, margin, new FrameRejectingStore());

        var result = await fixture.Coordinator.EnqueueAsync(
            fixture.MutationId,
            CancellationToken.None);

        Assert.Equal(NewOrderDispatchOutcome.ReconciliationRequired, result.Outcome);
        Assert.Equal(1, gateway.CallCount);
        Assert.DoesNotContain(
            fixture.Store.Events,
            evt => evt is OutboundTransportWriteCompletedEvent);
        Assert.Equal(0, margin.ReleaseCount);
        Assert.True(fixture.Drain.IsDraining);
    }

    private static Fixture CreateFixture(
        IExchangeGateway gateway,
        RecordingMarginProvider? margin = null,
        RecordingCommittedStore? store = null)
    {
        var protector = CreateProtector();
        var ledger = new OutboundMutationLedger(protector);
        store ??= new RecordingCommittedStore();
        var dispatcher = new EventDispatcher(store);
        var mutationId = new OutboundMutationId(
            Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var order = new Order(
            1001,
            new EndClientId("alice"),
            "PETR4",
            4321,
            OrderSide.Buy,
            OrderType.Limit,
            100,
            30m,
            "FIRM-A");
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(order));
        var command = new OutboundCanonicalCommand
        {
            ClOrdId = order.ClOrdId,
            SecurityId = order.SecurityId,
            Symbol = order.Symbol,
            Side = order.Side.ToString(),
            OrderType = order.Type.ToString(),
            Quantity = order.Quantity,
            Price = order.Price,
        };
        var approval = OutboundApprovalFactory.Create(
            mutationId,
            order.FirmId,
            command,
            new SensitiveOutboundCommand { EndClientId = order.Owner.Value },
            [OutboundSensitiveFieldRef.EndClientId],
            protector,
            DateTimeOffset.Parse("2026-07-19T00:00:00Z"));
        var approved = new OutboundApprovedEvent
        {
            MutationId = mutationId,
            MutationKind = OutboundMutationKind.New,
            FirmId = order.FirmId,
            EndClientRef = protector.CreateStableEndClientRef(
                order.FirmId,
                order.Owner.Value),
            Origin = OutboundMutationOrigin.Rest,
            PrimaryClOrdId = order.ClOrdId,
            RecordedAtUtc = DateTimeOffset.Parse("2026-07-19T00:00:00Z"),
            Approval = approval,
            TimestampUtc = DateTimeOffset.Parse("2026-07-19T00:00:00Z"),
        };
        dispatcher.DispatchCommitted(
            approved,
            () => ledger.Apply(approved),
            CancellationToken.None);
        var drain = new RecordingDrainController();
        var coordinator = new NewOrderOutboundCoordinator(
            ledger,
            new OutboundProcessEpoch(new ProcessEpochId(
                Guid.Parse("22222222-2222-2222-2222-222222222222"))),
            protector,
            gateway,
            dispatcher,
            book,
            margin ?? new RecordingMarginProvider(),
            drain,
            NullLogger<NewOrderOutboundCoordinator>.Instance);
        return new Fixture(
            coordinator,
            ledger,
            store,
            mutationId,
            order,
            drain);
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
                                    "new-order-outbound-coordinator-tests"))),
                    },
                ],
            });

    private sealed record Fixture(
        NewOrderOutboundCoordinator Coordinator,
        OutboundMutationLedger Ledger,
        RecordingCommittedStore Store,
        OutboundMutationId MutationId,
        Order Order,
        RecordingDrainController Drain);

    private class CompletingGateway : IExchangeGateway
    {
        public int CallCount { get; private set; }

        public Task SubmitAsync(Order order, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual async Task<ExchangeGatewayReceipt> SubmitWithReceiptAsync(
            OutboundNewOrderCommand command,
            ExchangeGatewayFramePreparedCallback onFramePrepared,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var frame = Frame(command);
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

    private sealed class ThrowAfterFrameGateway : CompletingGateway
    {
        public override async Task<ExchangeGatewayReceipt> SubmitWithReceiptAsync(
            OutboundNewOrderCommand command,
            ExchangeGatewayFramePreparedCallback onFramePrepared,
            CancellationToken cancellationToken)
        {
            var frame = Frame(command);
            await onFramePrepared(frame, cancellationToken);
            throw new IOException("socket outcome unknown");
        }
    }

    private sealed class ProvenUnsentGateway : CompletingGateway
    {
        public override Task<ExchangeGatewayReceipt> SubmitWithReceiptAsync(
            OutboundNewOrderCommand command,
            ExchangeGatewayFramePreparedCallback onFramePrepared,
            CancellationToken cancellationToken) =>
            Task.FromException<ExchangeGatewayReceipt>(
                new ExchangeGatewayAttemptException(
                    "encode failed",
                    ExchangeGatewayFailureDisposition.OutboundProvenUnsent,
                    ExchangeGatewayAttemptStage.SequenceReservedAndEncoded,
                    frame: null));
    }

    private static ExchangeGatewayFrameIdentity Frame(
        OutboundNewOrderCommand command) =>
        new(
            command.FirmId,
            10,
            20,
            30,
            ExchangeGatewayOperation.NewOrder,
            command.Canonical.ClOrdId,
            64,
            new string('a', 64));

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

    private sealed class RecordingDrainController : IDrainController
    {
        public bool IsDraining { get; private set; }
        public void BeginDrain(string reason) => IsDraining = true;
    }

    private class RecordingCommittedStore : IEventStore
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

    private sealed class FrameRejectingStore : RecordingCommittedStore
    {
        public override long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            if (evt is OutboundFramePreparedEvent)
                throw new WalBackpressureException("frame persistence failed");
            return base.Append(evt, preSerialisedPayload);
        }
    }
}
