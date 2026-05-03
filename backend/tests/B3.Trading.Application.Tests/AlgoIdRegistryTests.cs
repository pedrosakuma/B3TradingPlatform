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
}
