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
        var algoCap = (ctx.ParentAlgoId.HasValue
                       && !string.IsNullOrWhiteSpace(ctx.AlgoType)
                       && !string.IsNullOrWhiteSpace(ctx.FirmId))
            ? ResolveCap(rolling.PerAlgoType, ctx.AlgoType!)
            : null;

        if (!endClientCap.HasValue && !firmCap.HasValue && !algoCap.HasValue)
            return RiskDecision.Approve;
        if (_accountant.IsRecoveryFenced)
            return RiskDecision.Reject(
                "rolling notional unavailable during conservative restart fence");

        var notional = _accountant.NotionalFor(ctx);
        if (notional <= 0m) return RiskDecision.Approve; // bypass already metered
        var window = _accountant.Window;

        if (endClientCap is { } ecCap)
        {
            var current = _accountant.EndClientLedger.Sum(ctx.Owner.Value, window);
            if (current + notional > ecCap)
            {
                Observability.MetricsRegistry.ThrottleRejected.Add(1,
                    new KeyValuePair<string, object?>("check", "rolling_notional"),
                    new KeyValuePair<string, object?>("scope", "end_client"));
                return RiskDecision.Reject(
                    $"rolling notional {current + notional:0.##} would exceed end-client cap {ecCap:0.##} over last {window.TotalSeconds:0}s");
            }
        }
        if (firmCap is { } fmCap)
        {
            var current = _accountant.FirmLedger.Sum(ctx.FirmId!, window);
            if (current + notional > fmCap)
            {
                Observability.MetricsRegistry.ThrottleRejected.Add(1,
                    new KeyValuePair<string, object?>("check", "rolling_notional"),
                    new KeyValuePair<string, object?>("scope", "firm"));
                return RiskDecision.Reject(
                    $"rolling notional {current + notional:0.##} would exceed firm cap {fmCap:0.##} over last {window.TotalSeconds:0}s");
            }
        }
        if (algoCap is { } agCap)
        {
            var key = Accounting.RollingNotionalAccountant.AlgoKey(ctx.FirmId!, ctx.ParentAlgoId!.Value);
            var current = _accountant.AlgoLedger.Sum(key, window);
            if (current + notional > agCap)
            {
                Observability.MetricsRegistry.ThrottleRejected.Add(1,
                    new KeyValuePair<string, object?>("check", "rolling_notional"),
                    new KeyValuePair<string, object?>("scope", "algo"),
                    new KeyValuePair<string, object?>("algoType", ctx.AlgoType));
                return RiskDecision.Reject(
                    $"rolling notional {current + notional:0.##} would exceed per-algo cap {agCap:0.##} over last {window.TotalSeconds:0}s");
            }
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
        var algoMax = (ctx.ParentAlgoId.HasValue
                       && !string.IsNullOrWhiteSpace(ctx.AlgoType)
                       && !string.IsNullOrWhiteSpace(ctx.FirmId))
            ? ResolveMax(rate.PerAlgoType, ctx.AlgoType!)
            : null;

        if (!endClientMax.HasValue && !firmMax.HasValue && !algoMax.HasValue)
            return RiskDecision.Approve;
        if (_accountant.IsRecoveryFenced)
            return RiskDecision.Reject(
                "order rate unavailable during conservative restart fence");

        var window = _accountant.Window;

        if (endClientMax is { } ecMax)
        {
            var current = _accountant.EndClientLedger.Count(ctx.Owner.Value, window);
            if (current + 1 > ecMax)
            {
                Observability.MetricsRegistry.ThrottleRejected.Add(1,
                    new KeyValuePair<string, object?>("check", "order_rate"),
                    new KeyValuePair<string, object?>("scope", "end_client"));
                return RiskDecision.Reject(
                    $"order rate {current + 1} would exceed end-client cap {ecMax}/{window.TotalSeconds:0}s");
            }
        }
        if (firmMax is { } fmMax)
        {
            var current = _accountant.FirmLedger.Count(ctx.FirmId!, window);
            if (current + 1 > fmMax)
            {
                Observability.MetricsRegistry.ThrottleRejected.Add(1,
                    new KeyValuePair<string, object?>("check", "order_rate"),
                    new KeyValuePair<string, object?>("scope", "firm"));
                return RiskDecision.Reject(
                    $"order rate {current + 1} would exceed firm cap {fmMax}/{window.TotalSeconds:0}s");
            }
        }
        if (algoMax is { } agMax)
        {
            var key = Accounting.RollingNotionalAccountant.AlgoKey(ctx.FirmId!, ctx.ParentAlgoId!.Value);
            var current = _accountant.AlgoLedger.Count(key, window);
            if (current + 1 > agMax)
            {
                Observability.MetricsRegistry.ThrottleRejected.Add(1,
                    new KeyValuePair<string, object?>("check", "order_rate"),
                    new KeyValuePair<string, object?>("scope", "algo"),
                    new KeyValuePair<string, object?>("algoType", ctx.AlgoType));
                return RiskDecision.Reject(
                    $"order rate {current + 1} would exceed per-algo cap {agMax}/{window.TotalSeconds:0}s");
            }
        }
        return RiskDecision.Approve;
    }

    private static int? ResolveMax(IDictionary<string, OrderRateLimit> map, string key) =>
        map.TryGetValue(key, out var entry) ? entry.Max : null;
}
