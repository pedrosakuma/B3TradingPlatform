using B3.Trading.Application.Investor;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #472. Pins the contract of the no-op InvestorId resolver — every
/// order returns <c>null</c>, so the wire field stays omitted
/// (pre-#472 behavior). Operators that ship a real resolver swap
/// this default at the composition root.
/// </summary>
public class NullInvestorIdResolverTests
{
    [Fact]
    public void TryResolve_AnyOrder_ReturnsNull()
    {
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A");

        Assert.Null(NullInvestorIdResolver.Instance.TryResolve(order));
    }

    [Fact]
    public void TryResolve_OrderWithSubAccount_StillReturnsNull()
    {
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A", subAccountId: new SubAccountId("tradingdesk"));

        Assert.Null(NullInvestorIdResolver.Instance.TryResolve(order));
    }

    [Fact]
    public void TryResolve_NullOrder_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => NullInvestorIdResolver.Instance.TryResolve(null!));
    }

    [Fact]
    public void InvestorIdentity_RecordEquality_IsValueBased()
    {
        // Defensive: value-equality + non-zero fields survive a copy
        // round-trip. Confirms the readonly-record-struct semantics
        // the gateway relies on when comparing/logging.
        var a = new InvestorIdentity(7, 12345);
        var b = new InvestorIdentity(7, 12345);
        var c = new InvestorIdentity(7, 99999);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal((ushort)7, a.Prefix);
        Assert.Equal(12345u, a.Document);
    }
}
