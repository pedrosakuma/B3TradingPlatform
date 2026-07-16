using B3.Trading.Application.MarketData;
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
    private readonly IMarketValueCalculator _values;

    public MaxNotionalCheck(
        IOptionsMonitor<RiskOptions> options,
        IMarketValueCalculator? values = null)
    {
        _options = options;
        _values = values ?? EquityMarketValueCalculator.Instance;
    }

    public int Order => 100;
    public string Name => "max_notional";

    public RiskDecision Check(RiskContext ctx)
    {
        var opts = _options.CurrentValue;
        var max = RiskLimitsResolver.Resolve(opts, ctx.Owner.Value, ctx.FirmId, ctx.Symbol, l => l.MaxNotional);
        if (!max.HasValue || !ctx.Price.HasValue)
            return RiskDecision.Approve; // no cap, or market order — let venue handle
        // OPT-B (#484): _values applies contractMultiplier for option
        // symbols; equity stays price * qty (Equity fallback).
        var notional = _values.GetNotional(
            ctx.Symbol, ctx.Price.Value, ctx.ExecutableQuantity);
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
    private readonly IMarketValueCalculator _values;
    private readonly SymbolDirectory? _directory;

    public MinNotionalCheck(
        IOptionsMonitor<RiskOptions> options,
        IMarketValueCalculator? values = null,
        SymbolDirectory? directory = null)
    {
        _options = options;
        _values = values ?? EquityMarketValueCalculator.Instance;
        _directory = directory;
    }

    public int Order => 110; // after max-quantity / max-notional, before throttles
    public string Name => "min_notional";

    public RiskDecision Check(RiskContext ctx)
    {
        var opts = _options.CurrentValue;
        var min = RiskLimitsResolver.Resolve(opts, ctx.Owner.Value, ctx.FirmId, ctx.Symbol, l => l.MinNotional);
        if (!min.HasValue || !ctx.Price.HasValue)
            return RiskDecision.Approve; // no floor configured, or market order

        // OPT-C (#485). B3 OPT channel relaxes minPx to 0 for equity
        // options (upstream B3MatchingPlatform#473): cabinet trades and
        // worthless out-of-the-money closeouts legitimately price at 0.
        // The MinNotional dust floor is a fat-finger guard for equities;
        // applying it to a zero-priced option closeout would reject a
        // venue-legal order with a spurious dust reason. Skip when the
        // symbol resolves as Option AND price is exactly 0 — any
        // positive option price still trips the floor (so a 0.005×100=0.5
        // BRL real option still gets caught if the floor is set higher).
        if (ctx.Price.Value == 0m
            && _directory is { } dir
            && dir.TryGetSpec(ctx.Symbol, out var spec)
            && spec.SecurityType == SecurityType.Option)
        {
            return RiskDecision.Approve;
        }

        // OPT-B (#484): notional is in BRL (price * qty * multiplier
        // for options); fee/dust math compares apples-to-apples.
        var notional = _values.GetNotional(
            ctx.Symbol, ctx.Price.Value, ctx.ExecutableQuantity);
        if (notional < min.Value)
            return RiskDecision.Reject($"notional {notional} below min {min.Value}");
        return RiskDecision.Approve;
    }
}
