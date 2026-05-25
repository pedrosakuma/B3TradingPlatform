using B3.Trading.Application.Investor;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using Up = B3.EntryPoint.Client.Models;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Q1.1 (#253). Pins the Domain → SDK enum tables exposed by
/// <see cref="B3EntryPointClientGateway"/>. The mapping is the entire
/// outbound contract for the new order surface — a regression here
/// would silently mis-route Stop / GTD / IOC orders to the venue.
/// </summary>
public class B3EntryPointClientGatewayMapTests
{
    [Theory]
    [InlineData(OrderType.Limit, Up.OrderType.Limit)]
    [InlineData(OrderType.Market, Up.OrderType.Market)]
    [InlineData(OrderType.StopLoss, Up.OrderType.StopLoss)]
    [InlineData(OrderType.StopLimit, Up.OrderType.StopLimit)]
    [InlineData(OrderType.MarketWithLeftover, Up.OrderType.MarketWithLeftoverAsLimit)]
    public void MapOrderType_CoversAllDomainValues(OrderType domain, Up.OrderType sdk)
    {
        Assert.Equal(sdk, B3EntryPointClientGateway.MapOrderType(domain));
    }

    [Theory]
    [InlineData(TimeInForce.Day, Up.TimeInForce.Day)]
    [InlineData(TimeInForce.IOC, Up.TimeInForce.ImmediateOrCancel)]
    [InlineData(TimeInForce.FOK, Up.TimeInForce.FillOrKill)]
    [InlineData(TimeInForce.GTC, Up.TimeInForce.GoodTillCancel)]
    [InlineData(TimeInForce.GTD, Up.TimeInForce.GoodTillDate)]
    [InlineData(TimeInForce.AtClose, Up.TimeInForce.AtTheClose)]
    [InlineData(TimeInForce.GoodForAuction, Up.TimeInForce.GoodForAuction)]
    public void MapTimeInForce_CoversAllDomainValues(TimeInForce domain, Up.TimeInForce sdk)
    {
        Assert.Equal(sdk, B3EntryPointClientGateway.MapTimeInForce(domain));
    }

    [Fact]
    public void MapOrderType_UnmappedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => B3EntryPointClientGateway.MapOrderType((OrderType)999));
    }

    [Fact]
    public void MapTimeInForce_UnmappedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => B3EntryPointClientGateway.MapTimeInForce((TimeInForce)999));
    }

    [Fact]
    public void BuildNewOrderRequest_PlainOrder_LeavesMaxFloorNull()
    {
        // Q3.4 (#284). Full-disclosure (no reserve) orders must not
        // accidentally populate MaxFloor — a non-null value would
        // cause the venue to expose only a slice of the order on the
        // public book.
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM-A");

        var req = B3EntryPointClientGateway.BuildNewOrderRequest(order);

        Assert.Null(req.MaxFloor);
        Assert.Equal(100UL, req.OrderQty);
    }

    [Theory]
    [InlineData(10L, 100L)]
    [InlineData(1L, 1L)]
    [InlineData(50L, 50L)]
    public void BuildNewOrderRequest_Iceberg_MapsDisplayQtyToMaxFloor(long displayQty, long quantity)
    {
        // Q3.4 (#284). Native iceberg path: DisplayQty → MaxFloor on
        // the SDK request. Pin both bounds (min=1, equal-to-qty) to
        // detect any future ulong-cast regression.
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            quantity, 30m, "FIRM-A",
            displayQty: displayQty, displayResetPolicy: DisplayResetPolicy.Always);

        var req = B3EntryPointClientGateway.BuildNewOrderRequest(order);

        Assert.Equal((ulong)displayQty, req.MaxFloor);
        Assert.Equal((ulong)quantity, req.OrderQty);
    }

    // ------------------------------------------------------------------
    // #433 P1. Venue-side SelfTradePreventionInstruction wire mapping.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(B3.Trading.Application.Risk.SelfTradePreventionMode.None,
                Up.SelfTradePreventionInstruction.None)]
    [InlineData(B3.Trading.Application.Risk.SelfTradePreventionMode.CancelAggressorOrder,
                Up.SelfTradePreventionInstruction.CancelAggressorOrder)]
    [InlineData(B3.Trading.Application.Risk.SelfTradePreventionMode.CancelRestingOrder,
                Up.SelfTradePreventionInstruction.CancelRestingOrder)]
    [InlineData(B3.Trading.Application.Risk.SelfTradePreventionMode.CancelBothOrders,
                Up.SelfTradePreventionInstruction.CancelBothOrders)]
    public void MapStpInstruction_CoversAllDomainValues(
        B3.Trading.Application.Risk.SelfTradePreventionMode mode,
        Up.SelfTradePreventionInstruction sdk)
    {
        Assert.Equal(sdk, B3EntryPointClientGateway.MapStpInstruction(mode));
    }

    [Fact]
    public void MapStpInstruction_UnmappedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => B3EntryPointClientGateway.MapStpInstruction(
                (B3.Trading.Application.Risk.SelfTradePreventionMode)999));
    }

    [Fact]
    public void BuildNewOrderRequest_LegacyOverload_DefaultsStpInstructionToNone()
    {
        // The single-arg overload exists so pre-#433 tests stay green.
        // It must collapse to None on the wire (legacy behavior).
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A");

        var req = B3EntryPointClientGateway.BuildNewOrderRequest(order);

        Assert.Equal(Up.SelfTradePreventionInstruction.None, req.SelfTradePreventionInstruction);
    }

    [Theory]
    [InlineData(Up.SelfTradePreventionInstruction.None)]
    [InlineData(Up.SelfTradePreventionInstruction.CancelAggressorOrder)]
    [InlineData(Up.SelfTradePreventionInstruction.CancelRestingOrder)]
    [InlineData(Up.SelfTradePreventionInstruction.CancelBothOrders)]
    public void BuildNewOrderRequest_StampsResolvedStpInstruction(
        Up.SelfTradePreventionInstruction stp)
    {
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A");

        var req = B3EntryPointClientGateway.BuildNewOrderRequest(order, stp);

        Assert.Equal(stp, req.SelfTradePreventionInstruction);
    }

    [Fact]
    public void ResolveSelfTradePreventionMode_Default_IsNone()
    {
        // #433 P1. Default-everywhere semantics are intentionally
        // None so the venue does not enforce STP unless an operator
        // explicitly opts in — preserves historical behavior for
        // tenants that rely on crossed trades reaching the book
        // (e.g. cross-account hedging inside the same firm). The
        // app-side SelfTradePreventionCheck remains the primary
        // line of defense.
        var opts = new B3.Trading.Application.Risk.RiskOptions();

        var mode = B3.Trading.Application.Risk.RiskLimitsResolver
            .ResolveSelfTradePreventionMode(opts, "alice", "FIRM-A", "PETR4");

        Assert.Equal(
            B3.Trading.Application.Risk.SelfTradePreventionMode.None,
            mode);
    }

    [Fact]
    public void ResolveSelfTradePreventionMode_HonoursPrecedenceChain()
    {
        // per-end-client wins over per-firm wins over per-symbol wins
        // over Default — the same precedence as every other field in
        // RiskLimitsResolver.
        var opts = new B3.Trading.Application.Risk.RiskOptions
        {
            Default = new B3.Trading.Application.Risk.RiskLimits
            {
                SelfTradePreventionMode = B3.Trading.Application.Risk.SelfTradePreventionMode.None,
            },
        };
        opts.PerSymbol["PETR4"] = new B3.Trading.Application.Risk.RiskLimits
        {
            SelfTradePreventionMode = B3.Trading.Application.Risk.SelfTradePreventionMode.CancelBothOrders,
        };
        opts.PerFirm["FIRM-A"] = new B3.Trading.Application.Risk.RiskLimits
        {
            SelfTradePreventionMode = B3.Trading.Application.Risk.SelfTradePreventionMode.CancelRestingOrder,
        };
        opts.PerEndClient["alice"] = new B3.Trading.Application.Risk.RiskLimits
        {
            SelfTradePreventionMode = B3.Trading.Application.Risk.SelfTradePreventionMode.CancelAggressorOrder,
        };

        // alice wins (per-end-client)
        Assert.Equal(
            B3.Trading.Application.Risk.SelfTradePreventionMode.CancelAggressorOrder,
            B3.Trading.Application.Risk.RiskLimitsResolver
                .ResolveSelfTradePreventionMode(opts, "alice", "FIRM-A", "PETR4"));

        // unknown end-client → firm wins
        Assert.Equal(
            B3.Trading.Application.Risk.SelfTradePreventionMode.CancelRestingOrder,
            B3.Trading.Application.Risk.RiskLimitsResolver
                .ResolveSelfTradePreventionMode(opts, "bob", "FIRM-A", "PETR4"));

        // unknown firm → symbol wins
        Assert.Equal(
            B3.Trading.Application.Risk.SelfTradePreventionMode.CancelBothOrders,
            B3.Trading.Application.Risk.RiskLimitsResolver
                .ResolveSelfTradePreventionMode(opts, "bob", "FIRM-X", "PETR4"));

        // nothing matches → Default
        Assert.Equal(
            B3.Trading.Application.Risk.SelfTradePreventionMode.None,
            B3.Trading.Application.Risk.RiskLimitsResolver
                .ResolveSelfTradePreventionMode(opts, "bob", "FIRM-X", "VALE3"));
    }

    // ------------------------------------------------------------------
    // #471. TradingSubAccount wire field (SDK 0.15.0). The four-arg
    // BuildNewOrderRequest overload is the seam the gateway calls in
    // production; these tests pin null-passthrough, non-null stamping,
    // and the legacy overloads' wire-stable default of null.
    // ------------------------------------------------------------------

    [Fact]
    public void BuildNewOrderRequest_LegacyOverloads_LeaveTradingSubAccountNull()
    {
        // Both the single-arg and the two-arg overload pre-date #471.
        // They MUST leave the wire field null so existing tests stay
        // green and any caller that has not opted into the mapper does
        // not accidentally start stamping a wire id derived from
        // some default.
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A");

        Assert.Null(B3EntryPointClientGateway.BuildNewOrderRequest(order).TradingSubAccount);
        Assert.Null(B3EntryPointClientGateway
            .BuildNewOrderRequest(order, Up.SelfTradePreventionInstruction.None)
            .TradingSubAccount);
    }

    [Fact]
    public void BuildNewOrderRequest_StampsResolvedTradingSubAccount()
    {
        // The four-arg overload propagates whatever the gateway
        // resolved from its ISubAccountWireIdMapper into the wire
        // field. Null in → null out; non-null in → exact value out.
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A");

        var withId = B3EntryPointClientGateway.BuildNewOrderRequest(
            order, Up.SelfTradePreventionInstruction.None, tradingSubAccount: 12345u);
        Assert.Equal(12345u, withId.TradingSubAccount);

        var withoutId = B3EntryPointClientGateway.BuildNewOrderRequest(
            order, Up.SelfTradePreventionInstruction.None, tradingSubAccount: null);
        Assert.Null(withoutId.TradingSubAccount);
    }

    // ------------------------------------------------------------------
    // #458. CBLC Account wire field. The five-arg BuildNewOrderRequest
    // overload stamps whatever the gateway resolved from its
    // IVenueAccountResolver. Legacy overloads must leave the field
    // null so any caller that has not opted into a real resolver
    // continues to send the field omitted on the wire.
    // ------------------------------------------------------------------

    [Fact]
    public void BuildNewOrderRequest_LegacyOverloads_LeaveAccountNull()
    {
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A");

        Assert.Null(B3EntryPointClientGateway.BuildNewOrderRequest(order).Account);
        Assert.Null(B3EntryPointClientGateway
            .BuildNewOrderRequest(order, Up.SelfTradePreventionInstruction.None)
            .Account);
        Assert.Null(B3EntryPointClientGateway
            .BuildNewOrderRequest(order, Up.SelfTradePreventionInstruction.None, tradingSubAccount: 42u)
            .Account);
    }

    [Fact]
    public void BuildNewOrderRequest_StampsResolvedVenueAccount()
    {
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A");

        var withAccount = B3EntryPointClientGateway.BuildNewOrderRequest(
            order, Up.SelfTradePreventionInstruction.None,
            tradingSubAccount: null, venueAccount: 123456789UL);
        Assert.Equal(123456789UL, withAccount.Account);

        var withoutAccount = B3EntryPointClientGateway.BuildNewOrderRequest(
            order, Up.SelfTradePreventionInstruction.None,
            tradingSubAccount: null, venueAccount: null);
        Assert.Null(withoutAccount.Account);
    }

    // ------------------------------------------------------------------
    // #472. InvestorId wire field. The six-arg BuildNewOrderRequest
    // overload stamps whatever the gateway resolved from its
    // IInvestorIdResolver, translating the domain InvestorIdentity
    // record into the SDK's InvestorId struct. Legacy overloads must
    // leave the field null so any caller that has not opted into a
    // real resolver continues to send the field omitted on the wire.
    // ------------------------------------------------------------------

    [Fact]
    public void BuildNewOrderRequest_LegacyOverloads_LeaveInvestorIdNull()
    {
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A");

        Assert.Null(B3EntryPointClientGateway.BuildNewOrderRequest(order).InvestorId);
        Assert.Null(B3EntryPointClientGateway
            .BuildNewOrderRequest(order, Up.SelfTradePreventionInstruction.None)
            .InvestorId);
        Assert.Null(B3EntryPointClientGateway
            .BuildNewOrderRequest(order, Up.SelfTradePreventionInstruction.None, tradingSubAccount: 42u)
            .InvestorId);
        Assert.Null(B3EntryPointClientGateway
            .BuildNewOrderRequest(order, Up.SelfTradePreventionInstruction.None, tradingSubAccount: 42u, venueAccount: 99UL)
            .InvestorId);
    }

    [Fact]
    public void BuildNewOrderRequest_StampsResolvedInvestorId()
    {
        var owner = new EndClientId("alice");
        var order = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A");

        var withId = B3EntryPointClientGateway.BuildNewOrderRequest(
            order, Up.SelfTradePreventionInstruction.None,
            tradingSubAccount: null, venueAccount: null,
            investorId: new InvestorIdentity(7, 12345));
        Assert.NotNull(withId.InvestorId);
        Assert.Equal((ushort)7, withId.InvestorId!.Value.Prefix);
        Assert.Equal(12345u, withId.InvestorId!.Value.Document);

        var withoutId = B3EntryPointClientGateway.BuildNewOrderRequest(
            order, Up.SelfTradePreventionInstruction.None,
            tradingSubAccount: null, venueAccount: null,
            investorId: null);
        Assert.Null(withoutId.InvestorId);
    }
}

