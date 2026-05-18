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
/// <b>Policy (presence-based STP):</b> reject any incoming order if
/// the end-client already has at least one opposite-side working
/// order for the same symbol — <em>regardless of price</em>. This is
/// stricter than the price-crossing check it replaces, and is the
/// only correct policy given that the submit pipeline does not have
/// strict execution guarantees: a non-crossing pair today (e.g. Buy
/// 32.40 + Sell 32.50) can become crossing after a Modify, a partial
/// fill, or a market move, with no atomic check↔dispatch step we
/// could synchronise against. The price-crossing variant left the
/// burden of "is this pair safe right now AND going forward?" on
/// every state transition, which is impossible to guarantee from
/// pre-trade. By collapsing the rule to "no opposite-side
/// coexistence", we make the gate decidable from the snapshot the
/// check sees and fail closed by construction. Mode is
/// "newest-rejects" (a.k.a. STP-N / Cancel newest). Toggleable per
/// firm / per end-client via
/// <see cref="RiskLimits.AllowSelfTrade"/> (default: blocked) — set
/// to <c>true</c> for accounts that legitimately need both sides
/// resting (market makers, hedgers).
/// </para>
///
/// <para>
/// Native server STP (the second viable layer, see #103) is
/// stateful too and lives at the matching engine; #117 tracks
/// surfacing its restatement reasons in the UI when active.
/// </para>
///
/// <para>
/// Limitations (intentional):
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
        // PR #316 P2: scope the contra-order scan to the caller's firm
        // so an opposite-side working order in FIRM02 cannot reject a
        // FIRM01 order from the same JWT sub. Self-trading is a venue-
        // session concern (the matching engine groups orders by firm
        // session, not by end-client), so cross-firm pairs of the same
        // owner can never wash-trade through this platform.
        foreach (var existing in _orders.ForEndClientAndFirm(ctx.FirmId, ctx.Owner))
        {
            if (existing.Side != oppositeSide) continue;
            if (!string.Equals(existing.Symbol, ctx.Symbol, StringComparison.Ordinal)) continue;
            if (!IsStillRestable(existing)) continue;

            // Presence-based: do NOT consult prices. Any opposite-side
            // working order in the same symbol is enough to reject.
            return RiskDecision.Reject(
                $"self_trade_prevention: own opposite-side {existing.Side} order " +
                $"{existing.LeavesQuantity}@" +
                $"{(existing.Price.HasValue ? existing.Price.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "MKT")} " +
                $"is working on {ctx.Symbol} (clOrdId={existing.ClOrdId}); " +
                $"set AllowSelfTrade=true to opt out");
        }

        return RiskDecision.Approve;
    }

    // "Still restable" = order is on the book and has unfilled quantity.
    // We exclude terminal states (Filled/Cancelled/Rejected/Replaced)
    // defensively even though terminal orders should also have
    // LeavesQuantity == 0.
    private static bool IsStillRestable(Order o) =>
        o.LeavesQuantity > 0
        && o.Status is not OrderStatus.Filled
                       and not OrderStatus.Cancelled
                       and not OrderStatus.Rejected
                       and not OrderStatus.Replaced;
}
