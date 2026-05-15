using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Checks;
using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests.RiskQ12;

public class Q12RiskChecksTests
{
    private const string Owner = "alice";
    private const string Firm = "FIRM";
    private const string Symbol = "PETR4";

    private static IOptionsMonitor<RiskOptions> Wrap(RiskOptions o) =>
        new StaticOptionsMonitor<RiskOptions>(o);

    // ---- StopTriggerCheck ----

    private sealed class StubRefPrice : IReferencePrice
    {
        private readonly Dictionary<string, decimal> _table;
        public StubRefPrice(params (string Symbol, decimal Price)[] entries) =>
            _table = entries.ToDictionary(e => e.Symbol, e => e.Price, StringComparer.OrdinalIgnoreCase);
        public bool TryGet(string symbol, out decimal price) => _table.TryGetValue(symbol, out price);
    }

    private static RiskContext StopCtx(OrderSide side, OrderType type,
        decimal? stopPrice, decimal? price = null, string symbol = Symbol) =>
        new(new EndClientId(Owner), Firm, symbol, side, type, 100, price,
            StopPrice: stopPrice);

    [Fact]
    public void StopTrigger_Skips_NonStop_Orders()
    {
        var check = new StopTriggerCheck(new StubRefPrice());
        var ctx = new RiskContext(new EndClientId(Owner), Firm, Symbol,
            OrderSide.Buy, OrderType.Limit, 100, 10m);
        Assert.True(check.Check(ctx).Approved);
    }

    [Fact]
    public void StopTrigger_BuyStopLoss_Above_Ref_Approves()
    {
        var check = new StopTriggerCheck(new StubRefPrice((Symbol, 20m)));
        var ctx = StopCtx(OrderSide.Buy, OrderType.StopLoss, stopPrice: 22m);
        Assert.True(check.Check(ctx).Approved);
    }

    [Fact]
    public void StopTrigger_BuyStopLoss_Below_Ref_Rejects()
    {
        var check = new StopTriggerCheck(new StubRefPrice((Symbol, 20m)));
        var ctx = StopCtx(OrderSide.Buy, OrderType.StopLoss, stopPrice: 18m);
        var d = check.Check(ctx);
        Assert.False(d.Approved);
        Assert.StartsWith("stop_trigger_invalid", d.Reason);
        Assert.Contains("side=buy", d.Reason);
    }

    [Fact]
    public void StopTrigger_SellStopLoss_Below_Ref_Approves()
    {
        var check = new StopTriggerCheck(new StubRefPrice((Symbol, 20m)));
        var ctx = StopCtx(OrderSide.Sell, OrderType.StopLoss, stopPrice: 18m);
        Assert.True(check.Check(ctx).Approved);
    }

    [Fact]
    public void StopTrigger_SellStopLoss_Above_Ref_Rejects()
    {
        var check = new StopTriggerCheck(new StubRefPrice((Symbol, 20m)));
        var ctx = StopCtx(OrderSide.Sell, OrderType.StopLoss, stopPrice: 22m);
        var d = check.Check(ctx);
        Assert.False(d.Approved);
        Assert.StartsWith("stop_trigger_invalid", d.Reason);
        Assert.Contains("side=sell", d.Reason);
    }

    [Fact]
    public void StopTrigger_Lenient_Skip_When_No_Reference()
    {
        // No entries — Lookup returns Missing. Relation skipped, approve.
        var check = new StopTriggerCheck(new StubRefPrice());
        var ctx = StopCtx(OrderSide.Buy, OrderType.StopLoss, stopPrice: 5m);
        Assert.True(check.Check(ctx).Approved);
    }

    [Fact]
    public void StopTrigger_BuyStopLimit_Limit_Below_Stop_Rejects()
    {
        var check = new StopTriggerCheck(new StubRefPrice((Symbol, 20m)));
        // Buy stop above ref OK, but limit below stop fails StopLimit relation.
        var ctx = StopCtx(OrderSide.Buy, OrderType.StopLimit, stopPrice: 25m, price: 24m);
        var d = check.Check(ctx);
        Assert.False(d.Approved);
        Assert.StartsWith("stop_limit_price_invalid", d.Reason);
    }

    [Fact]
    public void StopTrigger_SellStopLimit_Limit_Above_Stop_Rejects()
    {
        var check = new StopTriggerCheck(new StubRefPrice((Symbol, 20m)));
        var ctx = StopCtx(OrderSide.Sell, OrderType.StopLimit, stopPrice: 18m, price: 19m);
        var d = check.Check(ctx);
        Assert.False(d.Approved);
        Assert.StartsWith("stop_limit_price_invalid", d.Reason);
    }

    [Fact]
    public void StopTrigger_StopLimit_Happy_Path()
    {
        var check = new StopTriggerCheck(new StubRefPrice((Symbol, 20m)));
        var ctx = StopCtx(OrderSide.Buy, OrderType.StopLimit, stopPrice: 22m, price: 23m);
        Assert.True(check.Check(ctx).Approved);
    }

    // ---- IocFokMarketWithLeftoverCheck ----

    private static RiskContext TifCtx(OrderType type, TimeInForce tif) =>
        new(new EndClientId(Owner), Firm, Symbol,
            OrderSide.Buy, type, 100, type == OrderType.Limit ? 10m : (decimal?)null,
            TimeInForce: tif);

    [Fact]
    public void IocFokLeftover_Limit_Day_Approves()
    {
        var check = new IocFokMarketWithLeftoverCheck();
        Assert.True(check.Check(TifCtx(OrderType.Limit, TimeInForce.Day)).Approved);
    }

