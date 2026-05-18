using System.Collections.Concurrent;

namespace B3.Trading.Api.RateLimit;

/// <summary>
/// Q4.4 (#304). Thread-safe in-memory token-bucket limiter keyed by
/// <c>(userKey, endpointKey)</c>.
/// </summary>
/// <remarks>
/// <para>
/// The bucket dictionary is a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// keyed by a struct composite key. Each bucket carries its own lock
/// (a plain <see cref="object"/>) — refill+deduct is a few floating-
/// point ops so the critical section is short enough that the simple
/// monitor wins over a CAS loop on a packed long. The "atomic via
/// Interlocked CAS on packed long" alternative is documented in the
/// design note on #304 but not implemented here; profile first if the
/// per-bucket lock ever shows up under contention.
/// </para>
/// <para>
/// Idle-bucket sweep: every <see cref="SweepInterval"/> the limiter
/// removes buckets whose <c>lastRefillUtc</c> is older than
/// <see cref="IdleTtl"/> (default: 1 hour). This bounds memory in the
/// face of long-tail user keys (e.g. one-shot scripts) without
/// affecting active sessions — an evicted bucket simply reappears full
/// on the next request, which is the correct semantics for a token
/// bucket that has been idle long enough to refill anyway.
/// </para>
/// </remarks>
public sealed class TokenBucketRateLimiter : IRateLimiter, IDisposable
{
    /// <summary>Window between idle-bucket sweeps.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A bucket whose last refill timestamp is older than this is
    /// eligible for eviction by the sweeper. One hour is well above
    /// any realistic refill horizon for the configured policies.
    /// </summary>
    public static readonly TimeSpan IdleTtl = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<BucketKey, Bucket> _buckets = new();
    private readonly Func<DateTime> _utcNow;
    private readonly Timer? _sweepTimer;

    public TokenBucketRateLimiter() : this(() => DateTime.UtcNow, startSweeper: true) { }

    // Test-only ctor — deterministic clock + opt-out of the timer so
    // unit tests don't race with the sweeper.
    internal TokenBucketRateLimiter(Func<DateTime> utcNow, bool startSweeper)
    {
        _utcNow = utcNow;
        if (startSweeper)
        {
            _sweepTimer = new Timer(_ => SweepIdleBuckets(), null, SweepInterval, SweepInterval);
        }
    }

    public bool TryAcquire(
        string userKey,
        string endpointKey,
        int burst,
        double refillPerSecond,
        out double retryAfterSeconds)
    {
        if (burst <= 0 || refillPerSecond <= 0)
        {
            // A misconfigured rule should fail open rather than lock
            // every user out forever; surface as "no limit applied".
            retryAfterSeconds = 0;
            return true;
        }

        var key = new BucketKey(userKey, endpointKey);

        // Outer loop handles the sweeper-vs-acquire race: the sweeper
        // may evict our bucket AFTER GetOrAdd returns it but BEFORE we
        // take its lock. The Evicted flag, set under the bucket lock
        // by the sweeper, lets us detect that and retry against the
        // (fresh) replacement bucket. Two iterations is the worst case:
        // by the second pass the sweeper would have to evict a bucket
        // we just inserted, which requires it to also pass the IdleTtl
        // check — impossible without time travel.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var bucket = _buckets.GetOrAdd(key, _ => new Bucket(burst, _utcNow()));
            OnBucketAcquiredForTest?.Invoke();

            lock (bucket.Gate)
            {
                if (bucket.Evicted)
                {
                    // The sweeper removed `bucket` from the dictionary
                    // between our GetOrAdd and this lock. Any tokens we
                    // hand out from this orphan would be in addition to
                    // those that the replacement bucket will hand out —
                    // i.e. up to 2× burst across two requests racing
                    // the sweep. Drop the lock and try again.
                    continue;
                }

                var now = _utcNow();
                var elapsed = (now - bucket.LastRefillUtc).TotalSeconds;
                if (elapsed > 0)
                {
                    bucket.Tokens = Math.Min(burst, bucket.Tokens + elapsed * refillPerSecond);
                    bucket.LastRefillUtc = now;
                }

                if (bucket.Tokens >= 1.0)
                {
                    bucket.Tokens -= 1.0;
                    retryAfterSeconds = 0;
                    return true;
                }

                // tokens shortfall / refillPerSecond → seconds until the
                // next whole token is available. Ceiling to a friendly
                // integer second below in the middleware.
                var needed = 1.0 - bucket.Tokens;
                retryAfterSeconds = needed / refillPerSecond;
                return false;
            }
        }

        // Defensive: if we somehow lost the race twice in a row, deny
        // rather than loop forever. In practice this branch is
        // unreachable — see the comment on the for loop above.
        retryAfterSeconds = 1.0 / refillPerSecond;
        return false;
    }

    /// <summary>
    /// Snapshot the current number of tracked buckets. Test-only.
    /// </summary>
    internal int BucketCount => _buckets.Count;

    /// <summary>
    /// Force an idle sweep. Test-only.
    /// </summary>
    internal void SweepIdleBucketsForTest() => SweepIdleBuckets();

    /// <summary>
    /// Test hook fired between <c>GetOrAdd</c> and the per-bucket lock
    /// in <see cref="TryAcquire"/>. Used to deterministically reproduce
    /// the sweeper-vs-acquire race; never set in production.
    /// </summary>
    internal Action? OnBucketAcquiredForTest { get; set; }

    private void SweepIdleBuckets()
    {
        var cutoff = _utcNow() - IdleTtl;
        foreach (var kvp in _buckets)
        {
            // Take the bucket's own lock so we don't race a TryAcquire
            // that is in the middle of advancing LastRefillUtc, and so
            // any TryAcquire that has already obtained a reference to
            // this bucket but is still waiting on the lock sees the
            // Evicted flag we set below and retries.
            lock (kvp.Value.Gate)
            {
                if (kvp.Value.LastRefillUtc >= cutoff) continue;
                kvp.Value.Evicted = true;
                // Atomic compare-and-remove: only drop the entry if it
                // STILL maps to the same bucket instance. Belt-and-
                // braces — the Evicted flag is already authoritative,
                // but this avoids accidentally removing a replacement
                // bucket inserted by a concurrent retry.
                ((ICollection<KeyValuePair<BucketKey, Bucket>>)_buckets)
                    .Remove(kvp);
            }
        }
    }

    public void Dispose() => _sweepTimer?.Dispose();

    private readonly record struct BucketKey(string UserKey, string EndpointKey);

    private sealed class Bucket
    {
        public readonly object Gate = new();
        public double Tokens;
        public DateTime LastRefillUtc;
        // Set by the sweeper under Gate when the bucket is removed
        // from the dictionary. TryAcquire checks this after taking
        // Gate and retries the outer GetOrAdd loop if true — without
        // it, a request that captured this bucket reference before
        // eviction would happily decrement tokens while a concurrent
        // request hands out a full fresh burst on the replacement.
        public bool Evicted;

        public Bucket(int burst, DateTime nowUtc)
        {
            Tokens = burst;
            LastRefillUtc = nowUtc;
        }
    }
}
