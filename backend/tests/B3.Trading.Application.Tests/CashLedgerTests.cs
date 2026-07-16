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
        var ledger = new CashLedger();
        var alice = new EndClientId("alice");

        ledger.ApplyFill(alice, OrderSide.Sell, 10, 50m);   // +500
        Assert.False(ledger.SeedIfAbsent(alice, 100_000m));
        Assert.Equal(500m, ledger.GetAvailable(alice));
    }

    [Fact]
    public void Snapshot_PreservesMaterialisedZeroAndNegativeBalances()
    {
        var ledger = new CashLedger();
        var alice = new EndClientId("alice");
        var bob = new EndClientId("bob");
        var carol = new EndClientId("carol");

        ledger.SeedIfAbsent(alice, 1_000m);
        ledger.SeedIfAbsent(bob, 0m);
        ledger.SeedIfAbsent(carol, -250m);         // kept (debt is real)

        var snap = ledger.Snapshot().ToDictionary(s => s.EndClientId, s => s.Available);
        var raw = ledger.RawSnapshot().ToDictionary(s => s.EndClientId, s => s.Available);

        Assert.Equal(3, snap.Count);
        Assert.Equal(1_000m, snap["alice"]);
        Assert.Equal(0m, snap["bob"]);
        Assert.Equal(-250m, snap["carol"]);
        Assert.Equal(0m, raw["bob"]);
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

    [Fact]
    public void SameOwner_IsFirmSegregated_ThroughSnapshotRestore()
    {
        var owner = new EndClientId("alice");
        var ledger = new CashLedger();
        ledger.SeedIfAbsent("FIRM01", owner, 1_000m);
        ledger.SeedIfAbsent("FIRM02", owner, 200m);
        ledger.ApplyFill("FIRM01", owner, OrderSide.Buy, 10, 30m);

        Assert.Equal(700m, ledger.GetAvailable("FIRM01", owner));
        Assert.Equal(200m, ledger.GetAvailable("FIRM02", owner));

        var restored = new CashLedger();
        restored.Restore(ledger.Snapshot());

        Assert.Equal(700m, restored.GetAvailable("FIRM01", owner));
        Assert.Equal(200m, restored.GetAvailable("FIRM02", owner));
        Assert.Equal(0m, restored.GetAvailable("FIRM03", owner));
    }

    [Fact]
    public void LegacySnapshotRow_SeedMapsBalanceWithoutApplyingSeed()
    {
        var owner = new EndClientId("alice");
        var ledger = new CashLedger();
        ledger.Restore([
            new B3.Trading.Application.Persistence.CashBalanceSnapshot("alice", 500m),
        ], firmScoped: false);

        Assert.Throws<InvalidOperationException>(
            ledger.EnsureNoUnmappedLegacyBalances);
        Assert.False(ledger.SeedIfAbsent("FIRM01", owner, 10_000m));
        Assert.Equal(500m, ledger.GetAvailable("FIRM01", owner));
        Assert.Equal(0m, ledger.GetAvailable("default", owner));
    }

    [Fact]
    public void LegacySnapshotRow_ConflictingFirmHintFailsClosed()
    {
        var ledger = new CashLedger();
        ledger.Restore([
            new B3.Trading.Application.Persistence.CashBalanceSnapshot("alice", 500m),
        ], firmScoped: false);
        ledger.ResolveLegacyBalances(new Dictionary<string, string>
        {
            ["alice"] = "FIRM01",
        });

        Assert.Throws<InvalidOperationException>(() =>
            ledger.ResolveLegacyBalances(new Dictionary<string, string>
            {
                ["alice"] = "FIRM02",
            }));
    }

    // ── #386 BalanceChanged event ────────────────────────────────────

    [Fact]
    public void BalanceChanged_FiresOnSeed_WithSeededAmount()
    {
        var ledger = new CashLedger();
        var owner = new EndClientId("alice");
        (EndClientId Owner, decimal Available)? observed = null;
        ledger.BalanceChanged += (_, o, a) => observed = (o, a);

        ledger.SeedIfAbsent(owner, 1_000m);

        Assert.NotNull(observed);
        Assert.Equal(owner, observed!.Value.Owner);
        Assert.Equal(1_000m, observed.Value.Available);
    }

    [Fact]
    public void BalanceChanged_FiresOnFill_WithPostMutationAvailable()
    {
        var ledger = new CashLedger();
        var owner = new EndClientId("alice");
        ledger.SeedIfAbsent(owner, 500m);
        var captured = new List<decimal>();
        ledger.BalanceChanged += (_, _, a) => captured.Add(a);

        ledger.ApplyFill(owner, OrderSide.Buy, 10, 25m); // -250

        Assert.Single(captured);
        Assert.Equal(250m, captured[0]);
    }

    [Fact]
    public void BalanceChanged_FiresOnFee_WithPostMutationAvailable()
    {
        var ledger = new CashLedger();
        var owner = new EndClientId("alice");
        ledger.SeedIfAbsent(owner, 100m);
        var captured = new List<decimal>();
        ledger.BalanceChanged += (_, _, a) => captured.Add(a);

        ledger.ApplyFee(owner, 7.50m);

        Assert.Single(captured);
        Assert.Equal(92.50m, captured[0]);
    }

    [Fact]
    public void BalanceChanged_DoesNotFire_OnZeroFee()
    {
        var ledger = new CashLedger();
        var owner = new EndClientId("alice");
        ledger.SeedIfAbsent(owner, 100m);
        var fireCount = 0;
        ledger.BalanceChanged += (_, _, _) => fireCount++;

        ledger.ApplyFee(owner, 0m);

        Assert.Equal(0, fireCount);
    }

    [Fact]
    public void BalanceChanged_ThrowingSubscriber_DoesNotPoisonLedger()
    {
        var ledger = new CashLedger();
        var owner = new EndClientId("alice");
        ledger.BalanceChanged += (_, _, _) => throw new InvalidOperationException("bad subscriber");

        // Must not throw out of the ledger.
        ledger.SeedIfAbsent(owner, 100m);
        ledger.ApplyFill(owner, OrderSide.Sell, 5, 10m); // +50

        Assert.Equal(150m, ledger.GetAvailable(owner));
    }
}
