using B3.Trading.MarketMakerBot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.MarketMakerBot.Tests;

public class MarketMakerPnlReporterTests
{
    [Fact]
    public void LogSnapshot_PublishesNullUnrealizedWithoutFreshMark_ThenValueWithMark()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-24T00:00:00Z"));
        var ledger = new MarketMakerPnlLedger(clock);
        var prices = new MarketPriceTracker(clock);
        var logger = new CapturingLogger<MarketMakerPnlReporter>();
        var options = Options.Create(new MarketMakerBotOptions
        {
            Telemetry = new MarketMakerTelemetryOptions
            {
                SnapshotInterval = TimeSpan.FromSeconds(30),
                MarkMaxAge = TimeSpan.FromSeconds(10),
            },
        });
        Assert.Equal(FillApplyStatus.Applied, ledger.Apply(new OwnFill(
            1, 1, "PETR4", true, 100, 30m, 100, 100, 0, true)).Status);
        var reporter = new MarketMakerPnlReporter(ledger, prices, options, clock, logger);

        reporter.LogSnapshot();
        Assert.Equal(clock.GetUtcNow(), logger.Entries[0]["AccountingPeriodStartedAtUtc"]);
        Assert.Null(logger.Entries[0]["UnrealizedPnl"]);
        Assert.Null(logger.Entries[0]["TotalPnl"]);
        Assert.Null(logger.Entries[0]["Mark"]);

        prices.OnTrade("PETR4", 31m);
        prices.SetConnected(true);
        reporter.LogSnapshot();
        Assert.Equal(100m, logger.Entries[1]["UnrealizedPnl"]);
        Assert.Equal(100m, logger.Entries[1]["TotalPnl"]);
        Assert.Equal(31m, logger.Entries[1]["Mark"]);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<Dictionary<string, object?>> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>());
        }
    }
}
