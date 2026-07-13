using System.Collections.Concurrent;
using System.Net;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Pre-Negotiate, pre-handshake connection-rate limiter for the FIXP
/// listener (RFC user-bot-fixp-mtls-v0 §10.5). Bounds the TLS-handshake
/// flood DoS vector that the per-Negotiate <see cref="RateLimiterRegistry"/>
/// cannot cover, because it sits in the accept loop before any application
/// (or TLS) bytes are processed.
///
/// <para>Disabled when <c>ConnectionsPerSecondPerIp</c> is 0 — the default
/// public posture relies on an upstream LB/WAF connection-rate cap.</para>
/// </summary>
internal sealed class AcceptConnectionRateLimiter
{
    private readonly ConcurrentDictionary<string, ConnTokenBucket> _byIp = new();
    private readonly double _ratePerSecond;
    private readonly int _burst;

    public AcceptConnectionRateLimiter(int connectionsPerSecondPerIp, int burstPerIp)
    {
        _ratePerSecond = connectionsPerSecondPerIp;
        _burst = Math.Max(1, burstPerIp);
    }

    /// <summary>True when no limit is configured (every connection admitted).</summary>
    public bool Disabled => _ratePerSecond <= 0;

    public bool TryAccept(IPAddress ip, TimeProvider clock)
    {
        if (Disabled) return true;
        var bucket = _byIp.GetOrAdd(ip.ToString(), _ => new ConnTokenBucket(_burst, _ratePerSecond));
        return bucket.TryConsume(clock);
    }
}

internal sealed class ConnTokenBucket
{
    private readonly int _capacity;
    private readonly double _refillPerTick;
    private double _tokens;
    private long _lastRefillTicks;
    private bool _initialized;
    private readonly object _gate = new();

    public ConnTokenBucket(int capacity, double ratePerSecond)
    {
        _capacity = capacity;
        _tokens = capacity;
        _refillPerTick = ratePerSecond / TimeSpan.FromSeconds(1).Ticks;
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

            if (_tokens < 1.0) return false;
            _tokens -= 1.0;
            return true;
        }
    }
}
