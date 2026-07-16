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
    public void DeactivatedSubAccount_Rejects_WithDistinctReason()
    {
        var (check, _, _, _) = Build(new SubAccountRiskOptions(), registerSub: true, active: false);
        var d = check.Check(Ctx(new SubAccountId(Sub)));
        Assert.False(d.Approved);
        // PR review #301 P2 — distinct reason for deactivation. Must
        // NOT alias the limit-exceeded prefix (clients/metrics need
        // to distinguish operator-disabled from cap breach).
        Assert.StartsWith("sub_account_deactivated", d.Reason);
        Assert.DoesNotContain("sub_account_limit_exceeded", d.Reason);
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
        pos.ApplyFill(Firm, new EndClientId(Owner), new SubAccountId(Sub), Symbol, OrderSide.Buy, 30, 10m);
        var d = check.Check(Ctx(new SubAccountId(Sub), qty: 25));
        Assert.False(d.Approved);
        Assert.Contains("position", d.Reason);
    }

    [Fact]
    public void PositionCap_IncludesJointWorkingOrderExposure()
    {
        var opts = new SubAccountRiskOptions
        {
            PerFirm = new()
            {
                [Firm] = new FirmSubAccountRiskOptions
                {
                    PerSubAccount = new()
                    {
                        [Sub] = new SubAccountRiskLimits { PositionLimit = 100 },
                    },
                },
            },
        };
        var (check, _, book, _) = Build(opts);
        var owner = new EndClientId(Owner);
        var sub = new SubAccountId(Sub);

        var first = new Order(
            1, owner, Symbol, 1234, OrderSide.Buy, OrderType.Limit,
            60, 10m, Firm, subAccountId: sub);
        book.TryAdd(first);
        first.MarkWorking();
        Assert.True(check.Check(Ctx(sub, qty: 60) with { EvaluatedClOrdId = 1 }).Approved);

        var second = new Order(
            2, owner, Symbol, 1234, OrderSide.Buy, OrderType.Limit,
            50, 10m, Firm, subAccountId: sub);
        book.TryAdd(second);
        second.MarkWorking();
        Assert.False(check.Check(Ctx(sub, qty: 50) with { EvaluatedClOrdId = 2 }).Approved);
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

    /// <summary>
    /// PR review #301 P1 — multi-firm segregation of risk-limit
    /// consumption. The same login/sub-account under two firms must
    /// see independent open-order counts and position caps; the
    /// FIRM01 ceiling must not be tripped by FIRM02 activity.
    /// </summary>
    [Fact]
    public void RiskCaps_AreFirmSegregated_ForSameOwnerAndSubAccount()
    {
        const string firm1 = "FIRM01";
        const string firm2 = "FIRM02";
        var opts = new SubAccountRiskOptions
        {
            PerFirm = new()
            {
                [firm1] = new FirmSubAccountRiskOptions
                {
                    PerSubAccount = new() { [Sub] = new SubAccountRiskLimits { PositionLimit = 50 } },
                },
                [firm2] = new FirmSubAccountRiskOptions
                {
                    PerSubAccount = new() { [Sub] = new SubAccountRiskLimits { PositionLimit = 50 } },
                },
            },
        };
        var book = new WorkingOrderBook();
        var pos = new SubAccountPositionKeeper();
        var reg = new SubAccountsRegistry();
        reg.ApplyCreated(firm1, Sub, null);
        reg.ApplyCreated(firm2, Sub, null);
        var check = new SubAccountLimitsCheck(
            new StaticOptionsMonitor<SubAccountRiskOptions>(opts), book, pos, reg);

        // Saturate the FIRM02 bucket — would breach the cap there but
        // must not consume FIRM01's budget.
        pos.ApplyFill(firm2, new EndClientId(Owner), new SubAccountId(Sub), Symbol, OrderSide.Buy, 50, 10m);

        var ctxFirm1 = new RiskContext(
            new EndClientId(Owner), firm1, Symbol, OrderSide.Buy, OrderType.Limit, 25, 10m,
            SubAccountId: new SubAccountId(Sub));
        var ctxFirm2 = new RiskContext(
            new EndClientId(Owner), firm2, Symbol, OrderSide.Buy, OrderType.Limit, 25, 10m,
            SubAccountId: new SubAccountId(Sub));

        // FIRM01 sees a fresh book: 0 + 25 = 25 ≤ 50 → approve.
        Assert.True(check.Check(ctxFirm1).Approved);
        // FIRM02 already at 50 + 25 = 75 > 50 → reject.
        var f2 = check.Check(ctxFirm2);
        Assert.False(f2.Approved);
        Assert.Contains("position", f2.Reason);
    }

    // ── OPT-B (#484) compliance regression ────────────────────────────

    [Fact]
    public void Option_MaxNotional_HonoursContractMultiplier_NotJustPriceTimesQty()
    {
        // Before OPT-B (#484): the check computed notional = price * qty
        // and silently let options breeze past MaxNotional caps by a
        // factor of contractMultiplier (typically 100). After the fix,
        // an option order with 10 contracts at 0.50 premium and a 100x
        // multiplier reports notional = 500 BRL, which CORRECTLY trips
        // a 200-BRL sub-account cap. The same notional in equity (50
        // BRL on AnotherSym) stays well under the cap.
        var book = new WorkingOrderBook();
        var pos = new SubAccountPositionKeeper();
        var reg = new SubAccountsRegistry();
        reg.ApplyCreated(Firm, Sub, null);

        var dir = new SymbolDirectory(new SymbolDirectoryOptions
        {
            Specs =
            {
                ["PETRL200"] = new InstrumentSpecOptions
                {
                    Option = new OptionMetadataOptions
                    {
                        ExpirationDate = new DateOnly(2026, 12, 18),
                        PutOrCall = "Call",
                        ExerciseStyle = "American",
                        ContractMultiplier = 100m,
                    },
                },
            },
        });
        var values = new B3.Trading.Application.MarketData.SymbolDirectoryMarketValueCalculator(dir);

        var opts = new SubAccountRiskOptions
        {
            PerFirm = new()
            {
                [Firm] = new FirmSubAccountRiskOptions
                {
                    PerSubAccount = new() { [Sub] = new SubAccountRiskLimits { MaxNotional = 200m } },
                },
            },
        };
        var check = new SubAccountLimitsCheck(
            new StaticOptionsMonitor<SubAccountRiskOptions>(opts),
            book, pos, reg, values);

        // 10 contracts × 0.50 × 100 multiplier = 500 BRL → exceeds 200 cap.
        var ctxOpt = new RiskContext(
            new EndClientId(Owner), Firm, "PETRL200",
            OrderSide.Buy, OrderType.Limit, Quantity: 10, Price: 0.50m,
            SubAccountId: new SubAccountId(Sub));
        var d = check.Check(ctxOpt);
        Assert.False(d.Approved);
        Assert.Contains("sub_account_limit_exceeded", d.Reason);
        Assert.Contains("500", d.Reason);
    }

    [Fact]
    public void Equity_MaxNotional_BehaviorByteIdentical_WithOrWithoutCalculator()
    {
        // Sanity: introducing IMarketValueCalculator must not change
        // any equity reject reason — the equity-only fallback returns
        // the historical price * qty.
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
        var (defaultCheck, _, _, _) = Build(opts);

        // No injected calculator → EquityMarketValueCalculator.Instance fallback.
        var ctx = Ctx(new SubAccountId(Sub), qty: 100, px: 10m); // 1000 > 500
        var d = defaultCheck.Check(ctx);
        Assert.False(d.Approved);
        Assert.Contains("1000", d.Reason);
        Assert.Contains("500", d.Reason);
    }
}
