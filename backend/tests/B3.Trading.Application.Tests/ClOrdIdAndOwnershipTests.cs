using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

public class ClOrdIdPrefixRegistryTests
{
    [Fact]
    public void Generate_PacksPrefixAndCounter_AsNonZeroUlong()
    {
        var registry = new ClOrdIdPrefixRegistry();
        var alice = new EndClientId("alice");

        var first = registry.Generate(alice);
        var second = registry.Generate(alice);

        Assert.NotEqual(0UL, first);
        Assert.NotEqual(first, second);

        // Counter advances by 1 for the same end-client.
        Assert.Equal(first + 1UL, second);

        // Same prefixIdx (top bits above CounterBits) for the same end-client.
        Assert.Equal(first >> ClOrdIdPrefixRegistry.CounterBits, second >> ClOrdIdPrefixRegistry.CounterBits);
    }

    [Fact]
    public void Generate_DifferentOwners_GetDistinctPrefixes()
    {
        var registry = new ClOrdIdPrefixRegistry();
        var a = registry.Generate(new EndClientId("alice"));
        var b = registry.Generate(new EndClientId("bob"));
        Assert.NotEqual(
            a >> ClOrdIdPrefixRegistry.CounterBits,
            b >> ClOrdIdPrefixRegistry.CounterBits);
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
        map.Register(42UL, owner);

        Assert.True(map.TryResolve(42UL, out var got));
        Assert.Equal(owner, got);
    }

    [Fact]
    public void Replacement_InheritsOwner()
    {
        var map = new OrderOwnershipMap();
        var owner = new EndClientId("alice");
        map.Register(100UL, owner);
        map.RegisterReplacement(100UL, 101UL);

        Assert.True(map.TryResolve(101UL, out var got));
        Assert.Equal(owner, got);
    }

    [Fact]
    public void CancelLink_InheritsOwner_AndIsResolvableBackToOriginal()
    {
        var map = new OrderOwnershipMap();
        var owner = new EndClientId("alice");
        map.Register(100UL, owner);
        map.RegisterCancelLink(101UL, 100UL);

        Assert.True(map.TryResolve(101UL, out var got));
        Assert.Equal(owner, got);

        Assert.True(map.TryResolveOrig(101UL, out var orig));
        Assert.Equal(100UL, orig);
    }

    [Fact]
    public void CancelLink_OnUnknownOriginal_DoesNotThrow_AndDoesNotInventOwner()
    {
        // Register the cancel-side ID even when the original isn't (yet)
        // tracked locally — covers cold-start race after a snapshot
        // restored the order but the ownership entry is still hydrating.
        var map = new OrderOwnershipMap();
        map.RegisterCancelLink(201UL, 200UL);

        Assert.False(map.TryResolve(201UL, out _));
        Assert.True(map.TryResolveOrig(201UL, out var orig));
        Assert.Equal(200UL, orig);
    }
}
