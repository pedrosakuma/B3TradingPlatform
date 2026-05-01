using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Checks;

public sealed class MaxQuantityCheck : IRiskCheck
{
    private readonly RiskOptions _options;
    public MaxQuantityCheck(IOptions<RiskOptions> options) => _options = options.Value;

    public int Order => 100;
    public string Name => "max_quantity";

    public RiskDecision Check(RiskContext ctx)
    {
        var max = RiskLimitsResolver.Resolve(_options, ctx.Owner.Value, ctx.Symbol, l => l.MaxQuantity);
        if (max.HasValue && ctx.Quantity > max.Value)
            return RiskDecision.Reject($"quantity {ctx.Quantity} exceeds max {max.Value}");
        return RiskDecision.Approve;
    }
}

public sealed class MaxNotionalCheck : IRiskCheck
{
    private readonly RiskOptions _options;
    public MaxNotionalCheck(IOptions<RiskOptions> options) => _options = options.Value;

    public int Order => 100;
    public string Name => "max_notional";

    public RiskDecision Check(RiskContext ctx)
    {
        var max = RiskLimitsResolver.Resolve(_options, ctx.Owner.Value, ctx.Symbol, l => l.MaxNotional);
        if (!max.HasValue || !ctx.Price.HasValue)
            return RiskDecision.Approve; // no cap, or market order — let venue handle
        var notional = ctx.Price.Value * ctx.Quantity;
        if (notional > max.Value)
            return RiskDecision.Reject($"notional {notional} exceeds max {max.Value}");
        return RiskDecision.Approve;
    }
}
