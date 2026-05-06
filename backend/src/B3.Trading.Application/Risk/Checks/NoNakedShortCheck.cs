using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Checks;

/// <summary>
/// Pre-trade gate that blocks naked short sales — Sells that, in the
/// pessimistic projection, would drive the end-client's position
/// negative for the symbol.
///
/// Background: B3 cash equities (mercado à vista) does not allow
/// short-selling without prior stock borrow (BTC). The platform
/// today does not integrate with a borrow registry, so the safe
/// default is to refuse any Sell that exceeds the seller's long
/// inventory — whether or not the matching engine would accept it.
/// Toggleable per firm / per end-client via
/// <see cref="RiskLimits.AllowShortSell"/> (default: blocked).
///
/// Pessimism rules (intentional):
///   * Subtract every <em>open Sell</em>'s remaining leaves from the
///     current net long, even if the new Sell could in theory share
///     inventory with one that's about to fill against a Buy.
///   * Do <strong>not</strong> credit open Buys' leaves: those may
///     be cancelled and would never become inventory.
///   * The order being evaluated is already in the working order
///     book by the time the pipeline runs (see
///     <see cref="WorkingOrderBook.SumOpenSellLeavesForSymbol"/>
///     remarks), so the sum already includes it. The reject
///     condition is therefore <c>sellable &lt; 0</c>, not
///     <c>incoming &gt; sellable</c>.
/// </summary>
public sealed class NoNakedShortCheck : IRiskCheck
{
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly PositionKeeper _positions;
    private readonly WorkingOrderBook _orders;

    public NoNakedShortCheck(
        IOptionsMonitor<RiskOptions> options,
        PositionKeeper positions,
        WorkingOrderBook orders)
    {
        _options = options;
        _positions = positions;
        _orders = orders;
    }

    // Sits between the throttle band (150/160/170) and PositionLimit
    // (200): naked-short is a hard inventory invariant, more
    // fundamental than the absolute |net| cap.
    public int Order => 180;

    public string Name => "no_naked_short";

    public RiskDecision Check(RiskContext ctx)
    {
        if (ctx.Side != OrderSide.Sell) return RiskDecision.Approve;

        var opts = _options.CurrentValue;
        var allow = RiskLimitsResolver.Resolve(
            opts, ctx.Owner.Value, ctx.FirmId, ctx.Symbol, l => l.AllowShortSell);
        if (allow == true) return RiskDecision.Approve;

        var currentLong = _positions.GetOrCreate(ctx.Owner, ctx.Symbol).NetQuantity;
        var openSellLeaves = _orders.SumOpenSellLeavesForSymbol(ctx.Owner, ctx.Symbol);

        // Modify (cancel-replace) projection — slice 3 of #122. The
        // original order is still in the book until the venue's
        // Replaced ER lands, so SumOpenSellLeavesForSymbol counts it.
        // For the projection to reflect the post-replace world we
        // subtract the original's leaves and add the replacement's
        // effective leaves (newQty - origCumQty). Without this, an
        // owner trying to downsize a working Sell that was already
        // pinned at their inventory ceiling would be incorrectly
        // rejected as if both legs co-existed.
        long projectionAdjustment = 0;
        if (ctx.ReplaceOriginalClOrdId is { } origId
            && _orders.TryGet(origId, out var orig)
            && orig is not null
            && orig.Side == OrderSide.Sell
            && string.Equals(orig.Symbol, ctx.Symbol, StringComparison.Ordinal)
            && orig.Owner == ctx.Owner)
        {
            // Only subtract if the original is still counted as open
            // (matches the predicate used in SumOpenSellLeavesForSymbol).
            if (orig.Status is not (OrderStatus.Filled or OrderStatus.Rejected
                or OrderStatus.Cancelled or OrderStatus.Replaced))
            {
                projectionAdjustment -= orig.LeavesQuantity;
                projectionAdjustment += ctx.EffectiveLeavesQuantity ?? ctx.Quantity;
            }
        }
        // The incoming Sell is already counted in openSellLeaves
        // because OrderSubmissionService.TryAdd runs before risk
        // evaluation. So `sellable` is the projected net assuming
        // every open Sell fills and no open Buy does.
        // For modifies the new order is NOT yet in the book — the
        // adjustment above accounts for it explicitly.
        var sellable = currentLong - (openSellLeaves + projectionAdjustment);
        if (sellable < 0)
        {
            return RiskDecision.Reject(
                $"naked short blocked: current long={currentLong}, open sell leaves={openSellLeaves} " +
                $"(set AllowShortSell=true to opt out)");
        }
        return RiskDecision.Approve;
    }
}
