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
    private readonly IOptionsMonitor<RiskOptions> _options;

    public OrderRateAccountant(IOptionsMonitor<RiskOptions> options, TimeProvider clock)
    {
        _options = options;
        _perEndClient = new SlidingWindowLedger(clock);
        _perFirm = new SlidingWindowLedger(clock);
    }

    public SlidingWindowLedger EndClientLedger => _perEndClient;
    public SlidingWindowLedger FirmLedger => _perFirm;

    public TimeSpan Window => TimeSpan.FromSeconds(
        Math.Max(1, _options.CurrentValue.OrderRate.WindowSeconds));

    public void RecordAccepted(RiskContext ctx)
    {
        _perEndClient.Append(ctx.Owner.Value, 1m);
        if (!string.IsNullOrWhiteSpace(ctx.FirmId))
            _perFirm.Append(ctx.FirmId, 1m);
    }
}
