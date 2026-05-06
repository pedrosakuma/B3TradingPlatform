using B3.Trading.Domain;

namespace B3.Trading.Domain.Tests;

public class CashBalanceTests
{
    [Fact]
    public void Buy_DebitsAvailable()
    {
        var b = new CashBalance(new EndClientId("alice"));

        b.ApplyFill(OrderSide.Buy, 100, 30m);

        Assert.Equal(-3000m, b.Available);
    }

    [Fact]
    public void Sell_CreditsAvailable()
    {
        var b = new CashBalance(new EndClientId("alice"));

        b.ApplyFill(OrderSide.Sell, 100, 30m);

        Assert.Equal(3000m, b.Available);
    }

    [Fact]
    public void BuyThenSell_Nets()
    {
        var b = new CashBalance(new EndClientId("alice"));

        b.ApplyFill(OrderSide.Buy, 100, 30m);   // -3000
        b.ApplyFill(OrderSide.Sell, 50, 31m);   // +1550
        b.ApplyFill(OrderSide.Sell, 50, 32m);   // +1600

        Assert.Equal(150m, b.Available);
    }

    [Fact]
    public void NegativeOrZeroQuantity_Throws()
    {
        var b = new CashBalance(new EndClientId("alice"));

        Assert.Throws<ArgumentOutOfRangeException>(() => b.ApplyFill(OrderSide.Buy, 0, 10m));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.ApplyFill(OrderSide.Sell, -5, 10m));
    }

    [Fact]
    public void NegativePrice_Throws()
    {
        var b = new CashBalance(new EndClientId("alice"));

        Assert.Throws<ArgumentOutOfRangeException>(() => b.ApplyFill(OrderSide.Buy, 1, -0.01m));
    }
}
