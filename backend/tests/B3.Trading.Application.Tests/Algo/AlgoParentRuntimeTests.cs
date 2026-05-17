using AlgoParentRuntime = B3.Trading.Application.AlgoEngine.AlgoParentRuntime;

namespace B3.Trading.Application.Tests.AlgoEngineTests;

/// <summary>
/// Pass-2 review (#299) P2. Targeted coverage for
/// <c>AlgoParentRuntime.RetireChildSlot</c> — the bounded FIFO that
/// caps <c>ChildBookedCum</c> growth across repeated modifies on the
/// same live parent. Mirrors the <c>CancelledChildRing</c> sizing
/// pattern from PR #296 (cap=8). Without the cap, each successful
/// modify left an orphan row in <c>ChildBookedCum</c> indefinitely;
/// the cap evicts the eldest retired slot AND drops its
/// <c>ChildBookedCum</c> row when overflowed, while keeping recent
/// retired slots so late stray ERs for them compute a zero delta
/// instead of re-booking from a missing-key default of 0.
/// </summary>
public class AlgoParentRuntimeTests
{
    [Fact]
    public void RetireChildSlot_BoundsChildBookedCumAtCap()
    {
        var rt = new AlgoParentRuntime();

        // Simulate 10 successive modify-then-adoption cycles on the same
        // parent. Each cycle hydrates a new child slot in ChildBookedCum
        // (mirroring OnChildErAsync's `rt.ChildBookedCum[child.ClOrdId] =
        // child.CumulativeQuantity` seed) and retires the OLD slot.
        for (ulong childId = 1; childId <= 10; childId++)
        {
            rt.ChildBookedCum[childId] = 0;
            if (childId > 1)
                rt.RetireChildSlot(childId - 1);
        }

        // Cap=8 retired entries + 1 live (the 10th, never retired) = 9.
        Assert.Equal(9, rt.ChildBookedCum.Count);
        Assert.Equal(8, rt.RetiredChildSlots.Count);

        // The eldest retired id (1) is evicted from BOTH the FIFO and
        // ChildBookedCum on the 10th retire (which pushed the queue
        // from 8 → 9, exceeding the cap by one).
        Assert.False(rt.ChildBookedCum.ContainsKey(1UL));
        Assert.DoesNotContain(1UL, rt.RetiredChildSlots);

        // The 8 most-recently-retired ids (2..9) plus the live slot (10)
        // remain accessible — a late stray ER for any retired id can
        // still compute delta = cum - prevBooked == 0 against the row.
        for (ulong stillPresent = 2; stillPresent <= 10; stillPresent++)
            Assert.True(rt.ChildBookedCum.ContainsKey(stillPresent),
                $"ChildBookedCum should still contain recently-retired/live id {stillPresent}.");
    }

    [Fact]
    public void RetireChildSlot_BelowCap_DoesNotEvict()
    {
        var rt = new AlgoParentRuntime();
        for (ulong childId = 1; childId <= 4; childId++)
        {
            rt.ChildBookedCum[childId] = 0;
            if (childId > 1)
                rt.RetireChildSlot(childId - 1);
        }

        // 3 retired + 1 live = 4 rows; nothing evicted (cap=8).
        Assert.Equal(4, rt.ChildBookedCum.Count);
        Assert.Equal(3, rt.RetiredChildSlots.Count);
        for (ulong id = 1; id <= 4; id++)
            Assert.True(rt.ChildBookedCum.ContainsKey(id));
    }

    [Fact]
    public void RetireChildSlot_FirstEvictionFlipsLatchOnce()
    {
        // Pass-3 review (#299) P2. The one-shot warn latch flips on the
        // FIRST eviction (transitions the queue from cap → cap+1) and
        // stays latched on subsequent evictions so callers can emit a
        // single warn per parent without log spam.
        var rt = new AlgoParentRuntime();

        // Fill the FIFO up to the cap (8 retired entries). The 9th
        // enqueue is the first that pushes past cap and evicts.
        for (ulong childId = 1; childId <= 8; childId++)
        {
            rt.ChildBookedCum[childId] = 0;
            rt.RetireChildSlot(childId, out var first0);
            Assert.False(first0);
        }
        Assert.False(rt.RetiredEvictionLogged);

        // 9th retire: queue grows to 9, the eldest (id=1) is evicted
        // → first eviction returns true exactly once.
        rt.ChildBookedCum[9UL] = 0;
        var evicted1 = rt.RetireChildSlot(9UL, out var firstOverflow);
        Assert.Equal(1, evicted1);
        Assert.True(firstOverflow);
        Assert.True(rt.RetiredEvictionLogged);
        Assert.False(rt.ChildBookedCum.ContainsKey(1UL));

        // 10th retire: still overflowing — eviction happens but latch
        // stays armed; the firstEviction out-param must NOT re-arm.
        rt.ChildBookedCum[10UL] = 0;
        var evicted2 = rt.RetireChildSlot(10UL, out var firstAgain);
        Assert.Equal(1, evicted2);
        Assert.False(firstAgain);
        Assert.True(rt.RetiredEvictionLogged);
    }
}
