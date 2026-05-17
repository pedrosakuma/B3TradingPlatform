using System.Diagnostics.Metrics;
using B3.Trading.Application;
using B3.Trading.Application.Observability;
using Microsoft.Extensions.Logging;
using Xunit;

namespace B3.Trading.Application.Tests.AlgoEngine;

public class PeggedRepegBookTests
{
    /// <summary>
    /// Pass-6 review (#296) P2. When the per-parent cancelled-child
    /// FIFO ring overflows past <see cref="PeggedRepegBook.CancelledHistoryCap"/>,
    /// each eviction MUST bump
    /// <see cref="MetricsRegistry.AlgoPeggedRepegDedupRingEvicted"/>
    /// so operators see when the dedup window stops covering venue
    /// tail-Fill latency. Adds at or below the cap MUST NOT bump.
    /// </summary>
    [Fact]
    public void MarkCancelledChild_RingOverflow_IncrementsEvictionCounter()
    {
        long captured = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "trading.algo.pegged.repeg_dedup_ring_evicted_total")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
        {
            Interlocked.Add(ref captured, measurement);
        });
        listener.Start();

        var book = new PeggedRepegBook();
        const string firm = "TEST";
        const ulong algoId = 42UL;
        var cap = PeggedRepegBook.CancelledHistoryCap;

        // Fill the ring exactly to the cap — no evictions yet.
        for (ulong i = 1; i <= (ulong)cap; i++)
        {
            book.MarkCancelledChild(firm, algoId, i);
        }
        Assert.Equal(0, Interlocked.Read(ref captured));

        // Every fresh id past the cap must evict the oldest and bump.
        book.MarkCancelledChild(firm, algoId, (ulong)cap + 1);
        Assert.Equal(1, Interlocked.Read(ref captured));

        book.MarkCancelledChild(firm, algoId, (ulong)cap + 2);
        book.MarkCancelledChild(firm, algoId, (ulong)cap + 3);
        Assert.Equal(3, Interlocked.Read(ref captured));

        // Duplicates are no-ops — must not bump.
        book.MarkCancelledChild(firm, algoId, (ulong)cap + 3);
        Assert.Equal(3, Interlocked.Read(ref captured));

        // The oldest id (1) has fallen out of the ring; the newest is
        // still recognised as cancelled.
        Assert.False(book.IsCancelledChild(firm, algoId, 1UL),
            "Oldest id should have been evicted past the cap.");
        Assert.True(book.IsCancelledChild(firm, algoId, (ulong)cap + 3),
            "Most recent id must still be present in the dedup ring.");
    }

    /// <summary>
    /// Pass-7 review (#296) P2. The per-ring "eviction warn already
    /// emitted" latch is in-memory; without persisting it across
    /// <see cref="PeggedRepegBook.SnapshotHistory"/> /
    /// <see cref="PeggedRepegBook.RestoreHistory"/>, a parent that
    /// already warned pre-restart would warn AGAIN on the next
    /// eviction post-restart. Pin the round-trip: snapshot a ring
    /// whose latch is set → restore → trigger another eviction → no
    /// new warn must be logged (eviction counter still bumps).
    /// </summary>
    [Fact]
    public void RestoreHistory_PreservesEvictionLoggedLatch_NoWarnAfterRestart()
    {
        const string firm = "TEST";
        const ulong algoId = 7UL;
        var cap = PeggedRepegBook.CancelledHistoryCap;

        var preLogger = new CountingLogger<PeggedRepegBook>();
        var pre = new PeggedRepegBook(preLogger);

        // Fill to cap + force one eviction so the latch flips to true
        // and one warn is logged on the pre-restart book.
        for (ulong i = 1; i <= (ulong)cap; i++) pre.MarkCancelledChild(firm, algoId, i);
        pre.MarkCancelledChild(firm, algoId, (ulong)cap + 1);
        Assert.Equal(1, preLogger.WarningCount);

        // Subsequent evictions on the same in-memory ring must stay
        // silent (sanity check on the latch within a single process).
        pre.MarkCancelledChild(firm, algoId, (ulong)cap + 2);
        Assert.Equal(1, preLogger.WarningCount);

        // Snapshot now carries (ids, EvictionLogged=true). Round-trip
        // through the SnapshotHistory tuple shape used by
        // StateSnapshotter.
        var snapshot = pre.SnapshotHistory().ToList();
        Assert.Single(snapshot);
        Assert.True(snapshot[0].EvictionLogged,
            "SnapshotHistory must emit the latched eviction-warn flag.");

        var postLogger = new CountingLogger<PeggedRepegBook>();
        var post = new PeggedRepegBook(postLogger);
        post.RestoreHistory(snapshot.Select(t =>
            (t.FirmId, t.AlgoId, t.ChildClOrdIds, t.EvictionLogged)));

        // Force another eviction on the rehydrated ring. The dedup
        // counter MUST still bump (functional behaviour unchanged) but
        // the warn MUST stay suppressed because the latch was restored.
        long evictedAfterRestore = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "trading.algo.pegged.repeg_dedup_ring_evicted_total")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, m, _, _) =>
            Interlocked.Add(ref evictedAfterRestore, m));
        listener.Start();

        post.MarkCancelledChild(firm, algoId, (ulong)cap + 3);

        Assert.Equal(1, Interlocked.Read(ref evictedAfterRestore));
        Assert.Equal(0, postLogger.WarningCount);
    }

    /// <summary>
    /// Forward-compat: snapshots pre-dating the
    /// <c>EvictionLogged</c> field default to <c>false</c> on restore,
    /// so the very next eviction WILL warn once (acceptable — at worst
    /// one extra log post-upgrade, no functional change).
    /// </summary>
    [Fact]
    public void RestoreHistory_LatchDefaultsFalse_WhenFlagAbsent()
    {
        const string firm = "TEST";
        const ulong algoId = 9UL;
        var cap = PeggedRepegBook.CancelledHistoryCap;

        var logger = new CountingLogger<PeggedRepegBook>();
        var book = new PeggedRepegBook(logger);

        // Pre-field snapshot shape: caller passes EvictionLogged=false
        // (or omits, taking the optional default).
        var ids = new List<ulong>();
        for (ulong i = 1; i <= (ulong)cap; i++) ids.Add(i);
        book.RestoreHistory(new[]
        {
            (firm, algoId, (IReadOnlyList<ulong>)ids, false),
        });

        book.MarkCancelledChild(firm, algoId, (ulong)cap + 1);
        Assert.Equal(1, logger.WarningCount);
    }

    private sealed class CountingLogger<T> : ILogger<T>
    {
        private int _warnings;

        public int WarningCount => Volatile.Read(ref _warnings);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Interlocked.Increment(ref _warnings);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
