using B3.Trading.Application;
using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Orders;

/// <summary>
/// Q3.4 (#284). End-to-end behaviour for the native iceberg /
/// reserve display-qty path: the submit pipeline must forward
/// <c>DisplayQty</c> + <c>DisplayResetPolicy</c> to the gateway
/// boundary intact, fills must accumulate as on a plain order (the
/// venue handles the display refresh server-side — there is no
/// client-side slicing the way <c>IcebergAlgo</c> does), and
/// validation must reject the invalid (qty, displayQty) pairs
/// called out in the issue before the WAL append.
/// </summary>
public class IcebergDisplayQtySubmitTests
{
    private static readonly EndClientId Alice = new("alice");

    [Fact]
    public async Task SubmitIceberg_ForwardsMaxFloorAndPolicy_AndFillsAccumulate()
    {
        var h = new Harness();

        var req = new OrderSubmissionRequest(
            Alice, "FIRM-A", "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            Quantity: 100, Price: 30m,
            DisplayQty: 10,
            DisplayResetPolicy: DisplayResetPolicy.Always);

        var result = await h.Submitter.SubmitAsync(req, CancellationToken.None);

        Assert.Equal(OrderSubmissionResultKind.Accepted, result.Kind);
        var submitted = Assert.Single(h.Gateway.SubmittedOrders);
        // The gateway boundary is what the SDK adapter reads from when
        // building NewOrderRequest.MaxFloor — pinning the domain Order
        // there is the strongest assertion the application layer can
        // make without spinning the real EntryPoint client.
        Assert.Equal(10L, submitted.DisplayQty);
        Assert.Equal(DisplayResetPolicy.Always, submitted.DisplayResetPolicy);

        // Drive a sequence of ERs simulating the venue draining the
        // hidden reserve in 10-lot slices. Because the venue handles
        // the display refresh server-side, the platform sees plain
        // cumulative-quantity advances; LeavesQuantity / Status must
        // still reach the terminal Filled state at 100.
        var clOrdId = result.ClOrdId;
        long[] cumulativeSteps = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        long expectedLastQty = 10;
        foreach (var cum in cumulativeSteps)
        {
            var kind = cum == 100 ? ExecKind.Fill : ExecKind.PartialFill;
            h.Processor.Apply(clOrdId, kind, leaves: 100 - cum, cumQty: cum, lastQty: expectedLastQty, lastPx: 30m, rejectReason: null);
        }

        Assert.True(h.Book.TryGet(clOrdId, out var order));
        Assert.NotNull(order);
        Assert.Equal(OrderStatus.Filled, order!.Status);
        Assert.Equal(0, order.LeavesQuantity);
        Assert.Equal(100, order.CumulativeQuantity);
        // The display fields are immutable post-submit: they describe
        // the venue contract, not in-flight state.
        Assert.Equal(10L, order.DisplayQty);
    }

    [Fact]
    public async Task SubmitIceberg_NeverPolicy_StillRoundTripsToGateway()
    {
        // SDK-limitation note (see TODO in B3EntryPointClientGateway):
        // the SDK does not currently plumb the refresh policy, so on
        // the wire `Never` collapses to the venue default (Always).
        // What the platform CAN guarantee end-to-end is that the
        // trader's chosen policy is preserved on the domain Order,
        // recorded on the WAL (Application.Tests.Persistence
        // .OrderDisplayQtyBackCompatTests), and visible to operators
        // via the REST/UI surface.
        var h = new Harness();
        var req = new OrderSubmissionRequest(
            Alice, "FIRM-A", "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            Quantity: 50, Price: 30m,
            DisplayQty: 5,
            DisplayResetPolicy: DisplayResetPolicy.Never);

        var result = await h.Submitter.SubmitAsync(req, CancellationToken.None);

        Assert.Equal(OrderSubmissionResultKind.Accepted, result.Kind);
        var submitted = Assert.Single(h.Gateway.SubmittedOrders);
        Assert.Equal(DisplayResetPolicy.Never, submitted.DisplayResetPolicy);
    }

