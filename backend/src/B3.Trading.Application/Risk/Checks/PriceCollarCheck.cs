using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Checks;

public sealed class PriceCollarCheck : IRiskCheck
{
    private readonly RiskOptions _options;
    private readonly IReferencePrice _refPrice;

    public PriceCollarCheck(IOptions<RiskOptions> options, IReferencePrice refPrice)
    {
        _options = options.Value;
        _refPrice = refPrice;
    }

    public int Order => 300;
    public string Name => "price_collar";

    public RiskDecision Check(RiskContext ctx)
    {
        if (!ctx.Price.HasValue) return RiskDecision.Approve; // market order
        var collarPct = RiskLimitsResolver.Resolve(_options, ctx.Owner.Value, ctx.Symbol, l => l.PriceCollarPercent);
        if (!collarPct.HasValue) return RiskDecision.Approve;
        if (!_refPrice.TryGet(ctx.Symbol, out var refPx) || refPx <= 0m)
            return RiskDecision.Approve; // no reference; can't enforce

        var lower = refPx * (1m - collarPct.Value / 100m);
        var upper = refPx * (1m + collarPct.Value / 100m);
        if (ctx.Price.Value < lower || ctx.Price.Value > upper)
            return RiskDecision.Reject(
                $"price {ctx.Price.Value} outside collar [{lower:0.####}, {upper:0.####}] around ref {refPx}");
        return RiskDecision.Approve;
    }
}
