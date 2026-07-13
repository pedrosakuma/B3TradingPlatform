using B3.Trading.Application.Observability;
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
    private readonly IBeneficialOwnerResolver _beneficialOwners;

    public SelfTradePreventionCheck(
        IOptionsMonitor<RiskOptions> options,
        WorkingOrderBook orders,
        IBeneficialOwnerResolver beneficialOwners)
    {
        _options = options;
        _orders = orders;
        _beneficialOwners = beneficialOwners;
    }

    // Back-compat ctor for tests + legacy DI that don't wire the
    // beneficial-owner resolver yet. Cross-firm scope is unreachable
    // when constructed this way (the resolver collapses BO == owner
    // and only ever returns the input owner from OwnersFor).
    public SelfTradePreventionCheck(
        IOptionsMonitor<RiskOptions> options,
        WorkingOrderBook orders)
        : this(options, orders, new DefaultBeneficialOwnerResolver())
    {
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

        // Phase 1 — same-firm scope (pre-#433 behavior, unchanged).
        // PR #316 P2: scope the contra-order scan to the caller's firm
        // so an opposite-side working order in FIRM02 cannot reject a
        // FIRM01 order from the same JWT sub. Self-trading is a venue-
        // session concern (the matching engine groups orders by firm
        // session, not by end-client), so same-firm same-owner pairs
        // ARE the wash trade.
        foreach (var existing in _orders.ForEndClientAndFirm(ctx.FirmId, ctx.Owner))
        {
            if (existing.Side != oppositeSide) continue;
            if (!string.Equals(existing.Symbol, ctx.Symbol, StringComparison.Ordinal)) continue;
            if (!IsStillRestable(existing)) continue;

            MetricsRegistry.SelfTradeRejected.Add(1,
                new KeyValuePair<string, object?>("scope", "same_firm"),
                new KeyValuePair<string, object?>("mode", "block"));
            return RiskDecision.Reject(BuildReason("same_firm", existing, ctx));
        }

        // Phase 2 — cross-firm beneficial-owner scope (#433).
        // CVM 168 práticas equitativas: a single beneficial owner cannot
        // wash-trade across firms. Opt-in via EnforceCrossFirmStp so
        // single-firm and same-BO-on-purpose deployments retain the
        // pre-existing semantics. The matching engine on B3 isolates by
        // firm session, so a cross-firm contra pair is allowed to fill
        // at the venue — which is exactly the wash-trade vector the
        // platform must close on its side.
        var enforceCrossFirm = RiskLimitsResolver.Resolve(
            opts, ctx.Owner.Value, ctx.FirmId, ctx.Symbol, l => l.EnforceCrossFirmStp);
        if (enforceCrossFirm != true) return RiskDecision.Approve;

        var beneficialOwnerId = _beneficialOwners.Resolve(ctx.Owner);
        foreach (var siblingOwner in _beneficialOwners.OwnersFor(beneficialOwnerId))
        {
            foreach (var existing in _orders.ForEndClient(siblingOwner))
            {
                if (string.Equals(existing.FirmId, ctx.FirmId, StringComparison.Ordinal))
                {
                    // Already covered by Phase 1 or intentionally left to
                    // same-firm / venue-side STP semantics.
                    continue;
                }
                if (existing.Side != oppositeSide) continue;
                if (!string.Equals(existing.Symbol, ctx.Symbol, StringComparison.Ordinal)) continue;
                if (!IsStillRestable(existing)) continue;

                MetricsRegistry.SelfTradeRejected.Add(1,
                    new KeyValuePair<string, object?>("scope", "cross_firm"),
                    new KeyValuePair<string, object?>("mode", "block"));
                return RiskDecision.Reject(BuildReason("cross_firm", existing, ctx, beneficialOwnerId));
            }
        }

        return RiskDecision.Approve;
    }

    private static string BuildReason(string scope, Order existing, RiskContext ctx, string? beneficialOwnerId = null)
    {
        var price = existing.Price.HasValue
            ? existing.Price.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "MKT";
        var boHint = beneficialOwnerId is null
            ? ""
            : $" (beneficial_owner={beneficialOwnerId}, contra_firm={existing.FirmId})";
        return
            $"self_trade_prevention[{scope}]: own opposite-side {existing.Side} order " +
            $"{existing.LeavesQuantity}@{price} is working on {ctx.Symbol} " +
            $"(clOrdId={existing.ClOrdId}){boHint}; set AllowSelfTrade=true to opt out";
    }

    // "Still restable" = order is on the book and has unfilled quantity.
    // We exclude terminal states (Filled/Cancelled/Rejected/Replaced)
    // defensively even though terminal orders should also have
    // LeavesQuantity == 0.
    //
    // #570: also exclude stale orders (e.g. session_rolled after a
    // matching-engine restart). Every other WorkingOrderBook query
    // used for matching/risk purposes skips stale orders, and a stale
    // order can never itself be cancelled (OrderCancelService refuses
    // to cancel IsStale orders) — counting it here would permanently
    // lock the end-client out of the opposite side with no way out.
    private static bool IsStillRestable(Order o) =>
        o.LeavesQuantity > 0
        && !o.IsStale
        && o.Status is not OrderStatus.Filled
                       and not OrderStatus.Cancelled
                       and not OrderStatus.Rejected
                       and not OrderStatus.Replaced;

    // Used only by the legacy two-arg ctor for back-compat: collapses
    // every owner to its own BO so cross-firm scope can't fire even
    // if EnforceCrossFirmStp is set, when wired through a host that
    // didn't register the real resolver yet.
    private sealed class DefaultBeneficialOwnerResolver : IBeneficialOwnerResolver
    {
        public string Resolve(EndClientId owner) => owner.Value;
        public IReadOnlyCollection<EndClientId> OwnersFor(string beneficialOwnerId) =>
            new[] { new EndClientId(beneficialOwnerId) };
    }
}
