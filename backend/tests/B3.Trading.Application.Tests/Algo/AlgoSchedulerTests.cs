using System.Collections.Generic;
using System.Linq;
using System.Threading;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace B3.Trading.Application.Tests.AlgoEngine;

public class AlgoSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private const string Firm = "TEST";

    /// <summary>Minimal mutable clock so tests can step time forward deterministically.</summary>
    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now;
        public MutableClock(DateTimeOffset now) => Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (AlgoBook algos, WorkingOrderBook orders, AlgoSignalQueue queue, MutableClock clock, AlgoScheduler scheduler) Build(DateTimeOffset now)
    {
        var algos = new AlgoBook();
        var orders = new WorkingOrderBook();
        var queue = new AlgoSignalQueue();
        var clock = new MutableClock(now);
        var scheduler = new AlgoScheduler(algos, orders, queue, clock,
            AlgoScheduler.DefaultTickInterval, NullLogger<AlgoScheduler>.Instance);
        return (algos, orders, queue, clock, scheduler);
    }

    private static Algo NewTwap(ulong id, int sliceCount = 4) =>
        new(id, new EndClientId("alice"), Firm, "PETR4", 4321UL, OrderSide.Buy, AlgoType.Twap,
            totalQuantity: 1000,
            new TwapParameters(Start, End, sliceCount, OrderType.Limit, 30m),
            createdAtUtc: Start);

    private static Order NewChild(ulong clOrdId, ulong parentAlgoId, int sliceSeq,
        long qty, OrderStatus status = OrderStatus.Working)
    {
        var o = new Order(clOrdId, new EndClientId("alice"), "PETR4", 4321UL,
            OrderSide.Buy, OrderType.Limit, qty, 30m, Firm, parentAlgoId, sliceSeq);
        o.MarkWorking();
        switch (status)
        {
            case OrderStatus.Filled:
                o.ApplyCumulativeFill(qty);
                break;
            case OrderStatus.Cancelled:
                o.MarkCancelled();
                break;
            case OrderStatus.Rejected:
                o.MarkRejected();
                break;
        }
        return o;
    }

    private static List<AlgoSignal> Drain(AlgoSignalQueue q)
    {
        q.Complete();
        var seen = new List<AlgoSignal>();
        while (q.Reader.TryRead(out var s)) seen.Add(s);
        return seen;
    }

    // ───────────────────────── Slice firing ─────────────────────────

    [Fact]
    public void Tick_BeforeStart_DoesNotEnqueue()
    {
        var (algos, _, queue, _, scheduler) = Build(now: Start.AddSeconds(-1));
        algos.TryAdd(NewTwap(1));
        scheduler.Tick();
        Assert.Empty(Drain(queue));
    }

    [Fact]
    public void Tick_AtStart_EnqueuesFirstSlice()
    {
        // Slice 0's plannedAtUtc == startUtc; at exactly startUtc the
        // scheduler must fire it.
        var (algos, _, queue, _, scheduler) = Build(now: Start);
        algos.TryAdd(NewTwap(1));
        scheduler.Tick();
        var signals = Drain(queue);
        var s = Assert.Single(signals);
        Assert.Equal(1UL, ((AlgoCreatedSignal)s).AlgoId);
    }

    [Fact]
    public void Tick_WithLiveChild_DoesNotEnqueue()
    {
        // Engine invariant: only one in-flight child per parent. Scheduler
        // must observe the live child and stay quiet until the engine
        // (via OnChildErAsync) clears it.
        var (algos, orders, queue, _, scheduler) = Build(now: Start.AddMinutes(20));
        algos.TryAdd(NewTwap(1));
        orders.TryAdd(NewChild(clOrdId: 100, parentAlgoId: 1, sliceSeq: 0, qty: 250));
        scheduler.Tick();
        Assert.Empty(Drain(queue));
    }

    [Fact]
    public void Tick_AfterFirstChildFilled_EnqueuesSecondSliceWhenDue()
    {
        // Slice 1 plannedAt = start + 15min. At t=20min slice 1 is overdue.
        var (algos, orders, queue, _, scheduler) = Build(now: Start.AddMinutes(20));
        algos.TryAdd(NewTwap(1));
        orders.TryAdd(NewChild(100, 1, sliceSeq: 0, qty: 250, status: OrderStatus.Filled));
        scheduler.Tick();
        Assert.Single(Drain(queue));
    }

    [Fact]
    public void Tick_NotYetDue_DoesNotEnqueue()
    {
        // Slice 1 plannedAt = start + 15min. At t=10min slice 1 is not
        // due yet, slice 0 is already filled — scheduler must wait.
        var (algos, orders, queue, _, scheduler) = Build(now: Start.AddMinutes(10));
        algos.TryAdd(NewTwap(1));
        orders.TryAdd(NewChild(100, 1, sliceSeq: 0, qty: 250, status: OrderStatus.Filled));
        scheduler.Tick();
        Assert.Empty(Drain(queue));
    }

    [Fact]
    public void Tick_AfterWindowEnd_StillEnqueuesSoEngineCanExpire()
    {
        // RFC §4.6: window-passed parents need an engine pass to mark
        // Expired. Scheduler enqueues a Created signal; the engine
        // recognises now>=endUtc and transitions terminal.
        var (algos, _, queue, _, scheduler) = Build(now: End.AddMinutes(1));
        algos.TryAdd(NewTwap(1));
        scheduler.Tick();
        Assert.Single(Drain(queue));
    }

    [Fact]
    public void Tick_PlanExhaustedBeforeWindow_DoesNotEnqueue()
    {
        // 4 slices already submitted; window still open. Scheduler has
        // nothing more to do until endUtc — no spurious signals.
        var (algos, orders, queue, _, scheduler) = Build(now: Start.AddMinutes(50));
        algos.TryAdd(NewTwap(1, sliceCount: 4));
        for (var seq = 0; seq < 4; seq++)
            orders.TryAdd(NewChild(clOrdId: (ulong)(100 + seq), parentAlgoId: 1, sliceSeq: seq,
                qty: 250, status: OrderStatus.Filled));
        scheduler.Tick();
        Assert.Empty(Drain(queue));
    }

    [Fact]
    public void Tick_IgnoresIcebergParents()
    {
        // Iceberg refills happen entirely in the engine's OnChildErAsync;
        // the scheduler must never inject extra signals for them.
        var (algos, _, queue, _, scheduler) = Build(now: Start);
        var ice = new Algo(2UL, new EndClientId("alice"), Firm, "PETR4", 4321UL,
            OrderSide.Buy, AlgoType.Iceberg, 1000,
            new IcebergParameters(100, 30m), Start);
        algos.TryAdd(ice);
        scheduler.Tick();
        Assert.Empty(Drain(queue));
    }

    [Fact]
    public void Tick_IgnoresCancellingParents()
    {
        // Operator already drove the parent into Cancelling — the engine
        // owns the next transition. Scheduler stays out.
        var (algos, _, queue, _, scheduler) = Build(now: Start.AddMinutes(20));
        var twap = NewTwap(1);
        twap.RequestCancel();
        algos.TryAdd(twap);
        scheduler.Tick();
        Assert.Empty(Drain(queue));
    }

    [Fact]
    public void Tick_CatchUp_OnlyOneSignalPerTickPerParent()
    {
        // RFC §4.6: no catch-up burst. Even if many slices are overdue
        // (host was down for 40min, slices 0..2 all due), the scheduler
        // must enqueue at most ONE signal per parent per tick. The engine
        // submits one slice; subsequent ticks then submit the next.
        var (algos, _, queue, _, scheduler) = Build(now: Start.AddMinutes(40));
        algos.TryAdd(NewTwap(1, sliceCount: 4));
        scheduler.Tick();
        Assert.Single(Drain(queue));
    }

    // ───────── Pass-4 review (#299) P1 — ambiguous-send TTL sweep ─────────

    [Fact]
    public async Task SweepAmbiguousReplaceIntents_ExpiredEntry_ReleasesReservationAndBumpsMetric()
    {
        // Pass-4 review (#299) P1. Approach A+TTL convergence: an
        // AlgoEngine modify whose gateway dispatch threw post-Prepare
        // intentionally KEEPS the upsize-delta reservation +
        // PendingReplacementRegistry intent so a late Replaced ER can
        // converge without re-checking capacity. If no terminal ER
        // arrives within RiskOptions.Margin.AmbiguousReplaceTtl, the
        // AlgoScheduler sweep MUST release the reservation (or it
        // leaks until the parent terminates) and bump
        // algo.modify_ambiguous_intent_expired_total.
        var algos = new AlgoBook();
        var orders = new WorkingOrderBook();
        var queue = new AlgoSignalQueue();
        var clock = new MutableClock(Start);

        // Owner with a known cash baseline; original 100 @ 30 = 3000
        // reserved on submit; modify upsizes price to 30.5 = 3050,
        // delta = 50 reserved at Prepare and held thereafter.
        var risk = new RiskOptions();
        risk.Margin.Enabled = true;
        risk.Margin.AmbiguousReplaceTtl = TimeSpan.FromSeconds(30);
#pragma warning disable CS0618 // Initial is the transition fallback used by the unit-test composition.
        risk.Margin.Initial["alice"] = 10_000m;
#pragma warning restore CS0618
        var monitor = new StaticOptionsMonitor<RiskOptions>(risk);
        var margin = new ReserveOnSubmitMarginProvider(monitor,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReserveOnSubmitMarginProvider>.Instance);
        var reg = new PendingReplacementRegistry();

        // Seed the original reservation as the submit path would have done.
        var bought = await margin.TryReserveAsync(
            clOrdId: 100UL,
            new RiskContext(new EndClientId("alice"), Firm, "PETR4",
                OrderSide.Buy, OrderType.Limit, 100, 30m),
            CancellationToken.None);
        Assert.True(bought.Approved);
        Assert.Equal(3000m, margin.ReservedForTesting("alice"));

        // Prepare a replace upsize to 30.5 (delta = 50). The transient
        // entry under newClOrdId=200 holds the delta; _reserved=3050.
        var prep = await ((IReplaceMarginCoordinator)margin).PrepareReplaceAsync(
            originalClOrdId: 100UL, newClOrdId: 200UL,
            owner: new EndClientId("alice"),
            newRemainingNotional: 3050m,
            CancellationToken.None);
        Assert.True(prep.Approved);
        Assert.Equal(3050m, margin.ReservedForTesting("alice"));

        // Simulate the pass-1 ambiguous-send: intent registered with
        // a known CreatedAt, then the gateway dispatch threw and the
        // engine marked the entry as still holding margin.
        var intent = new OrderReplacementIntent(
            OriginalClOrdId: 100UL, NewClOrdId: 200UL,
            Owner: new EndClientId("alice"),
            Symbol: "PETR4", SecurityId: 4321UL,
            Side: OrderSide.Buy, Type: OrderType.Limit,
            NewQuantity: 100, NewPrice: 30.5m, FirmId: Firm,
            ParentAlgoId: null, AlgoSliceSeq: null);
        Assert.True(reg.TryAdd(intent, clock.GetUtcNow()));
        Assert.True(reg.MarkAmbiguousMarginHeld(200UL));

        var scheduler = new AlgoScheduler(algos, orders, queue, clock,
            AlgoScheduler.DefaultTickInterval,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AlgoScheduler>.Instance,
            replacements: reg,
            replaceMargin: margin,
            riskOptions: monitor);

        // Within TTL: sweep does nothing.
        scheduler.SweepAmbiguousReplaceIntents(clock.GetUtcNow().AddSeconds(10));
        Assert.True(reg.IsOriginalInFlight(100UL));
        Assert.Equal(3050m, margin.ReservedForTesting("alice"));

        // Past TTL: sweep releases the reservation + clears the intent.
        long expiredCount = 0;
        string? expiredAlgoType = null;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (inst, ml) =>
        {
            if (inst.Name == "trading.algo.modify_ambiguous_intent_expired_total")
                ml.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((_, m, tags, _) =>
        {
            foreach (var t in tags)
                if (t.Key == "algoType") expiredAlgoType = t.Value as string;
            Interlocked.Add(ref expiredCount, m);
        });
        listener.Start();

        scheduler.SweepAmbiguousReplaceIntents(clock.GetUtcNow().AddSeconds(31));

        Assert.False(reg.IsOriginalInFlight(100UL));
        // Released: _reserved drops back to the original 3000m.
        Assert.Equal(3000m, margin.ReservedForTesting("alice"));
        listener.RecordObservableInstruments();
        Assert.Equal(1, Interlocked.Read(ref expiredCount));
        // ParentAlgoId is null in this synthetic test, so the tag
        // falls back to "unknown" (covered by the lookup branch).
        Assert.Equal("unknown", expiredAlgoType);
    }
}
