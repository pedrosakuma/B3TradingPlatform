using B3.Trading.MarketMakerBot;
using Microsoft.Extensions.Options;

namespace B3.Trading.MarketMakerBot.Tests;

public class VolatilitySpreadEstimatorTests
{
    [Fact]
    public void FirstValidTrade_EstablishesBaselineWithoutSample()
    {
        var estimator = Create(out _, minSamples: 1);
        estimator.SetConnected(true);

        Assert.Null(estimator.OnTrade("PETR4", 30m));
        var snapshot = estimator.GetSnapshot("PETR4");

        Assert.Null(snapshot.MoveEstimateTicks);
        Assert.Equal(0, snapshot.SampleCount);
        Assert.False(snapshot.IsReady);
        Assert.Equal(0, snapshot.AdditionalSpreadTicks);
    }

    [Fact]
    public void RepeatedValidTrade_AddsZeroMoveSample()
    {
        var estimator = Create(out _, minSamples: 1);
        estimator.SetConnected(true);
        estimator.OnTrade("PETR4", 30m);

        estimator.OnTrade("PETR4", 30m);
        var snapshot = estimator.GetSnapshot("PETR4");

        Assert.Equal(0m, snapshot.MoveEstimateTicks);
        Assert.Equal(1, snapshot.SampleCount);
        Assert.True(snapshot.IsReady);
        Assert.Equal(0, snapshot.AdditionalSpreadTicks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidTradePrice_IsIgnoredWithoutChangingBaseline(decimal invalidPrice)
    {
        var estimator = Create(out _, minSamples: 1);
        estimator.SetConnected(true);
        estimator.OnTrade("PETR4", 30m);

        estimator.OnTrade("PETR4", invalidPrice);
        var change = estimator.OnTrade("PETR4", 30.02m);

        var snapshot = estimator.GetSnapshot("PETR4");
        Assert.Equal(2m, snapshot.MoveEstimateTicks);
        Assert.NotNull(change);
        Assert.Equal(2, change.Value.Current.AdditionalSpreadTicks);
    }

    [Fact]
    public void WindowEviction_PrunesSamplesAndStalePreviousTradeOnRead()
    {
        var estimator = Create(out var clock, minSamples: 1, window: TimeSpan.FromSeconds(10));
        estimator.SetConnected(true);
        estimator.OnTrade("PETR4", 30m);
        Assert.NotNull(estimator.OnTrade("PETR4", 30.02m));

        clock.Advance(TimeSpan.FromSeconds(11));
        var snapshot = estimator.GetSnapshot("PETR4");

        Assert.Null(snapshot.MoveEstimateTicks);
        Assert.Equal(0, snapshot.SampleCount);
        Assert.Equal(0, snapshot.AdditionalSpreadTicks);
        var expiry = Assert.Single(estimator.Refresh());
        Assert.Equal(2, expiry.PreviousAdditionalSpreadTicks);
        Assert.Equal(0, expiry.Current.AdditionalSpreadTicks);

        estimator.OnTrade("PETR4", 31m);
        Assert.Equal(0, estimator.GetSnapshot("PETR4").SampleCount);
    }

    [Fact]
    public void MaxSamples_EvictsOldestMoves()
    {
        var estimator = Create(out _, minSamples: 1, maxSamples: 2);
        estimator.SetConnected(true);
        estimator.OnTrade("PETR4", 30m);
        estimator.OnTrade("PETR4", 30.01m); // 1 tick
        estimator.OnTrade("PETR4", 30.04m); // 3 ticks
        estimator.OnTrade("PETR4", 30.09m); // 5 ticks

        var snapshot = estimator.GetSnapshot("PETR4");

        Assert.Equal(2, snapshot.SampleCount);
        Assert.Equal(4m, snapshot.MoveEstimateTicks);
    }

    [Fact]
    public void MinimumSamples_KeepsStaticFallbackUntilReady()
    {
        var estimator = Create(out _, minSamples: 2);
        estimator.SetConnected(true);
        estimator.OnTrade("PETR4", 30m);
        Assert.Null(estimator.OnTrade("PETR4", 30.02m));
        Assert.Equal(0, estimator.GetSnapshot("PETR4").AdditionalSpreadTicks);

        var change = estimator.OnTrade("PETR4", 30.04m);

        Assert.NotNull(change);
        Assert.Equal(2, change.Value.Current.SampleCount);
        Assert.Equal(2, change.Value.Current.AdditionalSpreadTicks);
    }

    [Fact]
    public void MeanMultiplierCeilingAndCap_AreDeterministicDecimals()
    {
        var estimator = Create(out _, minSamples: 3, multiplier: 1.5m, cap: 4);
        estimator.SetConnected(true);
        estimator.OnTrade("PETR4", 30m);
        estimator.OnTrade("PETR4", 30.01m); // 1
        estimator.OnTrade("PETR4", 30.03m); // 2
        var change = estimator.OnTrade("PETR4", 30.07m); // 4; mean 7/3; ceil(3.5) = 4

        Assert.NotNull(change);
        Assert.Equal(7m / 3m, change.Value.Current.MoveEstimateTicks);
        Assert.Equal(4, change.Value.Current.AdditionalSpreadTicks);
    }

    [Fact]
    public void MinimumScalePositiveMultiplier_DoesNotOverflowThresholdArithmetic()
    {
        const decimal minimumScaleMultiplier = 0.0000000000000000000000000001m;
        var estimator = Create(out _, minSamples: 1, multiplier: minimumScaleMultiplier);
        estimator.SetConnected(true);
        estimator.OnTrade("PETR4", 30m);

        var change = estimator.OnTrade("PETR4", 30.02m);

        Assert.NotNull(change);
        Assert.Equal(minimumScaleMultiplier, checked(1m * minimumScaleMultiplier));
        Assert.Equal(1, change.Value.Current.AdditionalSpreadTicks);
    }

    [Fact]
    public void ScalingOverflow_SaturatesToConfiguredCap()
    {
        var estimator = Create(out _, minSamples: 1, multiplier: decimal.MaxValue, cap: 7);
        estimator.SetConnected(true);
        estimator.OnTrade("PETR4", 30m);

        var change = estimator.OnTrade("PETR4", 30.02m);

        Assert.NotNull(change);
        var estimate = 2m;
        var multiplier = decimal.MaxValue;
        Assert.Throws<OverflowException>(() => checked(estimate * multiplier));
        Assert.Equal(7, change.Value.Current.AdditionalSpreadTicks);
    }

    [Fact]
    public void SymbolsHaveIndependentBaselinesAndSamples()
    {
        var estimator = Create(out _, minSamples: 1, symbols: ["PETR4", "VALE3"]);
        estimator.SetConnected(true);
        estimator.OnTrade("PETR4", 30m);
        estimator.OnTrade("VALE3", 70m);
        estimator.OnTrade("PETR4", 30.01m);
        estimator.OnTrade("VALE3", 70.03m);

        Assert.Equal(1m, estimator.GetSnapshot("PETR4").MoveEstimateTicks);
        Assert.Equal(3m, estimator.GetSnapshot("VALE3").MoveEstimateTicks);
    }

    [Fact]
    public void DisconnectFallsBackToStaticAndReconnectRestoresRetainedReadyState()
    {
        var estimator = Create(out _, minSamples: 1);
        estimator.SetConnected(true);
        estimator.OnTrade("PETR4", 30m);
        estimator.OnTrade("PETR4", 30.02m);
        Assert.Equal(2, estimator.GetSnapshot("PETR4").AdditionalSpreadTicks);

        var disconnected = Assert.Single(estimator.SetConnected(false));
        Assert.Equal(0, disconnected.Current.AdditionalSpreadTicks);
        Assert.False(disconnected.Current.IsConnected);

        var reconnected = Assert.Single(estimator.SetConnected(true));
        Assert.Equal(2, reconnected.Current.AdditionalSpreadTicks);
        Assert.True(reconnected.Current.IsConnected);
    }

    [Fact]
    public void ReconnectAfterWindowExpiry_RemainsStaticUntilNewMoveSample()
    {
        var estimator = Create(out var clock, minSamples: 1, window: TimeSpan.FromSeconds(10));
        estimator.SetConnected(true);
        estimator.OnTrade("PETR4", 30m);
        estimator.OnTrade("PETR4", 30.02m);
        estimator.SetConnected(false);
        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.Empty(estimator.SetConnected(true));
        Assert.Equal(0, estimator.GetSnapshot("PETR4").AdditionalSpreadTicks);

        estimator.OnTrade("PETR4", 31m);
        Assert.Equal(0, estimator.GetSnapshot("PETR4").SampleCount);
        var change = estimator.OnTrade("PETR4", 31.01m);
        Assert.NotNull(change);
        Assert.Equal(1, change.Value.Current.AdditionalSpreadTicks);
    }

    private static VolatilitySpreadEstimator Create(
        out ManualTimeProvider clock,
        int minSamples = 1,
        int maxSamples = 120,
        TimeSpan? window = null,
        decimal multiplier = 1m,
        int cap = 20,
        string[]? symbols = null)
    {
        clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-24T00:00:00Z"));
        var instruments = (symbols ?? ["PETR4"]).Select((symbol, index) => new InstrumentConfig
        {
            Symbol = symbol,
            SecurityId = (ulong)(index + 1),
            RefPrice = 30m,
            TickSize = 0.01m,
            SpreadTicks = 5,
            VolatilitySpread = new VolatilitySpreadConfig
            {
                Enabled = true,
                Window = window ?? TimeSpan.FromMinutes(1),
                MaxSamples = maxSamples,
                MinSamples = minSamples,
                Multiplier = multiplier,
                MaxAdditionalSpreadTicks = cap,
            },
        }).ToList();
        return new VolatilitySpreadEstimator(
            Options.Create(new MarketMakerBotOptions { Instruments = instruments }),
            clock);
    }
}
