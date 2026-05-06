using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Checks;

public sealed class MaxQuantityCheck : IRiskCheck
{
    private readonly IOptionsMonitor<RiskOptions> _options;
    public MaxQuantityCheck(IOptionsMonitor<RiskOptions> options) => _options = options;

    public int Order => 100;
    public string Name => "max_quantity";

    public RiskDecision Check(RiskContext ctx)
    {
        var opts = _options.CurrentValue;
        var max = RiskLimitsResolver.Resolve(opts, ctx.Owner.Value, ctx.FirmId, ctx.Symbol, l => l.MaxQuantity);
        if (max.HasValue && ctx.Quantity > max.Value)
            return RiskDecision.Reject($"quantity {ctx.Quantity} exceeds max {max.Value}");
        return RiskDecision.Approve;
    }
}

public sealed class MaxNotionalCheck : IRiskCheck
{
    private readonly IOptionsMonitor<RiskOptions> _options;
    public MaxNotionalCheck(IOptionsMonitor<RiskOptions> options) => _options = options;

    public int Order => 100;
    public string Name => "max_notional";

    public RiskDecision Check(RiskContext ctx)
    {
        var opts = _options.CurrentValue;
        var max = RiskLimitsResolver.Resolve(opts, ctx.Owner.Value, ctx.FirmId, ctx.Symbol, l => l.MaxNotional);
        if (!max.HasValue || !ctx.Price.HasValue)
            return RiskDecision.Approve; // no cap, or market order — let venue handle
        var notional = ctx.Price.Value * ctx.Quantity;
        if (notional > max.Value)
            return RiskDecision.Reject($"notional {notional} exceeds max {max.Value}");
        return RiskDecision.Approve;
    }
}

/// <summary>
/// Anti-dust pre-trade gate. Rejects Limit orders whose notional
/// (price × quantity) sits below a per-symbol/firm/end-client floor.
/// Market orders skip the check (no price to evaluate; the venue and
/// the lot-size / max-quantity gates already bound the worst case).
///
/// Rationale: B3 cash equities have an implicit "lote padrão" floor
/// that the lot-size gate already enforces in shares, but a notional
/// floor is the operationally meaningful one for risk and surveillance
/// (avoids fragmented executions used to mark prices, dust positions
/// nobody can clear, and trivially-small orders that consume gateway
/// throughput for no economic purpose). Also serves as a cheap guard
/// against typo-fat-finger in the price field (e.g. 0.01 instead of
/// 30.00 on PETR4 still passes max-quantity but trips min-notional).
///
/// Default semantics when unset everywhere in the precedence chain
/// are permissive: no floor enforced (Approve). Configure via
/// <c>Trading:Risk:Default:MinNotional</c> or any of the per-X
/// overrides.
/// </summary>
public sealed class MinNotionalCheck : IRiskCheck
{
    private readonly IOptionsMonitor<RiskOptions> _options;
    public MinNotionalCheck(IOptionsMonitor<RiskOptions> options) => _options = options;

    public int Order => 110; // after max-quantity / max-notional, before throttles
    public string Name => "min_notional";

    public RiskDecision Check(RiskContext ctx)
    {
        var opts = _options.CurrentValue;
        var min = RiskLimitsResolver.Resolve(opts, ctx.Owner.Value, ctx.FirmId, ctx.Symbol, l => l.MinNotional);
        if (!min.HasValue || !ctx.Price.HasValue)
            return RiskDecision.Approve; // no floor configured, or market order
        var notional = ctx.Price.Value * ctx.Quantity;
        if (notional < min.Value)
            return RiskDecision.Reject($"notional {notional} below min {min.Value}");
        return RiskDecision.Approve;
    }
}
