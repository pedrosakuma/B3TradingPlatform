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
}
