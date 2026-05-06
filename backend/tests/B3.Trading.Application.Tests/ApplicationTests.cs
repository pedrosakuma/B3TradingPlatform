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

    [Fact]
    public void ReplacedStatus_IsTreatedAsTerminal_ByOpenOrderProjections()
    {
        // Slice 1 of #122 invariant: an order in Replaced status is
        // filtered out of every "open" projection (counts, sums,
        // enumerations) so risk/margin/UI cannot double-count the
        // original alongside the replacement.
        var book = new WorkingOrderBook();
        var alice = new EndClientId("alice");
        var sell = new Order(1UL, alice, "PETR4", 4321UL, OrderSide.Sell, OrderType.Limit, 100, 30m, "FIRM01");
        sell.MarkWorking();
        Assert.True(book.TryAdd(sell));

        Assert.Equal(1, book.CountOpenForOwner(alice));
        Assert.Equal(100, book.SumOpenSellLeavesForSymbol(alice, "PETR4"));

        sell.MarkReplaced();

        Assert.Equal(0, book.CountOpenForOwner(alice));
        Assert.Equal(0, book.SumOpenSellLeavesForSymbol(alice, "PETR4"));
        // Lookup still works — the original Order object is retained for
        // ER bookkeeping (late fills can still arrive) but it is no
        // longer surfaced as live.
        Assert.True(book.TryGet(1UL, out _));
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
