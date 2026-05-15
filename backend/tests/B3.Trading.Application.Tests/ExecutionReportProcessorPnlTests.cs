using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Q2.4 (#271). Integration coverage for the P&amp;L append on the
/// ExecutionReportProcessor live + replay paths. Mirrors the structure
/// of <see cref="ExecutionReportProcessorFeeTests"/>.
/// </summary>
public class ExecutionReportProcessorPnlTests
{
    private sealed class NullSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent ev) { }
    }

    private sealed class RecordingEventStore : IEventStore
    {
        public ConcurrentQueue<(long Seq, WalEvent Event)> Recorded { get; } = new();
        private long _seq;
        public long CurrentSeq => Interlocked.Read(ref _seq);
        public long Append(WalEvent evt)
        {
            var s = Interlocked.Increment(ref _seq);
            Recorded.Enqueue((s, evt));
            return s;
        }
        public long Append(WalEvent evt, ReadOnlyMemory<byte> _) => Append(evt);
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (ExecutionReportProcessor Proc, EventDispatcher Dispatcher,
        RecordingEventStore Store, PnlKeeper Pnl, OrderOwnershipMap Own,
        WorkingOrderBook Book, PositionKeeper Positions) Build()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var pnl = new PnlKeeper();
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var proc = new ExecutionReportProcessor(
            ownership, book, positions, new NullSink(), new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            algoSignals: null,
            cash: null,
            feeCalculator: null,
            feeKeeper: null,
            dispatcher: dispatcher,
            pnlKeeper: pnl);
        return (proc, dispatcher, store, pnl, ownership, book, positions);
    }

    [Fact]
    public void Live_OpeningFill_NoRealizedEvent_ButAdvancesAvgCost()
    {
        var (proc, dispatcher, store, pnl, ownership, book, _) = Build();
        var owner = new EndClientId("alice");
        book.TryAdd(new Order(1UL, owner, "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 100, 30m));
        ownership.Register(1UL, owner);

        var er = new ExecutionReportReceivedEvent
        {
            ClOrdId = 1UL,
            ExecKind = nameof(ExecKind.Fill),
            LeavesQuantity = 0,
            CumulativeQuantity = 100,
            LastQuantity = 100,
            LastPrice = 30m,
            Synthetic = false,
            OrigClOrdId = 0,
        };
        dispatcher.Dispatch(er, fanOut => proc.Apply(
            1UL, ExecKind.Fill, 0, 100, 100, 30m, null, 0, fanOut));

        // Only the ER is appended — no RealizedPnlEvent for opening fill.
        Assert.Single(store.Recorded);
        Assert.IsType<ExecutionReportReceivedEvent>(store.Recorded.ToArray()[0].Event);
        var s = pnl.GetAvgCost("alice", "PETR4")!;
        Assert.Equal(100, s.NetQuantity);
        Assert.Equal(30m, s.AvgPrice);
    }

    [Fact]
    public void Live_ClosingFill_AppendsRealizedPnlEventAfterEr_AndUpdatesKeeper()
    {
        var (proc, dispatcher, store, pnl, ownership, book, _) = Build();
        var owner = new EndClientId("alice");
        book.TryAdd(new Order(1UL, owner, "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 100, 30m));
        ownership.Register(1UL, owner);

        // Open.
        var er1 = new ExecutionReportReceivedEvent
        {
            ClOrdId = 1UL,
            ExecKind = nameof(ExecKind.Fill),
            LeavesQuantity = 0,
            CumulativeQuantity = 100,
            LastQuantity = 100,
            LastPrice = 30m,
            Synthetic = false,
            OrigClOrdId = 0,
        };
        dispatcher.Dispatch(er1, fanOut => proc.Apply(
            1UL, ExecKind.Fill, 0, 100, 100, 30m, null, 0, fanOut));

        // Close half on a separate sell order.
        book.TryAdd(new Order(2UL, owner, "PETR4", 1UL, OrderSide.Sell, OrderType.Limit, 50, 31m));
        ownership.Register(2UL, owner);
        var er2 = new ExecutionReportReceivedEvent
        {
            ClOrdId = 2UL,
            ExecKind = nameof(ExecKind.Fill),
            LeavesQuantity = 0,
            CumulativeQuantity = 50,
            LastQuantity = 50,
            LastPrice = 31m,
            Synthetic = false,
            OrigClOrdId = 0,
        };
        dispatcher.Dispatch(er2, fanOut => proc.Apply(
            2UL, ExecKind.Fill, 0, 50, 50, 31m, null, 0, fanOut));

        var rec = store.Recorded.ToArray();
        // ER1, ER2, RealizedPnlEvent (ordered after its originating ER).
        Assert.Equal(3, rec.Length);
        Assert.IsType<ExecutionReportReceivedEvent>(rec[0].Event);
        Assert.IsType<ExecutionReportReceivedEvent>(rec[1].Event);
        var rpe = Assert.IsType<RealizedPnlEvent>(rec[2].Event);
        Assert.Equal(2UL, rpe.ClOrdId);
        Assert.Equal("2:50", rpe.ExecutionId);
        Assert.Equal("alice", rpe.EndClientId);
        Assert.Equal("PETR4", rpe.Symbol);
        Assert.Equal(50m, rpe.DeltaRealized);
        Assert.Equal(50m, rpe.RunningTotal);
        Assert.Equal(50m, pnl.GetDayRealized("alice", "PETR4", rpe.DayKey));
    }

    [Fact]
    public void ReplayPath_DefersSynth_NoWalAppend_FinalizeMaterialises()
    {
        // Replay: capture pre-fill state then defer. No durable event
        // arrives → FinalizeReplay materialises the synth (true
        // ER-then-crash window per #277).
        var (proc, _, store, pnl, ownership, book, _) = Build();
        var owner = new EndClientId("alice");

        // Stage 1: open via replay (no realized).
        book.TryAdd(new Order(10UL, owner, "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 100, 30m));
        ownership.Register(10UL, owner);
        proc.Apply(10UL, ExecKind.Fill, 0, 100, 100, 30m, null, 0, null, isReplay: true);

        // Stage 2: closing fill via replay → synth pending.
        book.TryAdd(new Order(11UL, owner, "PETR4", 1UL, OrderSide.Sell, OrderType.Limit, 50, 31m));
        ownership.Register(11UL, owner);
        proc.Apply(11UL, ExecKind.Fill, 0, 50, 50, 31m, null, 0, null, isReplay: true);

        Assert.Empty(store.Recorded);
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(0m, pnl.GetDayRealized("alice", "PETR4", day));

        Assert.Equal(1, pnl.FinalizeReplay());
        Assert.Equal(50m, pnl.GetDayRealized("alice", "PETR4", day));
    }
}
