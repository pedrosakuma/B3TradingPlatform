using System.Collections.Concurrent;
using B3.Trading.Application.Observability;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Tracks pending order-entry operations (submit/cancel/replace) by their
/// outbound <c>ClOrdID</c> and records submit-to-first-ER latency to the
/// <c>trading.entrypoint.order_entry_to_ack_ms</c> histogram on the matching
/// inbound <see cref="ExecutionReportEnvelope"/>.
///
/// <para>
/// Designed to be wired into <see cref="B3EntryPointClientGateway"/>:
/// <see cref="OnSubmitted"/> is called <em>before</em> the SDK await (the
/// ER can race ahead of the await on a fast wire) and
/// <see cref="OnExecutionReport"/> is called as soon as a translated envelope
/// is available, before fan-out to subscribers — so a misbehaving subscriber
/// can't lose latency samples.
/// </para>
///
/// <para>
/// Pending entries are bounded by both <see cref="MaxPending"/> (cap-based
/// eviction of oldest) and <see cref="Ttl"/> (timestamp-based sweep). The
/// sweep runs at most every <see cref="SweepInterval"/> off the
/// <c>OnSubmitted</c> path, so an order rate of millions/sec doesn't trigger
/// O(N) work on every call.
/// </para>
///
/// <para>
/// Thread-safe. <see cref="OnExecutionReport"/> is idempotent — only the
/// first ER for a given <c>ClOrdID</c> records a sample.
/// </para>
/// </summary>
public sealed class OrderEntryLatencyProbe
{
    public const string OpSubmit = "submit";
    public const string OpCancel = "cancel";
    public const string OpReplace = "replace";

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<ulong, Pending> _pending = new();
    private long _nextSweepTicks;
    private int _approxCount;

    public int MaxPending { get; }
    public TimeSpan Ttl { get; }
    public TimeSpan SweepInterval { get; }

    public OrderEntryLatencyProbe(
        TimeProvider? clock = null,
        int maxPending = 50_000,
        TimeSpan? ttl = null,
        TimeSpan? sweepInterval = null)
    {
        _clock = clock ?? TimeProvider.System;
        MaxPending = maxPending;
        Ttl = ttl ?? TimeSpan.FromMinutes(5);
        SweepInterval = sweepInterval ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Approximate number of entries awaiting their first ER. Maintained via
    /// <see cref="Interlocked"/> to avoid the cost of
    /// <c>ConcurrentDictionary.Count</c> on the hot path.
    /// </summary>
    public int ApproxPending => Volatile.Read(ref _approxCount);

    /// <summary>
    /// Register a freshly-issued order-entry operation. Must be called BEFORE
    /// awaiting the SDK send to avoid losing samples to ER-before-await
    /// races. Idempotent — re-issuing the same ClOrdID overwrites the start
    /// timestamp (only the latest send is timed).
    /// </summary>
    public void OnSubmitted(ulong clOrdId, string firm, string op)
    {
        var now = _clock.GetTimestamp();
        var fresh = new Pending(now, firm, op);
        _pending.AddOrUpdate(
            clOrdId,
            _ => { Interlocked.Increment(ref _approxCount); return fresh; },
            (_, _) => fresh);

        MaybeSweep(now);
        if (Volatile.Read(ref _approxCount) > MaxPending)
            EvictOldest();
    }

    /// <summary>
    /// Drop a pending entry without recording a sample. Use for synchronous
    /// SDK failures where no ER will follow.
    /// </summary>
    public void Forget(ulong clOrdId)
    {
        if (_pending.TryRemove(clOrdId, out _))
            Interlocked.Decrement(ref _approxCount);
    }

    /// <summary>
    /// Match a translated ER's <c>ClOrdID</c> against any pending entry and,
    /// if found, record the elapsed time and remove the entry. Subsequent
    /// ERs for the same <c>ClOrdID</c> are ignored — only the first one is
    /// the ack the operator cares about.
    /// </summary>
    public void OnExecutionReport(ulong clOrdId)
    {
        if (!_pending.TryRemove(clOrdId, out var pending))
            return;

        Interlocked.Decrement(ref _approxCount);
        var elapsedMs = _clock.GetElapsedTime(pending.StartTicks).TotalMilliseconds;
        MetricsRegistry.OrderEntryToAckMs.Record(elapsedMs,
            new KeyValuePair<string, object?>("firm", pending.Firm),
            new KeyValuePair<string, object?>("op", pending.Op));
    }

    private void MaybeSweep(long now)
    {
        var next = Volatile.Read(ref _nextSweepTicks);
        if (now < next) return;
        var nextNext = now + (long)(SweepInterval.TotalSeconds * _clock.TimestampFrequency);
        // Only one sweeper at a time; if another caller already advanced the
        // gate, skip — the entries will be picked up on the following tick.
        if (Interlocked.CompareExchange(ref _nextSweepTicks, nextNext, next) != next)
            return;

        var ttlTicks = (long)(Ttl.TotalSeconds * _clock.TimestampFrequency);
        foreach (var kv in _pending)
        {
            if (now - kv.Value.StartTicks > ttlTicks &&
                _pending.TryRemove(kv.Key, out _))
            {
                Interlocked.Decrement(ref _approxCount);
            }
        }
    }

    private void EvictOldest()
    {
        // Cap-based fallback for environments where TTL hasn't kicked in
        // yet but pending entries are growing unbounded (silent broker, test
        // harnesses). Keeps the dictionary bounded; precision of "oldest"
        // doesn't matter much here.
        var target = MaxPending - (MaxPending / 10); // free up ~10%
        var snapshot = _pending.ToArray();
        Array.Sort(snapshot, static (a, b) => a.Value.StartTicks.CompareTo(b.Value.StartTicks));
        var toRemove = snapshot.Length - target;
        for (var i = 0; i < toRemove && i < snapshot.Length; i++)
        {
            if (_pending.TryRemove(snapshot[i].Key, out _))
                Interlocked.Decrement(ref _approxCount);
        }
    }

    private readonly record struct Pending(long StartTicks, string Firm, string Op);
}
