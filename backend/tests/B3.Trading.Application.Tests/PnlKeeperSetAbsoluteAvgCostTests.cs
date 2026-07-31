using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #671/#753 (RFC PR 1, code-review addendum). Focused unit coverage for
/// <see cref="PnlKeeper.SetAbsoluteAvgCost"/>: the companion of
/// <see cref="PositionKeeper.SetAbsolute"/> that keeps the avg-cost basis
/// in lockstep with an admin position overwrite, both live and
/// (indirectly, via <c>PositionAdjustmentRecoveryTests</c>) on replay.
/// Proves the concrete scenario called out in review: an adjusted long
/// 100@20 followed by a sell 50@25 realizes exactly +250 and leaves the
/// correct remaining 50@20 basis — i.e. <see cref="PnlKeeper.ApplyFillToAvgCost"/>
/// behaves identically whether the pre-fill basis was built up from fills
/// or set directly via an absolute admin adjustment.
/// </summary>
public class PnlKeeperSetAbsoluteAvgCostTests
{
    [Fact]
    public void SetAbsoluteAvgCost_ThenSell_RealizesCorrectPnlAndLeavesRemainingBasis()
    {
        var pnl = new PnlKeeper();

        // Admin adjusts alice's PETR4 position to an absolute long 100 @ 20.
        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 100, 20m);

        // Trading resumes: alice sells 50 @ 25.
        var realized = pnl.ApplyFillToAvgCost("FIRM01", "alice", "PETR4", OrderSide.Sell, 50, 25m);

        // (25 - 20) * 50 = 250.
        Assert.Equal(250m, realized);

        var remaining = pnl.GetAvgCost("FIRM01", "alice", "PETR4");
        Assert.NotNull(remaining);
        Assert.Equal(50, remaining!.NetQuantity);
        Assert.Equal(20m, remaining.AvgPrice);
    }

    [Fact]
    public void SetAbsoluteAvgCost_ZeroQuantity_ClearsBasisEntirely()
    {
        var pnl = new PnlKeeper();
        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 100, 20m);
        Assert.NotNull(pnl.GetAvgCost("FIRM01", "alice", "PETR4"));

        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 0, 0m);

        // Cleared outright — no stale (0, 0m) entry left behind.
        Assert.Null(pnl.GetAvgCost("FIRM01", "alice", "PETR4"));
    }

    [Fact]
    public void SetAbsoluteAvgCost_OverwritesPriorKnownBasis_DoesNotMerge()
    {
        var pnl = new PnlKeeper();
        pnl.ApplyFillToAvgCost("FIRM01", "alice", "PETR4", OrderSide.Buy, 100, 30m);
        pnl.ApplyFillToAvgCost("FIRM01", "alice", "PETR4", OrderSide.Buy, 50, 31m);

        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 500, 40m);

        var state = pnl.GetAvgCost("FIRM01", "alice", "PETR4");
        Assert.NotNull(state);
        Assert.Equal(500, state!.NetQuantity);
        Assert.Equal(40m, state.AvgPrice);
    }

    [Fact]
    public void SetAbsoluteAvgCost_ClearsStaleUnknownBasisLeg()
    {
        var pnl = new PnlKeeper();
        // Seed an "unknown basis" leg the way legacy-snapshot recovery
        // would (AverageEntryPrice <= 0 on a non-flat legacy row).
        pnl.SeedAvgCostFromLegacyPositions(new[]
        {
            new PositionSnapshot("alice", "PETR4", 100, 0m, "FIRM01"),
        });
        Assert.Equal(100, pnl.GetUnknownBasisQty("FIRM01", "alice", "PETR4"));
        Assert.Null(pnl.GetAvgCost("FIRM01", "alice", "PETR4"));

        // Admin adjustment establishes a KNOWN basis outright.
        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 200, 15m);

        Assert.Equal(0, pnl.GetUnknownBasisQty("FIRM01", "alice", "PETR4"));
        var state = pnl.GetAvgCost("FIRM01", "alice", "PETR4");
        Assert.NotNull(state);
        Assert.Equal(200, state!.NetQuantity);
        Assert.Equal(15m, state.AvgPrice);
    }

    [Fact]
    public void SetAbsoluteAvgCost_DoesNotRetroactivelyAlterAlreadyRealizedPnl()
    {
        var pnl = new PnlKeeper();
        pnl.ApplyFillToAvgCost("FIRM01", "alice", "PETR4", OrderSide.Buy, 100, 10m);
        var realizedBeforeAdjustment = pnl.ApplyFillToAvgCost("FIRM01", "alice", "PETR4", OrderSide.Sell, 40, 15m);
        Assert.Equal(200m, realizedBeforeAdjustment); // (15-10)*40

        var realizedTotalBefore = pnl.GetDayRealizedTotal("FIRM01", "alice", DateOnly.FromDateTime(DateTime.UtcNow));
        // Note: ApplyFillToAvgCost alone does not book into _realizedByDay
        // (that requires a RealizedPnlEvent via Apply) — this test only
        // asserts SetAbsoluteAvgCost doesn't throw or otherwise disturb
        // unrelated state when invoked after prior fills.
        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 0, 0m);
        var realizedTotalAfter = pnl.GetDayRealizedTotal("FIRM01", "alice", DateOnly.FromDateTime(DateTime.UtcNow));

        Assert.Equal(realizedTotalBefore, realizedTotalAfter);
    }

    [Fact]
    public void SetAbsoluteAvgCost_FirmScoped_DoesNotLeakAcrossFirms()
    {
        var pnl = new PnlKeeper();
        pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", 100, 20m);
        pnl.SetAbsoluteAvgCost("FIRM02", "alice", "PETR4", 999, 99m);

        var firm1 = pnl.GetAvgCost("FIRM01", "alice", "PETR4");
        var firm2 = pnl.GetAvgCost("FIRM02", "alice", "PETR4");
        Assert.NotNull(firm1);
        Assert.NotNull(firm2);
        Assert.Equal(100, firm1!.NetQuantity);
        Assert.Equal(999, firm2!.NetQuantity);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(100, 0)]
    [InlineData(100, -1)]
    public void SetAbsoluteAvgCost_ViolatesInvariant_Throws(long netQuantity, decimal averageEntryPrice)
    {
        var pnl = new PnlKeeper();
        Assert.Throws<ArgumentException>(
            () => pnl.SetAbsoluteAvgCost("FIRM01", "alice", "PETR4", netQuantity, averageEntryPrice));
    }
}
