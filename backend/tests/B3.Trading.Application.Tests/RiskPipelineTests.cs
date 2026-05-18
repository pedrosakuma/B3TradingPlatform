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
    public void SymbolHalted_BlocksThenResumes()
    {
        var halts = new SymbolHaltService();
        var check = new SymbolHaltedCheck(halts);
        Assert.True(check.Check(Ctx(symbol: "PETR4")).Approved);
        halts.Halt("PETR4");
        var d = check.Check(Ctx(symbol: "PETR4"));
        Assert.False(d.Approved);
        Assert.Contains("halted", d.Reason);
        Assert.True(check.Check(Ctx(symbol: "VALE3")).Approved); // unrelated symbol unaffected
        halts.Resume("PETR4");
        Assert.True(check.Check(Ctx(symbol: "PETR4")).Approved);
    }

    [Fact]
    public void SymbolHalted_IsCaseInsensitive()
    {
        var halts = new SymbolHaltService();
        halts.Halt("petr4");
        Assert.True(halts.IsHalted("PETR4"));
        Assert.False(new SymbolHaltedCheck(halts).Check(Ctx(symbol: "PETR4")).Approved);
    }

    [Fact]
    public void SymbolHaltService_RestoreReplacesPreviousState()
    {
        var halts = new SymbolHaltService();
        halts.Halt("PETR4");
        halts.Restore(new[] { "VALE3", "ITUB4" });
        Assert.False(halts.IsHalted("PETR4")); // gone
        Assert.True(halts.IsHalted("VALE3"));
        Assert.True(halts.IsHalted("ITUB4"));
    }

    // ── SessionPhase (#108) ────────────────────────────────────────────────

    [Fact]
    public void SessionPhase_DefaultContinuous_ApprovesEverything()
    {
        var svc = new SessionPhaseService(); // default = Continuous
        var check = new SessionPhaseCheck(svc);
        Assert.True(check.Check(Ctx(type: OrderType.Limit)).Approved);
        Assert.True(check.Check(Ctx(type: OrderType.Market, price: null)).Approved);
    }

    [Fact]
    public void SessionPhase_Closed_RejectsAll()
    {
        var svc = new SessionPhaseService(SessionPhase.Closed);
        var check = new SessionPhaseCheck(svc);
        var lim = check.Check(Ctx(type: OrderType.Limit));
        var mkt = check.Check(Ctx(type: OrderType.Market, price: null));
        Assert.False(lim.Approved);
        Assert.False(mkt.Approved);
        Assert.Contains("phase_not_allowed:closed", lim.Reason);
        Assert.Contains("phase_not_allowed:closed", mkt.Reason);
    }

    [Theory]
    [InlineData(SessionPhase.PreOpening)]
    [InlineData(SessionPhase.OpeningAuction)]
    [InlineData(SessionPhase.ClosingAuction)]
    public void SessionPhase_Auction_RejectsMarketAllowsLimit(SessionPhase phase)
    {
        var svc = new SessionPhaseService();
        svc.SetPhase("PETR4", phase);
        var check = new SessionPhaseCheck(svc);
        Assert.True(check.Check(Ctx(symbol: "PETR4", type: OrderType.Limit)).Approved);
        var mkt = check.Check(Ctx(symbol: "PETR4", type: OrderType.Market, price: null));
        Assert.False(mkt.Approved);
        Assert.Contains("phase_not_allowed:auction", mkt.Reason);
    }

    [Fact]
    public void SessionPhase_AfterHours_RejectsMarketAllowsLimit()
    {
        var svc = new SessionPhaseService();
        svc.SetPhase("PETR4", SessionPhase.AfterHours);
        var check = new SessionPhaseCheck(svc);
        Assert.True(check.Check(Ctx(symbol: "PETR4", type: OrderType.Limit)).Approved);
        var mkt = check.Check(Ctx(symbol: "PETR4", type: OrderType.Market, price: null));
        Assert.False(mkt.Approved);
        Assert.Contains("phase_not_allowed:after_hours", mkt.Reason);
    }

    [Fact]
    public void SessionPhase_PerSymbolOverrideWinsOverDefault()
    {
        // Default closed; PETR4 explicitly continuous → only PETR4 trades.
        var svc = new SessionPhaseService(SessionPhase.Closed);
        svc.SetPhase("PETR4", SessionPhase.Continuous);
        var check = new SessionPhaseCheck(svc);
        Assert.True(check.Check(Ctx(symbol: "PETR4")).Approved);
        Assert.False(check.Check(Ctx(symbol: "VALE3")).Approved);
    }

    [Fact]
    public void SessionPhase_ClearOverride_FallsBackToDefault()
    {
        var svc = new SessionPhaseService(SessionPhase.Closed);
        svc.SetPhase("PETR4", SessionPhase.Continuous);
        Assert.Equal(SessionPhase.Continuous, svc.GetPhase("PETR4"));
        Assert.True(svc.ClearPhase("PETR4"));
        Assert.Equal(SessionPhase.Closed, svc.GetPhase("PETR4"));
    }

    [Fact]
    public void SessionPhase_IsCaseInsensitive()
    {
        var svc = new SessionPhaseService();
        svc.SetPhase("petr4", SessionPhase.OpeningAuction);
        Assert.Equal(SessionPhase.OpeningAuction, svc.GetPhase("PETR4"));
    }

    [Fact]
    public void SessionPhase_Restore_ReplacesAllState()
    {
        var svc = new SessionPhaseService(SessionPhase.Continuous);
        svc.SetPhase("PETR4", SessionPhase.OpeningAuction);
        svc.Restore(SessionPhase.Closed, new[]
        {
            new KeyValuePair<string, SessionPhase>("VALE3", SessionPhase.AfterHours),
        });
        Assert.Equal(SessionPhase.Closed, svc.DefaultPhase);
        Assert.Equal(SessionPhase.AfterHours, svc.GetPhase("VALE3"));
        Assert.Equal(SessionPhase.Closed, svc.GetPhase("PETR4")); // override gone, falls back
    }

    [Fact]
    public void SessionPhase_DefaultPhaseChange_AffectsUnoverriddenSymbols()
    {
        var svc = new SessionPhaseService(SessionPhase.Continuous);
        svc.SetPhase("PETR4", SessionPhase.AfterHours);
        svc.SetDefaultPhase(SessionPhase.Closed);
        var check = new SessionPhaseCheck(svc);
        // VALE3 has no override → uses new default Closed → reject.
        Assert.False(check.Check(Ctx(symbol: "VALE3")).Approved);
        // PETR4 retains explicit AfterHours override → limit ok.
        Assert.True(check.Check(Ctx(symbol: "PETR4", type: OrderType.Limit)).Approved);
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
    public void MinNotional_RejectsBelowFloor_ApprovesAtAndAbove()
    {
        var opts = Wrap(new RiskOptions { Default = new RiskLimits { MinNotional = 1000m } });
        var check = new MinNotionalCheck(opts);
        Assert.True(check.Check(Ctx(qty: 100, price: 30m)).Approved);    // 3000 ≥ 1000
        Assert.True(check.Check(Ctx(qty: 100, price: 10m)).Approved);    // 1000 == floor
        var d = check.Check(Ctx(qty: 10, price: 10m));                   // 100 < 1000
        Assert.False(d.Approved);
        Assert.Contains("min", d.Reason);
    }

    [Fact]
    public void MinNotional_NullPriceIsApproved()
    {
        // Market orders don't carry a price; min-notional cannot fire.
        var opts = Wrap(new RiskOptions { Default = new RiskLimits { MinNotional = 999_999m } });
        Assert.True(new MinNotionalCheck(opts).Check(Ctx(price: null, type: OrderType.Market)).Approved);
    }

    [Fact]
    public void MinNotional_NoFloorConfigured_IsApproved()
    {
        // Default semantics: permissive when unset everywhere.
        var opts = Wrap(new RiskOptions());
        Assert.True(new MinNotionalCheck(opts).Check(Ctx(qty: 1, price: 0.01m)).Approved);
    }

    [Fact]
    public void MinNotional_PerEndClient_OverridesDefault()
    {
        var opts = Wrap(new RiskOptions
        {
            Default = new RiskLimits { MinNotional = 100m },
            PerEndClient = { ["alice"] = new RiskLimits { MinNotional = 5000m } },
        });
        var check = new MinNotionalCheck(opts);
        // Notional 1000 passes default but trips alice's tighter floor.
        Assert.False(check.Check(Ctx(owner: "alice", qty: 100, price: 10m)).Approved);
        Assert.True(check.Check(Ctx(owner: "bob", qty: 100, price: 10m)).Approved);
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
    public void PriceCollar_AbsoluteAlone_RejectsOutsideBand()
    {
        var refPx = new StubRef(("PETR4", 30m));
        var opts = Wrap(new RiskOptions { Default = new RiskLimits { PriceCollarAbsolute = 0.50m } });
        var check = new PriceCollarCheck(opts, refPx);
        Assert.True(check.Check(Ctx(price: 30.50m)).Approved);   // upper boundary
        Assert.True(check.Check(Ctx(price: 29.50m)).Approved);   // lower boundary
        Assert.False(check.Check(Ctx(price: 30.51m)).Approved);
        Assert.False(check.Check(Ctx(price: 29.49m)).Approved);
    }

    [Fact]
    public void PriceCollar_PercentAndAbsolute_IntersectionWins()
    {
        // ref=30, pct=10% → [27, 33]; abs=0.50 → [29.50, 30.50].
        // intersection [29.50, 30.50] — absolute is the narrower band.
        var refPx = new StubRef(("PETR4", 30m));
        var opts = Wrap(new RiskOptions
        {
            Default = new RiskLimits { PriceCollarPercent = 10m, PriceCollarAbsolute = 0.50m }
        });
        var check = new PriceCollarCheck(opts, refPx);
        Assert.True(check.Check(Ctx(price: 30.50m)).Approved);
        Assert.False(check.Check(Ctx(price: 31m)).Approved);   // inside pct, outside abs
        Assert.False(check.Check(Ctx(price: 29m)).Approved);
    }

    [Fact]
    public void MinTickSize_RejectsNonMultiple()
    {
        var dir = BuildDirectory(specs: new() { ["PETR4"] = new() { TickSize = 0.01m } });
        var check = new MinTickSizeCheck(dir);
        Assert.True(check.Check(Ctx(price: 30.01m)).Approved);
        Assert.True(check.Check(Ctx(price: 30.00m)).Approved);
        Assert.False(check.Check(Ctx(price: 30.001m)).Approved);
    }

    [Fact]
    public void MinTickSize_NoSpec_Approves()
    {
        var dir = BuildDirectory();
        Assert.True(new MinTickSizeCheck(dir).Check(Ctx(price: 30.001m)).Approved);
    }

    [Fact]
    public void MinTickSize_MarketOrder_Approves()
    {
        var dir = BuildDirectory(specs: new() { ["PETR4"] = new() { TickSize = 0.01m } });
        Assert.True(new MinTickSizeCheck(dir).Check(Ctx(price: null, type: OrderType.Market)).Approved);
    }

    [Fact]
    public void MinLotSize_RejectsNonMultiple()
    {
        var dir = BuildDirectory(specs: new() { ["PETR4"] = new() { LotSize = 100L } });
        var check = new MinLotSizeCheck(dir);
        Assert.True(check.Check(Ctx(qty: 100)).Approved);
        Assert.True(check.Check(Ctx(qty: 300)).Approved);
        Assert.False(check.Check(Ctx(qty: 150)).Approved);
        Assert.False(check.Check(Ctx(qty: 1)).Approved);
    }

    [Fact]
    public void MinLotSize_NoSpec_Approves()
    {
        Assert.True(new MinLotSizeCheck(BuildDirectory()).Check(Ctx(qty: 7)).Approved);
    }

    private static SymbolDirectory BuildDirectory(
        Dictionary<string, ulong>? securityIds = null,
        Dictionary<string, InstrumentSpecOptions>? specs = null) =>
        new(new SymbolDirectoryOptions
        {
            SecurityIds = securityIds ?? new(StringComparer.OrdinalIgnoreCase),
            Specs = specs ?? new(StringComparer.OrdinalIgnoreCase),
        });

    [Fact]
    public void PositionLimit_RejectsWhenProjectedExceeds()
    {
        var positions = new PositionKeeper();
        positions.ApplyFill("default", new EndClientId("alice"), "PETR4", OrderSide.Buy, 400, 30m);
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
