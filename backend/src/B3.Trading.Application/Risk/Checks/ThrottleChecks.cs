using B3.Trading.Application.Risk.Accounting;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Checks;

/// <summary>
/// Rejects orders whose notional, added to the rolling-window total
/// already submitted by the same end-client (or firm), would exceed
/// the configured cap. Anti-runaway-bot guard — see
/// <see cref="Accounting.SlidingWindowLedger"/> for the
/// non-atomic-by-design semantics.
/// </summary>
public sealed class RollingNotionalCheck : IRiskCheck
{
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly RollingNotionalAccountant _accountant;

    public RollingNotionalCheck(
        IOptionsMonitor<RiskOptions> options,
        RollingNotionalAccountant accountant)
    {
        _options = options;
        _accountant = accountant;
    }

    public int Order => 150;
    public string Name => "rolling_notional";

    public RiskDecision Check(RiskContext ctx)
    {
        var rolling = _options.CurrentValue.RollingNotional;
        var endClientCap = ResolveCap(rolling.PerEndClient, ctx.Owner.Value)
                           ?? rolling.Default.Cap;
        var firmCap = string.IsNullOrWhiteSpace(ctx.FirmId)
            ? null
            : ResolveCap(rolling.PerFirm, ctx.FirmId);

        if (!endClientCap.HasValue && !firmCap.HasValue) return RiskDecision.Approve;

        var notional = _accountant.NotionalFor(ctx);
        if (notional <= 0m) return RiskDecision.Approve; // bypass already metered
        var window = _accountant.Window;

        if (endClientCap is { } ecCap)
        {
            var current = _accountant.EndClientLedger.Sum(ctx.Owner.Value, window);
            if (current + notional > ecCap)
                return RiskDecision.Reject(
                    $"rolling notional {current + notional:0.##} would exceed end-client cap {ecCap:0.##} over last {window.TotalSeconds:0}s");
        }
        if (firmCap is { } fmCap)
        {
            var current = _accountant.FirmLedger.Sum(ctx.FirmId!, window);
            if (current + notional > fmCap)
                return RiskDecision.Reject(
                    $"rolling notional {current + notional:0.##} would exceed firm cap {fmCap:0.##} over last {window.TotalSeconds:0}s");
        }
        return RiskDecision.Approve;
    }

    private static decimal? ResolveCap(IDictionary<string, RollingNotionalLimit> map, string key) =>
        map.TryGetValue(key, out var entry) ? entry.Cap : null;
}

/// <summary>
/// Rejects orders that would exceed an end-client's (or firm's) order
/// submission rate over a rolling window. Both ledgers are checked
/// when configured; the first cap exceeded wins.
/// </summary>
public sealed class OrderRateLimitCheck : IRiskCheck
{
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly OrderRateAccountant _accountant;

    public OrderRateLimitCheck(
        IOptionsMonitor<RiskOptions> options,
        OrderRateAccountant accountant)
    {
        _options = options;
        _accountant = accountant;
    }

    public int Order => 160;
    public string Name => "order_rate_limit";

    public RiskDecision Check(RiskContext ctx)
    {
        var rate = _options.CurrentValue.OrderRate;
        var endClientMax = ResolveMax(rate.PerEndClient, ctx.Owner.Value) ?? rate.Default.Max;
        var firmMax = string.IsNullOrWhiteSpace(ctx.FirmId)
            ? null
            : ResolveMax(rate.PerFirm, ctx.FirmId);

        if (!endClientMax.HasValue && !firmMax.HasValue) return RiskDecision.Approve;

        var window = _accountant.Window;

        if (endClientMax is { } ecMax)
        {
            var current = _accountant.EndClientLedger.Count(ctx.Owner.Value, window);
            if (current + 1 > ecMax)
                return RiskDecision.Reject(
                    $"order rate {current + 1} would exceed end-client cap {ecMax}/{window.TotalSeconds:0}s");
        }
        if (firmMax is { } fmMax)
        {
            var current = _accountant.FirmLedger.Count(ctx.FirmId!, window);
            if (current + 1 > fmMax)
                return RiskDecision.Reject(
                    $"order rate {current + 1} would exceed firm cap {fmMax}/{window.TotalSeconds:0}s");
        }
        return RiskDecision.Approve;
    }

    private static int? ResolveMax(IDictionary<string, OrderRateLimit> map, string key) =>
        map.TryGetValue(key, out var entry) ? entry.Max : null;
}
