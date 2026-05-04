using B3.Trading.Application;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace B3.Trading.Application.Tests.AlgoEngine;

public class ExecutionReportProcessorAlgoSignalTests
{
    [Fact]
    public void ChildOrder_FillEr_EnqueuesChildExecutionObservedSignal()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new RecordingSink();
        var queue = new AlgoSignalQueue();
        var proc = new ExecutionReportProcessor(
            ownership, book, positions, sink,
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            queue);

        var owner = new EndClientId("alice");
        var child = new Order(
            42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m,
            firmId: "default", parentAlgoId: 7UL, algoSliceSeq: 1);
        book.TryAdd(child);
        ownership.Register(42UL, owner);

        proc.Apply(42UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 30m, rejectReason: null);

        // Drain the queue and verify the engine got a signal that points
        // back at the parent algo + the specific child that filled.
        queue.Complete();
        var seen = new List<AlgoSignal>();
        foreach (var s in DrainSync(queue)) seen.Add(s);
        var observed = Assert.Single(seen);
        var child_er = Assert.IsType<ChildExecutionObservedSignal>(observed);
        Assert.Equal(7UL, child_er.AlgoId);
        Assert.Equal(42UL, child_er.ChildClOrdId);
        Assert.Equal("default", child_er.FirmId);
    }

    [Fact]
    public void ManualOrder_FillEr_DoesNotEnqueueAnySignal()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new RecordingSink();
        var queue = new AlgoSignalQueue();
        var proc = new ExecutionReportProcessor(
            ownership, book, positions, sink,
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            queue);

        var owner = new EndClientId("alice");
        var manual = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(manual);
        ownership.Register(1UL, owner);

        proc.Apply(1UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 30m, rejectReason: null);

        queue.Complete();
        Assert.Empty(DrainSync(queue));
    }

    [Fact]
    public void NullSignalQueue_IsTolerated_PreservesLegacyConstructorSemantics()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new RecordingSink();
        // Older callers (and most existing unit tests) construct the
        // processor without an algo queue; this overload must keep working.
        var proc = new ExecutionReportProcessor(
            ownership, book, positions, sink,
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);

        var owner = new EndClientId("alice");
        var child = new Order(
            42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m,
            firmId: "default", parentAlgoId: 7UL, algoSliceSeq: 1);
        book.TryAdd(child);
        ownership.Register(42UL, owner);

        // Should not throw despite the parent linkage and missing queue.
        proc.Apply(42UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 30m, rejectReason: null);
        Assert.Equal(OrderStatus.Filled, child.Status);
    }

    private static IEnumerable<AlgoSignal> DrainSync(AlgoSignalQueue q)
    {
        while (q.Reader.TryRead(out var s)) yield return s;
    }

    private sealed class RecordingSink : IExecutionEventSink
    {
        public readonly List<ExecutionEvent> Events = new();
        public void Publish(ExecutionEvent ev) => Events.Add(ev);
    }
}
