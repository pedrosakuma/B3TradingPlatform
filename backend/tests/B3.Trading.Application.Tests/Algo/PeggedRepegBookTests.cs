using System.Diagnostics.Metrics;
using B3.Trading.Application;
using B3.Trading.Application.Observability;
using Microsoft.Extensions.Logging;
using Xunit;

namespace B3.Trading.Application.Tests.AlgoEngine;

public class PeggedRepegBookTests
{
    [Fact]
    public void UnmarkCancelledChild_RemovesMarkerWithoutCorruptingFifo()
    {
        var book = new PeggedRepegBook();
        const string firm = "TEST";
        const ulong algoId = 1UL;

        book.MarkCancelledChild(firm, algoId, 10UL);
        book.MarkCancelledChild(firm, algoId, 11UL);

        Assert.True(book.UnmarkCancelledChild(firm, algoId, 10UL));
        Assert.False(book.IsCancelledChild(firm, algoId, 10UL));
        Assert.True(book.IsCancelledChild(firm, algoId, 11UL));
        Assert.False(book.UnmarkCancelledChild(firm, algoId, 10UL));

        book.MarkCancelledChild(firm, algoId, 10UL);
        Assert.True(book.IsCancelledChild(firm, algoId, 10UL));
        Assert.Equal([11UL, 10UL], book.SnapshotHistory().Single().ChildClOrdIds);
    }

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

    /// <summary>
    /// Pass-8 review (#296) P2. The eviction and the per-ring
    /// "one-shot warn already emitted" latch MUST flip as a single
    /// atomic step under the ring's lock. Otherwise a
    /// <see cref="PeggedRepegBook.SnapshotHistory"/> call interleaved
    /// between <c>ring.Add</c> and an external <c>MarkEvictionLogged</c>
    /// could capture <c>EvictionLogged=false</c> for a ring whose
    /// eviction had already happened — causing a duplicate warn
    /// after a restart that re-hydrated the snapshot.
    ///
    /// <para>
    /// Exercises the invariant directly on
    /// <see cref="CancelledChildRing"/>: fill to the cap, push one
    /// more entry to trigger the eviction, then immediately call
    /// <see cref="CancelledChildRing.SnapshotWithLatch"/> WITHOUT any
    /// external <see cref="CancelledChildRing.MarkEvictionLogged"/>
    /// call. The snapshot must already report
    /// <c>EvictionLogged=true</c> — proving the latch flip is
    /// encapsulated inside <c>Add</c> rather than left to a
    /// separately-scheduled caller step.
    /// </para>
    /// </summary>
    [Fact]
    public void Add_FlipsEvictionLatchAtomicallyWithEviction_NoExternalMarkRequired()
    {
        var cap = PeggedRepegBook.CancelledHistoryCap;
        var ring = new CancelledChildRing(cap);

        for (ulong i = 1; i <= (ulong)cap; i++)
        {
            Assert.False(ring.Add(i, out var firstEviction),
                "Adds at or below the cap must not evict.");
            Assert.False(firstEviction);
        }

        // Snapshot BEFORE the first eviction: latch must still be false.
        var preSnap = ring.SnapshotWithLatch();
        Assert.False(preSnap.EvictionLogged);
        Assert.Equal(cap, preSnap.Ids.Count);

        // Trigger eviction. The latch MUST flip inside this Add call.
        Assert.True(ring.Add((ulong)cap + 1, out var firstEvictionOnOverflow),
            "Adding past the cap must report eviction.");
        Assert.True(firstEvictionOnOverflow,
            "The first eviction on a ring MUST report firstEviction=true so callers can emit the one-shot warn.");

        // Critical assertion: snapshot WITHOUT any external
        // MarkEvictionLogged() call must already see latch=true.
        var postSnap = ring.SnapshotWithLatch();
        Assert.True(postSnap.EvictionLogged,
            "EvictionLogged MUST be observable as true immediately after the evicting Add returns, with no external latch flip required.");
        Assert.Equal(cap, postSnap.Ids.Count);

        // Subsequent evictions still report `true` for the evicted
        // bool but firstEviction MUST stay false (latch already set).
        Assert.True(ring.Add((ulong)cap + 2, out var firstEvictionOnSecond));
        Assert.False(firstEvictionOnSecond,
            "Only the first eviction may report firstEviction=true; subsequent ones suppress the warn.");
    }

    /// <summary>
    /// Pass-8 review (#296) P2. End-to-end variant on
    /// <see cref="PeggedRepegBook"/>: after the public
    /// <see cref="PeggedRepegBook.MarkCancelledChild"/> call that
    /// causes the first eviction returns, the very next
    /// <see cref="PeggedRepegBook.SnapshotHistory"/> MUST observe
    /// <c>EvictionLogged=true</c>. This pins the book-level wiring
    /// (the per-parent ring's latch is what
    /// <c>SnapshotHistory</c> reads) so a future refactor that
    /// re-introduces an external latch-flip step is caught.
    /// </summary>
    [Fact]
    public void MarkCancelledChild_SnapshotImmediatelyAfterEvictionSeesLatchedFlag()
    {
        const string firm = "TEST";
        const ulong algoId = 13UL;
        var cap = PeggedRepegBook.CancelledHistoryCap;
        var book = new PeggedRepegBook();

        for (ulong i = 1; i <= (ulong)cap; i++) book.MarkCancelledChild(firm, algoId, i);

        // Pre-eviction snapshot: latch must be false.
        Assert.False(book.SnapshotHistory().Single().EvictionLogged);

        // First eviction.
        book.MarkCancelledChild(firm, algoId, (ulong)cap + 1);

        // Without any intervening "mark logged" call, the snapshot
        // must already see the latch.
        Assert.True(book.SnapshotHistory().Single().EvictionLogged);
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
