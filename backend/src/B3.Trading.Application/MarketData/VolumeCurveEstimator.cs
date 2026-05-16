using System.Collections.Concurrent;

namespace B3.Trading.Application.MarketData;

/// <summary>
/// Per-symbol intraday volume curve estimator (Q3.1 / #281).
///
/// <para>
/// Bucketing: <see cref="DefaultBucketSize"/> (5 minutes) across each UTC
/// trading day. Each call to <see cref="RecordTrade"/> with
/// <c>(symbol, qty, utc)</c> accrues qty into the bucket for the day-key
/// of <c>utc</c>. The CDF for a window <c>[start, end]</c> at time
/// <c>at</c> is <c>volumeIn(start..at) / volumeIn(start..end)</c>.
/// </para>
///
/// <para>
/// <b>Fallback.</b> When the symbol has no observed volume in the window
/// (e.g. on a fresh boot before any trade arrives), the estimator
/// returns a uniform CDF — <c>(at - start) / (end - start)</c> — so the
/// VWAP slice scheduler degrades gracefully into a TWAP-shaped
/// distribution.
/// </para>
///
/// <para>
/// <b>Data source.</b> v0 only consumes live intraday volume. The
/// repo's <c>MarketTrade</c> shape currently lacks a quantity field
/// (only price + timestamp), so the host wiring of "subscribe to
/// marketdata.trades.${symbol}" is left as a TODO — the estimator is
/// already API-stable so a future patch wiring qty-bearing trades is
/// purely additive. Tests drive <see cref="RecordTrade"/> directly.
/// </para>
///
/// <para>
/// <b>Persistence.</b> Historical (cross-day) curve persistence is
/// explicitly OUT OF SCOPE for #281 — the estimator forgets buckets
/// outside the in-memory <see cref="MaxRetentionDays"/> window. Future
/// work can layer a snapshot/replay path on top without changing the
/// public API.
/// </para>
///
/// <para>
/// All operations are O(1) amortised; the per-day-per-symbol bucket
/// arrays are sparse (lazy-initialised on first trade) and small
/// (288 buckets/day at 5min granularity).
/// </para>
/// </summary>
public sealed class VolumeCurveEstimator
{
    public static readonly TimeSpan DefaultBucketSize = TimeSpan.FromMinutes(5);
    public const int MaxRetentionDays = 7;

    private readonly TimeSpan _bucketSize;
    private readonly ConcurrentDictionary<(string Symbol, DateOnly Day), long[]> _buckets = new();

    public VolumeCurveEstimator() : this(DefaultBucketSize) { }

    public VolumeCurveEstimator(TimeSpan bucketSize)
    {
        if (bucketSize <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(bucketSize));
        _bucketSize = bucketSize;
    }

    public TimeSpan BucketSize => _bucketSize;

    /// <summary>
    /// Bucket count per UTC day. 288 at the 5-min default.
    /// </summary>
    public int BucketsPerDay => (int)(TimeSpan.FromDays(1).Ticks / _bucketSize.Ticks);

    /// <summary>
    /// Accrues <paramref name="qty"/> shares to the bucket containing
    /// <paramref name="atUtc"/> for <paramref name="symbol"/>. Non-positive
    /// qty is silently ignored — the caller doesn't need a guard.
    /// </summary>
    public void RecordTrade(string symbol, long qty, DateTimeOffset atUtc)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        if (qty <= 0) return;

        var day = DateOnly.FromDateTime(atUtc.UtcDateTime);
        var bucket = BucketIndex(atUtc);
        var arr = _buckets.GetOrAdd((symbol, day), _ => new long[BucketsPerDay]);
        // Coarse atomic add; concurrent trades for the same bucket are
        // rare in our load profile but we want no torn writes.
        Interlocked.Add(ref arr[bucket], qty);

        TrimRetention(symbol);
    }

    /// <summary>
    /// CDF value in <c>[0, 1]</c> for the fraction of expected volume
    /// that has accumulated between <paramref name="startUtc"/> and
    /// <paramref name="atUtc"/>, normalised by the total volume in
    /// <c>[startUtc, endUtc]</c>. Uniform fallback when the symbol has
    /// no observed volume in <c>[startUtc, endUtc]</c>.
    /// </summary>
    public double CdfAt(string symbol, DateTimeOffset startUtc, DateTimeOffset endUtc, DateTimeOffset atUtc)
    {
        if (endUtc <= startUtc) return 0;
        if (atUtc <= startUtc) return 0;
        if (atUtc >= endUtc) return 1;

        var totalInWindow = VolumeBetween(symbol, startUtc, endUtc);
        if (totalInWindow <= 0)
        {
            // Uniform fallback. Mirrors a TWAP-shaped curve so the
            // engine still makes forward progress when the estimator
            // hasn't seen anything yet.
            return (atUtc - startUtc).TotalSeconds / (endUtc - startUtc).TotalSeconds;
        }

        var elapsed = VolumeBetween(symbol, startUtc, atUtc);
        var cdf = (double)elapsed / totalInWindow;
        if (cdf < 0) return 0;
        if (cdf > 1) return 1;
        return cdf;
    }

    /// <summary>
    /// Sum of recorded volume in <c>[fromUtc, toUtc)</c> for
    /// <paramref name="symbol"/>. Spans day boundaries by walking the
    /// per-day bucket arrays. Public for tests + for the participation
    /// cap (engine asks "how much has traded recently?").
    /// </summary>
    public long VolumeBetween(string symbol, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (toUtc <= fromUtc) return 0;
        long total = 0;
        var bucketsPerDay = BucketsPerDay;
        var day = DateOnly.FromDateTime(fromUtc.UtcDateTime);
        var endDay = DateOnly.FromDateTime(toUtc.UtcDateTime);
        while (day <= endDay)
        {
            if (_buckets.TryGetValue((symbol, day), out var arr))
            {
                var dayStart = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                var rangeStart = fromUtc > dayStart ? fromUtc : dayStart;
                var rangeEnd = toUtc < dayStart.AddDays(1) ? toUtc : dayStart.AddDays(1);
                if (rangeEnd > rangeStart)
                {
                    var firstBucket = BucketIndex(rangeStart);
                    // Inclusive lower bound, exclusive upper bound.
                    var lastBucket = BucketIndex(rangeEnd.AddTicks(-1));
                    for (var b = firstBucket; b <= lastBucket && b < bucketsPerDay; b++)
                        total += Interlocked.Read(ref arr[b]);
                }
            }
            day = day.AddDays(1);
        }
        return total;
    }

    /// <summary>
    /// Drops day-keys older than <see cref="MaxRetentionDays"/> for the
    /// given symbol. Cheap (one Keys snapshot per call); could be moved
    /// to a periodic sweep if profiling ever flags it.
    /// </summary>
    private void TrimRetention(string symbol)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-MaxRetentionDays);
        foreach (var key in _buckets.Keys)
        {
            if (key.Symbol == symbol && key.Day < cutoff)
                _buckets.TryRemove(key, out _);
        }
    }

    private int BucketIndex(DateTimeOffset utc)
    {
        var midnight = new DateTimeOffset(
            DateOnly.FromDateTime(utc.UtcDateTime).ToDateTime(TimeOnly.MinValue),
            TimeSpan.Zero);
        var offset = (utc - midnight).Ticks;
        var idx = (int)(offset / _bucketSize.Ticks);
        // Defensive clamp; midnight wraparound or non-power-of-2 bucket
        // sizes could in theory push idx to BucketsPerDay.
        return Math.Clamp(idx, 0, BucketsPerDay - 1);
    }
}
