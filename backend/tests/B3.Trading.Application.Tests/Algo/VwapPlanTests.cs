using B3.Trading.Application;
using B3.Trading.Domain;
using Xunit;

namespace B3.Trading.Application.Tests.AlgoEngine;

public class VwapPlanTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    // ───────────────────────── PlannedAtUtc ─────────────────────────

    [Fact]
    public void PlannedAtUtc_FirstSlotFiresAtStart()
    {
        Assert.Equal(Start, VwapPlan.PlannedAtUtc(Start, Tick, 0));
    }

    [Fact]
    public void PlannedAtUtc_EvenSpacing()
    {
        Assert.Equal(Start.AddSeconds(30), VwapPlan.PlannedAtUtc(Start, Tick, 1));
        Assert.Equal(Start.AddSeconds(60), VwapPlan.PlannedAtUtc(Start, Tick, 2));
        Assert.Equal(Start.AddSeconds(300), VwapPlan.PlannedAtUtc(Start, Tick, 10));
    }

    [Fact]
    public void PlannedAtUtc_DeterministicAcrossInvocations()
    {
        // Scheduler + engine recompute independently; drift would
        // split-brain the tick. Hammer the helper for byte-equality.
        for (var seq = 0; seq < 20; seq++)
        {
            var a = VwapPlan.PlannedAtUtc(Start, Tick, seq);
            var b = VwapPlan.PlannedAtUtc(Start, Tick, seq);
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void PlannedAtUtc_RejectsNonPositiveTickInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VwapPlan.PlannedAtUtc(Start, TimeSpan.Zero, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VwapPlan.PlannedAtUtc(Start, TimeSpan.FromSeconds(-1), 0));
    }

    [Fact]
    public void PlannedAtUtc_RejectsNegativeSliceSeq()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VwapPlan.PlannedAtUtc(Start, Tick, -1));
    }

    // ───────────────────────── SlotCount ─────────────────────────

    [Fact]
    public void SlotCount_EvenWindowDivision()
    {
        // 1h / 30s = 120 slots
        Assert.Equal(120, VwapPlan.SlotCount(Start, End, Tick));
    }

    [Fact]
    public void SlotCount_RoundsUpForFractionalRemainder()
    {
        // 65s / 30s = 2.17 → 3 slots so the tail is reachable
        Assert.Equal(3, VwapPlan.SlotCount(Start, Start.AddSeconds(65), Tick));
    }

    [Fact]
    public void SlotCount_AlwaysAtLeastOneSlot()
    {
        Assert.Equal(1, VwapPlan.SlotCount(Start, Start.AddTicks(1), Tick));
    }

    [Fact]
    public void SlotCount_RejectsInvalidWindow()
    {
        Assert.Throws<ArgumentException>(() => VwapPlan.SlotCount(Start, Start, Tick));
        Assert.Throws<ArgumentException>(() => VwapPlan.SlotCount(End, Start, Tick));
    }

    // ───────────────────────── TargetCumQty ─────────────────────────

    [Fact]
    public void TargetCumQty_ClampedAtBoundaries()
    {
        Assert.Equal(0, VwapPlan.TargetCumQty(1000, 0));
        Assert.Equal(0, VwapPlan.TargetCumQty(1000, -0.1));
        Assert.Equal(1000, VwapPlan.TargetCumQty(1000, 1));
        Assert.Equal(1000, VwapPlan.TargetCumQty(1000, 1.5));
    }

    [Fact]
    public void TargetCumQty_RoundsToNearest()
    {
        Assert.Equal(500, VwapPlan.TargetCumQty(1000, 0.5));
        Assert.Equal(250, VwapPlan.TargetCumQty(1000, 0.25));
        Assert.Equal(333, VwapPlan.TargetCumQty(1000, 1.0 / 3.0));
    }

    [Fact]
    public void TargetCumQty_NaN_ReturnsZero()
    {
        Assert.Equal(0, VwapPlan.TargetCumQty(1000, double.NaN));
    }

    [Fact]
    public void TargetCumQty_RejectsNonPositiveTotal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VwapPlan.TargetCumQty(0, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => VwapPlan.TargetCumQty(-1, 0.5));
    }

    // ───────────────────────── SliceQty ─────────────────────────

    [Fact]
    public void SliceQty_ReturnsGap_WhenNoCapsConfigured()
    {
        // target 500 - executed 200 = gap 300, plenty of remaining.
        Assert.Equal(300, VwapPlan.SliceQty(500, 200, 800, 1000, null, null, 0));
    }

    [Fact]
    public void SliceQty_ZeroOrNegativeGap_ReturnsZero()
    {
        // Parent is ahead of curve — engine should skip this slot.
        Assert.Equal(0, VwapPlan.SliceQty(500, 600, 400, 1000, null, null, 0));
        Assert.Equal(0, VwapPlan.SliceQty(500, 500, 500, 1000, null, null, 0));
    }

    [Fact]
    public void SliceQty_CappedByRemaining()
    {
        // gap 300 but only 100 remaining.
        Assert.Equal(100, VwapPlan.SliceQty(500, 200, 100, 1000, null, null, 0));
    }

    [Fact]
    public void SliceQty_ZeroRemaining_ReturnsZero()
    {
        Assert.Equal(0, VwapPlan.SliceQty(500, 0, 0, 1000, null, null, 0));
    }

    [Fact]
    public void SliceQty_CappedBySliceMaxPct()
    {
        // 10% of 1000 = 100, but gap is 300.
        Assert.Equal(100, VwapPlan.SliceQty(500, 200, 800, 1000, 0.10m, null, 0));
    }

    [Fact]
    public void SliceQty_SliceMaxPctZeroCap_StillAllowsOneShare()
    {
        // A literal 0% cap would freeze the algo; floor to 1 share.
        Assert.Equal(1, VwapPlan.SliceQty(500, 200, 800, 1000, 0.00001m, null, 0));
    }

    [Fact]
    public void SliceQty_CappedByParticipation()
    {
        // 20% of recent market 500 = 100, but gap is 300.
        Assert.Equal(100, VwapPlan.SliceQty(500, 200, 800, 1000, null, 0.20m, 500));
    }

    [Fact]
    public void SliceQty_ParticipationIgnored_WhenNoRecentVolume()
    {
        // No recent volume → ignore the cap rather than going to 0.
        Assert.Equal(300, VwapPlan.SliceQty(500, 200, 800, 1000, null, 0.20m, 0));
    }

    [Fact]
    public void SliceQty_AllCapsCombined_TakesMinimum()
    {
        // gap=300, remaining=800, sliceMaxPct=15%*1000=150,
        // participation=10%*1000=100 → take 100.
        Assert.Equal(100, VwapPlan.SliceQty(500, 200, 800, 1000, 0.15m, 0.10m, 1000));
    }

    [Fact]
    public void SliceQty_RejectsNonPositiveTotal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VwapPlan.SliceQty(0, 0, 100, 0, null, null, 0));
    }

    // ───────────────────────── ClampPrice ─────────────────────────

    [Fact]
    public void ClampPrice_BuyTakesMin()
    {
        // Buy never pays more than the priceLimit ceiling.
        Assert.Equal(10m, VwapPlan.ClampPrice(12m, 10m, OrderSide.Buy));
        Assert.Equal(8m, VwapPlan.ClampPrice(8m, 10m, OrderSide.Buy));
    }

    [Fact]
    public void ClampPrice_SellTakesMax()
    {
        // Sell never accepts less than the priceLimit floor.
        Assert.Equal(10m, VwapPlan.ClampPrice(8m, 10m, OrderSide.Sell));
        Assert.Equal(12m, VwapPlan.ClampPrice(12m, 10m, OrderSide.Sell));
    }

    [Fact]
    public void ClampPrice_NoLimit_ReturnsRefPrice()
    {
        Assert.Equal(12m, VwapPlan.ClampPrice(12m, null, OrderSide.Buy));
        Assert.Null(VwapPlan.ClampPrice(null, null, OrderSide.Buy));
    }

    [Fact]
    public void ClampPrice_NoRefPrice_ReturnsLimit()
    {
        Assert.Equal(10m, VwapPlan.ClampPrice(null, 10m, OrderSide.Buy));
    }
}
