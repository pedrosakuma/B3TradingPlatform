using B3.Trading.Domain;

namespace B3.Trading.Domain.Tests;

public class OrderStaleTests
{
    private static Order Working()
    {
        var o = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        o.MarkWorking();
        return o;
    }

    [Fact]
    public void MarkStale_OnWorking_FlipsFlagAndRecordsMetadata()
    {
        var o = Working();
        var at = new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero);

        var changed = o.MarkStale("matching restart", at);

        Assert.True(changed);
        Assert.True(o.IsStale);
        Assert.Equal("matching restart", o.StaleReason);
        Assert.Equal(at, o.StaledAtUtc);
        Assert.Equal(OrderStatus.Working, o.Status);
        Assert.Equal(100, o.LeavesQuantity);
    }

    [Fact]
    public void MarkStale_OnPartiallyFilled_AlsoEligible()
    {
        var o = Working();
        o.ApplyCumulativeFill(40);
        Assert.Equal(OrderStatus.PartiallyFilled, o.Status);

        Assert.True(o.MarkStale("gap", DateTimeOffset.UtcNow));
        Assert.True(o.IsStale);
    }

    [Fact]
    public void MarkStale_OnPendingNew_IsNoOp()
    {
        var o = new Order(1UL, new EndClientId("a"), "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 10, 30m);
        Assert.Equal(OrderStatus.PendingNew, o.Status);

        Assert.False(o.MarkStale("x", DateTimeOffset.UtcNow));
        Assert.False(o.IsStale);
    }

    [Fact]
    public void MarkStale_OnFilled_IsNoOp()
    {
        var o = Working();
        o.ApplyCumulativeFill(100);
        Assert.Equal(OrderStatus.Filled, o.Status);

        Assert.False(o.MarkStale("x", DateTimeOffset.UtcNow));
        Assert.False(o.IsStale);
    }

    [Fact]
    public void MarkStale_OnCancelled_IsNoOp()
    {
        var o = Working();
        o.MarkCancelled();
        Assert.False(o.MarkStale("x", DateTimeOffset.UtcNow));
        Assert.False(o.IsStale);
    }

    [Fact]
    public void MarkStale_Idempotent_PreservesOriginalTimestamp()
    {
        var o = Working();
        var first = new DateTimeOffset(2026, 5, 7, 10, 0, 0, TimeSpan.Zero);
        o.MarkStale("first", first);

        var second = new DateTimeOffset(2026, 5, 7, 11, 0, 0, TimeSpan.Zero);
        var changed = o.MarkStale("second", second);

        Assert.False(changed);
        Assert.Equal("first", o.StaleReason);
        Assert.Equal(first, o.StaledAtUtc);
    }

    [Fact]
    public void MarkStale_BlankReason_Throws()
    {
        var o = Working();
        Assert.Throws<ArgumentException>(() => o.MarkStale("   ", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ClearStale_OnStale_ResetsAllFields()
    {
        var o = Working();
        o.MarkStale("x", DateTimeOffset.UtcNow);

        Assert.True(o.ClearStale());
        Assert.False(o.IsStale);
        Assert.Null(o.StaleReason);
        Assert.Null(o.StaledAtUtc);
    }

    [Fact]
    public void ClearStale_WhenNotStale_IsNoOp()
    {
        var o = Working();
        Assert.False(o.ClearStale());
    }

    [Fact]
    public void Hydrate_PreservesStaleFields()
    {
        var at = new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero);
        var hydrated = Order.Hydrate(
            clOrdId: 42UL, owner: new EndClientId("alice"), symbol: "PETR4", securityId: 1UL,
            side: OrderSide.Buy, type: OrderType.Limit,
            quantity: 100, price: 30m, leaves: 60, cumQty: 40,
            status: OrderStatus.PartiallyFilled,
            isStale: true, staleReason: "venue-gap", staledAtUtc: at);

        Assert.True(hydrated.IsStale);
        Assert.Equal("venue-gap", hydrated.StaleReason);
        Assert.Equal(at, hydrated.StaledAtUtc);
        Assert.Equal(OrderStatus.PartiallyFilled, hydrated.Status);
    }
}
