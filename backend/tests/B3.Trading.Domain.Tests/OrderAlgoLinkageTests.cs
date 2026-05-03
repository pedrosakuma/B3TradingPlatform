using B3.Trading.Domain;

namespace B3.Trading.Domain.Tests;

/// <summary>
/// Slice 2: Order grew nullable <c>ParentAlgoId</c>/<c>AlgoSliceSeq</c>
/// for algo-engine children. Manual orders must keep working with both
/// fields null; the pair must be validated together.
/// </summary>
public class OrderAlgoLinkageTests
{
    private static readonly EndClientId Alice = new("alice");

    [Fact]
    public void ManualOrder_DefaultsAlgoFieldsToNull()
    {
        var o = new Order(1UL, Alice, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        Assert.Null(o.ParentAlgoId);
        Assert.Null(o.AlgoSliceSeq);
    }

    [Fact]
    public void AlgoChild_CarriesBothParentAndSlice()
    {
        var o = new Order(1UL, Alice, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m,
            firmId: "F1", parentAlgoId: 42UL, algoSliceSeq: 0);
        Assert.Equal(42UL, o.ParentAlgoId);
        Assert.Equal(0, o.AlgoSliceSeq);
    }

    [Fact]
    public void AlgoChild_RejectsHalfSetPair()
    {
        Assert.Throws<ArgumentException>(() =>
            new Order(1UL, Alice, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m,
                parentAlgoId: 42UL));
        Assert.Throws<ArgumentException>(() =>
            new Order(1UL, Alice, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m,
                algoSliceSeq: 0));
    }

    [Fact]
    public void AlgoChild_RejectsZeroParentAlgoId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Order(1UL, Alice, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m,
                parentAlgoId: 0UL, algoSliceSeq: 0));
    }

    [Fact]
    public void AlgoChild_RejectsNegativeSliceSeq()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Order(1UL, Alice, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m,
                parentAlgoId: 42UL, algoSliceSeq: -1));
    }
}
