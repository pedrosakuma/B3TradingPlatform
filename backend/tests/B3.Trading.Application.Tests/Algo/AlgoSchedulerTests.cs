using System.Collections.Generic;
using System.Linq;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
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
}
