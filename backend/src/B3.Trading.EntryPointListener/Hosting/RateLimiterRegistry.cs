using System.Collections.Concurrent;
using System.Net;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Token-bucket rate limiter for FIXP Negotiate requests.
/// Two dimensions: per-IP (pre-auth) and per-credential (post-auth).
/// Thread-safe. Idle buckets are not reaped in v0 (known limitation —
/// bounded by distinct IPs/credentials seen, which is small for a
/// single-tenant platform).
/// </summary>
public sealed class RateLimiterRegistry
{
    private readonly ConcurrentDictionary<string, TokenBucket> _byIp = new();
    private readonly ConcurrentDictionary<Guid, TokenBucket> _byCredential = new();
    private readonly int _ipCapacity;
    private readonly int _credCapacity;

    public RateLimiterRegistry(EntryPointListenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ipCapacity = options.RateLimit.NegotiatesPerMinutePerIp;
        _credCapacity = options.RateLimit.NegotiatesPerMinutePerUsername;
    }

    public bool TryAcquireForIp(IPAddress ip, TimeProvider clock)
    {
        var key = ip.ToString();
        var bucket = _byIp.GetOrAdd(key, _ => new TokenBucket(_ipCapacity));
        return bucket.TryConsume(clock);
    }

    public bool TryAcquireForCredential(Guid credentialId, TimeProvider clock)
    {
        var bucket = _byCredential.GetOrAdd(credentialId, _ => new TokenBucket(_credCapacity));
        return bucket.TryConsume(clock);
    }
}

/// <summary>
/// Simple token-bucket: refills at <c>capacity</c> tokens per minute.
/// Thread-safe via lock.
/// </summary>
internal sealed class TokenBucket
{
    private readonly int _capacity;
    private readonly double _refillPerTick; // tokens per tick
    private double _tokens;
    private long _lastRefillTicks;
    private bool _initialized;
    private readonly object _gate = new();

    public TokenBucket(int capacity)
    {
        _capacity = capacity;
        _tokens = capacity;
        _refillPerTick = capacity / (double)TimeSpan.FromMinutes(1).Ticks;
    }

    public bool TryConsume(TimeProvider clock)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow().UtcTicks;
            if (!_initialized)
            {
                _lastRefillTicks = now;
                _initialized = true;
            }

            var elapsed = now - _lastRefillTicks;
            if (elapsed > 0)
            {
                _tokens = Math.Min(_capacity, _tokens + elapsed * _refillPerTick);
                _lastRefillTicks = now;
            }

            if (_tokens < 1.0)
                return false;

            _tokens -= 1.0;
            return true;
        }
    }
}
