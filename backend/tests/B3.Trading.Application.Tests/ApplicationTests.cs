using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

public class PositionKeeperTests
{
    [Fact]
    public void ApplyFill_AggregatesPerOwnerSymbol()
    {
        var keeper = new PositionKeeper();
        var alice = new EndClientId("alice");

        keeper.ApplyFill(alice, "PETR4", OrderSide.Buy, 100, 30m);
        keeper.ApplyFill(alice, "PETR4", OrderSide.Buy, 50, 31m);

        var positions = keeper.ForEndClient(alice);
        Assert.Single(positions);
        Assert.Equal(150, positions.Single().NetQuantity);
    }
}

public class WorkingOrderBookTests
{
    [Fact]
    public void TryAdd_DuplicateClOrdId_ReturnsFalse()
    {
        var book = new WorkingOrderBook();
        var order = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 1, 1m);

        Assert.True(book.TryAdd(order));
        Assert.False(book.TryAdd(order));
    }
}

public class EndClientRegistryTests
{
    [Fact]
    public void Register_IsIdempotent()
    {
        var reg = new EndClientRegistry();
        var a = reg.Register("Alice");
        var b = reg.Register("alice");
        Assert.Equal(a, b);
    }
}
