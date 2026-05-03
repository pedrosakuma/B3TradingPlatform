using B3.Trading.Application;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Checks;
using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests;

public class RiskPipelineTests
{
    private static IOptionsMonitor<RiskOptions> Wrap(RiskOptions o) => new StaticOptionsMonitor<RiskOptions>(o);

    private static RiskContext Ctx(
        string owner = "alice", string firm = "default", string symbol = "PETR4",
        OrderSide side = OrderSide.Buy, OrderType type = OrderType.Limit,
        long qty = 100, decimal? price = 30m) =>
        new(new EndClientId(owner), firm, symbol, side, type, qty, price);

    [Fact]
    public void Pipeline_RunsChecksInOrderAndShortCircuits()
    {
        var trace = new List<string>();
        var checks = new IRiskCheck[]
        {
            new SpyCheck("c", 300, _ => RiskDecision.Approve, trace),
            new SpyCheck("a", 100, _ => RiskDecision.Reject("nope"), trace),
            new SpyCheck("b", 200, _ => RiskDecision.Approve, trace),
        };
        var pipeline = new RiskPipeline(checks);

        Assert.Equal(new[] { "a", "b", "c" }, pipeline.CheckOrder);
        var d = pipeline.Evaluate(Ctx());
        Assert.False(d.Approved);
        Assert.Equal("nope", d.Reason);
        Assert.Equal(new[] { "a" }, trace); // short-circuited after 'a'
    }

    [Fact]
    public void Pipeline_AllApprove_ReturnsApprove()
    {
        var pipeline = new RiskPipeline(new IRiskCheck[]
        {
            new SpyCheck("a", 1, _ => RiskDecision.Approve, new()),
            new SpyCheck("b", 2, _ => RiskDecision.Approve, new()),
        });
        Assert.True(pipeline.Evaluate(Ctx()).Approved);
    }

    [Fact]
    public void KillSwitch_PerEndClient_BlocksThenRevives()
    {
        var ks = new KillSwitchService();
        var check = new KillSwitchCheck(ks);
        Assert.True(check.Check(Ctx()).Approved);
        ks.KillEndClient(new EndClientId("alice"));
        Assert.False(check.Check(Ctx()).Approved);
        ks.ReviveEndClient(new EndClientId("alice"));
        Assert.True(check.Check(Ctx()).Approved);
    }

    [Fact]
    public void KillSwitch_PerFirm_Blocks()
    {
        var ks = new KillSwitchService();
        var check = new KillSwitchCheck(ks);
        ks.KillFirm("default");
        var d = check.Check(Ctx());
        Assert.False(d.Approved);
        Assert.Contains("firm", d.Reason);
    }

    [Fact]
    public void MaxQuantity_RejectsOverflow_ApprovesUnderCap()
    {
        var opts = Wrap(new RiskOptions { Default = new RiskLimits { MaxQuantity = 100 } });
        var check = new MaxQuantityCheck(opts);
        Assert.True(check.Check(Ctx(qty: 100)).Approved);
        Assert.False(check.Check(Ctx(qty: 101)).Approved);
    }

    [Fact]
    public void MaxQuantity_PerEndClientOverridesDefault()
    {
        var opts = Wrap(new RiskOptions
        {
            Default = new RiskLimits { MaxQuantity = 1000 },
            PerEndClient = { ["alice"] = new RiskLimits { MaxQuantity = 10 } },
        });
        var check = new MaxQuantityCheck(opts);
        Assert.False(check.Check(Ctx(qty: 50)).Approved);
        Assert.True(check.Check(Ctx(owner: "bob", qty: 50)).Approved);
    }

    [Fact]
    public void MaxQuantity_PerFirm_AppliesWhenEndClientNotConfigured()
    {
        var opts = Wrap(new RiskOptions
        {
            Default = new RiskLimits { MaxQuantity = 1000 },
            PerFirm = { ["broker-a"] = new RiskLimits { MaxQuantity = 25 } },
        });
        var check = new MaxQuantityCheck(opts);
        Assert.False(check.Check(Ctx(firm: "broker-a", qty: 26)).Approved);
        Assert.True(check.Check(Ctx(firm: "broker-a", qty: 25)).Approved);
        // A firm without a per-firm cap falls back to default.
        Assert.True(check.Check(Ctx(firm: "broker-b", qty: 999)).Approved);
    }

