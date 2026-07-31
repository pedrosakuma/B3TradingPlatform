using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #671/#753 (RFC: admin account reset, PR 3). Unit coverage for the
/// three <see cref="SubAccountPnlKeeper"/> methods added for whole-
/// account reset: <see cref="SubAccountPnlKeeper.ClearAllBucketsForAccount"/>,
/// <see cref="SubAccountPnlKeeper.SnapshotBucketsForAccount"/> and
/// <see cref="SubAccountPnlKeeper.RestoreBucketsForAccount"/>.
/// </summary>
public class SubAccountPnlKeeperAccountResetTests
{
    [Fact]
    public void ClearAllBucketsForAccount_RemovesMasterAndNamedBuckets()
    {
        var keeper = new SubAccountPnlKeeper();
        keeper.ApplyBucketFill("FIRM01", "alice", null, "PETR4", OrderSide.Buy, 100, 28m);
        keeper.ApplyBucketFill("FIRM01", "alice", new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 50, 28m);
        keeper.ApplyBucketFill("FIRM01", "alice", new SubAccountId("SUB2"), "VALE3", OrderSide.Sell, 20, 60m);

        keeper.ClearAllBucketsForAccount("FIRM01", "alice");

        Assert.Null(keeper.GetBucketAvgCost("FIRM01", "alice", null, "PETR4"));
        Assert.Null(keeper.GetBucketAvgCost("FIRM01", "alice", new SubAccountId("SUB1"), "PETR4"));
        Assert.Null(keeper.GetBucketAvgCost("FIRM01", "alice", new SubAccountId("SUB2"), "VALE3"));
    }

    [Fact]
    public void ClearAllBucketsForAccount_DoesNotTouchOtherEndClients()
    {
        var keeper = new SubAccountPnlKeeper();
        keeper.ApplyBucketFill("FIRM01", "alice", null, "PETR4", OrderSide.Buy, 100, 28m);
        keeper.ApplyBucketFill("FIRM01", "bob", null, "PETR4", OrderSide.Buy, 100, 28m);

        keeper.ClearAllBucketsForAccount("FIRM01", "alice");

        Assert.Null(keeper.GetBucketAvgCost("FIRM01", "alice", null, "PETR4"));
        Assert.NotNull(keeper.GetBucketAvgCost("FIRM01", "bob", null, "PETR4"));
    }

    [Fact]
    public void ClearAllBucketsForAccount_DoesNotTouchOtherFirms()
    {
        var keeper = new SubAccountPnlKeeper();
        keeper.ApplyBucketFill("FIRM01", "alice", null, "PETR4", OrderSide.Buy, 100, 28m);
        keeper.ApplyBucketFill("FIRM02", "alice", null, "PETR4", OrderSide.Buy, 100, 28m);

        keeper.ClearAllBucketsForAccount("FIRM01", "alice");

        Assert.Null(keeper.GetBucketAvgCost("FIRM01", "alice", null, "PETR4"));
        Assert.NotNull(keeper.GetBucketAvgCost("FIRM02", "alice", null, "PETR4"));
    }

    [Fact]
    public void ClearAllBucketsForAccount_DoesNotTouchRealizedHistory()
    {
        var keeper = new SubAccountPnlKeeper();
        var day = new DateOnly(2024, 1, 2);
        keeper.Add("FIRM01", "alice", new SubAccountId("SUB1"), "PETR4", day, 500m);

        keeper.ClearAllBucketsForAccount("FIRM01", "alice");

        Assert.Equal(500m, keeper.GetDayRealized("FIRM01", "alice", new SubAccountId("SUB1"), "PETR4", day));
    }

    [Fact]
    public void SnapshotBucketsForAccount_EmptyAccount_ReturnsEmpty()
    {
        var keeper = new SubAccountPnlKeeper();
        Assert.Empty(keeper.SnapshotBucketsForAccount("FIRM01", "alice"));
    }

    [Fact]
    public void SnapshotThenRestore_RoundTripsExactState()
    {
        var keeper = new SubAccountPnlKeeper();
        keeper.ApplyBucketFill("FIRM01", "alice", null, "PETR4", OrderSide.Buy, 100, 28m);
        keeper.ApplyBucketFill("FIRM01", "alice", new SubAccountId("SUB1"), "VALE3", OrderSide.Buy, 40, 61m);

        var before = keeper.SnapshotBucketsForAccount("FIRM01", "alice");
        Assert.Equal(2, before.Count);

        // Mutate away from the snapshotted state.
        keeper.ApplyBucketFill("FIRM01", "alice", null, "PETR4", OrderSide.Buy, 900, 10m);
        keeper.ApplyBucketFill("FIRM01", "alice", new SubAccountId("SUB2"), "ITUB4", OrderSide.Buy, 10, 25m);
        Assert.NotEqual(before, keeper.SnapshotBucketsForAccount("FIRM01", "alice"));

        keeper.RestoreBucketsForAccount("FIRM01", "alice", before);

        var master = keeper.GetBucketAvgCost("FIRM01", "alice", null, "PETR4");
        Assert.NotNull(master);
        Assert.Equal(100, master!.NetQuantity);
        Assert.Equal(28m, master.AvgPrice);

        var sub1 = keeper.GetBucketAvgCost("FIRM01", "alice", new SubAccountId("SUB1"), "VALE3");
        Assert.NotNull(sub1);
        Assert.Equal(40, sub1!.NetQuantity);
        Assert.Equal(61m, sub1.AvgPrice);

        // The bucket seeded after the snapshot must be gone.
        Assert.Null(keeper.GetBucketAvgCost("FIRM01", "alice", new SubAccountId("SUB2"), "ITUB4"));
    }

    [Fact]
    public void RestoreBucketsForAccount_WithEmptySnapshot_ClearsCurrentState()
    {
        var keeper = new SubAccountPnlKeeper();
        keeper.ApplyBucketFill("FIRM01", "alice", null, "PETR4", OrderSide.Buy, 100, 28m);

        keeper.RestoreBucketsForAccount("FIRM01", "alice", Array.Empty<SubAccountPnlBucketEntry>());

        Assert.Null(keeper.GetBucketAvgCost("FIRM01", "alice", null, "PETR4"));
    }

    [Fact]
    public void SnapshotBucketsForAccount_DoesNotIncludeOtherAccounts()
    {
        var keeper = new SubAccountPnlKeeper();
        keeper.ApplyBucketFill("FIRM01", "alice", null, "PETR4", OrderSide.Buy, 100, 28m);
        keeper.ApplyBucketFill("FIRM01", "bob", null, "PETR4", OrderSide.Buy, 100, 28m);
        keeper.ApplyBucketFill("FIRM02", "alice", null, "PETR4", OrderSide.Buy, 100, 28m);

        var snapshot = keeper.SnapshotBucketsForAccount("FIRM01", "alice");

        Assert.Single(snapshot);
    }
}
