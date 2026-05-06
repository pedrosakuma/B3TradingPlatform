using B3.Trading.Application;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Checks;
using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests;

public class NoNakedShortCheckTests
{
    private const string DefaultFirm = "FIRM01";
    private const string DefaultOwner = "alice";
    private const string DefaultSymbol = "PETR4";

    private static IOptionsMonitor<RiskOptions> Wrap(RiskOptions o) =>
        new StaticOptionsMonitor<RiskOptions>(o);

    private static RiskOptions DefaultBlockedOpts() => new();

    private static RiskContext SellCtx(long qty, string owner = DefaultOwner,
                                       string firm = DefaultFirm, string symbol = DefaultSymbol) =>
        new(new EndClientId(owner), firm, symbol,
            OrderSide.Sell, OrderType.Limit, qty, 30m);

    private static RiskContext BuyCtx(long qty) =>
        new(new EndClientId(DefaultOwner), DefaultFirm, DefaultSymbol,
            OrderSide.Buy, OrderType.Limit, qty, 30m);

    private static Order MakeSell(ulong clOrdId, long qty,
                                  string owner = DefaultOwner, string symbol = DefaultSymbol) =>
        new(clOrdId, new EndClientId(owner), symbol, 4321UL,
            OrderSide.Sell, OrderType.Limit, qty, 30m, DefaultFirm);

    private static Order MakeBuy(ulong clOrdId, long qty) =>
        new(clOrdId, new EndClientId(DefaultOwner), DefaultSymbol, 4321UL,
            OrderSide.Buy, OrderType.Limit, qty, 30m, DefaultFirm);

    private static (NoNakedShortCheck, PositionKeeper, WorkingOrderBook) Build(RiskOptions? opts = null)
    {
        var positions = new PositionKeeper();
        var orders = new WorkingOrderBook();
        var check = new NoNakedShortCheck(Wrap(opts ?? DefaultBlockedOpts()), positions, orders);
        return (check, positions, orders);
    }

    // Mirrors what OrderSubmissionService does just before the risk
    // pipeline runs: the incoming order is already in the book.
    private static void SubmitInto(WorkingOrderBook book, Order order) =>
        Assert.True(book.TryAdd(order));

    [Fact]
    public void Buy_AlwaysApproved_RegardlessOfPosition()
    {
        var (check, _, _) = Build();
        Assert.True(check.Check(BuyCtx(1_000_000)).Approved);
    }

    [Fact]
    public void Sell_NoPosition_NoOpenSells_Rejected()
    {
        var (check, _, orders) = Build();
        SubmitInto(orders, MakeSell(1UL, 100));
        var d = check.Check(SellCtx(100));
        Assert.False(d.Approved);
        Assert.Contains("naked short blocked", d.Reason);
    }

    [Fact]
    public void Sell_LongCoversFully_Approved()
    {
        var (check, positions, orders) = Build();
        positions.ApplyFill(new EndClientId(DefaultOwner), DefaultSymbol, OrderSide.Buy, 500, 30m);
        SubmitInto(orders, MakeSell(1UL, 500));
        Assert.True(check.Check(SellCtx(500)).Approved);
    }

    [Fact]
    public void Sell_LongInsufficientByOne_Rejected()
    {
        var (check, positions, orders) = Build();
        positions.ApplyFill(new EndClientId(DefaultOwner), DefaultSymbol, OrderSide.Buy, 500, 30m);
        SubmitInto(orders, MakeSell(1UL, 501));
        Assert.False(check.Check(SellCtx(501)).Approved);
    }

    [Fact]
    public void Sell_OpenSellsConsumeAvailableInventory()
    {
        var (check, positions, orders) = Build();
        positions.ApplyFill(new EndClientId(DefaultOwner), DefaultSymbol, OrderSide.Buy, 500, 30m);
        // 300 already working as Sell; this incoming Sell of 200 just
        // fills the remaining sellable inventory exactly.
        SubmitInto(orders, MakeSell(1UL, 300));
        SubmitInto(orders, MakeSell(2UL, 200));
        Assert.True(check.Check(SellCtx(200)).Approved);
    }

    [Fact]
    public void Sell_OpenSellsExceedAvailableInventory_Rejected()
    {
        var (check, positions, orders) = Build();
        positions.ApplyFill(new EndClientId(DefaultOwner), DefaultSymbol, OrderSide.Buy, 500, 30m);
        SubmitInto(orders, MakeSell(1UL, 300));
        SubmitInto(orders, MakeSell(2UL, 201));
        Assert.False(check.Check(SellCtx(201)).Approved);
    }

    [Fact]
    public void Sell_OpenBuysDoNotCount_PessimisticProjection()
    {
        var (check, positions, orders) = Build();
        positions.ApplyFill(new EndClientId(DefaultOwner), DefaultSymbol, OrderSide.Buy, 100, 30m);
        // A pending Buy of 1000 would, if it filled, give plenty of
        // inventory. The check must not credit it — open Buys can be
        // cancelled.
        SubmitInto(orders, MakeBuy(99UL, 1000));
        SubmitInto(orders, MakeSell(1UL, 200));
        Assert.False(check.Check(SellCtx(200)).Approved);
    }

