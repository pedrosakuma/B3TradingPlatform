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
}
