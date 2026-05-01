using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

public class ExecutionReportProcessorTests
{
    private static (ExecutionReportProcessor Proc, OrderOwnershipMap Own, WorkingOrderBook Book, PositionKeeper Pos, RecordingSink Sink) Build()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new RecordingSink();
        var proc = new ExecutionReportProcessor(ownership, book, positions, sink, NullLogger<ExecutionReportProcessor>.Instance);
        return (proc, ownership, book, positions, sink);
    }

    [Fact]
    public void New_TransitionsOrderToWorking_AndPublishes()
    {
        var (proc, ownership, book, _, sink) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        proc.Apply(1UL, ExecKind.New, leaves: 100, cumQty: 0, lastQty: 0, lastPx: 0m, rejectReason: null);

        Assert.Equal(OrderStatus.Working, order.Status);
        Assert.Single(sink.Events);
        Assert.Equal(owner, sink.Events[0].Owner);
    }

    [Fact]
    public void Fill_AppliesToOrderAndPosition()
    {
        var (proc, ownership, book, positions, sink) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        proc.Apply(1UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 30m, rejectReason: null);

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(100, positions.GetOrCreate(owner, "PETR4").NetQuantity);
        Assert.Single(sink.Events);
    }

    [Fact]
    public void UnknownClOrdId_IsDroppedSilently()
    {
        var (proc, _, _, _, sink) = Build();
        proc.Apply(99999UL, ExecKind.New, 0, 0, 0, 0m, null);
        Assert.Empty(sink.Events);
    }

    private sealed class RecordingSink : IExecutionEventSink
    {
        public readonly List<ExecutionEvent> Events = new();
        public void Publish(ExecutionEvent ev) => Events.Add(ev);
    }
}
