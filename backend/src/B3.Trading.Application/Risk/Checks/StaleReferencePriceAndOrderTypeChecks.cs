using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Checks;

/// <summary>
/// Pre-trade gate that blocks Market orders when the platform has no
/// live reference price for the symbol. "Live" means
/// <see cref="ReferencePriceSource.Live"/> — a fresh sample from the
/// MD feed under the configured staleness budget. Static-table
/// fallback (<see cref="ReferencePriceSource.Fallback"/>) and missing
/// readings both reject with <c>stale_market_data</c>.
///
/// <para>
/// Limit orders bypass this check entirely: they carry an explicit
/// price and the band consequence of a degraded feed is already the
/// concern of <see cref="PriceCollarCheck"/>. Market orders are the
/// dangerous case — without a live anchor the user is sweeping the
/// book at whatever's there, which during an MD outage may be a
/// stale top-of-book that no longer reflects the venue.
/// </para>
///
/// <para>
/// Default semantics when <see cref="RiskLimits.MarketRequiresLiveRef"/>
/// is unset everywhere in the precedence chain are conservative
/// (<c>true</c> — Market requires Live). Set <c>false</c> per-firm /
/// per-end-client to opt out (e.g. test accounts). Pipeline order=295,
/// just before <see cref="PriceCollarCheck"/> at 300 — same locality
/// (both are reference-price-driven) and the cheaper short-circuit
/// runs first.
/// </para>
/// </summary>
public sealed class StaleReferencePriceCheck : IRiskCheck
{
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly IReferencePrice _refPrice;

    public StaleReferencePriceCheck(IOptionsMonitor<RiskOptions> options, IReferencePrice refPrice)
    {
        _options = options;
        _refPrice = refPrice;
    }

    public int Order => 295;
    public string Name => "stale_market_data";

    public RiskDecision Check(RiskContext ctx)
    {
        // Only Market orders need a live reference; Limit carries its
        // own price. The legitimate-Market-with-Price case does not
        // exist in our intake (OrdersEndpoints normalises Market →
        // Price=null), but checking Type rather than HasPrice keeps
        // the rule explicit and survives any future intake change.
        if (ctx.Type != OrderType.Market)
            return RiskDecision.Approve;

        var opts = _options.CurrentValue;
        var requireLive = RiskLimitsResolver.Resolve(
            opts, ctx.Owner.Value, ctx.FirmId, ctx.Symbol,
            l => l.MarketRequiresLiveRef) ?? true;

        var lookup = _refPrice.Lookup(ctx.Symbol);

        // Always reject Missing — even with the toggle off, sweeping
        // the book against an unknown symbol with zero price context
        // is never the user's intent. (A market order on a typo
        // ticker would otherwise reach the venue.)
        if (lookup.Source == ReferencePriceSource.Missing)
            return RiskDecision.Reject($"no reference price for '{ctx.Symbol}' — Market blocked");

        if (requireLive && lookup.Source != ReferencePriceSource.Live)
            return RiskDecision.Reject($"reference price for '{ctx.Symbol}' is not live (source={lookup.Source}); Market blocked");

        return RiskDecision.Approve;
    }
}

/// <summary>
/// Pre-trade gate that enforces an optional whitelist of
/// <see cref="OrderType"/> values per scope
/// (<see cref="RiskLimits.AllowedOrderTypes"/>). When the resolved
/// list is null/empty the check is a no-op (every type the venue
/// supports passes); otherwise a submission whose type is missing
/// from the list is rejected with <c>order_type_blocked</c>.
///
/// <para>
/// Pipeline order=15, between <see cref="SymbolHaltedCheck"/>
/// (order 10) and the per-instrument rules (20+). Cheap to run and
/// independent of any other state, so it can short-circuit the rest
/// of the pipeline for misconfigured order types before any allocation
/// or per-symbol lookup runs.
/// </para>
/// </summary>
public sealed class OrderTypeAllowedCheck : IRiskCheck
{
    private readonly IOptionsMonitor<RiskOptions> _options;

    public OrderTypeAllowedCheck(IOptionsMonitor<RiskOptions> options) => _options = options;

    public int Order => 15;
    public string Name => "order_type_blocked";

    public RiskDecision Check(RiskContext ctx)
    {
        var opts = _options.CurrentValue;
        var allowed = RiskLimitsResolver.ResolveRef(
            opts, ctx.Owner.Value, ctx.FirmId, ctx.Symbol,
            l => l.AllowedOrderTypes,
            v => v.Count > 0);

        if (allowed is null) return RiskDecision.Approve;

        foreach (var name in allowed)
        {
            if (Enum.TryParse<OrderType>(name, ignoreCase: true, out var t) && t == ctx.Type)
                return RiskDecision.Approve;
        }

        return RiskDecision.Reject(
            $"order type '{ctx.Type}' not in allowed list for this scope ({string.Join(",", allowed)})");
    }
}
