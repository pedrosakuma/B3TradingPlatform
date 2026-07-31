using B3.Trading.Application.Risk.Accounting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Risk;

/// <summary>
/// Periodically prunes empty buckets from the rolling-notional and
/// order-rate ledgers. Keeps the in-memory dictionaries from growing
/// unbounded under tenant churn (every distinct end-client/firm
/// allocates a bucket on first submit). Runs on a fixed cadence
/// (default 60s) — frequent enough to bound footprint, infrequent
/// enough to avoid contention with the hot path.
/// </summary>
public sealed class ThrottleLedgerSweeper : BackgroundService
{
    private readonly RollingNotionalAccountant _notional;
    private readonly OrderRateAccountant _rate;
    private readonly IOptionsMonitor<RiskOptions> _options;
    private readonly ILogger<ThrottleLedgerSweeper> _logger;
    private readonly TimeProvider _clock;

    public ThrottleLedgerSweeper(
        RollingNotionalAccountant notional,
        OrderRateAccountant rate,
        IOptionsMonitor<RiskOptions> options,
        ILogger<ThrottleLedgerSweeper> logger,
        TimeProvider clock)
    {
        _notional = notional;
        _rate = rate;
        _options = options;
        _logger = logger;
        _clock = clock;
    }

    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, _clock);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            SweepOnce();
        }
    }

    /// <summary>
    /// Runs a single sweep pass synchronously, independent of the
    /// hosted-service timer loop. Exposed so tests can exercise the
    /// pruning logic deterministically (inject a controllable
    /// <see cref="TimeProvider"/> into the accountants and call this
    /// directly) instead of racing the real <see cref="PeriodicTimer"/>
    /// against wall-clock <c>Task.Delay</c>.
    /// </summary>
    internal void SweepOnce()
    {
        // Window is read each call so config reloads take effect
        // without restarting the sweeper.
        var notionalWindow = _notional.Window;
        var rateWindow = _rate.Window;

        try
        {
            var n1 = _notional.EndClientLedger.SweepEmptyBuckets(notionalWindow);
            var n2 = _notional.FirmLedger.SweepEmptyBuckets(notionalWindow);
            var n3 = _notional.AlgoLedger.SweepEmptyBuckets(notionalWindow);
            var n4 = _rate.EndClientLedger.SweepEmptyBuckets(rateWindow);
            var n5 = _rate.FirmLedger.SweepEmptyBuckets(rateWindow);
            var n6 = _rate.AlgoLedger.SweepEmptyBuckets(rateWindow);
            if (n1 + n2 + n3 + n4 + n5 + n6 > 0)
                _logger.LogDebug(
                    "Throttle ledger sweep removed {Count} empty buckets",
                    n1 + n2 + n3 + n4 + n5 + n6);
        }
        catch (Exception ex)
        {
            // Sweeper failure must not take the host down.
            _logger.LogWarning(ex, "Throttle ledger sweep failed; will retry next interval");
        }
    }
}
