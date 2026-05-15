using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Q2.2 (#269). Unit-level coverage for <see cref="CashKeeper"/>: the
/// pure projection from <c>CashLedgerEvent</c> deposits/withdrawals.
/// End-to-end admin-endpoint + replay flows are covered separately
/// (api tests + persistence recovery tests).
/// </summary>
public class CashKeeperTests
{
    [Fact]
    public void Deposit_IncreasesBalance()
    {
        var keeper = new CashKeeper();
        var alice = new EndClientId("alice");

        keeper.ApplyDeposit(alice, 1_000m);
        Assert.Equal(1_000m, keeper.GetAvailable(alice));

        keeper.ApplyDeposit(alice, 250m);
        Assert.Equal(1_250m, keeper.GetAvailable(alice));
    }

    [Fact]
    public void TryWithdraw_DecreasesBalance_OnSuccess()
    {
        var keeper = new CashKeeper();
        var alice = new EndClientId("alice");
        keeper.ApplyDeposit(alice, 1_000m);

        Assert.True(keeper.TryWithdraw(alice, 400m));
        Assert.Equal(600m, keeper.GetAvailable(alice));
    }

    [Fact]
    public void TryWithdraw_OverBalance_ReturnsFalse_NoMutation()
    {
        var keeper = new CashKeeper();
        var alice = new EndClientId("alice");
        keeper.ApplyDeposit(alice, 100m);

        Assert.False(keeper.TryWithdraw(alice, 200m));
        Assert.Equal(100m, keeper.GetAvailable(alice));
    }

    [Fact]
    public void TryWithdraw_UnknownOwner_ReturnsFalse_NoMaterialise()
    {
        var keeper = new CashKeeper();
        Assert.False(keeper.TryWithdraw(new EndClientId("ghost"), 10m));
        // Empty raw snapshot — probing didn't pollute the dictionary.
        Assert.Empty(keeper.RawSnapshot());
    }

    [Fact]
    public void GetAvailable_UnknownOwner_ReturnsZero()
    {
        Assert.Equal(0m, new CashKeeper().GetAvailable(new EndClientId("ghost")));
    }

    [Fact]
    public void Deposit_ZeroOrNegative_Throws()
    {
        var keeper = new CashKeeper();
        Assert.Throws<ArgumentOutOfRangeException>(() => keeper.ApplyDeposit(new EndClientId("a"), 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => keeper.ApplyDeposit(new EndClientId("a"), -1m));
    }

    [Fact]
    public void TryWithdraw_ZeroOrNegative_Throws()
    {
        var keeper = new CashKeeper();
        Assert.Throws<ArgumentOutOfRangeException>(() => keeper.TryWithdraw(new EndClientId("a"), 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => keeper.TryWithdraw(new EndClientId("a"), -1m));
    }

    [Fact]
    public void Apply_ReplayPath_FoldsDepositsAndWithdrawals()
    {
        var keeper = new CashKeeper();
        var alice = new EndClientId("alice");

        keeper.Apply("Deposit", alice, 500m);
        keeper.Apply("Withdrawal", alice, 200m);
        keeper.Apply("Deposit", alice, 100m);

        Assert.Equal(400m, keeper.GetAvailable(alice));
    }

    [Fact]
    public void Apply_UnknownKind_Throws()
    {
        var keeper = new CashKeeper();
        Assert.Throws<InvalidOperationException>(() =>
            keeper.Apply("Bogus", new EndClientId("a"), 1m));
    }

    [Fact]
    public void RawSnapshot_SkipsZeroBalances()
    {
        var keeper = new CashKeeper();
        var alice = new EndClientId("alice");
        var bob = new EndClientId("bob");
        keeper.ApplyDeposit(alice, 100m);
        keeper.ApplyDeposit(bob, 50m);
        keeper.TryWithdraw(bob, 50m); // bob -> 0

        var snap = keeper.RawSnapshot();
        Assert.Single(snap);
        Assert.Equal("alice", snap[0].EndClientId);
        Assert.Equal(100m, snap[0].Available);
    }

    [Fact]
    public void Restore_RehydratesBalances_ClearsExisting()
    {
        var keeper = new CashKeeper();
        keeper.ApplyDeposit(new EndClientId("ghost"), 999m);

        keeper.Restore(new Dictionary<string, decimal>
        {
            ["alice"] = 1_000m,
            ["bob"] = 250m,
        });

        Assert.Equal(0m, keeper.GetAvailable(new EndClientId("ghost")));
        Assert.Equal(1_000m, keeper.GetAvailable(new EndClientId("alice")));
        Assert.Equal(250m, keeper.GetAvailable(new EndClientId("bob")));
    }

    [Fact]
    public void Snapshot_Restore_RoundTrips()
    {
        var src = new CashKeeper();
        src.ApplyDeposit(new EndClientId("alice"), 1_000m);
        src.ApplyDeposit(new EndClientId("bob"), 500m);
        src.TryWithdraw(new EndClientId("alice"), 200m);

        var raw = src.RawSnapshot();
        var dict = raw.ToDictionary(r => r.EndClientId, r => r.Available);

        var restored = new CashKeeper();
        restored.Restore(dict);

        Assert.Equal(800m, restored.GetAvailable(new EndClientId("alice")));
        Assert.Equal(500m, restored.GetAvailable(new EndClientId("bob")));
    }
}
