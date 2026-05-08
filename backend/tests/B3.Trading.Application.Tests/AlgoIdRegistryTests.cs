using B3.Trading.Application;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Application.Tests;

public class AlgoIdRegistryTests
{
    [Fact]
    public void Generate_PerFirm_IndependentCounters()
    {
        var reg = new AlgoIdRegistry();
        Assert.Equal(1UL, reg.Generate("FIRM-A"));
        Assert.Equal(2UL, reg.Generate("FIRM-A"));
        Assert.Equal(1UL, reg.Generate("FIRM-B"));
        Assert.Equal(3UL, reg.Generate("FIRM-A"));
        Assert.Equal(2UL, reg.Generate("FIRM-B"));
    }

    [Fact]
    public void Generate_BlankFirm_Throws()
    {
        var reg = new AlgoIdRegistry();
        Assert.Throws<ArgumentException>(() => reg.Generate(""));
        Assert.Throws<ArgumentException>(() => reg.Generate("   "));
    }

    [Fact]
    public void Snapshot_Restore_PreservesPerFirmWatermarks()
    {
        var reg = new AlgoIdRegistry();
        reg.Generate("FIRM-A");
        reg.Generate("FIRM-A");
        reg.Generate("FIRM-A");
        reg.Generate("FIRM-B");

        var snap = reg.Snapshot();
        var restored = new AlgoIdRegistry();
        restored.Restore(snap);

        Assert.Equal(4UL, restored.Generate("FIRM-A"));
        Assert.Equal(2UL, restored.Generate("FIRM-B"));
        Assert.Equal(1UL, restored.Generate("FIRM-NEW"));
    }

    [Fact]
    public void Restore_ReplacesExistingState()
    {
        // Restore must be a "clear+set" operation, not a merge — replay
        // semantics rely on the registry state matching the snapshot
        // exactly (otherwise re-applied WAL events would re-increment
        // already-restored counters).
        var reg = new AlgoIdRegistry();
        reg.Generate("FIRM-A");
        reg.Generate("FIRM-A");

        var emptySnap = new AlgoIdRegistrySnapshot();
        reg.Restore(emptySnap);
        Assert.Equal(1UL, reg.Generate("FIRM-A"));
    }

    [Fact]
    public void AdvanceCounterTo_NewFirm_AdoptsObservedWatermark()
    {
        var reg = new AlgoIdRegistry();
        reg.AdvanceCounterTo("FIRM-A", 7UL);
        Assert.Equal(8UL, reg.Generate("FIRM-A"));
    }

    [Fact]
    public void AdvanceCounterTo_Monotonic_DoesNotRegress()
    {
        var reg = new AlgoIdRegistry();
        reg.AdvanceCounterTo("FIRM-A", 10UL);
        reg.AdvanceCounterTo("FIRM-A", 5UL);
        Assert.Equal(11UL, reg.Generate("FIRM-A"));
    }

    [Fact]
    public void AdvanceCounterTo_Idempotent()
    {
        var reg = new AlgoIdRegistry();
        reg.AdvanceCounterTo("FIRM-A", 4UL);
        reg.AdvanceCounterTo("FIRM-A", 4UL);
        Assert.Equal(5UL, reg.Generate("FIRM-A"));
    }

    [Fact]
    public void AdvanceCounterTo_ZeroObservedAlgoId_Dropped()
    {
        var reg = new AlgoIdRegistry();
        reg.Generate("FIRM-A"); // counter=1
        reg.AdvanceCounterTo("FIRM-A", 0UL);
        // State unchanged; next Generate proceeds from 1.
        Assert.Equal(2UL, reg.Generate("FIRM-A"));
    }

    [Fact]
    public void AdvanceCounterTo_BlankFirm_Throws()
    {
        var reg = new AlgoIdRegistry();
        Assert.Throws<ArgumentException>(() => reg.AdvanceCounterTo("", 1UL));
    }

    [Fact]
    public void AdvanceCounterTo_AfterRestore_NextGenerateSkipsPast()
    {
        // Models the snapshot+WAL replay flow: snapshot at watermark N,
        // then replay advances past N via AdvanceCounterTo, then live
        // Generate must continue past the replayed watermark.
        var reg = new AlgoIdRegistry();
        reg.Generate("FIRM-A"); // 1
        reg.Generate("FIRM-A"); // 2
        var snap = reg.Snapshot();

        var restored = new AlgoIdRegistry();
        restored.Restore(snap);
        // Replay sees AlgoCreatedEvent.AlgoId=3 then 4.
        restored.AdvanceCounterTo("FIRM-A", 3UL);
        restored.AdvanceCounterTo("FIRM-A", 4UL);

        Assert.Equal(5UL, restored.Generate("FIRM-A"));
    }
}
