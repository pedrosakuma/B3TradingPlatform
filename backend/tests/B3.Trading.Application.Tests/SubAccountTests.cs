using B3.Trading.Application;
using B3.Trading.Domain;
using Xunit;

namespace B3.Trading.Application.Tests;

public class SubAccountsRegistryTests
{
    [Fact]
    public void ApplyCreated_TwoFirmsSameId_AreIndependent()
    {
        var r = new SubAccountsRegistry();
        r.ApplyCreated("FIRM01", "tradingdesk", "TD A");
        r.ApplyCreated("FIRM02", "tradingdesk", "TD B");

        Assert.True(r.TryGet("FIRM01", "tradingdesk", out var a));
        Assert.True(r.TryGet("FIRM02", "tradingdesk", out var b));
        Assert.Equal("TD A", a.DisplayName);
        Assert.Equal("TD B", b.DisplayName);
    }

    [Fact]
    public void ApplyDeactivated_KeepsEntryButFlipsActive()
    {
        var r = new SubAccountsRegistry();
        r.ApplyCreated("F", "td", null);
        Assert.True(r.ApplyDeactivated("F", "td"));

        Assert.False(r.IsActive("F", "td"));
        Assert.True(r.TryGet("F", "td", out var e));
        Assert.False(e.Active);
    }

    [Fact]
    public void ApplyCreated_AfterDeactivate_RevivesEntry()
    {
        var r = new SubAccountsRegistry();
        r.ApplyCreated("F", "td", "old");
        r.ApplyDeactivated("F", "td");
        r.ApplyCreated("F", "td", "new");

        Assert.True(r.IsActive("F", "td"));
        Assert.True(r.TryGet("F", "td", out var e));
        Assert.Equal("new", e.DisplayName);
    }

    [Fact]
    public void ListForFirm_ReturnsOnlyThatFirmsRows()
    {
        var r = new SubAccountsRegistry();
        r.ApplyCreated("F1", "a", null);
        r.ApplyCreated("F1", "b", null);
        r.ApplyCreated("F2", "a", null);

        var f1 = r.ListForFirm("F1").Select(e => e.Id).OrderBy(x => x).ToArray();
        var f2 = r.ListForFirm("F2").Select(e => e.Id).ToArray();
        Assert.Equal(new[] { "a", "b" }, f1);
        Assert.Equal(new[] { "a" }, f2);
    }

    [Fact]
    public void Snapshot_RoundTrips()
    {
        var r = new SubAccountsRegistry();
        r.ApplyCreated("F1", "a", "A");
        r.ApplyCreated("F1", "b", null);
        r.ApplyDeactivated("F1", "b");

        var snap = r.Snapshot();
        var r2 = new SubAccountsRegistry();
        r2.Restore(snap);

        Assert.True(r2.IsActive("F1", "a"));
        Assert.False(r2.IsActive("F1", "b"));
        Assert.True(r2.TryGet("F1", "b", out var b));
        Assert.Equal("F1", b.FirmId);
    }
}

public class SubAccountIdTests
{
    [Theory]
    [InlineData("tradingdesk")]
    [InlineData("prop")]
    [InlineData("sub.1_a-X")]
    [InlineData("a")]
    public void Constructor_AcceptsValidIds(string id)
    {
        var sa = new SubAccountId(id);
        Assert.Equal(id, sa.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("trailing!")]
    public void Constructor_RejectsInvalidIds(string id)
    {
        Assert.Throws<ArgumentException>(() => new SubAccountId(id));
    }

    [Fact]
    public void Constructor_RejectsTooLong()
    {
        var id = new string('a', 65);
        Assert.Throws<ArgumentException>(() => new SubAccountId(id));
    }

    [Fact]
    public void FromNullableString_PassesThroughNullAndEmpty()
    {
        Assert.Null(SubAccountId.FromNullableString(null));
        Assert.Null(SubAccountId.FromNullableString(""));
        Assert.Equal("td", SubAccountId.FromNullableString("td")!.Value);
    }
}

public class SubAccountPositionKeeperTests
{
    [Fact]
    public void ApplyFill_SegregatesBySubAccount()
    {
        var k = new SubAccountPositionKeeper();
        var owner = new EndClientId("trader-1");

        k.ApplyFill(owner, new SubAccountId("td"), "PETR4", OrderSide.Buy, 100, 30m);
        k.ApplyFill(owner, new SubAccountId("prop"), "PETR4", OrderSide.Buy, 50, 32m);

        var td = k.ForSubAccount(owner, new SubAccountId("td")).Single();
        var prop = k.ForSubAccount(owner, new SubAccountId("prop")).Single();

        Assert.Equal(100, td.NetQuantity);
        Assert.Equal(30m, td.AverageEntryPrice);
        Assert.Equal(50, prop.NetQuantity);
        Assert.Equal(32m, prop.AverageEntryPrice);
    }

    [Fact]
    public void Snapshot_RoundTrips_SegregatedPositions()
    {
        var k = new SubAccountPositionKeeper();
        var owner = new EndClientId("trader-1");
        k.ApplyFill(owner, new SubAccountId("td"), "PETR4", OrderSide.Buy, 100, 30m);
        k.ApplyFill(owner, new SubAccountId("td"), "VALE3", OrderSide.Sell, 20, 60m);

        var snap = k.Snapshot();
        var k2 = new SubAccountPositionKeeper();
        k2.Restore(snap);

        var rows = k2.ForSubAccount(owner, new SubAccountId("td")).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(100, rows.Single(p => p.Symbol == "PETR4").NetQuantity);
        Assert.Equal(-20, rows.Single(p => p.Symbol == "VALE3").NetQuantity);
    }
}

public class SubAccountPnlKeeperTests
{
    [Fact]
    public void Add_SegregatesBySubAccount()
    {
        var k = new SubAccountPnlKeeper();
        var day = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        k.Add("trader-1", new SubAccountId("td"), "PETR4", day, 100m);
        k.Add("trader-1", new SubAccountId("prop"), "PETR4", day, 50m);

        Assert.Equal(100m, k.GetDayRealized("trader-1", new SubAccountId("td"), "PETR4", day));
        Assert.Equal(50m, k.GetDayRealized("trader-1", new SubAccountId("prop"), "PETR4", day));
    }

    [Fact]
    public void Snapshot_RoundTrips()
    {
        var k = new SubAccountPnlKeeper();
        var day = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        k.Add("o", new SubAccountId("td"), "S", day, 7m);

        var snap = k.Snapshot();
        var k2 = new SubAccountPnlKeeper();
        k2.Restore(snap);

        Assert.Equal(7m, k2.GetDayRealized("o", new SubAccountId("td"), "S", day));
    }
}
