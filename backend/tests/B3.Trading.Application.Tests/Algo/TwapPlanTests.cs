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

    // ──────────────────── #518 lot-aware slicing ────────────────────

    [Fact]
    public void SliceQty_WithLot_DistributesInWholeLots()
    {
        // total 600, lot 100, 4 slices: 6 lots / 4 = floor 1 lot = 100 on
        // slices 0..2, remainder 300 on the last → every slice a whole lot.
        Assert.Equal(100, TwapPlan.SliceQty(600, 4, 0, lotSize: 100));
        Assert.Equal(100, TwapPlan.SliceQty(600, 4, 1, lotSize: 100));
        Assert.Equal(100, TwapPlan.SliceQty(600, 4, 2, lotSize: 100));
        Assert.Equal(300, TwapPlan.SliceQty(600, 4, 3, lotSize: 100));
    }

    [Fact]
    public void SliceQty_WithLot_NoInteriorSliceIsAnOddLot()
    {
        // The unrounded floor(300/4)=75 is an odd lot that MinLotSizeCheck
        // would reject (issue #518). In lot units every emitted slice is a
        // multiple of 100 and the sum still reconciles to the total.
        long sum = 0;
        for (var seq = 0; seq < 4; seq++)
        {
            var qty = TwapPlan.SliceQty(300, 4, seq, lotSize: 100);
            Assert.Equal(0, qty % 100);
            sum += qty;
        }
        Assert.Equal(300, sum);
    }

    [Fact]
    public void SliceQty_LotOne_MatchesShareLevelFloor()
    {
        // lotSize == 1 must be byte-identical to the original share-level
        // distribution so existing TWAPs are unaffected.
        for (var seq = 0; seq < 3; seq++)
            Assert.Equal(
                TwapPlan.SliceQty(1003, 3, seq),
                TwapPlan.SliceQty(1003, 3, seq, lotSize: 1));
    }

    [Fact]
    public void SliceQty_WithLot_SumReconcilesAcrossCombinations()
    {
        const long lot = 100;
        var totalLots = new long[] { 1, 3, 4, 7, 10, 123 };
        var sliceCounts = new[] { 1, 2, 3, 4 };
        foreach (var lots in totalLots)
        {
            var total = lots * lot;
            foreach (var count in sliceCounts)
            {
                if (count > lots) continue; // endpoint rejects this up front
                long sum = 0;
                for (var seq = 0; seq < count; seq++)
                {
                    var qty = TwapPlan.SliceQty(total, count, seq, lot);
                    Assert.Equal(0, qty % lot);
                    sum += qty;
                }
                Assert.Equal(total, sum);
            }
        }
    }

    [Fact]
    public void FloorSliceQty_WithLot_IsLotAligned()
    {
        // 300/4 in lot units → 0 (3 whole lots can't be split into 4),
        // so the endpoint rejects sliceCount > available lots.
        Assert.Equal(0, TwapPlan.FloorSliceQty(300, 4, lotSize: 100));
        // 600/4 → 1 whole lot = 100.
        Assert.Equal(100, TwapPlan.FloorSliceQty(600, 4, lotSize: 100));
    }

    [Fact]
    public void SliceQty_WithLot_MoreSlicesThanLots_LastSliceCarriesRemainder()
    {
        // #518 defense-in-depth. The endpoint rejects sliceCount > lots at
        // admission, but the lot table can become authoritative AFTER a TWAP
        // was admitted (SDK SecurityDefinition overlay). When that happens
        // the interior slices floor to zero in lot units and the last slice
        // carries the whole residue — every slice stays lot-aligned and the
        // sum still reconciles, so the engine works the order down (via the
        // remainder-bearing last slice) instead of stranding it.
        Assert.Equal(0, TwapPlan.SliceQty(300, 4, 0, lotSize: 100));
        Assert.Equal(0, TwapPlan.SliceQty(300, 4, 1, lotSize: 100));
        Assert.Equal(0, TwapPlan.SliceQty(300, 4, 2, lotSize: 100));
        Assert.Equal(300, TwapPlan.SliceQty(300, 4, 3, lotSize: 100));
    }
}
