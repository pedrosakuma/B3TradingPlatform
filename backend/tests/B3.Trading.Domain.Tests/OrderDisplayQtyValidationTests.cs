using B3.Trading.Domain;

namespace B3.Trading.Domain.Tests;

/// <summary>
/// Q3.4 (#284). Domain-level invariants for the native iceberg /
/// reserve display-qty fields on <see cref="Order"/>. The ctor is
/// the single chokepoint for both live submits and replay
/// hydration; rejecting a malformed (DisplayQty, DisplayResetPolicy)
/// pair here means no risk-pipeline, gateway, or recovery path can
/// reconstitute an illegal iceberg order.
/// </summary>
public class OrderDisplayQtyValidationTests
{
    private static readonly EndClientId Owner = new("alice");

    private static Order Build(long? displayQty = null, DisplayResetPolicy? policy = null, long quantity = 100) =>
        new(1UL, Owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, quantity, 30m,
            displayQty: displayQty, displayResetPolicy: policy);

    [Fact]
    public void NoDisplayQty_IsFullDisclosure_NullPolicy()
    {
        var o = Build();

        Assert.Null(o.DisplayQty);
        Assert.Null(o.DisplayResetPolicy);
    }

    [Fact]
    public void DisplayQty_DefaultsPolicy_ToAlways()
    {
        var o = Build(displayQty: 10);

        Assert.Equal(10, o.DisplayQty);
        Assert.Equal(DisplayResetPolicy.Always, o.DisplayResetPolicy);
    }

    [Theory]
    [InlineData(DisplayResetPolicy.Always)]
    [InlineData(DisplayResetPolicy.OnPartialFill)]
    [InlineData(DisplayResetPolicy.Never)]
    public void DisplayQty_PreservesExplicitPolicy(DisplayResetPolicy policy)
    {
        var o = Build(displayQty: 10, policy: policy);

        Assert.Equal(policy, o.DisplayResetPolicy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DisplayQty_NonPositive_Rejected(long bad)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(displayQty: bad));
        Assert.Contains("DisplayQty must be positive", ex.Message);
    }

    [Fact]
    public void DisplayQty_GreaterThanQuantity_Rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(displayQty: 101, quantity: 100));
        Assert.Contains("must not exceed order Quantity", ex.Message);
    }

    [Fact]
    public void DisplayQty_EqualToQuantity_Allowed()
    {
        // Edge case — equivalent to no reserve, but accepted so the
        // wire mapping stays trivial (MaxFloor = OrderQty) and the
        // trader can express "show everything explicitly".
        var o = Build(displayQty: 100, quantity: 100);
        Assert.Equal(100, o.DisplayQty);
    }

    [Fact]
    public void Policy_WithoutDisplayQty_Rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(policy: DisplayResetPolicy.Always));
        Assert.Contains("DisplayResetPolicy must be null when DisplayQty is null", ex.Message);
    }

    [Fact]
    public void Hydrate_RoundTripsDisplayFields()
    {
        var o = Order.Hydrate(
            1UL, Owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            quantity: 100, price: 30m, leaves: 90, cumQty: 10, status: OrderStatus.PartiallyFilled,
            displayQty: 10, displayResetPolicy: DisplayResetPolicy.Never);

        Assert.Equal(10, o.DisplayQty);
        Assert.Equal(DisplayResetPolicy.Never, o.DisplayResetPolicy);
        Assert.Equal(90, o.LeavesQuantity);
        Assert.Equal(10, o.CumulativeQuantity);
    }

    [Fact]
    public void HydrateReplacement_InheritsDisplayFields()
    {
        var original = Build(displayQty: 10, policy: DisplayResetPolicy.OnPartialFill, quantity: 100);

        var replacement = Order.HydrateReplacement(
            original, newClOrdId: 2UL, newQuantity: 80, newPrice: 31m, erLeaves: 80, erCumulative: 0);

        Assert.Equal(10, replacement.DisplayQty);
        Assert.Equal(DisplayResetPolicy.OnPartialFill, replacement.DisplayResetPolicy);
    }

    [Fact]
    public void HydrateReplacement_ClampsDisplayQtyWhenNewQtyShrinks()
    {
        // If the operator shrinks the order qty below the original
        // visible portion, the replacement's DisplayQty must be
        // clamped — otherwise the ctor invariant (DQ <= Quantity)
        // would throw and recovery would be poisoned.
        var original = Build(displayQty: 50, policy: DisplayResetPolicy.Always, quantity: 100);

        var replacement = Order.HydrateReplacement(
            original, newClOrdId: 2UL, newQuantity: 20, newPrice: 31m, erLeaves: 20, erCumulative: 0);

        Assert.Equal(20, replacement.DisplayQty);
    }
}
