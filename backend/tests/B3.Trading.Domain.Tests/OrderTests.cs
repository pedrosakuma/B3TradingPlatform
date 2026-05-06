using B3.Trading.Domain;

namespace B3.Trading.Domain.Tests;

public class OrderTests
{
    [Fact]
    public void ApplyFill_PartialThenFull_TransitionsStatus()
    {
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30.50m);

        order.ApplyFill(40);
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
        Assert.Equal(60, order.LeavesQuantity);

        order.ApplyFill(60);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(0, order.LeavesQuantity);
        Assert.Equal(100, order.CumulativeQuantity);
    }

    [Fact]
    public void ApplyFill_OverLeaves_Throws()
    {
        var order = new Order(2UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Sell, OrderType.Limit, 10, 30m);
        Assert.Throws<InvalidOperationException>(() => order.ApplyFill(11));
    }

    // -------- ApplyCumulativeFill (Phase 3/1a) --------

    [Fact]
    public void ApplyCumulativeFill_ForwardProgress_ReturnsDelta()
    {
        var order = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);

        Assert.Equal(40, order.ApplyCumulativeFill(40));
        Assert.Equal(40, order.CumulativeQuantity);
        Assert.Equal(60, order.LeavesQuantity);
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);

        Assert.Equal(60, order.ApplyCumulativeFill(100));
        Assert.Equal(0, order.LeavesQuantity);
        Assert.Equal(OrderStatus.Filled, order.Status);
    }

    [Fact]
    public void ApplyCumulativeFill_StaleOrDuplicate_ReturnsZero()
    {
        var order = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        order.ApplyCumulativeFill(60);

        Assert.Equal(0, order.ApplyCumulativeFill(60)); // duplicate
        Assert.Equal(0, order.ApplyCumulativeFill(40)); // stale (out-of-order)
        Assert.Equal(60, order.CumulativeQuantity);
        Assert.Equal(40, order.LeavesQuantity);
    }

    [Fact]
    public void ApplyCumulativeFill_PreservesTerminalStatus()
    {
        var order = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        order.MarkCancelled();

        // Late fill against a cancelled order — must book delta, keep status.
        var delta = order.ApplyCumulativeFill(40);
        Assert.Equal(40, delta);
        Assert.Equal(40, order.CumulativeQuantity);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public void ApplyCumulativeFill_Overfill_DoesNotThrow()
    {
        var order = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        var delta = order.ApplyCumulativeFill(150);
        Assert.Equal(150, delta);
        Assert.Equal(150, order.CumulativeQuantity);
        Assert.Equal(0, order.LeavesQuantity);
        Assert.Equal(OrderStatus.Filled, order.Status);
    }

    [Fact]
    public void MarkCancelled_AfterFilled_NoOp()
    {
        var order = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        order.ApplyFill(100);
        order.MarkCancelled();
        Assert.Equal(OrderStatus.Filled, order.Status);
    }

    [Fact]
    public void MarkRejected_AfterPartialFill_NoOp()
    {
        var order = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        order.ApplyFill(40);
        order.MarkRejected();
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
    }

    [Fact]
    public void MarkWorking_AfterPartialFill_NoOp()
    {
        var order = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        order.MarkWorking();
        order.ApplyFill(40);
        order.MarkWorking(); // replayed New ER
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
    }

    // -------- MarkReplaced (Slice 1 of #122) --------

    [Fact]
    public void MarkReplaced_FromWorking_TerminalisesOriginal()
    {
        var order = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        order.MarkWorking();
        order.MarkReplaced();
        Assert.Equal(OrderStatus.Replaced, order.Status);
    }

    [Fact]
    public void MarkReplaced_FromPartiallyFilled_TerminalisesOriginal()
    {
        // Cumulative fills survive on the original (the replacement order
        // takes them as its baseline); the original's status moves to
        // Replaced and is filtered out of restable surfaces.
        var order = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        order.ApplyFill(40);
        order.MarkReplaced();
        Assert.Equal(OrderStatus.Replaced, order.Status);
        Assert.Equal(40, order.CumulativeQuantity);
    }

    [Fact]
    public void MarkReplaced_AfterFilled_NoOp()
    {
        // Late Replaced ER must NOT erase a final fill — terminal status
        // wins, exchange truth is the position state.
        var order = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        order.ApplyFill(100);
        order.MarkReplaced();
        Assert.Equal(OrderStatus.Filled, order.Status);
    }

    [Fact]
    public void MarkReplaced_IsIdempotent()
    {
        var order = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        order.MarkReplaced();
        order.MarkReplaced();
        Assert.Equal(OrderStatus.Replaced, order.Status);
    }

    [Fact]
    public void MarkCancelled_AfterReplaced_NoOp()
    {
        // A Cancelled ER drifting in for an already-replaced original
        // must not regress the status (the new ClOrdID owns the live
        // surface now).
        var order = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        order.MarkReplaced();
        order.MarkCancelled();
        Assert.Equal(OrderStatus.Replaced, order.Status);
    }

    [Fact]
    public void ApplyCumulativeFill_AfterReplaced_PreservesReplacedStatus()
    {
        // Slice 1 invariant: a late fill ER against an already-Replaced
        // original still books position truth (cumQty advances) but
        // status stays Replaced — the live order is the replacement.
        var order = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        order.ApplyFill(40);
        order.MarkReplaced();
        var delta = order.ApplyCumulativeFill(50);
        Assert.Equal(10, delta);
        Assert.Equal(50, order.CumulativeQuantity);
        Assert.Equal(OrderStatus.Replaced, order.Status);
    }
}
