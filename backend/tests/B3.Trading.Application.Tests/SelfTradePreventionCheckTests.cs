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

    // #570: a stale working order (session_rolled after a matching-
    // engine restart) can never be cancelled by the end-client
    // (OrderCancelService refuses IsStale orders), so counting it as
    // "working" here would permanently deadlock the opposite side.
    // Every other WorkingOrderBook-backed query skips stale orders;
    // this check must too.
    [Fact]
    public void StaleContra_Ignored_Approves()
    {
        var (check, book) = Build();
        var buy = Make(1, OrderSide.Buy, 30.00m);
        Assert.True(book.TryAdd(buy));
        buy.MarkWorking();
        Assert.True(buy.MarkStale("session_rolled:3-4", DateTimeOffset.UtcNow));
        var decision = check.Check(Ctx(OrderSide.Sell, 40.00m));
        Assert.True(decision.Approved);
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

    // PR #316 P2.3. The owner index spans every firm; without an
    // explicit firm filter on the contra scan, a working opposite-side
    // order in FIRM02 would falsely reject an order from the SAME JWT
    // sub authenticated under FIRM01. Self-trade prevention only makes
    // sense within one matching-engine session (one firm).
    [Fact]
    public void ContraOrder_DifferentFirm_SameOwner_Approves()
    {
        var (check, book) = Build();
        // FIRM02 working buy for owner alice on PETR4.
        var firm02Order = new Order(
            clOrdId: 42, owner: OwnerId, symbol: Symbol, securityId: 4321UL,
            side: OrderSide.Buy, type: OrderType.Limit, quantity: 100, price: 32.40m,
            firmId: "FIRM02");
        Assert.True(book.TryAdd(firm02Order));

        // FIRM01 same owner submitting an opposite-side Sell on PETR4
        // must NOT trip STP — the FIRM02 contra is in a different
        // matching session and can never self-cross.
        var decision = check.Check(Ctx(OrderSide.Sell, 32.50m));
        Assert.True(decision.Approved);
    }

    [Fact]
    public void ContraOrder_SameFirm_SameOwner_StillRejects()
    {
        // Regression guard for the cross-firm fix: the within-firm
        // rejection path must keep working after we narrowed the scan.
        var (check, book) = Build();
        Assert.True(book.TryAdd(Make(7, OrderSide.Buy, 32.40m))); // FIRM01 (test default)
        var decision = check.Check(Ctx(OrderSide.Sell, 32.50m));
        Assert.False(decision.Approved);
        Assert.Contains("clOrdId=7", decision.Reason);
    }

    // ---- #433 cross-firm beneficial-owner scope -------------------------

    private const string FirmA = "FIRM-A";
    private const string FirmB = "FIRM-B";
    private const string OwnerA = "alice";
    private const string OwnerB = "alice_b";
    private const string BO = "CPF-123";

    private static (SelfTradePreventionCheck check, WorkingOrderBook book) BuildCrossFirm(
        bool enforceCrossFirm = true, Dictionary<string, string>? boMap = null)
    {
        var opts = new RiskOptions
        {
            Default = new RiskLimits { EnforceCrossFirmStp = enforceCrossFirm },
            BeneficialOwners = boMap ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [OwnerA] = BO,
                [OwnerB] = BO,
            },
        };
        var book = new WorkingOrderBook();
        var monitor = Wrap(opts);
        var resolver = new OptionsBeneficialOwnerResolver(monitor);
        var check = new SelfTradePreventionCheck(monitor, book, resolver);
        return (check, book);
    }

    private static Order MakeFor(string firmId, string owner, ulong clOrdId, OrderSide side, decimal price,
                                 string symbol = Symbol) =>
        new(clOrdId, new EndClientId(owner), symbol, 4321UL, side, OrderType.Limit, 100, price, firmId);

    private static RiskContext CtxFor(string firmId, string owner, OrderSide side, decimal price,
                                       string symbol = Symbol) =>
        new(new EndClientId(owner), firmId, symbol, side, OrderType.Limit, 100, price);

    [Fact]
    public void CrossFirm_SameBeneficialOwner_OptIn_Rejected()
    {
        // CVM 168 práticas equitativas: a single beneficial owner trading
        // through two firms on this trading-host cannot wash-trade across
        // the firms. Opt-in via EnforceCrossFirmStp + BeneficialOwners map.
        var (check, book) = BuildCrossFirm();
        Assert.True(book.TryAdd(MakeFor(FirmB, OwnerB, clOrdId: 99, OrderSide.Sell, 32.40m)));

        var decision = check.Check(CtxFor(FirmA, OwnerA, OrderSide.Buy, 32.50m));

        Assert.False(decision.Approved);
        Assert.Contains("cross_firm", decision.Reason);
        Assert.Contains($"beneficial_owner={BO}", decision.Reason);
        Assert.Contains($"contra_firm={FirmB}", decision.Reason);
        Assert.Contains("clOrdId=99", decision.Reason);
    }

    [Fact]
    public void CrossFirm_SameBeneficialOwner_OptOut_Approved()
    {
        // Default-off back-compat: with EnforceCrossFirmStp unset (= null)
        // the cross-firm wash-trade is allowed through. Tested by
        // explicitly setting the flag false here for symmetry with the
        // opt-in case.
        var (check, book) = BuildCrossFirm(enforceCrossFirm: false);
        Assert.True(book.TryAdd(MakeFor(FirmB, OwnerB, clOrdId: 99, OrderSide.Sell, 32.40m)));

        var decision = check.Check(CtxFor(FirmA, OwnerA, OrderSide.Buy, 32.50m));

        Assert.True(decision.Approved);
    }

    [Fact]
    public void CrossFirm_DifferentBeneficialOwners_Approved()
    {
        // Different BO = different real-world legal persons; the cross-
        // firm working order is legitimate counterparty activity even
        // with EnforceCrossFirmStp on.
        var (check, book) = BuildCrossFirm(boMap: new Dictionary<string, string>
        {
            [OwnerA] = "CPF-AAA",
            [OwnerB] = "CPF-BBB",
        });
        Assert.True(book.TryAdd(MakeFor(FirmB, OwnerB, clOrdId: 99, OrderSide.Sell, 32.40m)));

        var decision = check.Check(CtxFor(FirmA, OwnerA, OrderSide.Buy, 32.50m));

        Assert.True(decision.Approved);
    }

    [Fact]
    public void CrossFirm_AllowSelfTrade_Wins()
    {
        // AllowSelfTrade=true is the global opt-out; cross-firm scope
        // does not override it.
        var monitor = Wrap(new RiskOptions
        {
            Default = new RiskLimits { AllowSelfTrade = true, EnforceCrossFirmStp = true },
            BeneficialOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [OwnerA] = BO,
                [OwnerB] = BO,
            },
        });
        var book = new WorkingOrderBook();
        var check = new SelfTradePreventionCheck(monitor, book, new OptionsBeneficialOwnerResolver(monitor));
        Assert.True(book.TryAdd(MakeFor(FirmB, OwnerB, clOrdId: 99, OrderSide.Sell, 32.40m)));

        Assert.True(check.Check(CtxFor(FirmA, OwnerA, OrderSide.Buy, 32.50m)).Approved);
    }

    [Fact]
    public void CrossFirm_SameFirmContra_ReportedAsSameFirm_NotCrossFirm()
    {
        // Phase ordering: same-firm scope must run first so a contra
        // order in the SAME (firm, owner) tuple is attributed to the
        // same_firm bucket even with cross-firm enforcement on.
        var (check, book) = BuildCrossFirm();
        Assert.True(book.TryAdd(MakeFor(FirmA, OwnerA, clOrdId: 50, OrderSide.Sell, 32.40m)));

        var decision = check.Check(CtxFor(FirmA, OwnerA, OrderSide.Buy, 32.50m));

        Assert.False(decision.Approved);
        Assert.Contains("same_firm", decision.Reason);
        Assert.DoesNotContain("cross_firm", decision.Reason);
    }

    [Fact]
    public void CrossFirm_SameFirmSiblingOwner_SharedBeneficialOwner_Approved()
    {
        // #446 P1: cross-firm scope must skip every same-firm sibling
        // owner, not just the exact (firm, owner) tuple. Same-firm
        // behavior is handled by the regular app-side check and/or venue-
        // side STP; the cross-firm phase is strictly inter-firm only.
        var (check, book) = BuildCrossFirm();
        Assert.True(book.TryAdd(MakeFor(FirmA, OwnerB, clOrdId: 51, OrderSide.Sell, 32.40m)));

        var decision = check.Check(CtxFor(FirmA, OwnerA, OrderSide.Buy, 32.50m));

        Assert.True(decision.Approved);
    }

    [Fact]
    public void CrossFirm_NoBeneficialOwnerMap_CollapsesToOwnerSelf_NoFalseHit()
    {
        // With no BeneficialOwners entries, every owner is its own BO so
        // OwnersFor(BO) returns just {owner}; a cross-firm contra from a
        // DIFFERENT owner cannot match — no wash trade.
        var (check, book) = BuildCrossFirm(boMap: new Dictionary<string, string>());
        Assert.True(book.TryAdd(MakeFor(FirmB, OwnerB, clOrdId: 99, OrderSide.Sell, 32.40m)));

        var decision = check.Check(CtxFor(FirmA, OwnerA, OrderSide.Buy, 32.50m));

        Assert.True(decision.Approved);
    }

    [Fact]
    public void BeneficialOwnerResolver_OwnersFor_IncludesImplicitSelfAlongsideExplicitSiblings()
    {
        var monitor = Wrap(new RiskOptions
        {
            BeneficialOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [OwnerB] = OwnerA,
            },
        });
        var resolver = new OptionsBeneficialOwnerResolver(monitor);

        var owners = resolver.OwnersFor(OwnerA);

        Assert.Equal(2, owners.Count);
        Assert.Contains(new EndClientId(OwnerA), owners);
        Assert.Contains(new EndClientId(OwnerB), owners);
    }

    [Fact]
    public void BeneficialOwnerResolver_OwnersFor_DoesNotOverrideExplicitOwnerMapping()
    {
        var monitor = Wrap(new RiskOptions
        {
            BeneficialOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [OwnerB] = OwnerA,
                [OwnerA] = "CPF-OTHER",
            },
        });
        var resolver = new OptionsBeneficialOwnerResolver(monitor);

        var owners = resolver.OwnersFor(OwnerA);

        Assert.Single(owners);
        Assert.Contains(new EndClientId(OwnerB), owners);
        Assert.DoesNotContain(new EndClientId(OwnerA), owners);
    }

    [Fact]
    public void CrossFirm_MixedExplicitImplicitOwners_CatchesBareOwnerInOtherFirm()
    {
        // Mixed configuration: alice_b explicitly maps to BO alice while
        // bare alice relies on the implicit BO == owner default. The
        // cross-firm fan-out must still include both owners.
        var (check, book) = BuildCrossFirm(boMap: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [OwnerB] = OwnerA,
        });
        Assert.True(book.TryAdd(MakeFor(FirmB, OwnerA, clOrdId: 99, OrderSide.Sell, 32.40m)));

        var decision = check.Check(CtxFor(FirmA, OwnerB, OrderSide.Buy, 32.50m));

        Assert.False(decision.Approved);
        Assert.Contains("cross_firm", decision.Reason);
        Assert.Contains($"beneficial_owner={OwnerA}", decision.Reason);
        Assert.Contains($"contra_firm={FirmB}", decision.Reason);
        Assert.Contains("clOrdId=99", decision.Reason);
    }
}