    [Fact]
    public void MaxQuantity_PerEndClient_BeatsPerFirm()
    {
        var opts = Wrap(new RiskOptions
        {
            Default = new RiskLimits { MaxQuantity = 1000 },
            PerFirm = { ["broker-a"] = new RiskLimits { MaxQuantity = 25 } },
            PerEndClient = { ["alice"] = new RiskLimits { MaxQuantity = 5 } },
        });
        var check = new MaxQuantityCheck(opts);
        // Alice is capped at 5, even though her firm allows 25.
        Assert.False(check.Check(Ctx(firm: "broker-a", qty: 10)).Approved);
        // Bob has no per-end-client cap, so the firm limit applies.
        Assert.True(check.Check(Ctx(owner: "bob", firm: "broker-a", qty: 25)).Approved);
        Assert.False(check.Check(Ctx(owner: "bob", firm: "broker-a", qty: 26)).Approved);
    }

    [Fact]
    public void MaxQuantity_PerFirm_BeatsPerSymbol()
    {
        var opts = Wrap(new RiskOptions
        {
            Default = new RiskLimits { MaxQuantity = 1000 },
            PerFirm = { ["broker-a"] = new RiskLimits { MaxQuantity = 25 } },
            PerSymbol = { ["PETR4"] = new RiskLimits { MaxQuantity = 50 } },
        });
        var check = new MaxQuantityCheck(opts);
        // Firm wins over symbol on the same field.
        Assert.False(check.Check(Ctx(firm: "broker-a", qty: 26)).Approved);
        // A different firm with no per-firm entry falls through to symbol.
        Assert.True(check.Check(Ctx(firm: "broker-b", qty: 50)).Approved);
        Assert.False(check.Check(Ctx(firm: "broker-b", qty: 51)).Approved);
    }

    [Fact]
    public void Resolver_SkipsPerFirm_WhenFirmIdIsBlank()
    {
        var opts = new RiskOptions
        {
            Default = new RiskLimits { MaxQuantity = 999 },
            PerFirm = { ["broker-a"] = new RiskLimits { MaxQuantity = 1 } },
        };
        // Blank firm id should NOT match any per-firm entry.
        var resolved = RiskLimitsResolver.Resolve(
            opts, endClient: "x", firmId: "", symbol: "PETR4", l => l.MaxQuantity);
        Assert.Equal(999, resolved);
    }

    [Fact]
    public void HotReload_NewLimitsTakeEffectOnNextCheck()
    {
        // Start permissive, then tighten. With IOptionsMonitor the
        // change is observed on the very next Check call — no rebuild
        // of the check or the pipeline required.
        var monitor = new StaticOptionsMonitor<RiskOptions>(
            new RiskOptions { Default = new RiskLimits { MaxQuantity = 1000 } });
        var check = new MaxQuantityCheck(monitor);

        Assert.True(check.Check(Ctx(qty: 500)).Approved);

        monitor.Set(new RiskOptions { Default = new RiskLimits { MaxQuantity = 100 } });
        Assert.False(check.Check(Ctx(qty: 500)).Approved);
    }

    [Fact]
    public void ResolveAll_FoldsAllFieldsAcrossPrecedence()
    {
        var opts = new RiskOptions
        {
            Default = new RiskLimits { MaxQuantity = 1000, MaxNotional = 999_999m, PriceCollarPercent = 10m, PositionLimit = 5000 },
            PerFirm = { ["broker-a"] = new RiskLimits { MaxQuantity = 50 } },
            PerEndClient = { ["alice"] = new RiskLimits { PositionLimit = 100 } },
            PerSymbol = { ["PETR4"] = new RiskLimits { PriceCollarPercent = 2m } },
        };

        var resolved = RiskLimitsResolver.ResolveAll(
            opts, endClient: "alice", firmId: "broker-a", symbol: "PETR4");

        Assert.Equal(50, resolved.MaxQuantity);                // from PerFirm
        Assert.Equal(999_999m, resolved.MaxNotional);          // from Default
        Assert.Equal(2m, resolved.PriceCollarPercent);         // from PerSymbol
        Assert.Equal(100, resolved.PositionLimit);             // from PerEndClient
    }

