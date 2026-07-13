using B3.Trading.Application.SubAccount;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #458. Pins the contract of the no-op CBLC <c>Account</c> resolver
/// — every order returns <c>null</c> so the wire field stays omitted
/// and post-trade allocation continues to rely on the broker's
/// out-of-band matching (pre-#458 wire behavior). Operators that ship
/// a real resolver swap this default at the composition root.
/// </summary>
public class NullVenueAccountResolverTests
{
    [Fact]
    public void TryResolve_AnyOrder_ReturnsNull()
    {
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A");

        Assert.Null(NullVenueAccountResolver.Instance.TryResolve(order));
    }

    [Fact]
    public void TryResolve_OrderWithSubAccount_StillReturnsNull()
    {
        // SubAccount presence does not influence the null resolver —
        // CBLC Account is a fundamentally different identifier
        // (clearing-house issued, real number) and the default impl
        // refuses to invent one regardless of any domain context.
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A", subAccountId: new SubAccountId("tradingdesk"));

        Assert.Null(NullVenueAccountResolver.Instance.TryResolve(order));
    }

    [Fact]
    public void TryResolve_NullOrder_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => NullVenueAccountResolver.Instance.TryResolve(null!));
    }
}
