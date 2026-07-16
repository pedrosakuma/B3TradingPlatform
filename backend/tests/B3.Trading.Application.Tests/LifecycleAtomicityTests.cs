using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

public sealed class LifecycleAtomicityTests
{
    private static readonly EndClientId Owner = new("alice");

    [Fact]
    public async Task ConcurrentModify_Barrier_AllowsExactlyOneGatewayCall()
    {
        var gateway = new TestGateway { BlockReplace = true };
        var harness = BuildModify(gateway);
        using var barrier = new Barrier(3);
        var request = new OrderModifyRequest(Owner, 100, 120, 31m);

        var first = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await harness.Service.ModifyAsync(request, CancellationToken.None);
        });
        var second = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await harness.Service.ModifyAsync(request, CancellationToken.None);
        });

        barrier.SignalAndWait();
        await gateway.ReplaceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gateway.ReleaseReplace.SetResult();

        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, gateway.ReplaceCalls);
        Assert.Single(results, static r => r.Kind == OrderModifyResultKind.Accepted);
        Assert.Single(results, static r => r.Kind == OrderModifyResultKind.Conflict);
    }

    [Fact]
    public async Task PreSendReplaceFailure_AppendsTerminalResolution_AndReplayDoesNotResurrect()
    {
        var store = new RecordingEventStore();
        var gateway = new TestGateway
        {
            ReplaceException = new ExchangeGatewayPreSendException("not connected"),
        };
        var harness = BuildModify(gateway, store);

        var result = await harness.Service.ModifyAsync(
            new OrderModifyRequest(Owner, 100, 120, 31m), CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.GatewayFailed, result.Kind);
        Assert.Collection(store.Events,
            static e => Assert.IsType<OrderReplaceRequestedEvent>(e),
            static e => Assert.IsType<OrderReplacePreSendFailedEvent>(e));
        Assert.False(harness.Replacements.IsOriginalInFlight(100));

        var recovered = BuildRecoveryState();
        foreach (var evt in store.Events)
            recovered.Replayer.Apply(evt);

        Assert.False(recovered.Replacements.IsOriginalInFlight(100));
    }

    [Fact]
    public async Task AmbiguousReplaceFailure_RemainsPendingAcrossReplay()
    {
        var store = new RecordingEventStore();
        var gateway = new TestGateway
        {
            ReplaceException = new IOException("write outcome unknown"),
        };
        var harness = BuildModify(gateway, store);

        var result = await harness.Service.ModifyAsync(
            new OrderModifyRequest(Owner, 100, 120, 31m), CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.GatewayAmbiguous, result.Kind);
        Assert.True(harness.Replacements.IsOriginalInFlight(100));
        Assert.IsType<OrderReplaceAmbiguousMarginHeldEvent>(store.Events[^1]);

        var recovered = BuildRecoveryState();
        foreach (var evt in store.Events)
            recovered.Replayer.Apply(evt);

        Assert.True(recovered.Replacements.IsOriginalInFlight(100));
        Assert.True(Assert.Single(recovered.Replacements.Snapshot()).AmbiguousMarginHeld);
    }

    [Fact]
    public async Task DuplicateCancel_ReturnsExistingMutation_WithoutAllocatingOrSendingAgain()
    {
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var gateway = new TestGateway();
        var pending = new PendingCancelRegistry();
        var service = BuildCancel(clOrdIds, gateway, pending);

        var first = await service.CancelAsync(Owner, 100, CancellationToken.None);
        var duplicate = await service.CancelAsync(Owner, 100, CancellationToken.None);
        var nextAllocated = clOrdIds.Generate(Owner);

        Assert.Equal(OrderCancelResultKind.Accepted, first.Kind);
        Assert.Equal(first.CancelClOrdId, duplicate.CancelClOrdId);
        Assert.Equal(first.CancelClOrdId + 1, nextAllocated);
        Assert.Equal(1, gateway.CancelCalls);
    }

    [Fact]
    public async Task ReplayedPendingCancel_MakesRetryIdempotentWithoutGatewayCall()
    {
        var pending = new PendingCancelRegistry();
        var recovered = BuildRecoveryState(pendingCancels: pending);
        recovered.Replayer.Apply(new OrderCancelRequestedEvent
        {
            CancelClOrdId = 200,
            OriginalClOrdId = 100,
            OwnerEndClientId = Owner.Value,
        });
        var gateway = new TestGateway();
        var service = new OrderCancelService(
            recovered.ClOrdIds, recovered.Ownership, recovered.Book, gateway,
            new EventDispatcher(new NullEventStore()),
            NullLogger<OrderCancelService>.Instance,
            pendingCancels: pending);

        var retry = await service.CancelAsync(Owner, 100, CancellationToken.None);

        Assert.Equal(OrderCancelResultKind.Accepted, retry.Kind);
        Assert.Equal(200UL, retry.CancelClOrdId);
        Assert.Equal(0, gateway.CancelCalls);
    }

    [Fact]
    public void PendingCancelSnapshot_RestoresIdempotencyKey()
    {
        var original = new PendingCancelRegistry();
        Assert.True(original.TryAdd(100, 200));
        var restored = new PendingCancelRegistry();

        restored.Restore(original.Snapshot());
        var claim = restored.Claim(100);

        Assert.False(claim.IsAcquired);
        Assert.Equal(200UL, claim.ExistingCancelClOrdId);
    }

    [Fact]
    public void CancelReject_ResolvesPendingIntent_WithoutRejectingOriginalOrder()
    {
        var pending = new PendingCancelRegistry();
        Assert.True(pending.TryAdd(100, 200));
        var recovered = BuildRecoveryState(pendingCancels: pending);

        recovered.Replayer.Apply(new ExecutionReportReceivedEvent
        {
            ClOrdId = 200,
            OrigClOrdId = 100,
            ExecKind = nameof(ExecKind.Rejected),
            LeavesQuantity = 100,
            CumulativeQuantity = 0,
            LastQuantity = 0,
            LastPrice = 0m,
            RejectReason = "too_late_to_cancel",
            Synthetic = false,
        });

        Assert.True(recovered.Book.TryGet(100, out var order));
        Assert.Equal(OrderStatus.Working, order!.Status);
        Assert.Equal(0, pending.CountForTesting);
    }

    [Fact]
    public async Task TerminalCancel_IsRejectedWithoutAllocationOrGatewayCall()
    {
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var gateway = new TestGateway();
        var pending = new PendingCancelRegistry();
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var order = Working();
        order.MarkCancelled();
        Assert.True(book.TryAdd(order));
        ownership.Register(order.ClOrdId, Owner);
        var service = new OrderCancelService(
            clOrdIds, ownership, book, gateway, new EventDispatcher(new NullEventStore()),
            NullLogger<OrderCancelService>.Instance, pendingCancels: pending);

        var result = await service.CancelAsync(Owner, 100, CancellationToken.None);

        Assert.Equal(OrderCancelResultKind.Conflict, result.Kind);
        Assert.Equal(0, gateway.CancelCalls);
        Assert.Equal(1UL, clOrdIds.Generate(Owner));
    }

    [Theory]
    [InlineData(OrderType.Limit, null)]
    [InlineData(OrderType.Limit, -1d)]
    [InlineData(OrderType.Market, 30d)]
    [InlineData(OrderType.StopLoss, 30d)]
    public async Task ApplicationSubmit_RejectsInvalidPriceTypeFromNonRestIngress(
        OrderType type,
        double? price)
    {
        var gateway = new TestGateway();
        var book = new WorkingOrderBook();
        var submitter = new OrderSubmissionService(
            new ClOrdIdPrefixRegistry(), new OrderOwnershipMap(), book, gateway,
            new RecordingSink(), new RiskPipeline(Array.Empty<IRiskCheck>()),
            new NoOpMarginProvider(), new CompositeRiskAccountant(Array.Empty<IRiskAccountant>()),
            new EventDispatcher(new NullEventStore()), new NeverDrainController(),
            NullLogger<OrderSubmissionService>.Instance);

        var result = await submitter.SubmitAsync(
            new OrderSubmissionRequest(
                Owner, "FIRM", "PETR4", 4321, OrderSide.Buy, type, 100,
                price.HasValue ? (decimal)price.Value : null),
            CancellationToken.None);

        Assert.Equal(OrderSubmissionResultKind.BadRequest, result.Kind);
        Assert.Equal(0, gateway.SubmitCalls);
        Assert.Empty(book.Snapshot());
    }

    private static ModifyHarness BuildModify(
        TestGateway gateway,
        IEventStore? store = null)
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(Working()));
        ownership.Register(100, Owner);
        var replacements = new PendingReplacementRegistry();
        var service = new OrderModifyService(
            new ClOrdIdPrefixRegistry(), ownership, book, gateway, new RecordingSink(),
            new RiskPipeline(Array.Empty<IRiskCheck>()), new NoOpReplaceMargin(),
            replacements, new EventDispatcher(store ?? new NullEventStore()),
            new NeverDrainController(), NullLogger<OrderModifyService>.Instance);
        return new ModifyHarness(service, replacements);
    }

    private static OrderCancelService BuildCancel(
        ClOrdIdPrefixRegistry clOrdIds,
        TestGateway gateway,
        PendingCancelRegistry pending)
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(Working()));
        ownership.Register(100, Owner);
        return new OrderCancelService(
            clOrdIds, ownership, book, gateway, new EventDispatcher(new NullEventStore()),
            NullLogger<OrderCancelService>.Instance, pendingCancels: pending);
    }

    private static RecoveryHarness BuildRecoveryState(PendingCancelRegistry? pendingCancels = null)
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(Working()));
        ownership.Register(100, Owner);
        var replacements = new PendingReplacementRegistry();
        var margin = new NoOpReplaceMargin();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var processor = new ExecutionReportProcessor(
            ownership, book, new PositionKeeper(), new RecordingSink(),
            new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance,
            replacements: replacements, replaceMargin: margin,
            pendingCancels: pendingCancels);
        var replayer = new EventReplayer(
            book, ownership, new KillSwitchService(), new SymbolHaltService(),
            new SessionPhaseService(), processor, new AlgoBook(), clOrdIds,
            new AlgoIdRegistry(), replacements: replacements,
            replaceMargin: margin, pendingCancels: pendingCancels);
        return new RecoveryHarness(
            replayer, replacements, clOrdIds, ownership, book);
    }

    private static Order Working()
    {
        var order = new Order(
            100, Owner, "PETR4", 4321, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM");
        order.MarkWorking();
        return order;
    }

    private sealed record ModifyHarness(
        OrderModifyService Service,
        PendingReplacementRegistry Replacements);

    private sealed record RecoveryHarness(
        EventReplayer Replayer,
        PendingReplacementRegistry Replacements,
        ClOrdIdPrefixRegistry ClOrdIds,
        OrderOwnershipMap Ownership,
        WorkingOrderBook Book);

    private sealed class TestGateway : IExchangeGateway
    {
        public int SubmitCalls;
        public int CancelCalls;
        public int ReplaceCalls;
        public bool BlockReplace;
        public Exception? ReplaceException;
        public TaskCompletionSource ReplaceEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReplace { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SubmitAsync(Order order, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref SubmitCalls);
            return Task.CompletedTask;
        }

        public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CancelCalls);
            return Task.CompletedTask;
        }

        public async Task CancelReplaceAsync(
            Order original,
            ulong newClOrdId,
            long newQuantity,
            decimal? newPrice,
            TimeInForce? requestedTimeInForce,
            decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ReplaceCalls);
            ReplaceEntered.TrySetResult();
            if (BlockReplace)
                await ReleaseReplace.Task.WaitAsync(cancellationToken);
            if (ReplaceException is not null)
                throw ReplaceException;
        }
    }

    private sealed class NoOpReplaceMargin : IReplaceMarginCoordinator
    {
        public Task<RiskDecision> PrepareReplaceAsync(
            ulong originalClOrdId,
            ulong newClOrdId,
            EndClientId owner,
            decimal newRemainingNotional,
            CancellationToken ct) => Task.FromResult(RiskDecision.Approve);

        public void CommitReplace(ulong originalClOrdId, ulong newClOrdId, decimal newRemainingNotional) { }
        public void AbortReplace(ulong newClOrdId) { }
    }

    private sealed class RecordingSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent ev) { }
    }

    private sealed class NeverDrainController : Lifecycle.IDrainController
    {
        public bool IsDraining => false;
        public void BeginDrain(string reason) { }
    }

    private sealed class RecordingEventStore : IEventStore
    {
        private long _seq;
        public List<WalEvent> Events { get; } = new();
        public long CurrentSeq => Interlocked.Read(ref _seq);

        public long Append(WalEvent evt) => Append(evt, ReadOnlyMemory<byte>.Empty);

        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            lock (Events)
                Events.Add(evt);
            return Interlocked.Increment(ref _seq);
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            WalEvent[] snapshot;
            lock (Events)
                snapshot = Events.ToArray();
            for (var i = (int)sinceSeqExclusive; i < snapshot.Length; i++)
                yield return (i + 1, snapshot[i]);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