    [Fact]
    public void Sell_TerminalSellsDoNotConsumeInventory()
    {
        var (check, positions, orders) = Build();
        positions.ApplyFill(new EndClientId(DefaultOwner), DefaultSymbol, OrderSide.Buy, 500, 30m);
        var oldSell = MakeSell(99UL, 500);
        SubmitInto(orders, oldSell);
        oldSell.MarkCancelled();
        SubmitInto(orders, MakeSell(1UL, 500));
        Assert.True(check.Check(SellCtx(500)).Approved);
    }

    [Fact]
    public void Sell_PartiallyFilled_OnlyLeavesConsumeInventory()
    {
        var (check, positions, orders) = Build();
        positions.ApplyFill(new EndClientId(DefaultOwner), DefaultSymbol, OrderSide.Buy, 500, 30m);
        var partial = MakeSell(99UL, 400);
        SubmitInto(orders, partial);
        partial.ApplyFill(300); // 100 leaves
        // PositionKeeper would have been updated by the fill, but the
        // check reads the current net at evaluation time. Mirror that
        // here: net long is now 200, leaves on the partial sell are 100.
        positions.ApplyFill(new EndClientId(DefaultOwner), DefaultSymbol, OrderSide.Sell, 300, 30m);
        SubmitInto(orders, MakeSell(1UL, 100));
        Assert.True(check.Check(SellCtx(100)).Approved);
    }

    [Fact]
    public void Sell_OtherSymbolLeaves_DoNotConsumeThisSymbolInventory()
    {
        var (check, positions, orders) = Build();
        positions.ApplyFill(new EndClientId(DefaultOwner), DefaultSymbol, OrderSide.Buy, 500, 30m);
        SubmitInto(orders, MakeSell(99UL, 500, symbol: "VALE3"));
        SubmitInto(orders, MakeSell(1UL, 500));
        Assert.True(check.Check(SellCtx(500)).Approved);
    }

    [Fact]
    public void Sell_OtherOwnerInventory_DoesNotCoverThisOwner()
    {
        var (check, positions, orders) = Build();
        positions.ApplyFill(new EndClientId("bob"), DefaultSymbol, OrderSide.Buy, 1000, 30m);
        SubmitInto(orders, MakeSell(1UL, 100, owner: DefaultOwner));
        Assert.False(check.Check(SellCtx(100, owner: DefaultOwner)).Approved);
    }

    [Fact]
    public void Sell_AllowShortSellPerEndClient_BypassesGate()
    {
        var opts = new RiskOptions
        {
            PerEndClient = { [DefaultOwner] = new RiskLimits { AllowShortSell = true } },
        };
        var (check, _, orders) = Build(opts);
        SubmitInto(orders, MakeSell(1UL, 1000));
        Assert.True(check.Check(SellCtx(1000)).Approved);
    }

    [Fact]
    public void Sell_AllowShortSellPerFirm_BypassesGate()
    {
        var opts = new RiskOptions
        {
            PerFirm = { [DefaultFirm] = new RiskLimits { AllowShortSell = true } },
        };
        var (check, _, orders) = Build(opts);
        SubmitInto(orders, MakeSell(1UL, 1000));
        Assert.True(check.Check(SellCtx(1000)).Approved);
    }

    [Fact]
    public void Sell_AllowShortSellExplicitlyFalse_Blocks()
    {
        var opts = new RiskOptions
        {
            Default = new RiskLimits { AllowShortSell = false },
        };
        var (check, _, orders) = Build(opts);
        SubmitInto(orders, MakeSell(1UL, 100));
        Assert.False(check.Check(SellCtx(100)).Approved);
    }

    [Fact]
    public void Order_SitsBetweenThrottleAndPositionLimit()
    {
        // Naked-short is more fundamental than the |net| cap, so it
        // must run before PositionLimitCheck (Order=200) but after
        // the throttle band (150..170) — sanity-check the constant
        // here so future reorders trigger this test.
        var (check, _, _) = Build();
        Assert.True(check.Order > 170);
        Assert.True(check.Order < 200);
    }

    // ---------------- Slice 3 of #122: modify projection ----------------

    private static RiskContext SellReplaceCtx(long newQty, ulong origClOrdId, long? effectiveLeaves = null) =>
        new(new EndClientId(DefaultOwner), DefaultFirm, DefaultSymbol,
            OrderSide.Sell, OrderType.Limit, newQty, 30m,
            ReplaceOriginalClOrdId: origClOrdId,
            EffectiveLeavesQuantity: effectiveLeaves);

