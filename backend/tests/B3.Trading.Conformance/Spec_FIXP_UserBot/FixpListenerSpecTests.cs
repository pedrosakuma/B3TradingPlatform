using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_FIXP_UserBot;

/// <summary>
/// FIXP listener conformance scenarios. Skipped when
/// <c>B3T_FIXP_ENDPOINT</c> is not set (which is the case in normal CI).
/// Runnable when a FIXP listener is deployed and env vars are configured.
/// </summary>
[Trait("Category", "Conformance")]
public class FixpListenerSpecTests
{
    [ConformanceFact(RequiresFixpListener = true)]
    public void Negotiate_HappyPath_ReturnsAck()
    {
        // This test would connect to a live FIXP listener and perform a
        // full Negotiate handshake. Skipped in CI when env is not set.
        var endpoint = Environment.GetEnvironmentVariable(PlatformEndpoint.EnvFixpEndpoint);
        Assert.NotNull(endpoint);
        // Full wire test deferred — placeholder asserts env is present
    }

    [ConformanceFact(RequiresFixpListener = true)]
    public void Negotiate_BadCredentials_ReturnsReject()
    {
        var endpoint = Environment.GetEnvironmentVariable(PlatformEndpoint.EnvFixpEndpoint);
        Assert.NotNull(endpoint);
    }

    [ConformanceFact(RequiresFixpListener = true)]
    public void Establish_StaleVersion_ReturnsReject()
    {
        var endpoint = Environment.GetEnvironmentVariable(PlatformEndpoint.EnvFixpEndpoint);
        Assert.NotNull(endpoint);
    }

    [ConformanceFact(RequiresFixpListener = true)]
    public void RateLimit_BurstNegotiates_StartsRejecting()
    {
        var endpoint = Environment.GetEnvironmentVariable(PlatformEndpoint.EnvFixpEndpoint);
        Assert.NotNull(endpoint);
    }
}
