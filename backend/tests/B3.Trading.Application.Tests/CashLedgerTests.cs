using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

public class CashLedgerTests
{
    [Fact]
    public void GetAvailable_UnknownOwner_ReturnsZero_DoesNotMaterialise()
    {
        var ledger = new CashLedger();

        Assert.Equal(0m, ledger.GetAvailable(new EndClientId("ghost")));
        // Snapshot is empty: probing didn't pollute the dictionary.
        Assert.Empty(ledger.Snapshot());
    }

    [Fact]
    public void ApplyFill_TracksRunningBalance()
    {
        var ledger = new CashLedger();
        var alice = new EndClientId("alice");

        ledger.ApplyFill(alice, OrderSide.Buy, 100, 30m);   // -3000
        ledger.ApplyFill(alice, OrderSide.Sell, 50, 32m);   // +1600

        Assert.Equal(-1400m, ledger.GetAvailable(alice));
    }

    [Fact]
    public void SeedIfAbsent_AppliesOnce_RespectsExisting()
    {
        var ledger = new CashLedger();
        var alice = new EndClientId("alice");

        Assert.True(ledger.SeedIfAbsent(alice, 100_000m));
        Assert.Equal(100_000m, ledger.GetAvailable(alice));

        // Second seed is a no-op.
        Assert.False(ledger.SeedIfAbsent(alice, 1m));
        Assert.Equal(100_000m, ledger.GetAvailable(alice));
    }

    [Fact]
    public void SeedIfAbsent_AfterFill_DoesNotOverwrite()
    {
        // Mirrors the production lifecycle: WAL replay/fill happens
        // BEFORE the seed loop runs. Seed must never clobber real cash.
        var ledger = new CashLedger();
        var alice = new EndClientId("alice");

        ledger.ApplyFill(alice, OrderSide.Sell, 10, 50m);   // +500
        Assert.False(ledger.SeedIfAbsent(alice, 100_000m));
        Assert.Equal(500m, ledger.GetAvailable(alice));
    }

    [Fact]
    public void Snapshot_SkipsZeroBalances_KeepsNegatives()
    {
        var ledger = new CashLedger();
        var alice = new EndClientId("alice");
        var bob = new EndClientId("bob");
        var carol = new EndClientId("carol");

        ledger.SeedIfAbsent(alice, 1_000m);
        ledger.SeedIfAbsent(bob, 0m);              // skipped
        ledger.SeedIfAbsent(carol, -250m);         // kept (debt is real)

        var snap = ledger.Snapshot().ToDictionary(s => s.EndClientId, s => s.Available);

        Assert.Equal(2, snap.Count);
        Assert.Equal(1_000m, snap["alice"]);
        Assert.Equal(-250m, snap["carol"]);
        Assert.False(snap.ContainsKey("bob"));
    }

    [Fact]
    public void Restore_ReplacesState()
    {
        var ledger = new CashLedger();
        ledger.ApplyFill(new EndClientId("alice"), OrderSide.Buy, 1, 10m);

        ledger.Restore(new[]
        {
            new CashBalanceSnapshot("bob", 42_000m),
        });

        Assert.Equal(0m, ledger.GetAvailable(new EndClientId("alice")));
        Assert.Equal(42_000m, ledger.GetAvailable(new EndClientId("bob")));
    }

    [Fact]
    public void Restore_RoundTripsViaSnapshot()
    {
        var src = new CashLedger();
        src.ApplyFill(new EndClientId("alice"), OrderSide.Buy, 100, 30m);
        src.ApplyFill(new EndClientId("bob"), OrderSide.Sell, 50, 100m);

        var dst = new CashLedger();
        dst.Restore(src.Snapshot());

        Assert.Equal(src.GetAvailable(new EndClientId("alice")), dst.GetAvailable(new EndClientId("alice")));
        Assert.Equal(src.GetAvailable(new EndClientId("bob")), dst.GetAvailable(new EndClientId("bob")));
    }

    // #387. Cash debit hook used by FeeKeeper.Apply after the seen-set
    // gate succeeds. Idempotency lives in the keeper; from the ledger's
    // point of view ApplyFee is just an unconditional debit.
    [Fact]
    public void ApplyFee_DebitsAvailable()
    {
        var ledger = new CashLedger();
        var alice = new EndClientId("alice");
        ledger.SeedIfAbsent(alice, 1_000m);

        ledger.ApplyFee(alice, 0.50m);
        ledger.ApplyFee(alice, 0.25m);

        Assert.Equal(999.25m, ledger.GetAvailable(alice));
    }

    [Fact]
    public void ApplyFee_ZeroAmount_DoesNotMaterialiseRow()
    {
        var ledger = new CashLedger();
        var ghost = new EndClientId("ghost");

        ledger.ApplyFee(ghost, 0m);

        Assert.Empty(ledger.Snapshot());
    }

    [Fact]
    public void ApplyFee_NegativeAmount_Throws()
    {
        var ledger = new CashLedger();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ledger.ApplyFee(new EndClientId("alice"), -1m));
    }
}
