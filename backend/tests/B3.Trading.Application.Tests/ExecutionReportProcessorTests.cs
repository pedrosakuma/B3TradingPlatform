using B3.Trading.Application.Risk;
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
        var proc = new ExecutionReportProcessor(ownership, book, positions, sink, new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);
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

    // -------- Idempotency / replay safety (Phase 3/1a) --------

    [Fact]
    public void Fill_DuplicateEr_BooksPositionOnce()
    {
        var (proc, ownership, book, positions, sink) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        proc.Apply(1UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 30m, rejectReason: null);
        // Same ER replayed (FIXP retransmit after reconnect / WAL cold-start replay).
        proc.Apply(1UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 30m, rejectReason: null);

        Assert.Equal(100, positions.GetOrCreate(owner, "PETR4").NetQuantity);
        Assert.Equal(100, order.CumulativeQuantity);
        Assert.Equal(OrderStatus.Filled, order.Status);
        // Duplicate ER short-circuits before publish — downstream subscribers
        // (UI / event sink) don't re-emit a phantom fill on FIXP retransmit.
        Assert.Single(sink.Events);
    }

    [Fact]
    public void Fill_OutOfOrderArrival_AppliesCumulativeDelta()
    {
        var (proc, ownership, book, positions, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 200, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        // ER B arrives first: cumQty jumps to 150 (it implicitly subsumes the
        // missed ER A at cumQty=50). Position must book the full 150 at 30m.
        proc.Apply(1UL, ExecKind.PartialFill, leaves: 50, cumQty: 150, lastQty: 100, lastPx: 30m, rejectReason: null);
        // ER A arrives late: cumQty=50 is now stale and must be dropped, NOT
        // double-applied (the dangerous regression flagged by rubber-duck).
        proc.Apply(1UL, ExecKind.PartialFill, leaves: 150, cumQty: 50, lastQty: 50, lastPx: 30m, rejectReason: null);

        Assert.Equal(150, order.CumulativeQuantity);
        Assert.Equal(50, order.LeavesQuantity);
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
        Assert.Equal(150, positions.GetOrCreate(owner, "PETR4").NetQuantity);
    }

    [Fact]
    public void Fill_AfterCancelled_BooksPositionAndKeepsTerminalStatus()
    {
        var (proc, ownership, book, positions, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        // Cancel ack arrives first.
        proc.Apply(1UL, ExecKind.Canceled, leaves: 0, cumQty: 0, lastQty: 0, lastPx: 0m, rejectReason: null);
        Assert.Equal(OrderStatus.Cancelled, order.Status);

        // Late fill that actually happened pre-cancel arrives now. Exchange's
        // cumulative-quantity is the source of truth — must NOT throw and
        // must book the position; status stays Cancelled (no regression).
        proc.Apply(1UL, ExecKind.PartialFill, leaves: 60, cumQty: 40, lastQty: 40, lastPx: 30m, rejectReason: null);

        Assert.Equal(40, order.CumulativeQuantity);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(40, positions.GetOrCreate(owner, "PETR4").NetQuantity);
    }

    [Fact]
    public void Cancel_AfterFilled_DoesNotRegressStatus()
    {
        var (proc, ownership, book, _, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        proc.Apply(1UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 30m, rejectReason: null);
        Assert.Equal(OrderStatus.Filled, order.Status);

        // A stale cancel arrives after the final fill. Must NOT regress to Cancelled.
        proc.Apply(1UL, ExecKind.Canceled, leaves: 0, cumQty: 0, lastQty: 0, lastPx: 0m, rejectReason: null);

        Assert.Equal(OrderStatus.Filled, order.Status);
    }

    [Fact]
    public void Reject_AfterPartialFill_DoesNotRegressStatus()
    {
        var (proc, ownership, book, _, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        proc.Apply(1UL, ExecKind.PartialFill, leaves: 60, cumQty: 40, lastQty: 40, lastPx: 30m, rejectReason: null);
        proc.Apply(1UL, ExecKind.Rejected, leaves: 0, cumQty: 0, lastQty: 0, lastPx: 0m, rejectReason: "stale");

        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
    }

    [Fact]
    public void New_Replayed_DoesNotRegressFromWorking()
    {
        var (proc, ownership, book, _, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        proc.Apply(1UL, ExecKind.New, 100, 0, 0, 0m, null);
        proc.Apply(1UL, ExecKind.PartialFill, 60, 40, 40, 30m, null);
        // New ER replayed after reconnect — must not undo the partial fill state.
        proc.Apply(1UL, ExecKind.New, 100, 0, 0, 0m, null);

        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
        Assert.Equal(40, order.CumulativeQuantity);
    }

    [Fact]
    public void Cancel_WithMissingOrigClOrdId_ResolvesViaCancelLink()
    {
        // Upstream gateway sometimes drops OrigClOrdID on cancel acks
        // (issue #99). When it does, the processor must still resolve
        // back to the original order via the cancel-side → original
        // link recorded at cancel-request time.
        var (proc, ownership, book, _, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);
        order.MarkWorking();

        const ulong cancelClOrdId = 2UL;
        ownership.RegisterCancelLink(cancelClOrdId, order.ClOrdId);

        // Wire ER comes back addressing the cancel-side ID with no OrigClOrdID.
        proc.Apply(cancelClOrdId, ExecKind.Canceled, leaves: 0, cumQty: 0,
                   lastQty: 0, lastPx: 0m, rejectReason: null, origClOrdId: 0);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public void Cancel_PrefersExplicitOrigClOrdIdOverFallback()
    {
        var (proc, ownership, book, _, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);
        order.MarkWorking();

        const ulong cancelClOrdId = 2UL;
        ownership.RegisterCancelLink(cancelClOrdId, order.ClOrdId);

        proc.Apply(cancelClOrdId, ExecKind.Canceled, leaves: 0, cumQty: 0,
                   lastQty: 0, lastPx: 0m, rejectReason: null, origClOrdId: 1UL);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public void Overfill_DoesNotThrow_AndCapsLeavesAtZero()
    {
        // Overfill (cumQty > order.Quantity) is a wire-side bug, but Apply must
        // never throw on a replay-realistic input — that would poison WAL
        // recovery. Position books the reported delta; leaves clamps at 0.
        var (proc, ownership, book, positions, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        proc.Apply(1UL, ExecKind.Fill, leaves: 0, cumQty: 150, lastQty: 150, lastPx: 30m, rejectReason: null);

        Assert.Equal(150, order.CumulativeQuantity);
        Assert.Equal(0, order.LeavesQuantity);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(150, positions.GetOrCreate(owner, "PETR4").NetQuantity);
    }

    private sealed class RecordingSink : IExecutionEventSink
    {
        public readonly List<ExecutionEvent> Events = new();
        public void Publish(ExecutionEvent ev) => Events.Add(ev);
    }
}
