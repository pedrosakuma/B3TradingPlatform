using B3.Trading.Domain;

namespace B3.Trading.Domain.Tests;

/// <summary>
/// #457. Domain-level invariants for the FIX MinQty field on
/// <see cref="Order"/>. The ctor is the single chokepoint for both
/// live submits and replay hydration; rejecting a malformed MinQty
/// here means no risk-pipeline, gateway, or recovery path can
/// reconstitute an illegal order with MinQty &gt; Quantity.
/// </summary>
public class OrderMinQtyValidationTests
{
    private static readonly EndClientId Owner = new("alice");

    private static Order Build(long? minQty = null, long quantity = 100) =>
        new(1UL, Owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, quantity, 30m,
            minQty: minQty);

    [Fact]
    public void NoMinQty_DefaultsToNull()
    {
        var o = Build();

        Assert.Null(o.MinQty);
    }

    [Theory]
    [InlineData(1L, 1L)]
    [InlineData(1L, 100L)]
    [InlineData(50L, 100L)]
    [InlineData(100L, 100L)]
    public void MinQty_AcceptsPositiveUpToQuantity(long minQty, long quantity)
    {
        var o = Build(minQty: minQty, quantity: quantity);

        Assert.Equal(minQty, o.MinQty);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void MinQty_NonPositive_Throws(long minQty)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(minQty: minQty));
        Assert.Contains("MinQty must be positive", ex.Message);
    }

    [Fact]
    public void MinQty_GreaterThanQuantity_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(minQty: 101, quantity: 100));
        Assert.Contains("must not exceed order Quantity", ex.Message);
    }

    [Fact]
    public void HydrateReplacement_InheritsMinQty()
    {
        var original = Build(minQty: 20, quantity: 100);

        var replacement = Order.HydrateReplacement(
            original, newClOrdId: 99UL, newQuantity: 80, newPrice: 31m,
            erLeaves: 80, erCumulative: 0);

        Assert.Equal(20, replacement.MinQty);
    }

    [Fact]
    public void HydrateReplacement_ClampsMinQtyToNewQuantity()
    {
        // Mirror of DisplayQty clamp: when the replacement quantity drops
        // below the original MinQty, the ctor invariant would otherwise
        // refuse to construct the order. Clamp the minimum down so the
        // replacement is legal (the venue gets a coherent
        // MinQty <= OrderQty pair on the wire).
        var original = Build(minQty: 60, quantity: 100);

        var replacement = Order.HydrateReplacement(
            original, newClOrdId: 99UL, newQuantity: 40, newPrice: 31m,
            erLeaves: 40, erCumulative: 0);

        Assert.Equal(40, replacement.MinQty);
    }
}
