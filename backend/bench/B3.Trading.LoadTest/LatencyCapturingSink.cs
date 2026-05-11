using System.Diagnostics;

using B3.Trading.Application;

namespace B3.Trading.LoadTest;

/// <summary>
/// In-memory sample store: one slot per ClOrdId allocated up-front so
/// the harness avoids per-message allocations on the hot path. Producer
/// writes <c>T0</c> (submit-call-start tick); the
/// <see cref="LatencyCapturingSink"/> writes <c>T1</c> (publish-observed
/// tick). Whichever side observes both fields populated finalises the
/// sample — recording <c>(T1 − T0)</c> into a flat <c>long[]</c> result
/// buffer that is sorted at end-of-run for percentiles.
///
/// <para>
/// The sample slot is keyed by the ClOrdId counter portion (low 40
/// bits) — since the harness uses a single end-client, counters are
/// strictly monotonic from <c>1</c> and form a dense index into a
/// pre-sized array. Capacity is set by
/// <see cref="LoadTestRig.ComputeCapacity"/> with an over-provision
/// factor so the producer cannot run off the end at the configured
/// rate × duration.
/// </para>
/// </summary>
public sealed class LatencySampleStore
{
    private readonly Sample[] _samples;
    private readonly long[] _latencies;
    private readonly ulong _counterMask;
    // Slot reservation watermark and visible-publish watermark are
    // separated so consumers (CopyLatencies, the quiesce loop) only
    // ever see a count that bounds an array region whose writes are
    // already published (Interlocked.Increment after the store acts as
    // a release barrier on .NET).
    private long _reservedCount;
    private long _publishedCount;

    public LatencySampleStore(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _samples = new Sample[capacity];
        _latencies = new long[capacity];
        _counterMask = (1UL << 40) - 1;
    }

    public int Capacity => _samples.Length;

    public long FinalisedCount => Interlocked.Read(ref _publishedCount);

    public long[] CopyLatencies()
    {
        // Snapshot the published watermark BEFORE reading the array so
        // we don't accidentally include slots whose underlying tick
        // value has not yet been published by a racing finaliser.
        var n = (int)Math.Min(Interlocked.Read(ref _publishedCount), _latencies.LongLength);
        var copy = new long[n];
        Array.Copy(_latencies, copy, n);
        return copy;
    }

    public void RecordSubmit(ulong clOrdId, long t0)
    {
        var idx = IndexFor(clOrdId);
        if (idx < 0) return;
        Interlocked.Exchange(ref _samples[idx].T0, t0);
        TryFinalise(idx);
    }

    public void RecordPublish(ulong clOrdId, long t1)
    {
        var idx = IndexFor(clOrdId);
        if (idx < 0) return;
        Interlocked.Exchange(ref _samples[idx].T1, t1);
        TryFinalise(idx);
    }

    private int IndexFor(ulong clOrdId)
    {
        // Counter occupies the low CounterBits (40) bits; the prefix
        // sits above. Single-end-client harness ⇒ index is just the
        // counter minus 1 (counters start at 1).
        var counter = clOrdId & _counterMask;
        if (counter == 0 || counter > (ulong)_samples.LongLength) return -1;
        return (int)(counter - 1);
    }

    private void TryFinalise(int idx)
    {
        ref var s = ref _samples[idx];
        var t0 = Interlocked.Read(ref s.T0);
        var t1 = Interlocked.Read(ref s.T1);
        if (t0 == 0 || t1 == 0) return;
        if (Interlocked.CompareExchange(ref s.State, 1, 0) != 0) return;

        // Tick deltas are converted to nanoseconds at report time so the
        // hot path does only an integer subtract + array store.
        var elapsed = t1 - t0;
        if (elapsed < 0) elapsed = 0;
        var slot = Interlocked.Increment(ref _reservedCount) - 1;
        if (slot < _latencies.LongLength)
        {
            // Write the value FIRST; the subsequent
            // Interlocked.Increment publishes the slot to readers (it
            // acts as a release fence and bumps the visible watermark).
            // CopyLatencies and the quiesce loop both gate on
            // _publishedCount, so they cannot observe a slot whose
            // value is still the default 0 — eliminating the artificial
            // zero-latency samples that would otherwise pull p50 down.
            Volatile.Write(ref _latencies[slot], elapsed);
            Interlocked.Increment(ref _publishedCount);
        }
        else
        {
            // Capacity exhausted (rate × duration exceeded the
            // pre-sized buffer). Drop the sample but DO NOT touch
            // _publishedCount so the watermark remains a true bound on
            // the published prefix of the buffer.
        }
    }

    private struct Sample
    {
        public long T0;
        public long T1;
        public int State;
    }
}

/// <summary>
/// Minimal <see cref="IExecutionEventSink"/> that records the publish
/// timestamp for every observed event and forwards it to N "bot" sink
/// counters so the per-bot fan-out cost is at least represented in the
/// timing path. The first bot to observe an event is the one whose tick
/// is recorded as the end-to-end latency reference.
///
/// <para>
/// The <c>--bots N</c> CLI flag drives a tight per-Publish counter loop
/// of length <c>N</c>; this stands in for the per-session work that
/// <c>BotErMultiplexer.Route</c> would do in production. The harness
/// does <b>not</b> spin up real FIXP sessions because the inbound
/// SOFH/SBE handshake is out of scope for the §7.2 latency
/// measurement; sub-issues that need to characterise outbound socket
/// latency specifically should extend the bench harness (PR #213) with
/// their own fixture.
/// </para>
/// </summary>
public sealed class LatencyCapturingSink : IExecutionEventSink
{
    private readonly LatencySampleStore _store;
    private readonly int _botCount;
    private readonly long[] _botPublishCounts;
    public long PublishCount;

    public LatencyCapturingSink(LatencySampleStore store, int botCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(botCount, 1);
        _store = store;
        _botCount = botCount;
        _botPublishCounts = new long[botCount];
    }

    public IReadOnlyList<long> BotPublishCounts => _botPublishCounts;

    public void Publish(ExecutionEvent ev)
    {
        // Tick captured first so the per-event work below does not bias
        // the e2e number with sink-internal accounting.
        var t1 = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref PublishCount);
        _store.RecordPublish(ev.ClOrdId, t1);

        // Per-bot fan-out — Interlocked.Increment per slot to mirror the
        // shape of BotErMultiplexer.Route's per-session work (one
        // bookkeeping increment + one queue-enqueue analogue). The
        // increment is unconditional to keep the loop branch-free under
        // load; --bots 1 reduces this to a single increment.
        for (var i = 0; i < _botCount; i++)
            Interlocked.Increment(ref _botPublishCounts[i]);
    }
}
