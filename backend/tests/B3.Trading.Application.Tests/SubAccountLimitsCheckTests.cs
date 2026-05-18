using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Checks;
using B3.Trading.Domain;
using Xunit;

namespace B3.Trading.Application.Tests;

public class SubAccountLimitsCheckTests
{
    private const string Owner = "alice";
    private const string Firm = "FIRM01";
    private const string Symbol = "PETR4";
    private const string Sub = "tradingdesk";

    private static (SubAccountLimitsCheck Check, SubAccountsRegistry Reg, WorkingOrderBook Book, SubAccountPositionKeeper Pos)
        Build(SubAccountRiskOptions opts, bool registerSub = true, bool active = true)
    {
        var book = new WorkingOrderBook();
        var pos = new SubAccountPositionKeeper();
        var reg = new SubAccountsRegistry();
        if (registerSub)
        {
            reg.ApplyCreated(Firm, Sub, null);
            if (!active) reg.ApplyDeactivated(Firm, Sub);
        }
        var check = new SubAccountLimitsCheck(
            new StaticOptionsMonitor<SubAccountRiskOptions>(opts),
            book, pos, reg);
        return (check, reg, book, pos);
    }

    private static RiskContext Ctx(SubAccountId? sub, OrderSide side = OrderSide.Buy, long qty = 100, decimal? px = 10m) =>
        new(new EndClientId(Owner), Firm, Symbol, side, OrderType.Limit, qty, px, SubAccountId: sub);

    [Fact]
    public void NoSubAccount_OnContext_Approves()
    {
        var (check, _, _, _) = Build(new SubAccountRiskOptions());
        var d = check.Check(Ctx(sub: null));
        Assert.True(d.Approved);
    }

    [Fact]
    public void DeactivatedSubAccount_Rejects()
    {
        var (check, _, _, _) = Build(new SubAccountRiskOptions(), registerSub: true, active: false);
        var d = check.Check(Ctx(new SubAccountId(Sub)));
        Assert.False(d.Approved);
        Assert.StartsWith("sub_account_limit_exceeded", d.Reason);
        Assert.Contains("deactivated", d.Reason);
    }

    [Fact]
    public void NoConfigForFirm_Approves()
    {
        var (check, _, _, _) = Build(new SubAccountRiskOptions());
        var d = check.Check(Ctx(new SubAccountId(Sub)));
        Assert.True(d.Approved);
    }

    [Fact]
    public void NotionalCap_Exceeded_Rejects()
    {
        var opts = new SubAccountRiskOptions
        {
            PerFirm = new()
            {
                [Firm] = new FirmSubAccountRiskOptions
                {
                    PerSubAccount = new() { [Sub] = new SubAccountRiskLimits { MaxNotional = 500m } },
                },
            },
        };
        var (check, _, _, _) = Build(opts);
        // 100 * 10 = 1000 > 500
        var d = check.Check(Ctx(new SubAccountId(Sub)));
        Assert.False(d.Approved);
        Assert.StartsWith("sub_account_limit_exceeded", d.Reason);
    }

    [Fact]
    public void PositionCap_Exceeded_Rejects()
    {
        var opts = new SubAccountRiskOptions
        {
            PerFirm = new()
            {
                [Firm] = new FirmSubAccountRiskOptions
                {
                    PerSubAccount = new() { [Sub] = new SubAccountRiskLimits { PositionLimit = 50 } },
                },
            },
        };
        var (check, _, _, pos) = Build(opts);
        // existing 30 net buy + new 25 buy = 55 > 50
        pos.ApplyFill(new EndClientId(Owner), new SubAccountId(Sub), Symbol, OrderSide.Buy, 30, 10m);
        var d = check.Check(Ctx(new SubAccountId(Sub), qty: 25));
        Assert.False(d.Approved);
        Assert.Contains("position", d.Reason);
    }

    [Fact]
    public void PerFirmDefault_AppliesWhenSubAccountNotListed()
    {
        var opts = new SubAccountRiskOptions
        {
            PerFirm = new()
            {
                [Firm] = new FirmSubAccountRiskOptions
                {
                    Default = new SubAccountRiskLimits { MaxNotional = 100m },
                },
            },
        };
        var (check, _, _, _) = Build(opts);
        var d = check.Check(Ctx(new SubAccountId(Sub)));
        Assert.False(d.Approved);
    }
}
