using B3.Trading.Infrastructure;

namespace B3.Trading.Application.Tests;

public class SessionVerIdResolverTests
{
    [Fact]
    public void NoPersisted_ReturnsConfigured()
    {
        Assert.Equal(7u, SessionVerIdResolver.Resolve(7u, null));
    }

    [Fact]
    public void PersistedAhead_ReturnsPersistedPlusOne()
    {
        // Persisted snapshot wins after a crash so the gateway doesn't reject
        // with InvalidSessionVerId.
        Assert.Equal(11u, SessionVerIdResolver.Resolve(configured: 1u, persisted: 10u));
    }

    [Fact]
    public void ConfiguredAhead_StillUsedAsOperatorOverride()
    {
        // Operator manually bumped the configured value past anything seen
        // on disk — honour it (e.g. recovery from a corrupted state dir).
        Assert.Equal(50u, SessionVerIdResolver.Resolve(configured: 50u, persisted: 5u));
    }

    [Fact]
    public void PersistedEqualsConfigured_ReturnsPersistedPlusOne()
    {
        // Both at 5: the gateway already saw 5, so we MUST advance.
        Assert.Equal(6u, SessionVerIdResolver.Resolve(configured: 5u, persisted: 5u));
    }

    [Fact]
    public void PersistedAtMax_Overflows()
    {
        Assert.Throws<OverflowException>(() => SessionVerIdResolver.Resolve(0u, uint.MaxValue));
    }
}
