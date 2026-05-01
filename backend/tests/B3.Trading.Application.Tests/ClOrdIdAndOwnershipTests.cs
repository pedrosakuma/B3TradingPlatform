using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

public class ClOrdIdPrefixRegistryTests
{
    [Fact]
    public void Generate_ProducesFixedFormat()
    {
        var registry = new ClOrdIdPrefixRegistry();
        var alice = new EndClientId("alice");

        var first = registry.Generate(alice);
        var second = registry.Generate(alice);

        Assert.Equal(17, first.Length);
        Assert.Matches("^[0-9a-z]{4}-[0-9]{12}$", first);
        Assert.NotEqual(first, second);

        // Same prefix for the same end-client.
        Assert.Equal(first[..4], second[..4]);
    }

    [Fact]
    public void Generate_DifferentOwners_GetDistinctPrefixes()
    {
        var registry = new ClOrdIdPrefixRegistry();
        var a = registry.Generate(new EndClientId("alice"));
        var b = registry.Generate(new EndClientId("bob"));
        Assert.NotEqual(a[..4], b[..4]);
    }

    [Fact]
    public void AllocatePrefix_IsIdempotent()
    {
        var registry = new ClOrdIdPrefixRegistry();
        var owner = new EndClientId("alice");
        Assert.Equal(registry.AllocatePrefix(owner), registry.AllocatePrefix(owner));
    }
}

public class OrderOwnershipMapTests
{
    [Fact]
    public void Resolve_ReturnsRegisteredOwner()
    {
        var map = new OrderOwnershipMap();
        var owner = new EndClientId("alice");
        map.Register("0001-000000000001", owner);

        Assert.True(map.TryResolve("0001-000000000001", out var got));
        Assert.Equal(owner, got);
    }

    [Fact]
    public void Replacement_InheritsOwner()
    {
        var map = new OrderOwnershipMap();
        var owner = new EndClientId("alice");
        map.Register("orig", owner);
        map.RegisterReplacement("orig", "newer");

        Assert.True(map.TryResolve("newer", out var got));
        Assert.Equal(owner, got);
    }
}
