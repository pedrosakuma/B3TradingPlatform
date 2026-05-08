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

    // ── #157 AdvanceCounterTo (WAL replay watermark) ──────────────────────

    [Fact]
    public void AdvanceCounterTo_NewEndClient_AdoptsObservedPrefixAndCounter()
    {
        var registry = new ClOrdIdPrefixRegistry();
        var alice = new EndClientId("alice");
        // Observed ID: prefix=5, counter=42.
        var observed = (5UL << ClOrdIdPrefixRegistry.CounterBits) | 42UL;
        registry.AdvanceCounterTo(alice, observed);

        // Next Generate must return prefix=5, counter=43 — never the same as observed.
        var next = registry.Generate(alice);
        Assert.Equal(5UL, next >> ClOrdIdPrefixRegistry.CounterBits);
        Assert.Equal(43UL, next & ClOrdIdPrefixRegistry.CounterMask);
    }

    [Fact]
    public void AdvanceCounterTo_BumpsNextPrefixSoFutureEndClientsDoNotCollide()
    {
        var registry = new ClOrdIdPrefixRegistry();
        var alice = new EndClientId("alice");
        // Pretend a snapshot+WAL stream observed alice with prefix=7.
        var observed = (7UL << ClOrdIdPrefixRegistry.CounterBits) | 1UL;
        registry.AdvanceCounterTo(alice, observed);

        // A brand-new end-client must NOT be assigned prefix 7 (which alice owns).
        var bob = registry.Generate(new EndClientId("bob"));
        Assert.NotEqual(7UL, bob >> ClOrdIdPrefixRegistry.CounterBits);
        Assert.True((bob >> ClOrdIdPrefixRegistry.CounterBits) >= 8UL);
    }

    [Fact]
    public void AdvanceCounterTo_IsMonotonic_DoesNotRegressCounter()
    {
        var registry = new ClOrdIdPrefixRegistry();
        var alice = new EndClientId("alice");
        var high = (3UL << ClOrdIdPrefixRegistry.CounterBits) | 100UL;
        var low = (3UL << ClOrdIdPrefixRegistry.CounterBits) | 50UL;
        registry.AdvanceCounterTo(alice, high);
        registry.AdvanceCounterTo(alice, low); // must NOT regress

        var next = registry.Generate(alice);
        Assert.Equal(101UL, next & ClOrdIdPrefixRegistry.CounterMask);
    }

    [Fact]
    public void AdvanceCounterTo_Idempotent_OnReObservation()
    {
        var registry = new ClOrdIdPrefixRegistry();
        var alice = new EndClientId("alice");
        var observed = (1UL << ClOrdIdPrefixRegistry.CounterBits) | 10UL;
        registry.AdvanceCounterTo(alice, observed);
        registry.AdvanceCounterTo(alice, observed);
        Assert.Equal(11UL, registry.Generate(alice) & ClOrdIdPrefixRegistry.CounterMask);
    }

    [Fact]
    public void AdvanceCounterTo_InvalidObservedClOrdId_IsDropped()
    {
        var registry = new ClOrdIdPrefixRegistry();
        var alice = new EndClientId("alice");
        // Counter zero is never produced by Generate — treat as corruption.
        registry.AdvanceCounterTo(alice, observedClOrdId: 0UL);
        registry.AdvanceCounterTo(alice, observedClOrdId: 1UL << ClOrdIdPrefixRegistry.CounterBits); // counter=0
        // Registry must remain pristine: alice is unknown, Generate falls into fresh-allocation path.
        var first = registry.Generate(alice);
        Assert.Equal(1UL, first & ClOrdIdPrefixRegistry.CounterMask);
    }

    [Fact]
    public void AdvanceCounterTo_PrefixMismatch_KeepsExistingButReservesObservedPrefixGlobally()
    {
        var registry = new ClOrdIdPrefixRegistry();
        var alice = new EndClientId("alice");
        // Live allocation: alice gets prefix 0.
        var aliceFirst = registry.Generate(alice);
        var alicePrefix = aliceFirst >> ClOrdIdPrefixRegistry.CounterBits;

        // Replay observes alice with a different prefix (data corruption / bug).
        var corruptPrefix = alicePrefix + 5;
        var corrupt = (corruptPrefix << ClOrdIdPrefixRegistry.CounterBits) | 99UL;
        registry.AdvanceCounterTo(alice, corrupt);

        // Alice's existing entry preserved (next Generate keeps prefix 0).
        var aliceNext = registry.Generate(alice);
        Assert.Equal(alicePrefix, aliceNext >> ClOrdIdPrefixRegistry.CounterBits);

        // But the corrupt prefix is globally reserved — bob can NOT receive it.
        var bobPrefix = registry.Generate(new EndClientId("bob")) >> ClOrdIdPrefixRegistry.CounterBits;
        Assert.NotEqual(corruptPrefix, bobPrefix);
        Assert.True(bobPrefix >= corruptPrefix + 1);
    }

    [Fact]
    public void AdvanceCounterTo_AfterRestore_NextGenerateSkipsPastWalReplayedIds()
    {
        // Full snapshot-then-WAL-replay scenario: snapshot watermark
        // captured at counter N; WAL events advanced to N+k; restore +
        // replay must put the next Generate past N+k, not at N+1.
        var live = new ClOrdIdPrefixRegistry();
        var alice = new EndClientId("alice");
        var first = live.Generate(alice);            // counter=1
        var second = live.Generate(alice);           // counter=2
        var snap = live.Snapshot();                  // snapshot at counter=2

        // Pretend the live process kept allocating after the snapshot.
        var third = live.Generate(alice);            // counter=3
        var fourth = live.Generate(alice);           // counter=4

        // Cold boot: fresh registry, restore snapshot, replay WAL.
        var recovered = new ClOrdIdPrefixRegistry();
        recovered.Restore(snap);
        recovered.AdvanceCounterTo(alice, third);
        recovered.AdvanceCounterTo(alice, fourth);

        // Next Generate MUST be 5 (not 3, which would collide with `third`).
        var nextAfterReplay = recovered.Generate(alice);
        Assert.Equal(5UL, nextAfterReplay & ClOrdIdPrefixRegistry.CounterMask);
        Assert.NotEqual(third, nextAfterReplay);
        Assert.NotEqual(fourth, nextAfterReplay);
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

    [Fact]
    public void RegisterReplaceLink_PopulatesOwner_AndOrigFallback()
    {
        // Slice 1 of #122: cancel-replace must register both the owner of
        // the new ClOrdID AND the new→orig fallback link so the
        // processor can resolve a Replaced ER even when the venue omits
        // OrigClOrdID. Mirrors the cancel-link guarantees so the same
        // dropout-recovery story applies to modify.
        var map = new OrderOwnershipMap();
        var owner = new EndClientId("alice");
        map.Register(300UL, owner);

        map.RegisterReplaceLink(originalClOrdId: 300UL, newClOrdId: 301UL);

        Assert.True(map.TryResolve(301UL, out var got));
        Assert.Equal(owner, got);
        Assert.True(map.TryResolveOrig(301UL, out var orig));
        Assert.Equal(300UL, orig);
    }

    [Fact]
    public void RegisterReplaceLink_OnUnknownOriginal_Throws()
    {
        // Replace against an unregistered original is always a programmer
        // error — there's no plausible code path that fires Modify
        // without first having submitted the original.
        var map = new OrderOwnershipMap();
        Assert.Throws<InvalidOperationException>(() => map.RegisterReplaceLink(originalClOrdId: 999UL, newClOrdId: 1000UL));
    }
}
