using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests.Audit;

/// <summary>
/// #438. Audit drops on the best-effort <c>Log</c> path and the
/// fail-closed <c>LogOrFail</c> path must increment the
/// <c>trading.audit.dropped_total</c> counter with the right
/// <c>reason</c>, so operator dashboards can alert on audit loss
/// independently of the broader WAL backpressure metric.
/// </summary>
public class AuditLoggerDropMetricTests
{
    [Fact]
    public void Log_WalBackpressure_IsSwallowed_AndBumpsDroppedCounter()
        => AssertDropOnLog(
            store: new WalBackpressureStore(),
            expectedReason: "wal_backpressure",
            expectedEventType: "auth.login.failure",
            expectedCallSite: "auth");

    [Fact]
    public void Log_KeeperException_IsSwallowed_AndBumpsDroppedCounter()
        => AssertDropOnLog(
            store: new ThrowingStore(new InvalidOperationException("keeper boom")),
            expectedReason: "exception",
            expectedEventType: "totp.verify.failure",
            expectedCallSite: "totp");

    [Fact]
    public void LogOrFail_WalBackpressure_Rethrows_AndDoesNotBumpDroppedCounter()
    {
        // Contract: on the fail-closed path, WAL backpressure must
        // surface to the caller (admin endpoint → HTTP 503). The
        // dedicated audit-drop counter is NOT bumped: the broader
        // wal.backpressure{call_site=audit.log_or_fail} counter and
        // the propagated exception already make the loss observable
        // to operators and to the caller, so an additional
        // audit-drop tick would double-count from a dashboard
        // perspective. RecordEmitMetric IS called (offered load).
        var store = new WalBackpressureStore();
        var dispatcher = new EventDispatcher(store);
        var keeper = new AuditLogKeeper(Options.Create(new AuditLogOptions { Capacity = 16 }));
        var logger = new AuditLogger(dispatcher, keeper, NullLogger<AuditLogger>.Instance);

        var capture = new MetricCapture("trading.audit.dropped_total", "trading.wal.backpressure");
        try
        {
            var evt = new AuditLogEvent
            {
                EventType = "admin.config.change",
                Outcome = "success",
            };
            Assert.Throws<WalBackpressureException>(() => logger.LogOrFail(evt));

            Assert.False(capture.HasCounter("trading.audit.dropped_total"),
                "LogOrFail must not bump trading.audit.dropped_total when WAL backpressure is rethrown.");
            Assert.True(capture.HasCounter("trading.wal.backpressure"),
                "LogOrFail must bump trading.wal.backpressure with the audit call_site tag.");
        }
        finally { capture.Dispose(); }
    }

    [Fact]
    public void LogOrFail_KeeperException_IsSwallowed_AndBumpsDroppedCounter()
    {
        var store = new ThrowingStore(new InvalidOperationException("keeper boom"));
        var dispatcher = new EventDispatcher(store);
        var keeper = new AuditLogKeeper(Options.Create(new AuditLogOptions { Capacity = 16 }));
        var logger = new AuditLogger(dispatcher, keeper, NullLogger<AuditLogger>.Instance);

        var capture = new MetricCapture("trading.audit.dropped_total");
        try
        {
            var evt = new AuditLogEvent
            {
                EventType = "admin.config.change",
                Outcome = "success",
            };
            logger.LogOrFail(evt);

            var inc = capture.GetSum("trading.audit.dropped_total",
                ("call_site", "admin"),
                ("event_type", "admin.config.change"),
                ("reason", "exception"));
            Assert.Equal(1, inc);
        }
        finally { capture.Dispose(); }
    }

    private static void AssertDropOnLog(IEventStore store, string expectedReason, string expectedEventType, string expectedCallSite)
    {
        var dispatcher = new EventDispatcher(store);
        var keeper = new AuditLogKeeper(Options.Create(new AuditLogOptions { Capacity = 16 }));
        var logger = new AuditLogger(dispatcher, keeper, NullLogger<AuditLogger>.Instance);

        var capture = new MetricCapture("trading.audit.dropped_total");
        try
        {
            var evt = new AuditLogEvent
            {
                EventType = expectedEventType,
                Outcome = "failure",
            };

            // Best-effort: must not throw.
            logger.Log(evt);

            var inc = capture.GetSum("trading.audit.dropped_total",
                ("call_site", expectedCallSite),
                ("event_type", expectedEventType),
                ("reason", expectedReason));
            Assert.Equal(1, inc);
        }
        finally { capture.Dispose(); }
    }

    private sealed class WalBackpressureStore : IEventStore
    {
        public long CurrentSeq => 0;
        public long Append(WalEvent evt) => throw new WalBackpressureException("synthetic backpressure");
        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload) =>
            throw new WalBackpressureException("synthetic backpressure");
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await ValueTask.CompletedTask; yield break; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingStore : IEventStore
    {
        private readonly Exception _ex;
        public ThrowingStore(Exception ex) { _ex = ex; }
        public long CurrentSeq => 0;
        public long Append(WalEvent evt) => throw _ex;
        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload) => throw _ex;
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await ValueTask.CompletedTask; yield break; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Per-test MeterListener that captures named counter increments
    /// from the global "B3.Trading" meter. Filters by tag key/value
    /// since the same meter is process-global and other parallel
    /// tests may fire the same instruments — see the testing memory.
    /// </summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly HashSet<string> _names;
        private readonly ConcurrentBag<(string Name, long Value, Dictionary<string, string?> Tags)> _samples = new();
        private readonly MeterListener _listener;

        public MetricCapture(params string[] instrumentNames)
        {
            _names = new HashSet<string>(instrumentNames, StringComparer.Ordinal);
            _listener = new MeterListener
            {
                InstrumentPublished = (inst, l) =>
                {
                    if (inst.Meter.Name == "B3.Trading" && _names.Contains(inst.Name))
                        l.EnableMeasurementEvents(inst);
                },
            };
            _listener.SetMeasurementEventCallback<long>(OnMeasurement);
            _listener.Start();
        }

        private void OnMeasurement(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            var dict = new Dictionary<string, string?>(tags.Length, StringComparer.Ordinal);
            foreach (var kv in tags)
                dict[kv.Key] = kv.Value?.ToString();
            _samples.Add((instrument.Name, value, dict));
        }

        public bool HasCounter(string name) => _samples.Any(s => s.Name == name);

        public long GetSum(string name, params (string Key, string Value)[] mustMatchTags)
        {
            long sum = 0;
            foreach (var s in _samples)
            {
                if (s.Name != name) continue;
                var ok = true;
                foreach (var (k, v) in mustMatchTags)
                {
                    if (!s.Tags.TryGetValue(k, out var actual) || actual != v) { ok = false; break; }
                }
                if (ok) sum += s.Value;
            }
            return sum;
        }

        public void Dispose() => _listener.Dispose();
    }
}