    [Fact]
    public void IocFokLeftover_MarketWithLeftover_Day_Approves()
    {
        var check = new IocFokMarketWithLeftoverCheck();
        Assert.True(check.Check(TifCtx(OrderType.MarketWithLeftover, TimeInForce.Day)).Approved);
    }

    [Fact]
    public void IocFokLeftover_MarketWithLeftover_IOC_Rejects()
    {
        var check = new IocFokMarketWithLeftoverCheck();
        var d = check.Check(TifCtx(OrderType.MarketWithLeftover, TimeInForce.IOC));
        Assert.False(d.Approved);
        Assert.StartsWith("tif_incompatible_with_market_with_leftover", d.Reason);
    }

    [Fact]
    public void IocFokLeftover_MarketWithLeftover_FOK_Rejects()
    {
        var check = new IocFokMarketWithLeftoverCheck();
        var d = check.Check(TifCtx(OrderType.MarketWithLeftover, TimeInForce.FOK));
        Assert.False(d.Approved);
        Assert.StartsWith("tif_incompatible_with_market_with_leftover", d.Reason);
    }

    // ---- GoodForAuctionPhaseCheck ----

    private sealed class FakePhaseProvider : IPhaseProvider
    {
        public TradingPhase Phase { get; set; } = TradingPhase.Open;
        public TradingPhase GetPhase(string symbol) => Phase;
    }

    private static RiskContext GfaCtx(TimeInForce tif = TimeInForce.GoodForAuction) =>
        new(new EndClientId(Owner), Firm, Symbol,
            OrderSide.Buy, OrderType.Limit, 100, 10m, TimeInForce: tif);

    [Theory]
    [InlineData(TradingPhase.OpeningCall)]
    [InlineData(TradingPhase.FinalClosingCall)]
    public void Gfa_AuctionPhases_Approve(TradingPhase phase)
    {
        var phases = new FakePhaseProvider { Phase = phase };
        Assert.True(new GoodForAuctionPhaseCheck(phases).Check(GfaCtx()).Approved);
    }

    [Theory]
    [InlineData(TradingPhase.Reserved)]
    [InlineData(TradingPhase.Open)]
    [InlineData(TradingPhase.Close)]
    [InlineData(TradingPhase.Unknown)]
    public void Gfa_NonAuctionPhases_Reject(TradingPhase phase)
    {
        var phases = new FakePhaseProvider { Phase = phase };
        var d = new GoodForAuctionPhaseCheck(phases).Check(GfaCtx());
        Assert.False(d.Approved);
        Assert.StartsWith("gfa_outside_auction_phase", d.Reason);
        Assert.Contains($"phase={phase}", d.Reason);
    }

    [Fact]
    public void Gfa_NonGfaTif_Skipped_Even_In_Open()
    {
        var phases = new FakePhaseProvider { Phase = TradingPhase.Open };
        Assert.True(new GoodForAuctionPhaseCheck(phases).Check(GfaCtx(TimeInForce.Day)).Approved);
    }

    [Fact]
    public void NoPhaseProvider_Default_Stub_Returns_Open()
    {
        // Documents the stub posture: GFA always rejects until #257 wires the real provider.
        var stub = new NoPhaseProvider();
        Assert.Equal(TradingPhase.Open, stub.GetPhase("ANY"));
        var d = new GoodForAuctionPhaseCheck(stub).Check(GfaCtx());
        Assert.False(d.Approved);
    }

    // ---- GtdBoundsCheck ----

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static RiskContext GtdCtx(DateTimeOffset? gtd, TimeInForce tif = TimeInForce.GTD) =>
        new(new EndClientId(Owner), Firm, Symbol,
            OrderSide.Buy, OrderType.Limit, 100, 10m,
            TimeInForce: tif, GoodTillDate: gtd);

    [Fact]
    public void Gtd_NonGtd_Skipped()
    {
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var check = new GtdBoundsCheck(Wrap(new RiskOptions()), clock);
        Assert.True(check.Check(GtdCtx(null, TimeInForce.Day)).Approved);
    }

    [Fact]
    public void Gtd_NowMinus1s_Rejects()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var check = new GtdBoundsCheck(Wrap(new RiskOptions()), new FixedClock(now));
        var d = check.Check(GtdCtx(now.AddSeconds(-1)));
        Assert.False(d.Approved);
        Assert.StartsWith("gtd_invalid", d.Reason);
        Assert.Contains("maxHorizonDays=30", d.Reason);
    }

    [Fact]
    public void Gtd_NowPlus1s_Approves()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var check = new GtdBoundsCheck(Wrap(new RiskOptions()), new FixedClock(now));
        Assert.True(check.Check(GtdCtx(now.AddSeconds(1))).Approved);
    }

    [Fact]
    public void Gtd_AtHorizon_Approves()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var opts = new RiskOptions { MaxGtdHorizon = TimeSpan.FromDays(30) };
        var check = new GtdBoundsCheck(Wrap(opts), new FixedClock(now));
        Assert.True(check.Check(GtdCtx(now.AddDays(30))).Approved);
    }

    [Fact]
    public void Gtd_BeyondHorizon_Rejects()
    {
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var opts = new RiskOptions { MaxGtdHorizon = TimeSpan.FromDays(30) };
        var check = new GtdBoundsCheck(Wrap(opts), new FixedClock(now));
        var d = check.Check(GtdCtx(now.AddDays(30).AddSeconds(1)));
        Assert.False(d.Approved);
        Assert.StartsWith("gtd_invalid", d.Reason);
    }

    [Fact]
    public void Gtd_Now_Equal_Expiry_Rejects()
    {
        // Boundary: expiry == now is "not strictly in the future" — reject.
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var check = new GtdBoundsCheck(Wrap(new RiskOptions()), new FixedClock(now));
        Assert.False(check.Check(GtdCtx(now)).Approved);
    }
}
