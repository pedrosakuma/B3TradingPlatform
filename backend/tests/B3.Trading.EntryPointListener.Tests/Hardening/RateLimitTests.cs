using B3.Trading.EntryPointListener.Hosting;

namespace B3.Trading.EntryPointListener.Tests.Hardening;

public class RateLimitTests
{
    [Fact]
    public void TokenBucket_InitialTokens_AllowsCapacity()
    {
        var bucket = new TokenBucket(5);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        for (int i = 0; i < 5; i++)
            Assert.True(bucket.TryConsume(clock));
        Assert.False(bucket.TryConsume(clock));
    }

    [Fact]
    public void TokenBucket_RefillsAfterOneMinute()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        var bucket = new TokenBucket(5);

        // Exhaust tokens
        for (int i = 0; i < 5; i++)
            Assert.True(bucket.TryConsume(clock));
        Assert.False(bucket.TryConsume(clock));

        // Advance 1 minute — should refill all 5 tokens
        clock.Advance(TimeSpan.FromMinutes(1));
        for (int i = 0; i < 5; i++)
            Assert.True(bucket.TryConsume(clock));
        Assert.False(bucket.TryConsume(clock));
    }

    [Fact]
    public void RateLimiterRegistry_IpLimiter_RejectsAfterCap()
    {
        var opts = new EntryPointListenerOptions
        {
            RateLimit = new EntryPointListenerOptions.RateLimitOptions
            {
                NegotiatesPerMinutePerIp = 3,
                NegotiatesPerMinutePerUsername = 10,
            },
        };
        var registry = new RateLimiterRegistry(opts);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var ip = System.Net.IPAddress.Loopback;

        for (int i = 0; i < 3; i++)
            Assert.True(registry.TryAcquireForIp(ip, clock));
        Assert.False(registry.TryAcquireForIp(ip, clock));
    }

    [Fact]
    public void RateLimiterRegistry_CredentialLimiter_RejectsAfterCap()
    {
        var opts = new EntryPointListenerOptions
        {
            RateLimit = new EntryPointListenerOptions.RateLimitOptions
            {
                NegotiatesPerMinutePerIp = 30,
                NegotiatesPerMinutePerUsername = 2,
            },
        };
        var registry = new RateLimiterRegistry(opts);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var credId = Guid.NewGuid();

        Assert.True(registry.TryAcquireForCredential(credId, clock));
        Assert.True(registry.TryAcquireForCredential(credId, clock));
        Assert.False(registry.TryAcquireForCredential(credId, clock));
    }

    [Fact]
    public void AcceptLimiter_DisabledWhenRateZero_AdmitsEverything()
    {
        var limiter = new AcceptConnectionRateLimiter(0, 30);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Assert.True(limiter.Disabled);
        for (int i = 0; i < 1000; i++)
            Assert.True(limiter.TryAccept(System.Net.IPAddress.Loopback, clock));
    }

    [Fact]
    public void AcceptLimiter_RejectsBurstThenRefillsPerSecond()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new AcceptConnectionRateLimiter(connectionsPerSecondPerIp: 2, burstPerIp: 3);
        var ip = System.Net.IPAddress.Loopback;

        for (int i = 0; i < 3; i++)
            Assert.True(limiter.TryAccept(ip, clock));
        Assert.False(limiter.TryAccept(ip, clock));

        clock.Advance(TimeSpan.FromSeconds(1)); // +2 tokens
        Assert.True(limiter.TryAccept(ip, clock));
        Assert.True(limiter.TryAccept(ip, clock));
        Assert.False(limiter.TryAccept(ip, clock));
    }

    [Fact]
    public void AcceptLimiter_PerIpIsolation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new AcceptConnectionRateLimiter(connectionsPerSecondPerIp: 1, burstPerIp: 1);
        var a = System.Net.IPAddress.Parse("10.0.0.1");
        var b = System.Net.IPAddress.Parse("10.0.0.2");

        Assert.True(limiter.TryAccept(a, clock));
        Assert.False(limiter.TryAccept(a, clock));
        Assert.True(limiter.TryAccept(b, clock)); // independent bucket
    }
}

internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan duration) => _now += duration;
}
