using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;

namespace B3.Trading.Application.Tests;

public class OrderStalenessServiceTests
{
    private static (OrderStalenessService svc, WorkingOrderBook book, EventDispatcher dispatcher) Build()
    {
        var book = new WorkingOrderBook();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var svc = new OrderStalenessService(dispatcher, book);
        return (svc, book, dispatcher);
    }

    private static Order AddWorking(WorkingOrderBook book, ulong clOrdId, string firmId = "FIRM01")
    {
        var o = new Order(clOrdId, new EndClientId("alice"), "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId);
        o.MarkWorking();
        book.TryAdd(o);
        return o;
    }

    [Fact]
    public void MarkStale_OnWorkingOrder_Marks()
    {
        var (svc, book, _) = Build();
        var order = AddWorking(book, 1UL);

        var r = svc.MarkStale("FIRM01", 1UL, "matching restart", DateTimeOffset.UtcNow, "admin");

        Assert.Equal(MarkStaleResult.Marked, r);
        Assert.True(order.IsStale);
        Assert.Equal("matching restart", order.StaleReason);
    }

    [Fact]
    public void MarkStale_Idempotent_ReturnsAlreadyStale()
    {
        var (svc, book, _) = Build();
        AddWorking(book, 1UL);
        svc.MarkStale("FIRM01", 1UL, "x", DateTimeOffset.UtcNow, null);

        var r = svc.MarkStale("FIRM01", 1UL, "y", DateTimeOffset.UtcNow, null);

        Assert.Equal(MarkStaleResult.AlreadyStale, r);
    }

    [Fact]
    public void MarkStale_Unknown_ReturnsNotFound()
    {
        var (svc, _, _) = Build();
        Assert.Equal(MarkStaleResult.NotFound, svc.MarkStale("FIRM01", 99UL, "x", DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public void MarkStale_WrongFirm_ReturnsWrongFirm()
    {
        var (svc, book, _) = Build();
        AddWorking(book, 1UL, firmId: "FIRM01");
        Assert.Equal(MarkStaleResult.WrongFirm, svc.MarkStale("FIRM02", 1UL, "x", DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public void MarkStale_PendingNew_ReturnsNotEligible()
    {
        var (svc, book, _) = Build();
        var o = new Order(1UL, new EndClientId("alice"), "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM01");
        book.TryAdd(o);
        Assert.Equal(OrderStatus.PendingNew, o.Status);

        Assert.Equal(MarkStaleResult.NotEligible, svc.MarkStale("FIRM01", 1UL, "x", DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public void MarkStale_FilledOrder_ReturnsNotEligible()
    {
        var (svc, book, _) = Build();
        var o = AddWorking(book, 1UL);
        o.ApplyCumulativeFill(100);
        Assert.Equal(OrderStatus.Filled, o.Status);

        Assert.Equal(MarkStaleResult.NotEligible, svc.MarkStale("FIRM01", 1UL, "x", DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public void ClearStale_OnStaleOrder_Clears()
    {
        var (svc, book, _) = Build();
        AddWorking(book, 1UL);
        svc.MarkStale("FIRM01", 1UL, "x", DateTimeOffset.UtcNow, null);

        Assert.Equal(ClearStaleResult.Cleared, svc.ClearStale("FIRM01", 1UL, "admin"));
    }

    [Fact]
    public void ClearStale_NotStale_ReturnsNotStale()
    {
        var (svc, book, _) = Build();
        AddWorking(book, 1UL);
        Assert.Equal(ClearStaleResult.NotStale, svc.ClearStale("FIRM01", 1UL, null));
    }

    [Fact]
    public void Snapshot_RoundTrip_PreservesStaleFields()
    {
        var book = new WorkingOrderBook();
        var o = new Order(1UL, new EndClientId("alice"), "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM01");
        o.MarkWorking();
        var at = new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero);
        o.MarkStale("matching restart", at);
        book.TryAdd(o);

        var snaps = book.Snapshot().ToList();
        Assert.True(snaps[0].IsStale);
        Assert.Equal("matching restart", snaps[0].StaleReason);
        Assert.Equal(at, snaps[0].StaledAtUtc);

        var book2 = new WorkingOrderBook();
        book2.Restore(snaps);

        Assert.True(book2.TryGet(1UL, out var restored));
        Assert.NotNull(restored);
        Assert.True(restored!.IsStale);
        Assert.Equal("matching restart", restored.StaleReason);
        Assert.Equal(at, restored.StaledAtUtc);
    }

    private sealed class CapturingSink : IExecutionEventSink
    {
        public List<ExecutionEvent> Events { get; } = new();
        public void Publish(ExecutionEvent ev) => Events.Add(ev);
    }

    private static (OrderStalenessService svc, WorkingOrderBook book, CapturingSink sink) BuildWithSink()
    {
        var book = new WorkingOrderBook();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var sink = new CapturingSink();
        var svc = new OrderStalenessService(dispatcher, book, sink);
        return (svc, book, sink);
    }

    [Fact]
    public void MarkStale_PublishesSyntheticSuspendedEvent()
    {
        // Slice 5 of #132. Downstream consumers (UI executions log,
        // orders.me push, future risk projections) need a real-time
        // signal, otherwise the trader only sees the badge after a
        // refresh / reconnect.
        var (svc, book, sink) = BuildWithSink();
        var order = AddWorking(book, 1UL);
        var at = DateTimeOffset.Parse("2026-05-07T20:00:00Z");

        var r = svc.MarkStale("FIRM01", 1UL, "inbound_gap:50-52", at, "admin");

        Assert.Equal(MarkStaleResult.Marked, r);
        var ev = Assert.Single(sink.Events);
        Assert.Equal(ExecKind.Suspended, ev.Kind);
        Assert.Equal(1UL, ev.ClOrdId);
        Assert.Equal(order.Owner, ev.Owner);
        Assert.Equal("PETR4", ev.Symbol);
        Assert.Equal(0, ev.LastQuantity);
        Assert.Equal(0m, ev.LastPrice);
        Assert.Equal(order.LeavesQuantity, ev.LeavesQuantity);
        Assert.Equal("inbound_gap:50-52", ev.RejectReason);
        Assert.Equal(at, ev.TimestampUtc);
    }

    [Fact]
    public void MarkStale_DoesNotPublish_WhenAlreadyStale()
    {
        // Idempotency invariant: a re-mark must not produce a second
        // synthetic Suspended event, otherwise the executions log
        // would grow on every duplicate admin click / bulk re-run.
        var (svc, book, sink) = BuildWithSink();
        AddWorking(book, 1UL);
        svc.MarkStale("FIRM01", 1UL, "x", DateTimeOffset.UtcNow, null);
        sink.Events.Clear();

        var r = svc.MarkStale("FIRM01", 1UL, "y", DateTimeOffset.UtcNow, null);

        Assert.Equal(MarkStaleResult.AlreadyStale, r);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void MarkAllWorkingByFirm_PublishesOneSuspendedPerNewlyMarkedOrder()
    {
        // Slice 2's bulk path must fan out one event per actually-
        // marked order (no event for the already-stale one) so the UI
        // updates each row independently.
        var (svc, book, sink) = BuildWithSink();
        AddWorking(book, 1UL);
        AddWorking(book, 2UL);
        var pre = AddWorking(book, 3UL);
        Assert.True(pre.MarkStale("preexisting", DateTimeOffset.UtcNow));

        var marked = svc.MarkAllWorkingByFirm("FIRM01", "venue_desync", DateTimeOffset.UtcNow, "auto");

        Assert.Equal(2, marked);
        Assert.Equal(2, sink.Events.Count);
        Assert.All(sink.Events, e => Assert.Equal(ExecKind.Suspended, e.Kind));
        Assert.All(sink.Events, e => Assert.Equal("venue_desync", e.RejectReason));
        Assert.DoesNotContain(sink.Events, e => e.ClOrdId == 3UL);
    }

    [Fact]
    public void ClearStale_PublishesSyntheticRestoredEvent()
    {
        // Admin clear path: mirror the Suspended publish so the UI
        // can lift the badge without a refresh. Auto-clear via genuine
        // ER (ExecutionReportProcessor) intentionally does NOT publish
        // a Restored — the genuine ER already broadcasts the new state.
        var (svc, book, sink) = BuildWithSink();
        AddWorking(book, 1UL);
        svc.MarkStale("FIRM01", 1UL, "x", DateTimeOffset.UtcNow, null);
        sink.Events.Clear();

        var r = svc.ClearStale("FIRM01", 1UL, "admin");

        Assert.Equal(ClearStaleResult.Cleared, r);
        var ev = Assert.Single(sink.Events);
        Assert.Equal(ExecKind.Restored, ev.Kind);
        Assert.Equal(1UL, ev.ClOrdId);
        Assert.Equal(0, ev.LastQuantity);
    }

    [Fact]
    public void ClearStale_DoesNotPublish_WhenNotStale()
    {
        var (svc, book, sink) = BuildWithSink();
        AddWorking(book, 1UL);

        var r = svc.ClearStale("FIRM01", 1UL, "admin");

        Assert.Equal(ClearStaleResult.NotStale, r);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void PublishSyntheticEvent_SwallowsSinkExceptions()
    {
        // Sink failures must NOT bubble up: the WAL event is already
        // committed at this point and re-attempting would risk a
        // duplicate. The mark/clear API contract should still report
        // success so admin tooling does not retry endlessly.
        var book = new WorkingOrderBook();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var throwing = new ThrowingSink();
        var svc = new OrderStalenessService(dispatcher, book, throwing);
        AddWorking(book, 1UL);

        var r = svc.MarkStale("FIRM01", 1UL, "x", DateTimeOffset.UtcNow, null);

        Assert.Equal(MarkStaleResult.Marked, r);
    }

    private sealed class ThrowingSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent ev) => throw new InvalidOperationException("subscriber down");
    }

    private sealed class CapturingMargin : Risk.IMarginProvider
    {
        public List<(ulong ClOrdId, ExecKind Kind, long LastQty)> Calls { get; } = new();
        public Task<Risk.RiskDecision> TryReserveAsync(ulong clOrdId, Risk.RiskContext ctx, CancellationToken ct)
            => Task.FromResult(Risk.RiskDecision.Approve);
        public void OnExecution(ulong clOrdId, ExecKind kind, long lastQty)
            => Calls.Add((clOrdId, kind, lastQty));
        public void ReleaseReservation(ulong clOrdId) { }
    }

    private sealed class ThrowingMargin : Risk.IMarginProvider
    {
        public Task<Risk.RiskDecision> TryReserveAsync(ulong clOrdId, Risk.RiskContext ctx, CancellationToken ct)
            => Task.FromResult(Risk.RiskDecision.Approve);
        public void OnExecution(ulong clOrdId, ExecKind kind, long lastQty)
            => throw new InvalidOperationException("ledger down");
        public void ReleaseReservation(ulong clOrdId) { }
    }

    private static (OrderStalenessService svc, WorkingOrderBook book, CapturingSink sink, CapturingMargin margin) BuildWithSinkAndMargin()
    {
        var book = new WorkingOrderBook();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var sink = new CapturingSink();
        var margin = new CapturingMargin();
        var svc = new OrderStalenessService(dispatcher, book, sink, margin);
        return (svc, book, sink, margin);
    }

    [Fact]
    public void MarkStale_NotifiesMarginWithSuspended()
    {
        // #153. The cash hold for a stale order must be released so
        // ghosts stop blocking new trading. The margin call mirrors
        // the synthetic event publish: one Suspended notification per
        // mark, lastQty=0 (the staleness flip carries no fill data).
        var (svc, book, _, margin) = BuildWithSinkAndMargin();
        AddWorking(book, 1UL);

        var r = svc.MarkStale("FIRM01", 1UL, "venue_desync", DateTimeOffset.UtcNow, "auto");

        Assert.Equal(MarkStaleResult.Marked, r);
        var call = Assert.Single(margin.Calls);
        Assert.Equal((1UL, ExecKind.Suspended, 0L), call);
    }

    [Fact]
    public void MarkStale_DoesNotNotifyMargin_WhenAlreadyStale()
    {
        // Idempotency: a duplicate mark must not double-release cash
        // (the margin provider is itself idempotent on Suspended, but
        // we do not want to spend the call either).
        var (svc, book, _, margin) = BuildWithSinkAndMargin();
        AddWorking(book, 1UL);
        svc.MarkStale("FIRM01", 1UL, "x", DateTimeOffset.UtcNow, null);
        margin.Calls.Clear();

        var r = svc.MarkStale("FIRM01", 1UL, "y", DateTimeOffset.UtcNow, null);

        Assert.Equal(MarkStaleResult.AlreadyStale, r);
        Assert.Empty(margin.Calls);
    }

    [Fact]
    public void ClearStale_NotifiesMarginWithRestored()
    {
        var (svc, book, _, margin) = BuildWithSinkAndMargin();
        AddWorking(book, 1UL);
        svc.MarkStale("FIRM01", 1UL, "x", DateTimeOffset.UtcNow, null);
        margin.Calls.Clear();

        var r = svc.ClearStale("FIRM01", 1UL, "admin");

        Assert.Equal(ClearStaleResult.Cleared, r);
        var call = Assert.Single(margin.Calls);
        Assert.Equal((1UL, ExecKind.Restored, 0L), call);
    }

    [Fact]
    public void MarkAllWorkingByFirm_NotifiesMarginPerNewlyMarkedOrder()
    {
        var (svc, book, _, margin) = BuildWithSinkAndMargin();
        AddWorking(book, 1UL);
        AddWorking(book, 2UL);
        var pre = AddWorking(book, 3UL);
        Assert.True(pre.MarkStale("preexisting", DateTimeOffset.UtcNow));
        margin.Calls.Clear();

        var marked = svc.MarkAllWorkingByFirm("FIRM01", "venue_desync", DateTimeOffset.UtcNow, "auto");

        Assert.Equal(2, marked);
        Assert.Equal(2, margin.Calls.Count);
        Assert.All(margin.Calls, c => Assert.Equal(ExecKind.Suspended, c.Kind));
        Assert.DoesNotContain(margin.Calls, c => c.ClOrdId == 3UL);
    }

    [Fact]
    public void MarkStale_SwallowsMarginException_StillReturnsMarked()
    {
        // #153. Margin failures must NOT bubble: the WAL event for
        // MarkStale is already committed; if the admin call faulted
        // here, the operator would think the stale state never changed
        // when in fact it did. We log + emit a metric instead.
        var book = new WorkingOrderBook();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var svc = new OrderStalenessService(dispatcher, book, sink: null, margin: new ThrowingMargin());
        AddWorking(book, 1UL);

        var r = svc.MarkStale("FIRM01", 1UL, "x", DateTimeOffset.UtcNow, null);

        Assert.Equal(MarkStaleResult.Marked, r);
    }
}
