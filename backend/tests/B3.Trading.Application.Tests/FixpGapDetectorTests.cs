using B3.Trading.Infrastructure;

namespace B3.Trading.Application.Tests;

public class FixpGapDetectorTests
{
    [Fact]
    public void First_Observation_AdoptsIncomingAsHighWater()
    {
        ulong last = 0;
        var result = FixpGapDetector.Observe(42UL, ref last);

        Assert.Equal(GapObservation.First, result);
        Assert.Equal(42UL, last);
    }

    [Fact]
    public void Sequential_InOrder_AdvancesHighWater()
    {
        ulong last = 10UL;
        Assert.Equal(GapObservation.InOrder, FixpGapDetector.Observe(11UL, ref last));
        Assert.Equal(11UL, last);
        Assert.Equal(GapObservation.InOrder, FixpGapDetector.Observe(12UL, ref last));
        Assert.Equal(12UL, last);
    }

    [Fact]
    public void Gap_AdvancesHighWaterToIncomingSoSubsequentInOrderIsAccepted()
    {
        ulong last = 10UL;
        Assert.Equal(GapObservation.Gap, FixpGapDetector.Observe(15UL, ref last));
        Assert.Equal(15UL, last);
        // The next in-order message (16) must NOT be flagged as another gap.
        Assert.Equal(GapObservation.InOrder, FixpGapDetector.Observe(16UL, ref last));
    }

    [Fact]
    public void Duplicate_DoesNotRegressHighWater()
    {
        ulong last = 10UL;
        Assert.Equal(GapObservation.Duplicate, FixpGapDetector.Observe(10UL, ref last));
        Assert.Equal(10UL, last);
        Assert.Equal(GapObservation.Duplicate, FixpGapDetector.Observe(5UL, ref last));
        Assert.Equal(10UL, last);
    }

    [Fact]
    public void DuplicateThenInOrder_StillAdvancesAsExpected()
    {
        ulong last = 10UL;
        FixpGapDetector.Observe(10UL, ref last); // duplicate
        Assert.Equal(GapObservation.InOrder, FixpGapDetector.Observe(11UL, ref last));
        Assert.Equal(11UL, last);
    }
}
