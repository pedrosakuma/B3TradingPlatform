using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

public class PositionKeeperSeedTests
{
    [Fact]
    public void SeedIfAbsent_AppliesWhenSlotEmpty()
    {
        var keeper = new PositionKeeper();
        var owner = new EndClientId("alice");

        var applied = keeper.SeedIfAbsent(owner, "PETR4", 2000, 32.50m);

        Assert.True(applied);
        var pos = keeper.GetOrCreate(owner, "PETR4");
        Assert.Equal(2000, pos.NetQuantity);
        Assert.Equal(32.50m, pos.AverageEntryPrice);
    }

    [Fact]
    public void SeedIfAbsent_NoOpWhenPositionAlreadyPresent()
    {
        // Simulates the warm-restart path: PersistenceRecovery has
        // already restored a real fill, so the seed must NOT clobber it.
        var keeper = new PositionKeeper();
        var owner = new EndClientId("alice");
        keeper.ApplyFill(owner, "PETR4", OrderSide.Buy, 100, 30m);

        var applied = keeper.SeedIfAbsent(owner, "PETR4", 2000, 32.50m);

        Assert.False(applied);
        var pos = keeper.GetOrCreate(owner, "PETR4");
        Assert.Equal(100, pos.NetQuantity);
        Assert.Equal(30m, pos.AverageEntryPrice);
    }

    [Fact]
    public void SeedIfAbsent_ScopedPerOwnerAndSymbol()
    {
        var keeper = new PositionKeeper();
        var alice = new EndClientId("alice");
        var bob = new EndClientId("bob");

        Assert.True(keeper.SeedIfAbsent(alice, "PETR4", 2000, 32.50m));
        Assert.True(keeper.SeedIfAbsent(alice, "VALE3", 2000, 65.00m));
        Assert.True(keeper.SeedIfAbsent(bob, "PETR4", 2000, 32.50m));

        Assert.Equal(2000, keeper.GetOrCreate(alice, "PETR4").NetQuantity);
        Assert.Equal(2000, keeper.GetOrCreate(alice, "VALE3").NetQuantity);
        Assert.Equal(2000, keeper.GetOrCreate(bob, "PETR4").NetQuantity);
        Assert.Equal(0, keeper.GetOrCreate(bob, "VALE3").NetQuantity);
    }
}
