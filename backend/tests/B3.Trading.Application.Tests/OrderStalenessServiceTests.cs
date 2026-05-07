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
}
