using System.Collections.Concurrent;

namespace B3.Trading.Application.Risk.Accounting;

/// <summary>
/// Per-key sliding-window aggregate of <see cref="decimal"/> values
/// over a configurable time horizon. Used by the slice-7 throttle
/// checks (rolling notional, order rate). Each key owns a queue of
/// timestamped entries plus a running sum/count so neither
/// <see cref="Sum"/> nor <see cref="Append"/> need to walk the queue
/// on every call — only the head is pruned.
/// </summary>
///
/// <remarks>
/// <para>
/// <b>Concurrency.</b> Each bucket has its own lock (a plain
/// <c>object</c>); cross-key contention is therefore the same as
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>'s. <c>Sum</c> and
/// <c>Append</c> both prune lazily, so reads are not free, but the
/// hot path is bounded by the number of entries that actually fall
/// outside the window since the last call.
/// </para>
///
/// <para>
/// <b>Check / record race.</b> The check (<c>Sum</c>) and the record
/// (<c>Append</c>) are intentionally <em>not</em> a single atomic
/// operation. Risk evaluation runs first and the accountant only
/// records after both the synchronous pipeline <em>and</em> the async
/// margin reservation approve — wrapping a lock around that span
/// would serialize order entry. This is acceptable for an anti-runaway
/// guard: under N concurrent submits, the cap can be overshot by up
/// to N. Documented at <c>docs/rfcs/pre-trade-risk-v2.md</c> §4.4.
/// </para>
///
/// <para>
/// <b>Memory.</b> Empty buckets are removed by
/// <see cref="SweepEmptyBuckets"/>, called periodically by a hosted
/// service. Removal is reference-equality based so a racing Append
/// that has already mutated the bucket cannot lose its entry.
/// </para>
/// </remarks>
public sealed class SlidingWindowLedger
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public SlidingWindowLedger(TimeProvider clock)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Sum of values whose timestamp is within
    /// <c>(now - <paramref name="window"/>, now]</c>. Returns 0 for
    /// unknown keys without allocating a bucket.
    /// </summary>
    public decimal Sum(string key, TimeSpan window)
    {
        if (!_buckets.TryGetValue(key, out var bucket)) return 0m;
        var cutoff = _clock.GetUtcNow() - window;
        return bucket.PruneAndSum(cutoff);
    }

    /// <summary>
    /// Count of entries whose timestamp is within the window. Same
    /// no-allocation behavior as <see cref="Sum"/>.
    /// </summary>
    public int Count(string key, TimeSpan window)
    {
        if (!_buckets.TryGetValue(key, out var bucket)) return 0;
        var cutoff = _clock.GetUtcNow() - window;
        return bucket.PruneAndCount(cutoff);
    }

    /// <summary>
    /// Append a value at the current clock time. Creates the bucket
    /// on first use.
    /// </summary>
    public void Append(string key, decimal value)
    {
        var bucket = _buckets.GetOrAdd(key, static _ => new Bucket());
        bucket.Append(_clock.GetUtcNow(), value);
    }

    /// <summary>
    /// Removes buckets whose pruned content is empty for the given
    /// window. Intended to be called from a periodic background
    /// sweeper to bound memory under tenant churn.
    /// </summary>
    public int SweepEmptyBuckets(TimeSpan window)
    {
        var cutoff = _clock.GetUtcNow() - window;
        var removed = 0;
        foreach (var kv in _buckets)
        {
            // Prune first so a long-idle bucket releases its inner
            // queue capacity even when it would otherwise remain
            // mapped.
            kv.Value.PruneAndCount(cutoff);
            if (kv.Value.IsEmpty)
            {
                // Reference-equality remove: a concurrent Append that
                // raced past PruneAndCount has already mutated the
                // bucket and made it non-empty, so the value
                // comparison fails and we skip removal. The bucket
                // stays mapped and the entry survives.
                if (((ICollection<KeyValuePair<string, Bucket>>)_buckets)
                    .Remove(new KeyValuePair<string, Bucket>(kv.Key, kv.Value)))
                {
                    removed++;
                }
            }
        }
        return removed;
    }

    public int ActiveBucketCount => _buckets.Count;

    /// <summary>
    /// Diagnostic-only enumeration of currently-tracked keys. Used by
    /// admin endpoints; not on the hot path.
    /// </summary>
    public IEnumerable<string> Keys => _buckets.Keys;

    private sealed class Bucket
    {
        private readonly object _lock = new();
        private readonly Queue<Entry> _entries = new();
        private decimal _runningSum;

        public bool IsEmpty
        {
            get
            {
                lock (_lock) return _entries.Count == 0;
            }
        }

        public void Append(DateTimeOffset ts, decimal value)
        {
            lock (_lock)
            {
                _entries.Enqueue(new Entry(ts, value));
                _runningSum += value;
            }
        }

        public decimal PruneAndSum(DateTimeOffset cutoff)
        {
            lock (_lock)
            {
                Prune(cutoff);
                return _runningSum;
            }
        }

        public int PruneAndCount(DateTimeOffset cutoff)
        {
            lock (_lock)
            {
                Prune(cutoff);
                return _entries.Count;
            }
        }

        private void Prune(DateTimeOffset cutoff)
        {
            while (_entries.Count > 0 && _entries.Peek().Timestamp <= cutoff)
            {
                var dropped = _entries.Dequeue();
                _runningSum -= dropped.Value;
            }
        }

        private readonly record struct Entry(DateTimeOffset Timestamp, decimal Value);
    }
}
