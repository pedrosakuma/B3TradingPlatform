using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk.Accounting;

/// <summary>
/// Holds the per-end-client and per-firm sliding ledgers used by
/// <see cref="Checks.OrderRateLimitCheck"/>. Each accepted submit
/// counts as a single entry of value 1 in both ledgers.
/// </summary>
public sealed class OrderRateAccountant : IRiskAccountant
{
    private readonly SlidingWindowLedger _perEndClient;
    private readonly SlidingWindowLedger _perFirm;
    private readonly SlidingWindowLedger _perAlgo;
    private readonly IOptionsMonitor<RiskOptions> _options;

    public OrderRateAccountant(IOptionsMonitor<RiskOptions> options, TimeProvider clock)
    {
        _options = options;
        _perEndClient = new SlidingWindowLedger(clock);
        _perFirm = new SlidingWindowLedger(clock);
        _perAlgo = new SlidingWindowLedger(clock);
    }

    public SlidingWindowLedger EndClientLedger => _perEndClient;
    public SlidingWindowLedger FirmLedger => _perFirm;

    /// <summary>#435. Per-(firm, parentAlgoId) ledger.</summary>
    public SlidingWindowLedger AlgoLedger => _perAlgo;

    public TimeSpan Window => TimeSpan.FromSeconds(
        Math.Max(1, _options.CurrentValue.OrderRate.WindowSeconds));

    public void RecordAccepted(RiskContext ctx)
    {
        _perEndClient.Append(ctx.Owner.Value, 1m);
        if (!string.IsNullOrWhiteSpace(ctx.FirmId))
            _perFirm.Append(ctx.FirmId, 1m);
        if (ctx.ParentAlgoId is { } algoId && !string.IsNullOrWhiteSpace(ctx.FirmId))
            _perAlgo.Append(RollingNotionalAccountant.AlgoKey(ctx.FirmId, algoId), 1m);
    }
}
