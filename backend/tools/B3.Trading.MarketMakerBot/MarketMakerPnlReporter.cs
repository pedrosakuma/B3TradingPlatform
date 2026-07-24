using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.MarketMakerBot;

internal sealed class MarketMakerPnlReporter : BackgroundService
{
    private readonly MarketMakerPnlLedger _ledger;
    private readonly MarketPriceTracker _prices;
    private readonly MarketMakerTelemetryOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<MarketMakerPnlReporter> _log;

    public MarketMakerPnlReporter(
        MarketMakerPnlLedger ledger,
        MarketPriceTracker prices,
        IOptions<MarketMakerBotOptions> options,
        TimeProvider clock,
        ILogger<MarketMakerPnlReporter> log)
    {
        _ledger = ledger;
        _prices = prices;
        _options = options.Value.Telemetry;
        _clock = clock;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.SnapshotInterval, _clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            LogSnapshot();
    }

    internal void LogSnapshot()
    {
        foreach (var snapshot in _ledger.SnapshotAll())
        {
            decimal? markPrice = null;
            decimal? unrealizedPnl = null;
            decimal? totalPnl = null;
            TimeSpan? markAge = null;
            if (_prices.TryGetFreshMark(snapshot.Symbol, _options.MarkMaxAge, out var mark))
            {
                markPrice = mark.Price;
                unrealizedPnl = snapshot.UnrealizedPnl(mark.Price);
                totalPnl = snapshot.TotalPnl(mark.Price);
                markAge = _clock.GetUtcNow() - mark.ObservedAtUtc;
            }

            _log.LogInformation(
                "[mm-pnl] accountingPeriodStartedAtUtc={AccountingPeriodStartedAtUtc} symbol={Symbol} position={Position} averageCost={AverageCost} realizedPnl={RealizedPnl} unrealizedPnl={UnrealizedPnl} totalPnl={TotalPnl} mark={Mark} markAge={MarkAge}",
                snapshot.AccountingPeriodStartedAtUtc,
                snapshot.Symbol,
                snapshot.Position,
                snapshot.AverageCost,
                snapshot.RealizedPnl,
                unrealizedPnl,
                totalPnl,
                markPrice,
                markAge);
        }
    }
}
