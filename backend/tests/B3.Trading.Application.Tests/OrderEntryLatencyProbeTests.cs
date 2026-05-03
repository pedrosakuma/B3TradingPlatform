using System.Diagnostics.Metrics;
using B3.Trading.Application.Observability;
using B3.Trading.Infrastructure;

namespace B3.Trading.Application.Tests;

public class OrderEntryLatencyProbeTests
{
    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public long FrequencyOverride { get; init; } = TimeSpan.TicksPerSecond;
        public override long TimestampFrequency => FrequencyOverride;
        public override long GetTimestamp() => Volatile.Read(ref _ticks);
        public void Advance(TimeSpan dt) =>
            Interlocked.Add(ref _ticks, (long)(dt.TotalSeconds * FrequencyOverride));
    }

    private static (List<Measurement<double>> samples, IDisposable subscription) CaptureToAck()
    {
        var samples = new List<Measurement<double>>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instr, l) =>
            {
                if (instr.Meter.Name == "B3.Trading" &&
                    instr.Name == "trading.entrypoint.order_entry_to_ack_ms")
                    l.EnableMeasurementEvents(instr);
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            lock (samples) samples.Add(new Measurement<double>(value, tags.ToArray()));
        });
        listener.Start();
        return (samples, listener);
    }

    [Fact]
    public void OnExecutionReport_RecordsElapsedMs_WithFirmAndOpTags()
    {
        var (samples, sub) = CaptureToAck();
        using var _ = sub;
        var clock = new TestClock();
        var probe = new OrderEntryLatencyProbe(clock);

        probe.OnSubmitted(42, "FIRM_A", OrderEntryLatencyProbe.OpSubmit);
        clock.Advance(TimeSpan.FromMilliseconds(7.5));
        probe.OnExecutionReport(42);

        var sample = Assert.Single(samples);
        Assert.Equal(7.5, sample.Value, precision: 3);
        var tags = sample.Tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("FIRM_A", tags["firm"]);
        Assert.Equal("submit", tags["op"]);
    }

    [Fact]
    public void OnExecutionReport_IsIdempotent_OnlyFirstErRecords()
    {
        var (samples, sub) = CaptureToAck();
        using var _ = sub;
        var clock = new TestClock();
        var probe = new OrderEntryLatencyProbe(clock);

        probe.OnSubmitted(7, "F", OrderEntryLatencyProbe.OpSubmit);
        clock.Advance(TimeSpan.FromMilliseconds(2));
        probe.OnExecutionReport(7);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        probe.OnExecutionReport(7); // second ER for same ClOrdID — no-op

        Assert.Single(samples);
    }

    [Fact]
    public void OnExecutionReport_UnknownClOrdId_NoRecord()
    {
        var (samples, sub) = CaptureToAck();
        using var _ = sub;
        var probe = new OrderEntryLatencyProbe(new TestClock());

        probe.OnExecutionReport(999);

        Assert.Empty(samples);
        Assert.Equal(0, probe.ApproxPending);
    }

    [Fact]
    public void Forget_DropsPendingWithoutRecording()
    {
        var (samples, sub) = CaptureToAck();
        using var _ = sub;
        var probe = new OrderEntryLatencyProbe(new TestClock());

        probe.OnSubmitted(5, "F", OrderEntryLatencyProbe.OpSubmit);
        Assert.Equal(1, probe.ApproxPending);
        probe.Forget(5);
        Assert.Equal(0, probe.ApproxPending);

        // Subsequent ER should not fire — probe was forgotten.
        probe.OnExecutionReport(5);
        Assert.Empty(samples);
    }

    [Fact]
    public void OnSubmitted_OverwritesExistingPending()
    {
        var (samples, sub) = CaptureToAck();
        using var _ = sub;
        var clock = new TestClock();
        var probe = new OrderEntryLatencyProbe(clock);

        probe.OnSubmitted(1, "F", OrderEntryLatencyProbe.OpSubmit);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        probe.OnSubmitted(1, "F", OrderEntryLatencyProbe.OpSubmit); // re-issue resets timer
        clock.Advance(TimeSpan.FromMilliseconds(3));
        probe.OnExecutionReport(1);

        Assert.Equal(1, probe.ApproxPending + samples.Count); // 0 pending + 1 sample
        Assert.Equal(3, samples[0].Value, precision: 1);
    }

    [Fact]
    public void Sweep_EvictsEntriesOlderThanTtl()
    {
        var clock = new TestClock();
        var probe = new OrderEntryLatencyProbe(
            clock,
            ttl: TimeSpan.FromSeconds(2),
            sweepInterval: TimeSpan.FromMilliseconds(1));

        probe.OnSubmitted(1, "F", OrderEntryLatencyProbe.OpSubmit);
        clock.Advance(TimeSpan.FromSeconds(3));

        // A subsequent OnSubmitted triggers MaybeSweep which evicts ClOrdID=1.
        probe.OnSubmitted(2, "F", OrderEntryLatencyProbe.OpSubmit);

        // ClOrdID=1 was swept; only ClOrdID=2 remains.
        Assert.Equal(1, probe.ApproxPending);
    }

    [Fact]
    public void EvictOldest_BoundsDictionaryWhenCapExceeded()
    {
        var clock = new TestClock();
        var probe = new OrderEntryLatencyProbe(
            clock,
            maxPending: 10,
            ttl: TimeSpan.FromHours(1),       // disable TTL eviction
            sweepInterval: TimeSpan.FromHours(1));

        for (ulong i = 1; i <= 20; i++)
        {
            probe.OnSubmitted(i, "F", OrderEntryLatencyProbe.OpSubmit);
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        // After overflow, eviction frees ~10% headroom; pending is at-or-below cap.
        Assert.True(probe.ApproxPending <= 10,
            $"Expected pending <= 10 after eviction, got {probe.ApproxPending}");
        // Newest entry (ClOrdID=20) should still be present; record an ER and
        // expect it to be measured.
        var (samples, sub) = CaptureToAck();
        using var _ = sub;
        probe.OnExecutionReport(20);
        Assert.Single(samples);
    }

    [Fact]
    public async Task ConcurrentSubmitAndAck_ProducesOneSamplePerClOrdId()
    {
        var (samples, sub) = CaptureToAck();
        using var _ = sub;
        var probe = new OrderEntryLatencyProbe(); // real clock OK — small N

        const int n = 500;
        var tasks = new List<Task>();
        for (var i = 0; i < n; i++)
        {
            var id = (ulong)i + 1;
            tasks.Add(Task.Run(() =>
            {
                probe.OnSubmitted(id, "F", OrderEntryLatencyProbe.OpSubmit);
                probe.OnExecutionReport(id);
                probe.OnExecutionReport(id); // duplicate — must not double-record
            }));
        }
        await Task.WhenAll(tasks);

        Assert.Equal(n, samples.Count);
        Assert.Equal(0, probe.ApproxPending);
    }
}
