using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #671/#753 (RFC PR 1, code-review addendum #2). Focused unit coverage
/// for <see cref="SubAccountPnlKeeper.SetAbsoluteMasterBucketAvgCost"/>:
/// the sub-account-keeper companion of
/// <see cref="PnlKeeper.SetAbsoluteAvgCost"/> that keeps the MASTER
/// bucket's avg-cost basis in lockstep with an admin position overwrite.
/// v1 admin position adjustment is account-wide/master-only, so these
/// tests specifically prove it never fabricates or alters a NAMED
/// sub-account bucket.
/// </summary>
public class SubAccountPnlKeeperSetAbsoluteMasterBucketAvgCostTests
{
    private const string Firm = "FIRM01";

    [Fact]
    public void SetAbsoluteMasterBucketAvgCost_ThenSell_RealizesCorrectPnlAndLeavesRemainingBasis()
    {
        var k = new SubAccountPnlKeeper();

        // Admin adjusts alice's PETR4 MASTER position to an absolute long 100 @ 20.
        k.SetAbsoluteMasterBucketAvgCost(Firm, "alice", "PETR4", 100, 20m);

        // Trading resumes: alice sells 50 @ 25 with no sub-account tag (master fill).
        var realized = k.ApplyBucketFill(Firm, "alice", subAccount: null, "PETR4", OrderSide.Sell, 50, 25m);

        // (25 - 20) * 50 = 250.
        Assert.Equal(250m, realized);

        var remaining = k.GetBucketAvgCost(Firm, "alice", subAccount: null, "PETR4");
        Assert.NotNull(remaining);
        Assert.Equal(50, remaining!.NetQuantity);
        Assert.Equal(20m, remaining.AvgPrice);
    }

    [Fact]
    public void SetAbsoluteMasterBucketAvgCost_ZeroQuantity_ClearsMasterBucketEntirely()
    {
        var k = new SubAccountPnlKeeper();
        k.SetAbsoluteMasterBucketAvgCost(Firm, "alice", "PETR4", 100, 20m);
        Assert.NotNull(k.GetBucketAvgCost(Firm, "alice", subAccount: null, "PETR4"));

        k.SetAbsoluteMasterBucketAvgCost(Firm, "alice", "PETR4", 0, 0m);

        // Cleared outright — no stale (0, 0m) entry left behind.
        Assert.Null(k.GetBucketAvgCost(Firm, "alice", subAccount: null, "PETR4"));
    }

    [Fact]
    public void SetAbsoluteMasterBucketAvgCost_OverwritesPriorKnownBasis_DoesNotMerge()
    {
        var k = new SubAccountPnlKeeper();
        k.ApplyBucketFill(Firm, "alice", subAccount: null, "PETR4", OrderSide.Buy, 100, 30m);
        k.ApplyBucketFill(Firm, "alice", subAccount: null, "PETR4", OrderSide.Buy, 50, 31m);

        k.SetAbsoluteMasterBucketAvgCost(Firm, "alice", "PETR4", 500, 40m);

        var state = k.GetBucketAvgCost(Firm, "alice", subAccount: null, "PETR4");
        Assert.NotNull(state);
        Assert.Equal(500, state!.NetQuantity);
        Assert.Equal(40m, state.AvgPrice);
    }

    [Fact]
    public void SetAbsoluteMasterBucketAvgCost_FirmScoped_DoesNotLeakAcrossFirms()
    {
        var k = new SubAccountPnlKeeper();
        k.SetAbsoluteMasterBucketAvgCost(Firm, "alice", "PETR4", 100, 20m);
        k.SetAbsoluteMasterBucketAvgCost("FIRM02", "alice", "PETR4", 999, 99m);

        var firm1 = k.GetBucketAvgCost(Firm, "alice", subAccount: null, "PETR4");
        var firm2 = k.GetBucketAvgCost("FIRM02", "alice", subAccount: null, "PETR4");
        Assert.NotNull(firm1);
        Assert.NotNull(firm2);
        Assert.Equal(100, firm1!.NetQuantity);
        Assert.Equal(999, firm2!.NetQuantity);
    }

    /// <summary>
    /// The critical v1-scope guard: a master adjustment must NEVER
    /// fabricate or alter a NAMED sub-account bucket, even one that
    /// already exists for the same (firm, endclient, symbol).
    /// </summary>
    [Fact]
    public void SetAbsoluteMasterBucketAvgCost_NeverTouchesNamedSubAccountBucket()
    {
        var k = new SubAccountPnlKeeper();
        var subA = new SubAccountId("subA");

        // Seed a pre-existing named sub-account bucket.
        k.ApplyBucketFill(Firm, "alice", subA, "PETR4", OrderSide.Buy, 50, 31m);
        var subBasisBefore = k.GetBucketAvgCost(Firm, "alice", subA, "PETR4");
        Assert.NotNull(subBasisBefore);
        Assert.Equal(50, subBasisBefore!.NetQuantity);
        Assert.Equal(31m, subBasisBefore.AvgPrice);

        // Master adjustment for the SAME (firm, endclient, symbol).
        k.SetAbsoluteMasterBucketAvgCost(Firm, "alice", "PETR4", 100, 20m);

        // Named sub-account bucket is untouched.
        var subBasisAfter = k.GetBucketAvgCost(Firm, "alice", subA, "PETR4");
        Assert.NotNull(subBasisAfter);
        Assert.Equal(50, subBasisAfter!.NetQuantity);
        Assert.Equal(31m, subBasisAfter.AvgPrice);

        // Master bucket reflects the adjustment.
        var masterBasis = k.GetBucketAvgCost(Firm, "alice", subAccount: null, "PETR4");
        Assert.NotNull(masterBasis);
        Assert.Equal(100, masterBasis!.NetQuantity);
        Assert.Equal(20m, masterBasis.AvgPrice);

        // A zero-quantity master flatten also leaves the named
        // sub-account bucket untouched.
        k.SetAbsoluteMasterBucketAvgCost(Firm, "alice", "PETR4", 0, 0m);
        Assert.Null(k.GetBucketAvgCost(Firm, "alice", subAccount: null, "PETR4"));
        var subBasisAfterFlatten = k.GetBucketAvgCost(Firm, "alice", subA, "PETR4");
        Assert.NotNull(subBasisAfterFlatten);
        Assert.Equal(50, subBasisAfterFlatten!.NetQuantity);
        Assert.Equal(31m, subBasisAfterFlatten.AvgPrice);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(100, 0)]
    [InlineData(100, -1)]
    public void SetAbsoluteMasterBucketAvgCost_ViolatesInvariant_Throws(long netQuantity, decimal averageEntryPrice)
    {
        var k = new SubAccountPnlKeeper();
        Assert.Throws<ArgumentException>(
            () => k.SetAbsoluteMasterBucketAvgCost(Firm, "alice", "PETR4", netQuantity, averageEntryPrice));
    }
}