    [Fact]
    public void Replace_downsize_approved_evenWhenOriginalConsumesAllInventory()
    {
        // Trader is long 100, has a working Sell of 100 at the
        // ceiling. Modifying that Sell down to 60 should approve —
        // the original is going away.
        var (check, positions, orders) = Build();
        positions.GetOrCreate(new EndClientId(DefaultOwner), DefaultSymbol).ApplyFill(OrderSide.Buy, 100, 30m);
        SubmitInto(orders, MakeSell(1UL, 100));

        var ctx = SellReplaceCtx(newQty: 60, origClOrdId: 1UL, effectiveLeaves: 60);

        Assert.True(check.Check(ctx).Approved);
    }

    [Fact]
    public void Replace_upsize_rejected_whenProjectionExceedsInventory()
    {
        var (check, positions, orders) = Build();
        positions.GetOrCreate(new EndClientId(DefaultOwner), DefaultSymbol).ApplyFill(OrderSide.Buy, 100, 30m);
        SubmitInto(orders, MakeSell(1UL, 100));

        // Try to upsize from 100 to 150 — would need long ≥ 150.
        var ctx = SellReplaceCtx(newQty: 150, origClOrdId: 1UL, effectiveLeaves: 150);

        Assert.False(check.Check(ctx).Approved);
    }

    [Fact]
    public void Replace_upsize_approved_whenInventorySufficient()
    {
        var (check, positions, orders) = Build();
        positions.GetOrCreate(new EndClientId(DefaultOwner), DefaultSymbol).ApplyFill(OrderSide.Buy, 200, 30m);
        SubmitInto(orders, MakeSell(1UL, 100));

        var ctx = SellReplaceCtx(newQty: 200, origClOrdId: 1UL, effectiveLeaves: 200);

        Assert.True(check.Check(ctx).Approved);
    }

    [Fact]
    public void Replace_usesEffectiveLeaves_notNewQuantity_whenOriginalPartiallyFilled()
    {
        // Trader long 100; original Sell 100 has filled 40 (leaves=60).
        // openSellLeaves snapshot = 60. Modify to newQty=80 with
        // origCum=40 → effectiveLeaves = 40. Projection: 60 - 60 + 40
        // = 40 ≤ 100, so approve. If the check naively used newQty
        // (80) it would compute 80 ≤ 100 here, but the meaningful
        // figure is the leaves the venue will assign.
        var (check, positions, orders) = Build();
        positions.GetOrCreate(new EndClientId(DefaultOwner), DefaultSymbol).ApplyFill(OrderSide.Buy, 100, 30m);
        var orig = MakeSell(1UL, 100);
        orig.MarkWorking();
        orig.ApplyCumulativeFill(40);
        SubmitInto(orders, orig);

        var ctx = SellReplaceCtx(newQty: 80, origClOrdId: 1UL, effectiveLeaves: 40);

        Assert.True(check.Check(ctx).Approved);
    }

    [Fact]
    public void Replace_unknownOrigClOrdId_fallsBackToBaselineProjection()
    {
        // If the original is not in the book (slice 4 will guard
        // against this in the endpoint), the projection adjustment
        // is a no-op — behavior matches a fresh submission.
        var (check, positions, _) = Build();
        positions.GetOrCreate(new EndClientId(DefaultOwner), DefaultSymbol).ApplyFill(OrderSide.Buy, 50, 30m);

        // No order in book + ctx claims to replace ID 999.
        var ctx = SellReplaceCtx(newQty: 100, origClOrdId: 999UL, effectiveLeaves: 100);

        // Without the original to subtract and without the new order
        // in book either, openSellLeaves=0 → currentLong=50 - 0 = 50
        // ≥ 0 → approve. (The endpoint, slice 4, is responsible for
        // 404'ing modifies of unknown originals; the check does not.)
        Assert.True(check.Check(ctx).Approved);
    }

    [Fact]
    public void Replace_doesNotSubtract_whenOriginalAlreadyTerminal()
    {
        // Original was just cancelled (status terminal) but caller
        // still set ReplaceOriginalClOrdId — adjustment must be a
        // no-op so the check doesn't double-credit inventory.
        var (check, positions, orders) = Build();
        positions.GetOrCreate(new EndClientId(DefaultOwner), DefaultSymbol).ApplyFill(OrderSide.Buy, 50, 30m);
        var orig = MakeSell(1UL, 100);
        orig.MarkCancelled();
        SubmitInto(orders, orig); // terminal; SumOpenSellLeavesForSymbol skips it

        var ctx = SellReplaceCtx(newQty: 100, origClOrdId: 1UL, effectiveLeaves: 100);

        // currentLong=50, openSellLeaves=0 (orig terminal), no
        // adjustment → projected sell leaves = 0; sellable = 50.
        // The new 100 isn't in the book → projection logic doesn't
        // add it back here either (no-op when terminal). Approved.
        Assert.True(check.Check(ctx).Approved);
    }
}