    [Theory]
    [InlineData(0, "DisplayQty must be positive")]
    [InlineData(-5, "DisplayQty must be positive")]
    [InlineData(101, "must not exceed order Quantity")]
    public async Task SubmitIceberg_InvalidDisplayQty_Rejected(long badDisplayQty, string expectedFragment)
    {
        var h = new Harness();
        var req = new OrderSubmissionRequest(
            Alice, "FIRM-A", "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            Quantity: 100, Price: 30m,
            DisplayQty: badDisplayQty,
            DisplayResetPolicy: DisplayResetPolicy.Always);

        var result = await h.Submitter.SubmitAsync(req, CancellationToken.None);

        Assert.Equal(OrderSubmissionResultKind.BadRequest, result.Kind);
        Assert.NotNull(result.Reason);
        Assert.Contains(expectedFragment, result.Reason);
        Assert.Empty(h.Gateway.SubmittedOrders);
    }

    [Fact]
    public async Task SubmitIceberg_Snapshot_PreservesDisplayFieldsAcrossRestart()
    {
        // Snapshot capture + restore round-trip with an in-flight
        // iceberg order. After hydration, the rebuilt order must
        // carry the same DisplayQty / policy so the operator sees
        // the same iceberg state and a future cancel-replace can
        // inherit the visible-portion semantics correctly.
        var h = new Harness();
        var req = new OrderSubmissionRequest(
            Alice, "FIRM-A", "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            Quantity: 100, Price: 30m,
            DisplayQty: 10,
            DisplayResetPolicy: DisplayResetPolicy.OnPartialFill);
        var submitResult = await h.Submitter.SubmitAsync(req, CancellationToken.None);

        // Advance fills 30/100 so the snapshot captures non-trivial
        // leaves/cum alongside the display fields.
        h.Processor.Apply(submitResult.ClOrdId, ExecKind.PartialFill, leaves: 70, cumQty: 30, lastQty: 30, lastPx: 30m, rejectReason: null);

        var captured = h.Book.Snapshot().ToList();

        var fresh = new WorkingOrderBook();
        fresh.Restore(captured);

        Assert.True(fresh.TryGet(submitResult.ClOrdId, out var restored));
        Assert.NotNull(restored);
        Assert.Equal(10L, restored!.DisplayQty);
        Assert.Equal(DisplayResetPolicy.OnPartialFill, restored.DisplayResetPolicy);
        Assert.Equal(70, restored.LeavesQuantity);
        Assert.Equal(30, restored.CumulativeQuantity);
        Assert.Equal(OrderStatus.PartiallyFilled, restored.Status);
    }

    private sealed class Harness
    {
        public WorkingOrderBook Book { get; } = new();
        public OrderOwnershipMap Ownership { get; } = new();
        public ClOrdIdPrefixRegistry ClOrdIds { get; } = new();
        public NullEventStore Store { get; } = new();
        public EventDispatcher Dispatcher { get; }
        public RecordingGateway Gateway { get; } = new();
        public PositionKeeper Positions { get; } = new();
        public NoOpExecutionEventSink Sink { get; } = new();
        public RiskPipeline Risk { get; } = new(Array.Empty<IRiskCheck>());
        public NoOpMarginProvider Margin { get; } = new();
        public CompositeRiskAccountant Accountant { get; } = new(Array.Empty<IRiskAccountant>());
        public NeverDrainingGate Drain { get; } = new();
        public OrderSubmissionService Submitter { get; }
        public ExecutionReportProcessor Processor { get; }

        public Harness()
        {
            Dispatcher = new EventDispatcher(Store);
            Submitter = new OrderSubmissionService(
                ClOrdIds, Ownership, Book, Gateway, Sink, Risk, Margin, Accountant,
                Dispatcher, Drain, NullLogger<OrderSubmissionService>.Instance);
            Processor = new ExecutionReportProcessor(
                Ownership, Book, Positions, Sink, Margin,
                NullLogger<ExecutionReportProcessor>.Instance);
        }
    }

    private sealed class RecordingGateway : IExchangeGateway
    {
        public List<Order> SubmittedOrders { get; } = new();
        public Task SubmitAsync(Order order, CancellationToken ct)
        {
            SubmittedOrders.Add(order);
            return Task.CompletedTask;
        }
        public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken ct) => Task.CompletedTask;
        public Task CancelReplaceAsync(
            Order original, ulong newClOrdId, long newQuantity, decimal? newPrice,
            TimeInForce? requestedTimeInForce, decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NeverDrainingGate : IDrainGate
    {
        public bool IsDraining => false;
    }

    private sealed class NoOpExecutionEventSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent ev) { }
    }
}
