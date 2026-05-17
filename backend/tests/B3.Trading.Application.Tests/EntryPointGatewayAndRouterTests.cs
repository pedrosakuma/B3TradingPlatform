using B3.Trading.Application.Risk;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

public class EntryPointGatewayAndRouterTests
{
    [Fact]
    public async Task Gateway_Submit_ForwardsToClient_WithCorrectFirmAndFields()
    {
        var client = new MockEntryPointClient();
        var gateway = new EntryPointClientGateway(client, "FIRM-A");
        var order = new Order(42UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Sell, OrderType.Limit, 50, 31.25m);

        await gateway.SubmitAsync(order, CancellationToken.None);

        var sent = Assert.Single(client.SubmittedNewOrders);
        Assert.Equal(42UL, sent.ClOrdId);
        Assert.Equal(4321UL, sent.SecurityId);
        Assert.Equal("FIRM-A", sent.FirmId);
        Assert.Equal(EpSide.Sell, sent.Side);
        Assert.Equal(EpOrderType.Limit, sent.Type);
        Assert.Equal(50, sent.Quantity);
        Assert.Equal(31.25m, sent.Price);
        // Q3.4 (#284). Plain (no-reserve) orders must not surface a
        // MaxFloor on the wire — a non-null value would cause the
        // venue to expose only a slice of the order.
        Assert.Null(sent.MaxFloor);
    }

    [Fact]
    public async Task Gateway_Submit_Iceberg_ForwardsDisplayQtyAsMaxFloor()
    {
        // Q3.4 (#284) pass-1 (#297). Pin DisplayQty → MaxFloor wire
        // mapping through the IEntryPointClient seam (the real SDK
        // path is pinned by B3EntryPointClientGatewayMapTests).
        var client = new MockEntryPointClient();
        var gateway = new EntryPointClientGateway(client, "FIRM-A");
        var order = new Order(42UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A", displayQty: 10, displayResetPolicy: DisplayResetPolicy.Always);

        await gateway.SubmitAsync(order, CancellationToken.None);

        var sent = Assert.Single(client.SubmittedNewOrders);
        Assert.Equal(10L, sent.MaxFloor);
        Assert.Equal(100, sent.Quantity);
    }

    [Fact]
    public async Task Gateway_CancelReplace_ForwardsToClient()
    {
        var client = new MockEntryPointClient();
        var gateway = new EntryPointClientGateway(client, "FIRM-A");
        var original = new Order(100UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);

        await gateway.CancelReplaceAsync(original, 101UL, 200, 30m, null, null, null, CancellationToken.None);

        var sent = Assert.Single(client.SubmittedReplaces);
        Assert.Equal(100UL, sent.OriginalClOrdId);
        Assert.Equal(101UL, sent.NewClOrdId);
        Assert.Equal(4321UL, sent.SecurityId);
        Assert.Equal(EpSide.Buy, sent.Side);
        Assert.Equal(200, sent.NewQuantity);
        // Q3.4 (#284). Plain (no-reserve) replace must not surface MaxFloor.
        Assert.Null(sent.MaxFloor);
    }

    [Fact]
    public async Task Gateway_CancelReplace_Iceberg_InheritsAndClampsMaxFloor()
    {
        // Q3.4 (#284) pass-1 (#297). Replace inherits the original's
        // visible portion (MaxFloor). When the new order qty shrinks
        // below the original DisplayQty, MaxFloor must clamp to the
        // new qty so the venue invariant (MaxFloor <= OrderQty) holds.
        var client = new MockEntryPointClient();
        var gateway = new EntryPointClientGateway(client, "FIRM-A");
        var original = new Order(100UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A", displayQty: 50, displayResetPolicy: DisplayResetPolicy.Always);

        // 1) Replace grows the qty — MaxFloor stays at the original 50.
        await gateway.CancelReplaceAsync(original, 101UL, 200, 30m, null, null, null, CancellationToken.None);
        var grown = client.SubmittedReplaces.Last();
        Assert.Equal(50L, grown.MaxFloor);

        // 2) Replace shrinks below DisplayQty — MaxFloor clamps to newQty.
        await gateway.CancelReplaceAsync(original, 102UL, 20, 30m, null, null, null, CancellationToken.None);
        var shrunk = client.SubmittedReplaces.Last();
        Assert.Equal(20L, shrunk.MaxFloor);
    }

    [Fact]
    public void Router_DeliversERToProcessor()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        var sink = new TestSink();
        var proc = new ExecutionReportProcessor(ownership, book, positions, sink, new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);
        var client = new MockEntryPointClient();
        // RFC §5.2 (F2). Wire the test sink as a fan-out sink so the
        // dispatcher's per-sink-channel fan-out (under the lock) routes
        // captured ERs into it. The legacy synchronous sink.Publish
        // path is no longer invoked from inside Apply when an
        // ExecutionFanOut writer is supplied.
        var dispatcher = new EventDispatcher(new NullEventStore(), new[] { (IExecutionFanOutSink)sink });
        using var router = new EntryPointExecutionReportRouter(client, proc, dispatcher);

        client.EmitExecutionReport(new ExecutionReportEnvelope(1UL, EpExecType.Fill, 0, 100, 100, 30m, null));

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Single(sink.Events);
    }

    private sealed class TestSink : IExecutionEventSink, IExecutionFanOutSink
    {
        public readonly List<ExecutionEvent> Events = new();
        public ExecutionFanOutTargets Target => ExecutionFanOutTargets.All;
        public void Publish(ExecutionEvent ev) { lock (Events) Events.Add(ev); }
        public void Enqueue(long seq, ExecutionEvent ev) { lock (Events) Events.Add(ev); }
    }
}
