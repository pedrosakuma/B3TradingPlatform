using B3.Trading.Application.Risk;
using B3.Trading.Application;
using B3.Trading.Application.UserBots;
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

    [Fact]
    public void Cancel_WithNativeStpReason_MarksEventAsNativeStp()
    {
        var (proc, ownership, book, _, sink) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);
        order.MarkWorking();

        proc.Apply(1UL, ExecKind.Canceled, leaves: 0, cumQty: 0, lastQty: 0, lastPx: 0m,
                   rejectReason: "CancelRestingOrderOnSelfTrade");

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        var ev = Assert.Single(sink.Events);
        Assert.True(ev.IsNativeStp);
        Assert.Equal("CancelRestingOrderOnSelfTrade", ev.RejectReason);
    }

    [Fact]
    public void Cancel_WithGenericReason_DoesNotMarkAsNativeStp()
    {
        var (proc, ownership, book, _, sink) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(2UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(2UL, owner);
        order.MarkWorking();

        proc.Apply(2UL, ExecKind.Canceled, leaves: 0, cumQty: 0, lastQty: 0, lastPx: 0m,
                   rejectReason: "RiskManagementCancellation");

        var ev = Assert.Single(sink.Events);
        Assert.False(ev.IsNativeStp);
    }

    [Fact]
    public void Fill_WithStpLikeReason_DoesNotMarkAsNativeStp()
    {
        // Defensive: native-STP marker is gated on Kind == Canceled.
        // A Fill ER with a freaky reason text must not be miscategorised.
        var (proc, ownership, book, _, sink) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(3UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(3UL, owner);

        proc.Apply(3UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 30m,
                   rejectReason: "SelfTradingPrevention");

        var ev = Assert.Single(sink.Events);
        Assert.False(ev.IsNativeStp);
    }

    [Fact]
    public void TerminalFill_OnStaleOrder_AutoClearsStale()
    {
        var (proc, ownership, book, _, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);
        order.MarkWorking();
        order.MarkStale("matching restart", DateTimeOffset.UtcNow);
        Assert.True(order.IsStale);

        proc.Apply(1UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 30m, rejectReason: null);

        // Real fill arrived → venue still knew the order → false-positive
        // stale flag is auto-lifted (slice 1 of #132).
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.False(order.IsStale);
        Assert.Null(order.StaleReason);
    }

    [Fact]
    public void TerminalCancel_OnStaleOrder_AutoClearsStale()
    {
        var (proc, ownership, book, _, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);
        order.MarkWorking();
        order.MarkStale("matching restart", DateTimeOffset.UtcNow);

        proc.Apply(1UL, ExecKind.Canceled, leaves: 100, cumQty: 0, lastQty: 0, lastPx: 0m, rejectReason: null);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.False(order.IsStale);
    }

    [Fact]
    public void PartialFill_OnStaleOrder_DoesNotClearStale()
    {
        var (proc, ownership, book, _, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);
        order.MarkWorking();
        order.MarkStale("gap", DateTimeOffset.UtcNow);

        proc.Apply(1UL, ExecKind.PartialFill, leaves: 60, cumQty: 40, lastQty: 40, lastPx: 30m, rejectReason: null);

        // Partial fill: venue knew about the original child but the
        // remainder may still be ghosted; trader's stale concern stands.
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
        Assert.True(order.IsStale);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TerminalFill_ConsumesOnlyDurablyClassifiedPeggedRepegIntent(
        bool isPeggedRepeg,
        bool expectedInFlight)
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var replacements = new PendingReplacementRegistry();
        var proc = new ExecutionReportProcessor(
            ownership,
            book,
            new PositionKeeper(),
            new RecordingSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            replacements: replacements);

        var owner = new EndClientId("alice");
        var order = new Order(
            1UL,
            owner,
            "PETR4",
            4321UL,
            OrderSide.Buy,
            OrderType.Limit,
            100,
            30m,
            firmId: "FIRM01",
            parentAlgoId: 42UL,
            algoSliceSeq: 1);
        book.TryAdd(order);
        ownership.Register(order.ClOrdId, owner);
        order.MarkWorking();

        Assert.True(replacements.TryAdd(new OrderReplacementIntent(
            OriginalClOrdId: order.ClOrdId,
            NewClOrdId: 2UL,
            Owner: owner,
            Symbol: order.Symbol,
            SecurityId: order.SecurityId,
            Side: order.Side,
            Type: order.Type,
            NewQuantity: order.Quantity,
            NewPrice: 30.1m,
            FirmId: order.FirmId,
            ParentAlgoId: order.ParentAlgoId,
            AlgoSliceSeq: order.AlgoSliceSeq,
            IsPeggedRepeg: isPeggedRepeg)));

        proc.Apply(
            order.ClOrdId,
            ExecKind.Fill,
            leaves: 0,
            cumQty: 100,
            lastQty: 100,
            lastPx: 30m,
            rejectReason: null);

        Assert.Equal(expectedInFlight, replacements.IsOriginalInFlight(order.ClOrdId));
    }

    [Fact]
    public void ApplyReplaceRejected_EmitsUiVisibleEvent_ScopedToOriginalClOrdId()
    {
        // #381 regression: a venue replace-reject (PUT /api/orders/{orig} ->
        // venue rejected the new ClOrdID) used to publish ONLY the synthetic
        // Rejected event tagged BotRouter-only (per #172 F), so the WS hub
        // never observed the rejection. The trader who clicked Modify saw
        // nothing — Modify button silently re-enabled, no row update, no
        // toast. Now ApplyReplaceRejected MUST also publish a second event
        // scoped to intent.OriginalClOrdId with ExecKind.ReplaceRejected so
        // the operator sees the modify failed.
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new RecordingSink();
        var botRouter = new RecordingBotErRouter();
        var replacements = new PendingReplacementRegistry();
        var proc = new ExecutionReportProcessor(
            ownership, book, positions, sink, new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            replacements: replacements,
            botErRouter: botRouter);

        var owner = new EndClientId("alice");
        var orig = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Sell, OrderType.Limit, 100, 32.50m);
        book.TryAdd(orig);
        ownership.Register(1UL, owner);
        orig.MarkWorking();

        // Operator clicks Modify: a fresh replace-side ClOrdID (3) is
        // registered as in-flight against orig=1. The new request reaches
        // the venue which rejects it (reject_code=5 / unknown order).
        var intent = new OrderReplacementIntent(
            OriginalClOrdId: 1UL, NewClOrdId: 3UL,
            Owner: owner, Symbol: "PETR4", SecurityId: 4321UL,
            Side: OrderSide.Sell, Type: OrderType.Limit,
            NewQuantity: 100, NewPrice: 32.55m,
            FirmId: "FIRM01", ParentAlgoId: null, AlgoSliceSeq: null);
        Assert.True(replacements.TryAdd(intent));

        proc.Apply(3UL, ExecKind.Rejected, leaves: 0, cumQty: 0, lastQty: 0, lastPx: 0m, rejectReason: "reject_code=5");

        // Bot side: replace-side ClOrdID receives the synthetic Rejected
        // (preserves #172 F — bots own the ClOrdIDs they issue).
        Assert.Single(botRouter.Events);
        var botEv = botRouter.Events[0];
        Assert.Equal(3UL, botEv.ClOrdId);
        Assert.Equal(ExecKind.Rejected, botEv.Kind);
        Assert.Equal(OrderStatus.Rejected, botEv.Status);

        // UI side: the new #381 event scoped to OrigClOrdId, status preserved
        // (Working, since orig was MarkWorking-ed), Leaves/Cum copied from
        // the still-alive original Order, and kind discriminated as
        // ReplaceRejected so the FE can clear inflightModifies + toast.
        Assert.Single(sink.Events);
        var uiEv = sink.Events[0];
        Assert.Equal(intent.OriginalClOrdId, uiEv.ClOrdId);
        Assert.Equal(ExecKind.ReplaceRejected, uiEv.Kind);
        Assert.Equal(OrderStatus.Working, uiEv.Status);
        Assert.Equal(orig.LeavesQuantity, uiEv.LeavesQuantity);
        Assert.Equal(orig.CumulativeQuantity, uiEv.CumulativeQuantity);
        Assert.Equal("reject_code=5", uiEv.RejectReason);
        Assert.Equal(intent.FirmId, uiEv.FirmId);

        // Original order itself is untouched (replace-reject is non-economic).
        Assert.Equal(OrderStatus.Working, orig.Status);
    }

    [Fact]
    public void ApplyReplaceRejected_OrigMissingFromBook_StillEmitsUiVisibleEvent_WithSafeDefaults()
    {
        // Edge case: the original Order has already terminalised (rare race
        // — late ApplyReplaceRejected for a replace that was issued just
        // before a fill arrived and Filled the original). The UI-visible
        // event still ships so the operator's Modify button gets unstuck;
        // Leaves/Cum fall back to 0 and Status defaults to Working (the
        // FE only uses the event as a "modify failed" signal in this case).
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var sink = new RecordingSink();
        var replacements = new PendingReplacementRegistry();
        var proc = new ExecutionReportProcessor(
            ownership, book, new PositionKeeper(), sink, new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            replacements: replacements);

        var owner = new EndClientId("alice");
        ownership.Register(1UL, owner);
        var intent = new OrderReplacementIntent(
            OriginalClOrdId: 1UL, NewClOrdId: 3UL,
            Owner: owner, Symbol: "PETR4", SecurityId: 4321UL,
            Side: OrderSide.Sell, Type: OrderType.Limit,
            NewQuantity: 100, NewPrice: 32.55m,
            FirmId: "FIRM01", ParentAlgoId: null, AlgoSliceSeq: null);
        Assert.True(replacements.TryAdd(intent));

        proc.Apply(3UL, ExecKind.Rejected, leaves: 0, cumQty: 0, lastQty: 0, lastPx: 0m, rejectReason: "reject_code=5");

        Assert.Single(sink.Events);
        var uiEv = sink.Events[0];
        Assert.Equal(1UL, uiEv.ClOrdId);
        Assert.Equal(ExecKind.ReplaceRejected, uiEv.Kind);
        Assert.Equal(0, uiEv.LeavesQuantity);
        Assert.Equal(0, uiEv.CumulativeQuantity);
    }

    private sealed class RecordingSink : IExecutionEventSink
    {
        public readonly List<ExecutionEvent> Events = new();
        public void Publish(ExecutionEvent ev) => Events.Add(ev);
    }

    private sealed class RecordingBotErRouter : IBotErRouter
    {
        public readonly List<ExecutionEvent> Events = new();
        public void Route(ExecutionEvent ev) => Events.Add(ev);
    }
}
