using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Checks;

public sealed class PositionLimitCheck : IRiskCheck
{
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly PositionKeeper _positions;
    private readonly WorkingOrderBook _orders;

    public PositionLimitCheck(IOptionsMonitor<RiskOptions> options, PositionKeeper positions)
        : this(options, positions, new WorkingOrderBook())
    {
    }

    public PositionLimitCheck(
        IOptionsMonitor<RiskOptions> options,
        PositionKeeper positions,
        WorkingOrderBook orders)
    {
        _options = options;
        _positions = positions;
        _orders = orders;
    }

    public int Order => 200;
    public string Name => "position_limit";

    public RiskDecision Check(RiskContext ctx)
    {
        var opts = _options.CurrentValue;
        var limit = RiskLimitsResolver.Resolve(opts, ctx.Owner.Value, ctx.FirmId, ctx.Symbol, l => l.PositionLimit);
        if (!limit.HasValue) return RiskDecision.Approve;

        var current = _positions.GetOrCreate(ctx.FirmId, ctx.Owner, ctx.Symbol).NetQuantity;
        var openLeaves = _orders.SumOpenLeavesForSymbolAndFirm(
            ctx.FirmId, ctx.Owner, ctx.Symbol, ctx.Side);
        var adjustment = ProjectionAdjustment(ctx);
        var directionalExposure = openLeaves + adjustment;
        var signed = ctx.Side == OrderSide.Buy
            ? directionalExposure
            : -directionalExposure;
        var projected = Math.Abs(current + signed);
        if (projected > limit.Value)
            return RiskDecision.Reject(
                $"projected position {projected} exceeds limit {limit.Value}");
        return RiskDecision.Approve;
    }

    private long ProjectionAdjustment(RiskContext ctx)
    {
        if (ctx.ReplaceOriginalClOrdId is { } originalClOrdId)
        {
            long adjustment = ctx.ExecutableQuantity;
            if (_orders.TryGet(originalClOrdId, out var original)
                && original is not null
                && original.Owner == ctx.Owner
                && original.Side == ctx.Side
                && string.Equals(original.FirmId, ctx.FirmId, StringComparison.Ordinal)
                && string.Equals(original.Symbol, ctx.Symbol, StringComparison.Ordinal)
                && !original.IsStale
                && original.Status is not (OrderStatus.Filled or OrderStatus.Cancelled
                    or OrderStatus.Rejected or OrderStatus.Replaced))
            {
                adjustment -= original.LeavesQuantity;
            }
            return adjustment;
        }

        if (ctx.EvaluatedClOrdId is { } clOrdId
            && _orders.TryGet(clOrdId, out var evaluated)
            && evaluated is not null
            && !evaluated.IsStale
            && evaluated.Status is not (OrderStatus.Filled or OrderStatus.Cancelled
                or OrderStatus.Rejected or OrderStatus.Replaced))
        {
            return 0;
        }

        return ctx.ExecutableQuantity;
    }
}
