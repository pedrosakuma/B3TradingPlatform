using B3.Trading.Application;
using Xunit;

namespace B3.Trading.Application.Tests.AlgoEngine;

public class TwapPlanTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    // ───────────────────────── PlannedAtUtc ─────────────────────────

    [Fact]
    public void PlannedAtUtc_FirstSliceFiresAtStart()
    {
        var t = TwapPlan.PlannedAtUtc(Start, End, sliceCount: 4, sliceSeq: 0);
        Assert.Equal(Start, t);
    }

    [Fact]
    public void PlannedAtUtc_LastSliceFiresStrictlyBeforeEnd()
    {
        // Plan must reserve endUtc as the expiry boundary — no slice
        // should be scheduled at endUtc itself, otherwise the
        // window-expiry path and the slice-fire path race.
        var t = TwapPlan.PlannedAtUtc(Start, End, sliceCount: 4, sliceSeq: 3);
        Assert.True(t < End, $"expected last slice < end ({End}), got {t}");
    }

    [Fact]
    public void PlannedAtUtc_EvenSpacing_4Slices_Each15Minutes()
    {
        Assert.Equal(Start, TwapPlan.PlannedAtUtc(Start, End, 4, 0));
        Assert.Equal(Start.AddMinutes(15), TwapPlan.PlannedAtUtc(Start, End, 4, 1));
        Assert.Equal(Start.AddMinutes(30), TwapPlan.PlannedAtUtc(Start, End, 4, 2));
        Assert.Equal(Start.AddMinutes(45), TwapPlan.PlannedAtUtc(Start, End, 4, 3));
    }

    [Fact]
    public void PlannedAtUtc_DeterministicAcrossInvocations()
    {
        // The plan is recomputed at each scheduler tick AND at every
        // recovery — drift between calls would split-brain the
        // engine/scheduler. Hammer the helper to assert byte-equality.
        for (var seq = 0; seq < 10; seq++)
        {
            var a = TwapPlan.PlannedAtUtc(Start, End, 10, seq);
            var b = TwapPlan.PlannedAtUtc(Start, End, 10, seq);
            Assert.Equal(a, b);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PlannedAtUtc_RejectsNonPositiveSliceCount(int sliceCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TwapPlan.PlannedAtUtc(Start, End, sliceCount, 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(5)]
    public void PlannedAtUtc_RejectsOutOfRangeSliceSeq(int sliceSeq)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TwapPlan.PlannedAtUtc(Start, End, 4, sliceSeq));
    }

    [Fact]
    public void PlannedAtUtc_RejectsNonPositiveWindow()
    {
        Assert.Throws<ArgumentException>(() =>
            TwapPlan.PlannedAtUtc(Start, Start, 4, 0));
        Assert.Throws<ArgumentException>(() =>
            TwapPlan.PlannedAtUtc(End, Start, 4, 0));
    }

    // ───────────────────────── SliceQty ─────────────────────────

    [Fact]
    public void SliceQty_EvenDivision_AllSlicesEqual()
    {
        Assert.Equal(250, TwapPlan.SliceQty(1000, 4, 0));
        Assert.Equal(250, TwapPlan.SliceQty(1000, 4, 1));
        Assert.Equal(250, TwapPlan.SliceQty(1000, 4, 2));
        Assert.Equal(250, TwapPlan.SliceQty(1000, 4, 3));
    }

    [Fact]
    public void SliceQty_UnevenDivision_LastSliceCarriesRemainder()
    {
        // 1003 / 3 = 334, remainder 1 → slices 0,1 = 334 each, slice 2 = 335.
        Assert.Equal(334, TwapPlan.SliceQty(1003, 3, 0));
        Assert.Equal(334, TwapPlan.SliceQty(1003, 3, 1));
        Assert.Equal(335, TwapPlan.SliceQty(1003, 3, 2));
        Assert.Equal(1003,
            TwapPlan.SliceQty(1003, 3, 0)
            + TwapPlan.SliceQty(1003, 3, 1)
            + TwapPlan.SliceQty(1003, 3, 2));
    }

    [Fact]
    public void SliceQty_SingleSlice_TakesAll()
    {
        Assert.Equal(1000, TwapPlan.SliceQty(1000, 1, 0));
    }

    [Fact]
    public void SliceQty_SumAlwaysMatchesTotal()
    {
        // Property test: across plausible combinations the sum of all
        // slice quantities must reconcile to totalQuantity (RFC §4.8
        // exact-match invariant).
        var totals = new long[] { 1, 100, 999, 1000, 12_345, 1_000_000 };
        var sliceCounts = new[] { 1, 2, 3, 7, 10, 100 };
        foreach (var total in totals)
        {
            foreach (var count in sliceCounts)
            {
                if (count > total) continue; // would produce zero floor
                long sum = 0;
                for (var seq = 0; seq < count; seq++)
                    sum += TwapPlan.SliceQty(total, count, seq);
                Assert.Equal(total, sum);
            }
        }
    }

    [Fact]
    public void SliceQty_RejectsNonPositiveTotal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TwapPlan.SliceQty(0, 4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TwapPlan.SliceQty(-1, 4, 0));
    }

    [Fact]
    public void FloorSliceQty_ReturnsPlannedFloor()
    {
        Assert.Equal(250, TwapPlan.FloorSliceQty(1000, 4));
        Assert.Equal(0, TwapPlan.FloorSliceQty(3, 4));
        Assert.Equal(0, TwapPlan.FloorSliceQty(100, 0));
    }
}
