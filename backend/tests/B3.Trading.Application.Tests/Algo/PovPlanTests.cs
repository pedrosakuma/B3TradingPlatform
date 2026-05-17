using B3.Trading.Application;
using B3.Trading.Domain;
using Xunit;

namespace B3.Trading.Application.Tests.AlgoEngine;

/// <summary>
/// Slice-math tests for the POV scheduler (Q3.2 / #282). Mirrors the
/// shape of <see cref="VwapPlanTests"/>: every branch of
/// <see cref="PovPlan.SliceQty"/> + the slot-time helper.
/// </summary>
public class PovPlanTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    // ───────────────────────── PlannedAtUtc ─────────────────────────

    [Fact]
    public void PlannedAtUtc_FirstSlotFiresAtStart()
    {
        Assert.Equal(Start, PovPlan.PlannedAtUtc(Start, Tick, 0));
    }

    [Fact]
    public void PlannedAtUtc_EvenSpacing()
    {
        Assert.Equal(Start.AddSeconds(5), PovPlan.PlannedAtUtc(Start, Tick, 1));
        Assert.Equal(Start.AddSeconds(50), PovPlan.PlannedAtUtc(Start, Tick, 10));
    }

    [Fact]
    public void PlannedAtUtc_DeterministicAcrossInvocations()
    {
        for (var seq = 0; seq < 20; seq++)
        {
            var a = PovPlan.PlannedAtUtc(Start, Tick, seq);
            var b = PovPlan.PlannedAtUtc(Start, Tick, seq);
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void PlannedAtUtc_RejectsNonPositiveTickInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PovPlan.PlannedAtUtc(Start, TimeSpan.Zero, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PovPlan.PlannedAtUtc(Start, TimeSpan.FromSeconds(-1), 0));
    }

    [Fact]
    public void PlannedAtUtc_RejectsNegativeSliceSeq()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PovPlan.PlannedAtUtc(Start, Tick, -1));
    }

    // ───────────────────────── SliceQty ─────────────────────────

    [Fact]
    public void SliceQty_MarketWalkV_AtRate_SlicesAboutVTimesRate()
    {
        // Market traded 1000 shares, target rate 10%, nothing executed
        // yet → pending = 100. Plenty of remaining; min slice = 1.
        Assert.Equal(100, PovPlan.SliceQty(
            cumMarketVolume: 1000, executedCum: 0, remainingQuantity: 5000,
            participationRate: 0.10m, minSliceQty: 1));
    }

    [Fact]
    public void SliceQty_NoMarketVolume_ReturnsZero()
    {
        // Nothing traded yet → no opportunistic share to take.
        Assert.Equal(0, PovPlan.SliceQty(
            cumMarketVolume: 0, executedCum: 0, remainingQuantity: 5000,
            participationRate: 0.10m, minSliceQty: 1));
    }

    [Fact]
    public void SliceQty_ParentAhead_ReturnsZero()
    {
        // 10% of 1000 = 100 target, already executed 150 → ahead of
        // schedule; emit nothing this tick.
        Assert.Equal(0, PovPlan.SliceQty(
            cumMarketVolume: 1000, executedCum: 150, remainingQuantity: 4850,
            participationRate: 0.10m, minSliceQty: 1));
    }

    [Fact]
    public void SliceQty_CappedByRemaining_SoftCapAtTotalQty()
    {
        // 50% of 10000 = 5000 target, executed 0, but only 200 left.
        Assert.Equal(200, PovPlan.SliceQty(
            cumMarketVolume: 10000, executedCum: 0, remainingQuantity: 200,
            participationRate: 0.50m, minSliceQty: 1));
    }

    [Fact]
    public void SliceQty_ZeroRemaining_ReturnsZero()
    {
        Assert.Equal(0, PovPlan.SliceQty(
            cumMarketVolume: 10000, executedCum: 1000, remainingQuantity: 0,
            participationRate: 0.10m, minSliceQty: 1));
    }

    [Fact]
    public void SliceQty_BelowMinSliceQty_Defers()
    {
        // 10% of 50 = 5 pending; minSlice=10 → defer to next tick.
        Assert.Equal(0, PovPlan.SliceQty(
            cumMarketVolume: 50, executedCum: 0, remainingQuantity: 5000,
            participationRate: 0.10m, minSliceQty: 10));
    }

    [Fact]
    public void SliceQty_AtOrAboveMinSliceQty_Emits()
    {
        // 10% of 100 = 10 pending; minSlice=10 → exactly at floor.
        Assert.Equal(10, PovPlan.SliceQty(
            cumMarketVolume: 100, executedCum: 0, remainingQuantity: 5000,
            participationRate: 0.10m, minSliceQty: 10));
    }

    [Fact]
    public void SliceQty_FractionalShare_FloorsDown()
    {
        // 10% of 17 = 1.7 → floor 1.
        Assert.Equal(1, PovPlan.SliceQty(
            cumMarketVolume: 17, executedCum: 0, remainingQuantity: 5000,
            participationRate: 0.10m, minSliceQty: 1));
    }

    [Fact]
    public void SliceQty_RejectsInvalidRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PovPlan.SliceQty(100, 0, 100, 0m, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PovPlan.SliceQty(100, 0, 100, -0.1m, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PovPlan.SliceQty(100, 0, 100, 1.5m, 1));
    }

    [Fact]
    public void SliceQty_RejectsZeroMinSliceQty()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PovPlan.SliceQty(100, 0, 100, 0.10m, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PovPlan.SliceQty(100, 0, 100, 0.10m, -5));
    }

    // ───────────────────────── ClampPrice ─────────────────────────

    [Fact]
    public void ClampPrice_BuyTakesMin()
    {
        Assert.Equal(10m, PovPlan.ClampPrice(12m, 10m, OrderSide.Buy));
        Assert.Equal(8m, PovPlan.ClampPrice(8m, 10m, OrderSide.Buy));
    }

    [Fact]
    public void ClampPrice_SellTakesMax()
    {
        Assert.Equal(10m, PovPlan.ClampPrice(8m, 10m, OrderSide.Sell));
        Assert.Equal(12m, PovPlan.ClampPrice(12m, 10m, OrderSide.Sell));
    }

    [Fact]
    public void ClampPrice_NoLimit_ReturnsRefPrice()
    {
        Assert.Equal(12m, PovPlan.ClampPrice(12m, null, OrderSide.Buy));
        Assert.Null(PovPlan.ClampPrice(null, null, OrderSide.Buy));
    }

    [Fact]
    public void ClampPrice_NoRefPrice_ReturnsLimit()
    {
        Assert.Equal(10m, PovPlan.ClampPrice(null, 10m, OrderSide.Buy));
    }
}
