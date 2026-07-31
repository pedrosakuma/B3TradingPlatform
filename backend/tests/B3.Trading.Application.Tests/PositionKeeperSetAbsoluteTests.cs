using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #671/#753 (RFC: admin account reset + runtime position adjustment,
/// PR 1). Focused unit coverage for <see cref="PositionKeeper.SetAbsolute"/>:
/// overwrite (never accumulate) semantics, firm/owner/symbol scoping,
/// and the RFC #753 zero-quantity/average-price invariant. WAL/replay
/// coverage of the same behaviour lives in
/// <c>PositionAdjustmentRecoveryTests</c>; HTTP-surface coverage lives
/// in <c>PositionAdjustmentAdminEndpointTests</c> (API test project).
/// </summary>
public class PositionKeeperSetAbsoluteTests
{
    [Fact]
    public void SetAbsolute_CreatesPositionWhenAbsent()
    {
        var keeper = new PositionKeeper();
        var owner = new EndClientId("alice");

        keeper.SetAbsolute(owner, "PETR4", 2000, 32.50m);

        var pos = keeper.GetOrCreate(owner, "PETR4");
        Assert.Equal(2000, pos.NetQuantity);
        Assert.Equal(32.50m, pos.AverageEntryPrice);
    }

    [Fact]
    public void SetAbsolute_OverwritesExistingAccumulatedFills_DoesNotMerge()
    {
        var keeper = new PositionKeeper();
        var owner = new EndClientId("alice");
        keeper.ApplyFill(owner, "PETR4", OrderSide.Buy, 100, 30m);
        keeper.ApplyFill(owner, "PETR4", OrderSide.Buy, 50, 31m);

        keeper.SetAbsolute(owner, "PETR4", 500, 40m);

        var pos = keeper.GetOrCreate(owner, "PETR4");
        // Absolute overwrite: 500/40, not 150 (the pre-existing fills'
        // net) plus/merged with the new value in any way.
        Assert.Equal(500, pos.NetQuantity);
        Assert.Equal(40m, pos.AverageEntryPrice);
    }

    [Fact]
    public void SetAbsolute_CalledTwice_SecondCallWinsOutright()
    {
        var keeper = new PositionKeeper();
        var owner = new EndClientId("alice");

        keeper.SetAbsolute(owner, "PETR4", 100, 20m);
        keeper.SetAbsolute(owner, "PETR4", -40, 22m);

        var pos = keeper.GetOrCreate(owner, "PETR4");
        Assert.Equal(-40, pos.NetQuantity);
        Assert.Equal(22m, pos.AverageEntryPrice);
    }

    [Fact]
    public void SetAbsolute_CanFlattenToZero_WithZeroPrice()
    {
        var keeper = new PositionKeeper();
        var owner = new EndClientId("alice");
        keeper.SetAbsolute(owner, "PETR4", 100, 20m);

        keeper.SetAbsolute(owner, "PETR4", 0, 0m);

        var pos = keeper.GetOrCreate(owner, "PETR4");
        Assert.Equal(0, pos.NetQuantity);
        Assert.Equal(0m, pos.AverageEntryPrice);
    }

    [Fact]
    public void SetAbsolute_ZeroQuantity_WithNonZeroPrice_Throws()
    {
        var keeper = new PositionKeeper();
        var owner = new EndClientId("alice");

        Assert.Throws<ArgumentException>(() => keeper.SetAbsolute(owner, "PETR4", 0, 10m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SetAbsolute_NonZeroQuantity_WithNonPositivePrice_Throws(decimal price)
    {
        var keeper = new PositionKeeper();
        var owner = new EndClientId("alice");

        Assert.Throws<ArgumentException>(() => keeper.SetAbsolute(owner, "PETR4", 100, price));
    }

    [Fact]
    public void SetAbsolute_NegativeQuantity_WithPositivePrice_IsAllowed()
    {
        var keeper = new PositionKeeper();
        var owner = new EndClientId("alice");

        keeper.SetAbsolute(owner, "PETR4", -250, 18m);

        var pos = keeper.GetOrCreate(owner, "PETR4");
        Assert.Equal(-250, pos.NetQuantity);
        Assert.Equal(18m, pos.AverageEntryPrice);
    }

    [Fact]
    public void SetAbsolute_FirmScoped_DoesNotLeakAcrossFirms()
    {
        var keeper = new PositionKeeper();
        var owner = new EndClientId("alice");

        keeper.SetAbsolute("FIRM01", owner, "PETR4", 100, 20m);
        keeper.SetAbsolute("FIRM02", owner, "PETR4", 999, 99m);

        var firm01 = Assert.Single(keeper.ForEndClientAndFirm("FIRM01", owner));
        Assert.Equal(100, firm01.NetQuantity);
        Assert.Equal(20m, firm01.AverageEntryPrice);

        var firm02 = Assert.Single(keeper.ForEndClientAndFirm("FIRM02", owner));
        Assert.Equal(999, firm02.NetQuantity);
        Assert.Equal(99m, firm02.AverageEntryPrice);

        Assert.Empty(keeper.ForEndClientAndFirm(PositionKeeper.DefaultFirmId, owner));
    }

    [Fact]
    public void SetAbsolute_ScopedPerOwnerAndSymbol()
    {
        var keeper = new PositionKeeper();
        var alice = new EndClientId("alice");
        var bob = new EndClientId("bob");

        keeper.SetAbsolute(alice, "PETR4", 100, 20m);
        keeper.SetAbsolute(alice, "VALE3", 200, 55m);
        keeper.SetAbsolute(bob, "PETR4", 300, 30m);

        Assert.Equal(100, keeper.GetOrCreate(alice, "PETR4").NetQuantity);
        Assert.Equal(200, keeper.GetOrCreate(alice, "VALE3").NetQuantity);
        Assert.Equal(300, keeper.GetOrCreate(bob, "PETR4").NetQuantity);
        Assert.Equal(0, keeper.GetOrCreate(bob, "VALE3").NetQuantity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void SetAbsolute_BlankFirmId_Throws(string? firmId)
    {
        var keeper = new PositionKeeper();
        var owner = new EndClientId("alice");

        Assert.ThrowsAny<ArgumentException>(() => keeper.SetAbsolute(firmId!, owner, "PETR4", 100, 20m));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void SetAbsolute_BlankSymbol_Throws(string? symbol)
    {
        var keeper = new PositionKeeper();
        var owner = new EndClientId("alice");

        Assert.ThrowsAny<ArgumentException>(() => keeper.SetAbsolute(owner, symbol!, 100, 20m));
    }
}
