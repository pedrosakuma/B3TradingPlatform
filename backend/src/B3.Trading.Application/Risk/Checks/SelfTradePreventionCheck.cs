using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Checks;

/// <summary>
/// Pre-trade gate that blocks an end-client from trading against
/// themselves (self-cross / wash trade).
///
/// <para>
/// Background: the downstream B3.EntryPoint matching engine knows the
/// FIRM session but not the end-client identity our platform layers on
/// top. Two orders submitted by the same end-client within the same
/// firm are seen as independent and are matched normally if their
/// prices cross. Issue #102 documents the dogfood incident where
/// <c>bob</c>'s Buy 200 @ 32.50 self-crossed with their own Sell 100
/// @ 32.40 and reported a Filled trade with the position unchanged.
/// </para>
///
/// <para>
/// Policy: when an incoming order would cross any of the end-client's
/// own opposite-side working orders for the same symbol, reject the
/// incoming (newest-rejects). This is the most conservative STP mode
/// and matches what most exchanges call "STP-N" / "Cancel newest".
/// Toggleable per firm / per end-client via
/// <see cref="RiskLimits.AllowSelfTrade"/> (default: blocked).
/// </para>
///
/// <para>
/// Limitations (intentional, tracked in #103):
/// <list type="bullet">
///   <item>TOCTOU: there is a small window between this check and
///   the gateway dispatch in which a contra order could be added by
///   another concurrent submission. The pipeline runs synchronously
///   under the same submission flow, so the only race is across
///   different end-clients — and those legitimately match. The same
///   end-client can only submit serially through the API.</item>
///   <item>Does not block crossing against orders of <em>other</em>
///   end-clients in the same firm — that is legitimate inter-trader
///   activity at the firm level.</item>
///   <item>Does not look at orders mid-cancellation: a working order
///   for which the user already sent a Cancel still counts as
///   "working" until the cancel-ack ER arrives.</item>
/// </list>
/// </para>
/// </summary>
public sealed class SelfTradePreventionCheck : IRiskCheck
{
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly WorkingOrderBook _orders;

    public SelfTradePreventionCheck(IOptionsMonitor<RiskOptions> options, WorkingOrderBook orders)
    {
        _options = options;
        _orders = orders;
    }

    // Ordered just after no_naked_short (180) and before position
    // limits (200). STP is a counterparty-identity invariant and
    // independent of inventory; cheap to evaluate (per-owner index
    // scoped to the symbol).
    public int Order => 190;

    public string Name => "self_trade_prevention";

    public RiskDecision Check(RiskContext ctx)
    {
        var opts = _options.CurrentValue;
        var allow = RiskLimitsResolver.Resolve(
            opts, ctx.Owner.Value, ctx.FirmId, ctx.Symbol, l => l.AllowSelfTrade);
        if (allow == true) return RiskDecision.Approve;

        var oppositeSide = ctx.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        foreach (var existing in _orders.ForEndClient(ctx.Owner))
        {
            if (existing.Side != oppositeSide) continue;
            if (!string.Equals(existing.Symbol, ctx.Symbol, StringComparison.Ordinal)) continue;
            if (!IsStillRestable(existing)) continue;
            if (!WouldCross(ctx, existing)) continue;

            return RiskDecision.Reject(
                $"self_trade_prevention: would cross own working " +
                $"{existing.Side} {existing.LeavesQuantity}@" +
                $"{(existing.Price.HasValue ? existing.Price.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "MKT")} " +
                $"(clOrdId={existing.ClOrdId}); set AllowSelfTrade=true to opt out");
        }

        return RiskDecision.Approve;
    }

    // "Still restable" = order is on the book and has unfilled quantity.
    // We exclude terminal states (Filled/Cancelled/Rejected) defensively
    // even though a fully-cancelled or filled order should also have
    // LeavesQuantity == 0.
    private static bool IsStillRestable(Order o) =>
        o.LeavesQuantity > 0
        && o.Status is not OrderStatus.Filled
                       and not OrderStatus.Cancelled
                       and not OrderStatus.Rejected;

    // Crossing rules:
    //   * If either side is Market, the new order will sweep — always cross.
    //   * Buy@P crosses Sell@Q when P >= Q.
    //   * Sell@P crosses Buy@Q when P <= Q.
    private static bool WouldCross(RiskContext incoming, Order existing)
    {
        if (incoming.Type == OrderType.Market || existing.Type == OrderType.Market)
            return true;

        // Limit-vs-Limit requires both prices set; defensive nulls treat as no-cross.
        if (!incoming.Price.HasValue || !existing.Price.HasValue)
            return false;

        return incoming.Side == OrderSide.Buy
            ? incoming.Price.Value >= existing.Price.Value
            : incoming.Price.Value <= existing.Price.Value;
    }
}
