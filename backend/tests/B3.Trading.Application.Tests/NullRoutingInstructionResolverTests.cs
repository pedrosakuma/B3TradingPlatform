using B3.Trading.Application.Routing;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #473. Pins the contract of the no-op RoutingInstruction resolver —
/// every order returns <c>null</c>, so the wire field stays omitted
/// (pre-#473 behavior) and the pre-trade whitelist check is a no-op
/// for that order. Operators that ship a real resolver swap this
/// default at the composition root and must also opt in via
/// <c>RiskLimits.AllowedRoutingInstructions</c>.
/// </summary>
public class NullRoutingInstructionResolverTests
{
    [Fact]
    public void TryResolve_AnyOrder_ReturnsNull()
    {
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A");

        Assert.Null(NullRoutingInstructionResolver.Instance.TryResolve(order));
    }

    [Fact]
    public void TryResolve_OrderWithSubAccount_StillReturnsNull()
    {
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A", subAccountId: new SubAccountId("tradingdesk"));

        Assert.Null(NullRoutingInstructionResolver.Instance.TryResolve(order));
    }

    [Fact]
    public void TryResolve_NullOrder_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => NullRoutingInstructionResolver.Instance.TryResolve(null!));
    }
}
