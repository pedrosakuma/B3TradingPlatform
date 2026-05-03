using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Checks;

public sealed class PositionLimitCheck : IRiskCheck
{
    private readonly RiskOptions _options;
    private readonly PositionKeeper _positions;

    public PositionLimitCheck(IOptions<RiskOptions> options, PositionKeeper positions)
    {
        _options = options.Value;
        _positions = positions;
    }

    public int Order => 200;
    public string Name => "position_limit";

    public RiskDecision Check(RiskContext ctx)
    {
        var limit = RiskLimitsResolver.Resolve(_options, ctx.Owner.Value, ctx.FirmId, ctx.Symbol, l => l.PositionLimit);
        if (!limit.HasValue) return RiskDecision.Approve;

        var current = _positions.GetOrCreate(ctx.Owner, ctx.Symbol).NetQuantity;
        var signed = ctx.Side == OrderSide.Buy ? ctx.Quantity : -ctx.Quantity;
        var projected = Math.Abs(current + signed);
        if (projected > limit.Value)
            return RiskDecision.Reject(
                $"projected position {projected} exceeds limit {limit.Value}");
        return RiskDecision.Approve;
    }
}
