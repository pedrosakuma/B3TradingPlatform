using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

public class OrderSubmissionFailClosedTests
{
    [Fact]
    public async Task SyntheticGatewayRejection_WalSaturation_DoesNotReportOrApplyTerminalState()
    {
        var store = new SaturatingAfterFirstAppendStore();
        var dispatcher = new EventDispatcher(store);
        var book = new WorkingOrderBook();
        var sink = new RecordingSink();
        var drain = new TestDrainController();
        var submitter = new OrderSubmissionService(
            new ClOrdIdPrefixRegistry(),
            new OrderOwnershipMap(),
            book,
            new ThrowingGateway(),
            sink,
            new RiskPipeline(Array.Empty<IRiskCheck>()),
            new NoOpMarginProvider(),
            new CompositeRiskAccountant(Array.Empty<IRiskAccountant>()),
            dispatcher,
            drain,
            NullLogger<OrderSubmissionService>.Instance);

        var result = await submitter.SubmitAsync(
            new OrderSubmissionRequest(
                new EndClientId("alice"), "FIRM-A", "PETR4", 4321UL,
                OrderSide.Buy, OrderType.Limit, 100, 30m),
            CancellationToken.None);

        Assert.Equal(OrderSubmissionResultKind.ReconciliationRequired, result.Kind);
        Assert.Equal("wal_reconciliation_required", result.Code);
        Assert.NotEqual(0UL, result.ClOrdId);
        Assert.True(book.TryGet(result.ClOrdId, out var order));
        Assert.NotNull(order);
        Assert.Equal(OrderStatus.PendingNew, order!.Status);
        Assert.Empty(sink.Events);
        Assert.IsType<OrderSubmittedEvent>(Assert.Single(store.Appended));
        Assert.True(drain.IsDraining);
        Assert.Equal("wal_synthetic_terminal_reconciliation_required", drain.Reason);
    }

    private sealed class SaturatingAfterFirstAppendStore : IEventStore
    {
        private long _seq;
        public List<WalEvent> Appended { get; } = new();
        public long CurrentSeq => _seq;

        public long Append(WalEvent evt) => Append(evt, ReadOnlyMemory<byte>.Empty);

        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            if (Appended.Count > 0)
                throw new WalBackpressureException("forced saturation");
            Appended.Add(evt);
            return ++_seq;
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingGateway : IExchangeGateway
    {
        public Task SubmitAsync(Order order, CancellationToken ct) =>
            Task.FromException(new InvalidOperationException("venue unavailable"));
        public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken ct) =>
            Task.CompletedTask;
        public Task CancelReplaceAsync(
            Order original,
            ulong newClOrdId,
            long newQuantity,
            decimal? newPrice,
            TimeInForce? requestedTimeInForce,
            decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingSink : IExecutionEventSink
    {
        public List<ExecutionEvent> Events { get; } = new();
        public void Publish(ExecutionEvent ev) => Events.Add(ev);
    }

    private sealed class TestDrainController : IDrainController
    {
        public bool IsDraining { get; private set; }
        public string? Reason { get; private set; }
        public void BeginDrain(string reason)
        {
            IsDraining = true;
            Reason = reason;
        }
    }
}