    [Fact]
    public void MaxNotional_RejectsOverNotional()
    {
        var opts = Wrap(new RiskOptions { Default = new RiskLimits { MaxNotional = 1000m } });
        var check = new MaxNotionalCheck(opts);
        Assert.True(check.Check(Ctx(qty: 10, price: 100m)).Approved);   // 1000
        Assert.False(check.Check(Ctx(qty: 11, price: 100m)).Approved);  // 1100
    }

    [Fact]
    public void MaxNotional_NullPriceIsApproved()
    {
        var opts = Wrap(new RiskOptions { Default = new RiskLimits { MaxNotional = 1m } });
        Assert.True(new MaxNotionalCheck(opts).Check(Ctx(price: null, type: OrderType.Market)).Approved);
    }

    [Fact]
    public void PriceCollar_RejectsOutsideBand()
    {
        var refPx = new StubRef(("PETR4", 30m));
        var opts = Wrap(new RiskOptions { Default = new RiskLimits { PriceCollarPercent = 10m } });
        var check = new PriceCollarCheck(opts, refPx);
        Assert.True(check.Check(Ctx(price: 33m)).Approved);   // +10% boundary
        Assert.True(check.Check(Ctx(price: 27m)).Approved);   // -10% boundary
        Assert.False(check.Check(Ctx(price: 33.01m)).Approved);
        Assert.False(check.Check(Ctx(price: 26.99m)).Approved);
    }

    [Fact]
    public void PriceCollar_NoReference_Approves()
    {
        var refPx = new StubRef();
        var opts = Wrap(new RiskOptions { Default = new RiskLimits { PriceCollarPercent = 1m } });
        Assert.True(new PriceCollarCheck(opts, refPx).Check(Ctx(price: 100m)).Approved);
    }

    [Fact]
    public void PositionLimit_RejectsWhenProjectedExceeds()
    {
        var positions = new PositionKeeper();
        positions.ApplyFill(new EndClientId("alice"), "PETR4", OrderSide.Buy, 400, 30m);
        var opts = Wrap(new RiskOptions { Default = new RiskLimits { PositionLimit = 500 } });
        var check = new PositionLimitCheck(opts, positions);
        Assert.True(check.Check(Ctx(qty: 100, side: OrderSide.Buy)).Approved);   // 400+100=500
        Assert.False(check.Check(Ctx(qty: 101, side: OrderSide.Buy)).Approved);  // 501
        Assert.True(check.Check(Ctx(qty: 900, side: OrderSide.Sell)).Approved);  // |400-900|=500
    }

    private sealed class SpyCheck : IRiskCheck
    {
        private readonly Func<RiskContext, RiskDecision> _impl;
        private readonly List<string> _trace;
        public SpyCheck(string name, int order, Func<RiskContext, RiskDecision> impl, List<string> trace)
        { Name = name; Order = order; _impl = impl; _trace = trace; }
        public int Order { get; }
        public string Name { get; }
        public RiskDecision Check(RiskContext ctx) { _trace.Add(Name); return _impl(ctx); }
    }

    private sealed class StubRef : IReferencePrice
    {
        private readonly Dictionary<string, decimal> _prices;
        public StubRef(params (string, decimal)[] entries) =>
            _prices = entries.ToDictionary(e => e.Item1, e => e.Item2, StringComparer.OrdinalIgnoreCase);
        public bool TryGet(string symbol, out decimal price) => _prices.TryGetValue(symbol, out price);
    }
}
