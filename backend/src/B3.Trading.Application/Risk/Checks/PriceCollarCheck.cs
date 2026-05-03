using B3.Trading.Application.Observability;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Checks;

public sealed class PriceCollarCheck : IRiskCheck
{
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly IReferencePrice _refPrice;

    public PriceCollarCheck(IOptionsMonitor<RiskOptions> options, IReferencePrice refPrice)
    {
        _options = options;
        _refPrice = refPrice;
    }

    public int Order => 300;
    public string Name => "price_collar";

    public RiskDecision Check(RiskContext ctx)
    {
        if (!ctx.Price.HasValue) return RiskDecision.Approve; // market order
        var opts = _options.CurrentValue;
        var collarPct = RiskLimitsResolver.Resolve(opts, ctx.Owner.Value, ctx.FirmId, ctx.Symbol, l => l.PriceCollarPercent);
        if (!collarPct.HasValue) return RiskDecision.Approve;

        var lookup = _refPrice.Lookup(ctx.Symbol);
        MetricsRegistry.RefPriceLookups.Add(1,
            new KeyValuePair<string, object?>("symbol", ctx.Symbol),
            new KeyValuePair<string, object?>("source", SourceTag(lookup.Source)));

        if (!lookup.Found || lookup.Price <= 0m)
        {
            // Fail-open: a configured collar with no reference cannot be
            // enforced. The counter below is the only signal ops gets
            // that this happened — a steady non-zero rate is the cue to
            // either seed the static table or fix the MD feed.
            MetricsRegistry.CollarBypassedNoReference.Add(1,
                new KeyValuePair<string, object?>("symbol", ctx.Symbol));
            return RiskDecision.Approve;
        }

        var refPx = lookup.Price;
        var lower = refPx * (1m - collarPct.Value / 100m);
        var upper = refPx * (1m + collarPct.Value / 100m);
        if (ctx.Price.Value < lower || ctx.Price.Value > upper)
            return RiskDecision.Reject(
                $"price {ctx.Price.Value} outside collar [{lower:0.####}, {upper:0.####}] around ref {refPx}");
        return RiskDecision.Approve;
    }

    private static string SourceTag(ReferencePriceSource source) => source switch
    {
        ReferencePriceSource.Live => "live",
        ReferencePriceSource.Fallback => "fallback",
        _ => "missing",
    };
}
