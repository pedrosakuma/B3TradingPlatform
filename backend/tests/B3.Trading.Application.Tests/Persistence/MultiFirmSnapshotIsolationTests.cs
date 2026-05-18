using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests.Persistence;

/// <summary>
/// Q4.2 (#302). Multi-firm snapshot &amp; restore contract. The platform
/// keeps a SINGLE global snapshot with <c>FirmId</c> carried as a
/// dimension on every owner-keyed structure (Position, PnL avg-cost,
/// Order, Ownership…). This test asserts that capturing the snapshot
/// for three firms and rehydrating into a fresh keeper produces the
/// SAME per-firm slices — no cross-firm leak across the
/// snapshot/restore boundary.
///
/// The narrower per-keeper recovery tests (CashKeeperRecoveryTests,
/// PnlKeeperRecoveryTests …) already exercise the WAL-replay leg;
/// this test focuses on the multi-firm shape of the snapshot DTOs
/// themselves (PositionSnapshot.FirmId, PnlAvgCostSnapshot.FirmId).
/// </summary>
public class MultiFirmSnapshotIsolationTests
{
    [Fact]
    public void PositionKeeper_Snapshot_Restore_PreservesPerFirmSlices()
    {
        var alice = new EndClientId("alice");
        var bob = new EndClientId("bob");
        var charlie = new EndClientId("charlie");

        var src = new PositionKeeper();
        src.ApplyFill("FIRM01", alice, "PETR4", OrderSide.Buy, 100, 30m);
        src.ApplyFill("FIRM02", bob, "VALE3", OrderSide.Buy, 200, 60m);
        src.ApplyFill("FIRM03", charlie, "ITUB4", OrderSide.Buy, 300, 25m);

        // Same JWT-sub spanning two firms: distinct (firmId, owner, symbol)
        // keys must remain distinct rows through snapshot/restore.
        src.ApplyFill("FIRM01", bob, "PETR4", OrderSide.Buy, 50, 30m);
        src.ApplyFill("FIRM02", bob, "PETR4", OrderSide.Buy, 70, 31m);

        var snap = src.Snapshot().ToList();
        Assert.Equal(5, snap.Count);
        Assert.All(snap, s => Assert.False(string.IsNullOrEmpty(s.FirmId)));

        var dst = new PositionKeeper();
        dst.Restore(snap);

        Assert.Single(dst.ForEndClientAndFirm("FIRM01", alice));
        Assert.Equal(2, dst.ForEndClientAndFirm("FIRM02", bob).Count);
        Assert.Single(dst.ForEndClientAndFirm("FIRM03", charlie));
        Assert.Empty(dst.ForEndClientAndFirm("FIRM01", charlie));
        Assert.Empty(dst.ForEndClientAndFirm("FIRM03", alice));
        Assert.Empty(dst.ForEndClientAndFirm("FIRM03", bob));

        // bob's two-firm split must round-trip cleanly: FIRM01 keeps
        // the 50@30 fill, FIRM02 keeps the 70@31 fill. Cross-firm
        // hydrate-and-merge would collapse these into a single row
        // with the sum quantity — the assertion below would fail.
        var bobF1 = dst.ForEndClientAndFirm("FIRM01", bob).Single();
        Assert.Equal("PETR4", bobF1.Symbol);
        Assert.Equal(50, bobF1.NetQuantity);
        var bobF2 = dst.ForEndClientAndFirm("FIRM02", bob).Single(p => p.Symbol == "PETR4");
        Assert.Equal(70, bobF2.NetQuantity);
        Assert.Equal(31m, bobF2.AverageEntryPrice);
    }
}
