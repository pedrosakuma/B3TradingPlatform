using System.Diagnostics.Metrics;
using B3.Trading.Application;
using B3.Trading.Application.Observability;
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
}
