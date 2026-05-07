using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Checks;
using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests;

public class StaleReferencePriceCheckTests
{
    private static IOptionsMonitor<RiskOptions> Wrap(RiskOptions o) => new StaticOptionsMonitor<RiskOptions>(o);

    private static RiskContext Ctx(OrderType type = OrderType.Market, decimal? price = null) =>
        new(new EndClientId("alice"), "default", "PETR4", OrderSide.Buy, type, 100, price);

    private sealed class StubRef : IReferencePrice
    {
        private readonly ReferencePriceLookup _lookup;
        public StubRef(ReferencePriceLookup lookup) => _lookup = lookup;
        public bool TryGet(string symbol, out decimal price)
        {
            price = _lookup.Price;
            return _lookup.Found;
        }
        public ReferencePriceLookup Lookup(string symbol) => _lookup;
    }

    [Fact]
    public void Limit_Bypassed_RegardlessOfRefSource()
    {
        var check = new StaleReferencePriceCheck(
            Wrap(new RiskOptions()),
            new StubRef(ReferencePriceLookup.NotFound));

        var d = check.Check(Ctx(type: OrderType.Limit, price: 30m));
        Assert.True(d.Approved);
    }

    [Fact]
    public void Market_LiveSource_Approves()
    {
        var check = new StaleReferencePriceCheck(
            Wrap(new RiskOptions()),
            new StubRef(new ReferencePriceLookup(30m, ReferencePriceSource.Live)));

        Assert.True(check.Check(Ctx()).Approved);
    }

    [Fact]
    public void Market_DefaultPolicy_RejectsFallback()
    {
        var check = new StaleReferencePriceCheck(
            Wrap(new RiskOptions()),
            new StubRef(new ReferencePriceLookup(30m, ReferencePriceSource.Fallback)));

        var d = check.Check(Ctx());
        Assert.False(d.Approved);
        Assert.Contains("not live", d.Reason);
    }

    [Fact]
    public void Market_DefaultPolicy_RejectsMissing()
    {
        var check = new StaleReferencePriceCheck(
            Wrap(new RiskOptions()),
            new StubRef(ReferencePriceLookup.NotFound));

        var d = check.Check(Ctx());
        Assert.False(d.Approved);
        Assert.Contains("no reference price", d.Reason);
    }

    [Fact]
    public void Market_OptOutPerFirm_AcceptsFallback_ButStillRejectsMissing()
    {
        var opts = new RiskOptions
        {
            PerFirm = { ["default"] = new RiskLimits { MarketRequiresLiveRef = false } },
        };
        var fallback = new StaleReferencePriceCheck(
            Wrap(opts),
            new StubRef(new ReferencePriceLookup(30m, ReferencePriceSource.Fallback)));
        Assert.True(fallback.Check(Ctx()).Approved);

        var missing = new StaleReferencePriceCheck(
            Wrap(opts),
            new StubRef(ReferencePriceLookup.NotFound));
        Assert.False(missing.Check(Ctx()).Approved);
    }

    [Fact]
    public void Order_IsBeforeCollar()
    {
        var check = new StaleReferencePriceCheck(Wrap(new RiskOptions()),
            new StubRef(ReferencePriceLookup.NotFound));
        Assert.True(check.Order < 300, "must run before PriceCollarCheck (Order=300)");
    }
}

public class OrderTypeAllowedCheckTests
{
    private static IOptionsMonitor<RiskOptions> Wrap(RiskOptions o) => new StaticOptionsMonitor<RiskOptions>(o);

    private static RiskContext Ctx(OrderType type, string firm = "default") =>
        new(new EndClientId("alice"), firm, "PETR4", OrderSide.Buy, type, 100, type == OrderType.Limit ? 30m : null);

    [Fact]
    public void NullList_ApprovesEverything()
    {
        var check = new OrderTypeAllowedCheck(Wrap(new RiskOptions()));
        Assert.True(check.Check(Ctx(OrderType.Limit)).Approved);
        Assert.True(check.Check(Ctx(OrderType.Market)).Approved);
    }

    [Fact]
    public void EmptyList_ApprovesEverything()
    {
        var opts = new RiskOptions
        {
            Default = new RiskLimits { AllowedOrderTypes = new List<string>() },
        };
        var check = new OrderTypeAllowedCheck(Wrap(opts));
        Assert.True(check.Check(Ctx(OrderType.Market)).Approved);
    }

    [Fact]
    public void Whitelist_ApprovesMember()
    {
        var opts = new RiskOptions
        {
            Default = new RiskLimits { AllowedOrderTypes = new List<string> { "Limit" } },
        };
        var check = new OrderTypeAllowedCheck(Wrap(opts));
        Assert.True(check.Check(Ctx(OrderType.Limit)).Approved);
    }

    [Fact]
    public void Whitelist_RejectsNonMember()
    {
        var opts = new RiskOptions
        {
            Default = new RiskLimits { AllowedOrderTypes = new List<string> { "Limit" } },
        };
        var check = new OrderTypeAllowedCheck(Wrap(opts));
        var d = check.Check(Ctx(OrderType.Market));
        Assert.False(d.Approved);
        Assert.Contains("not in allowed list", d.Reason);
    }

    [Fact]
    public void Whitelist_CaseInsensitive()
    {
        var opts = new RiskOptions
        {
            Default = new RiskLimits { AllowedOrderTypes = new List<string> { "limit", "MARKET" } },
        };
        var check = new OrderTypeAllowedCheck(Wrap(opts));
        Assert.True(check.Check(Ctx(OrderType.Limit)).Approved);
        Assert.True(check.Check(Ctx(OrderType.Market)).Approved);
    }

    [Fact]
    public void PerFirm_OverridesDefault()
    {
        var opts = new RiskOptions
        {
            Default = new RiskLimits { AllowedOrderTypes = new List<string> { "Limit", "Market" } },
            PerFirm =
            {
                ["strict"] = new RiskLimits { AllowedOrderTypes = new List<string> { "Limit" } },
            },
        };
        var check = new OrderTypeAllowedCheck(Wrap(opts));

        Assert.True(check.Check(Ctx(OrderType.Market, firm: "default")).Approved);
        Assert.False(check.Check(Ctx(OrderType.Market, firm: "strict")).Approved);
    }

    [Fact]
    public void Order_IsEarly()
    {
        var check = new OrderTypeAllowedCheck(Wrap(new RiskOptions()));
        Assert.True(check.Order > 0 && check.Order < 100,
            "should run after KillSwitch (0) but before per-instrument checks (>=20)");
    }
}
