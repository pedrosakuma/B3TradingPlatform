using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #671/#753 (RFC: admin account reset, PR 3, code-review addendum
/// #2). Unit coverage for the three <see cref="SubAccountPositionKeeper"/>
/// methods added for whole-account reset: <see cref="SubAccountPositionKeeper.ClearAllForAccount"/>,
/// <see cref="SubAccountPositionKeeper.SnapshotForAccount"/> and
/// <see cref="SubAccountPositionKeeper.RestoreForAccount"/>. Mirrors
/// <see cref="SubAccountPnlKeeperAccountResetTests"/>'s coverage shape
/// so both stores' reset-clearing behavior is proven symmetrically.
/// </summary>
public class SubAccountPositionKeeperAccountResetTests
{
    [Fact]
    public void ClearAllForAccount_RemovesEveryNamedSubAccountRow()
    {
        var keeper = new SubAccountPositionKeeper();
        var alice = new EndClientId("alice");
        keeper.ApplyFill("FIRM01", alice, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);
        keeper.ApplyFill("FIRM01", alice, new SubAccountId("SUB2"), "VALE3", OrderSide.Sell, 20, 60m);

        keeper.ClearAllForAccount("FIRM01", alice);

        Assert.Empty(keeper.EnumerateForOwner("FIRM01", alice));
    }

    [Fact]
    public void ClearAllForAccount_DoesNotTouchOtherEndClients()
    {
        var keeper = new SubAccountPositionKeeper();
        var alice = new EndClientId("alice");
        var bob = new EndClientId("bob");
        keeper.ApplyFill("FIRM01", alice, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);
        keeper.ApplyFill("FIRM01", bob, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);

        keeper.ClearAllForAccount("FIRM01", alice);

        Assert.Empty(keeper.EnumerateForOwner("FIRM01", alice));
        Assert.Single(keeper.EnumerateForOwner("FIRM01", bob));
    }

    [Fact]
    public void ClearAllForAccount_DoesNotTouchOtherFirms()
    {
        var keeper = new SubAccountPositionKeeper();
        var alice = new EndClientId("alice");
        keeper.ApplyFill("FIRM01", alice, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);
        keeper.ApplyFill("FIRM02", alice, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);

        keeper.ClearAllForAccount("FIRM01", alice);

        Assert.Empty(keeper.EnumerateForOwner("FIRM01", alice));
        Assert.Single(keeper.EnumerateForOwner("FIRM02", alice));
    }

    [Fact]
    public void SnapshotForAccount_EmptyAccount_ReturnsEmpty()
    {
        var keeper = new SubAccountPositionKeeper();
        Assert.Empty(keeper.SnapshotForAccount("FIRM01", new EndClientId("alice")));
    }

    [Fact]
    public void SnapshotForAccount_SkipsFlatRows()
    {
        var keeper = new SubAccountPositionKeeper();
        var alice = new EndClientId("alice");
        // Buy then sell the same quantity -> flat (NetQuantity == 0).
        keeper.ApplyFill("FIRM01", alice, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);
        keeper.ApplyFill("FIRM01", alice, new SubAccountId("SUB1"), "PETR4", OrderSide.Sell, 100, 30m);

        Assert.Empty(keeper.SnapshotForAccount("FIRM01", alice));
    }

    [Fact]
    public void SnapshotForAccount_DoesNotIncludeOtherAccounts()
    {
        var keeper = new SubAccountPositionKeeper();
        var alice = new EndClientId("alice");
        var bob = new EndClientId("bob");
        keeper.ApplyFill("FIRM01", alice, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);
        keeper.ApplyFill("FIRM01", bob, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);
        keeper.ApplyFill("FIRM02", alice, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);

        var snapshot = keeper.SnapshotForAccount("FIRM01", alice);

        Assert.Single(snapshot);
        Assert.Equal("SUB1", snapshot[0].SubAccount);
        Assert.Equal("PETR4", snapshot[0].Symbol);
        Assert.Equal(100, snapshot[0].NetQuantity);
        Assert.Equal(28m, snapshot[0].AverageEntryPrice);
    }

    [Fact]
    public void SnapshotThenRestore_RoundTripsExactState()
    {
        var keeper = new SubAccountPositionKeeper();
        var alice = new EndClientId("alice");
        keeper.ApplyFill("FIRM01", alice, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);
        keeper.ApplyFill("FIRM01", alice, new SubAccountId("SUB2"), "VALE3", OrderSide.Buy, 40, 61m);

        var before = keeper.SnapshotForAccount("FIRM01", alice);
        Assert.Equal(2, before.Count);

        // Mutate away from the snapshotted state.
        keeper.ApplyFill("FIRM01", alice, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 900, 10m);
        keeper.ApplyFill("FIRM01", alice, new SubAccountId("SUB3"), "ITUB4", OrderSide.Buy, 10, 25m);
        Assert.Equal(3, keeper.EnumerateForOwner("FIRM01", alice).Count);

        keeper.RestoreForAccount("FIRM01", alice, before);

        var sub1 = keeper.ForSubAccount("FIRM01", alice, new SubAccountId("SUB1")).Single(p => p.Symbol == "PETR4");
        Assert.Equal(100, sub1.NetQuantity);
        Assert.Equal(28m, sub1.AverageEntryPrice);

        var sub2 = keeper.ForSubAccount("FIRM01", alice, new SubAccountId("SUB2")).Single(p => p.Symbol == "VALE3");
        Assert.Equal(40, sub2.NetQuantity);
        Assert.Equal(61m, sub2.AverageEntryPrice);

        // The row seeded after the snapshot must be gone.
        Assert.Empty(keeper.ForSubAccount("FIRM01", alice, new SubAccountId("SUB3")));
    }

    [Fact]
    public void RestoreForAccount_WithEmptySnapshot_ClearsCurrentState()
    {
        var keeper = new SubAccountPositionKeeper();
        var alice = new EndClientId("alice");
        keeper.ApplyFill("FIRM01", alice, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);

        keeper.RestoreForAccount("FIRM01", alice, Array.Empty<SubAccountPositionEntry>());

        Assert.Empty(keeper.EnumerateForOwner("FIRM01", alice));
    }

    [Fact]
    public void RestoreForAccount_DoesNotTouchOtherAccounts()
    {
        var keeper = new SubAccountPositionKeeper();
        var alice = new EndClientId("alice");
        var bob = new EndClientId("bob");
        keeper.ApplyFill("FIRM01", alice, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);
        keeper.ApplyFill("FIRM01", bob, new SubAccountId("SUB1"), "PETR4", OrderSide.Buy, 100, 28m);

        keeper.RestoreForAccount("FIRM01", alice, Array.Empty<SubAccountPositionEntry>());

        Assert.Empty(keeper.EnumerateForOwner("FIRM01", alice));
        Assert.Single(keeper.EnumerateForOwner("FIRM01", bob));
    }
}
