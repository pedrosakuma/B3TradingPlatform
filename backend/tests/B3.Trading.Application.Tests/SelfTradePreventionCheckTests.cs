using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Checks;
using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests;

public class SelfTradePreventionCheckTests
{
    private const string Firm = "FIRM01";
    private const string Owner = "alice";
    private const string Symbol = "PETR4";
    private static readonly EndClientId OwnerId = new(Owner);

    private static IOptionsMonitor<RiskOptions> Wrap(RiskOptions o) =>
        new StaticOptionsMonitor<RiskOptions>(o);

    private static (SelfTradePreventionCheck check, WorkingOrderBook book) Build(RiskOptions? opts = null)
    {
        var book = new WorkingOrderBook();
        var check = new SelfTradePreventionCheck(Wrap(opts ?? new RiskOptions()), book);
        return (check, book);
    }

    private static Order Make(ulong clOrdId, OrderSide side, decimal? price, OrderType type = OrderType.Limit,
                              long qty = 100, string owner = Owner, string symbol = Symbol) =>
        new(clOrdId, new EndClientId(owner), symbol, 4321UL, side, type, qty, price, Firm);

    private static RiskContext Ctx(OrderSide side, decimal? price, OrderType type = OrderType.Limit,
                                   long qty = 100, string owner = Owner, string symbol = Symbol) =>
        new(new EndClientId(owner), Firm, symbol, side, type, qty, price);

    [Fact]
    public void NoOpenOrders_Approves()
    {
        var (check, _) = Build();
        Assert.True(check.Check(Ctx(OrderSide.Buy, 30m)).Approved);
    }

    [Fact]
    public void Buy_CrossesOwnSell_Rejected()
    {
        var (check, book) = Build();
        Assert.True(book.TryAdd(Make(1, OrderSide.Sell, 32.40m)));
        var decision = check.Check(Ctx(OrderSide.Buy, 32.50m));
        Assert.False(decision.Approved);
        Assert.Contains("self_trade_prevention", decision.Reason);
        Assert.Contains("clOrdId=1", decision.Reason);
    }

    [Fact]
    public void Sell_CrossesOwnBuy_Rejected()
    {
        var (check, book) = Build();
        Assert.True(book.TryAdd(Make(7, OrderSide.Buy, 32.50m)));
        var decision = check.Check(Ctx(OrderSide.Sell, 32.40m));
        Assert.False(decision.Approved);
        Assert.Contains("clOrdId=7", decision.Reason);
    }

    // Presence-based STP (no price-cross filter): a non-crossing pair
    // is also rejected, because Modify/partial-fill/market-move can
    // turn it crossing later and we have no atomic check↔dispatch
    // step to re-validate. Opt out via AllowSelfTrade=true.
    [Fact]
    public void Buy_BelowOwnSellPrice_Rejected_PresenceBased()
    {
        var (check, book) = Build();
        Assert.True(book.TryAdd(Make(1, OrderSide.Sell, 32.50m)));
        var decision = check.Check(Ctx(OrderSide.Buy, 32.40m));
        Assert.False(decision.Approved);
        Assert.Contains("self_trade_prevention", decision.Reason);
        Assert.Contains("clOrdId=1", decision.Reason);
    }

    [Fact]
    public void Sell_AboveOwnBuyPrice_Rejected_PresenceBased()
    {
        var (check, book) = Build();
        Assert.True(book.TryAdd(Make(1, OrderSide.Buy, 32.40m)));
        var decision = check.Check(Ctx(OrderSide.Sell, 32.50m));
        Assert.False(decision.Approved);
        Assert.Contains("self_trade_prevention", decision.Reason);
        Assert.Contains("clOrdId=1", decision.Reason);
    }

    [Fact]
    public void EqualPrice_Crosses()
    {
        var (check, book) = Build();
        Assert.True(book.TryAdd(Make(1, OrderSide.Sell, 32.50m)));
        Assert.False(check.Check(Ctx(OrderSide.Buy, 32.50m)).Approved);
    }

    [Fact]
    public void IncomingMarket_AnyContraWorking_Rejected()
    {
        var (check, book) = Build();
        Assert.True(book.TryAdd(Make(1, OrderSide.Sell, 99.99m)));
        Assert.False(check.Check(Ctx(OrderSide.Buy, price: null, type: OrderType.Market)).Approved);
    }

    [Fact]
    public void RestingMarket_NewLimit_Rejected()
    {
        var (check, book) = Build();
        Assert.True(book.TryAdd(Make(1, OrderSide.Sell, price: null, type: OrderType.Market)));
        Assert.False(check.Check(Ctx(OrderSide.Buy, 0.01m)).Approved);
    }

    [Fact]
    public void ContraOrder_DifferentOwner_Approves()
    {
        var (check, book) = Build();
        Assert.True(book.TryAdd(Make(1, OrderSide.Sell, 32.40m, owner: "bob")));
        Assert.True(check.Check(Ctx(OrderSide.Buy, 32.50m, owner: Owner)).Approved);
    }

    [Fact]
    public void ContraOrder_DifferentSymbol_Approves()
    {
        var (check, book) = Build();
        Assert.True(book.TryAdd(Make(1, OrderSide.Sell, 32.40m, symbol: "VALE3")));
        Assert.True(check.Check(Ctx(OrderSide.Buy, 32.50m, symbol: Symbol)).Approved);
    }

    [Fact]
    public void TerminalContra_Ignored_Approves()
    {
        var (check, book) = Build();
        var sell = Make(1, OrderSide.Sell, 32.40m);
        Assert.True(book.TryAdd(sell));
        // Drive to Cancelled — leaves should hit 0 too, but we test the
        // status branch defensively.
        sell.MarkCancelled();
        Assert.True(check.Check(Ctx(OrderSide.Buy, 32.50m)).Approved);
    }

    [Fact]
    public void AllowSelfTrade_OptIn_Approves()
    {
        var opts = new RiskOptions
        {
            PerEndClient =
            {
                [Owner] = new RiskLimits { AllowSelfTrade = true },
            },
        };
        var (check, book) = Build(opts);
        Assert.True(book.TryAdd(Make(1, OrderSide.Sell, 32.40m)));
        Assert.True(check.Check(Ctx(OrderSide.Buy, 32.50m)).Approved);
    }

    // Opt-in covers BOTH crossing and non-crossing pairs.
    [Fact]
    public void AllowSelfTrade_OptIn_NonCrossing_Approves()
    {
        var opts = new RiskOptions
        {
            PerEndClient =
            {
                [Owner] = new RiskLimits { AllowSelfTrade = true },
            },
        };
        var (check, book) = Build(opts);
        Assert.True(book.TryAdd(Make(1, OrderSide.Sell, 32.50m)));
        Assert.True(check.Check(Ctx(OrderSide.Buy, 32.40m)).Approved);
    }

    [Fact]
    public void SameSide_Ignored_Approves()
    {
        var (check, book) = Build();
        Assert.True(book.TryAdd(Make(1, OrderSide.Buy, 32.40m)));
        Assert.True(check.Check(Ctx(OrderSide.Buy, 32.50m)).Approved);
    }
}
