using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Q2.3 (#270). Integration coverage: a fill processed via the live
/// dispatcher path must (1) compute fees off the fill delta using the
/// configured schedule, (2) append a <see cref="FeeAccruedEvent"/> to
/// the WAL <i>after</i> the originating ER, and (3) advance the
/// <see cref="FeeKeeper"/> running totals.
/// </summary>
public class ExecutionReportProcessorFeeTests
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
        RecordingEventStore Store, FeeKeeper Keeper, OrderOwnershipMap Own,
        WorkingOrderBook Book, BpsFeeCalculator Calc, StaticOptionsMonitor<FeeOptions> Opts) Build()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var cash = new CashLedger();
        var keeper = new FeeKeeper();
        var optsMonitor = new StaticOptionsMonitor<FeeOptions>(new FeeOptions
        {
            BrokerageBps = 5m,
            BrokerageMin = 0m,
            EmolumentosBps = 3.25m,
            LiquidacaoBps = 2.75m,
        });
        var calc = new BpsFeeCalculator(optsMonitor);
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var proc = new ExecutionReportProcessor(
            ownership, book, positions, new NullSink(), new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            algoSignals: null,
            cash: cash,
            feeCalculator: calc,
            feeKeeper: keeper,
            dispatcher: dispatcher);
        return (proc, dispatcher, store, keeper, ownership, book, calc, optsMonitor);
    }

    [Fact]
    public void Fill_DispatchesFeeAccruedEvent_AndUpdatesKeeper()
    {
        var (proc, dispatcher, store, keeper, ownership, book, _, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 100, 1_000m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        // Drive through the dispatcher so fanOut is non-null (this is
        // what gates the fee dispatch — replay/test paths bypass it).
        var er = new ExecutionReportReceivedEvent
        {
            ClOrdId = 1UL,
            ExecKind = nameof(ExecKind.Fill),
            LeavesQuantity = 0,
            CumulativeQuantity = 100,
            LastQuantity = 100,
            LastPrice = 1_000m,
            RejectReason = null,
            Synthetic = false,
            OrigClOrdId = 0,
        };
        dispatcher.Dispatch(er, fanOut => proc.Apply(
            1UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 1_000m,
            rejectReason: null, origClOrdId: 0, fanOut: fanOut));

        // WAL: ER first, FeeAccruedEvent second.
        var recorded = store.Recorded.ToArray();
        Assert.Equal(2, recorded.Length);
        Assert.IsType<ExecutionReportReceivedEvent>(recorded[0].Event);
        var fae = Assert.IsType<FeeAccruedEvent>(recorded[1].Event);
        Assert.Equal(2, recorded[1].Seq);
        Assert.Equal(1UL, fae.ClOrdId);
        Assert.Equal("1:100", fae.ExecutionId);
        Assert.Equal("alice", fae.EndClientId);
        Assert.Equal("PETR4", fae.Symbol);
        Assert.Equal("Buy", fae.Side);
        Assert.Equal(100, fae.FillQuantity);
        Assert.Equal(1_000m, fae.FillPrice);
        Assert.Equal(100_000m, fae.Notional);
        // 100k notional: brokerage 5 bps = 50, emol 3.25 bps = 32.50,
        // liq 2.75 bps = 27.50, total 110.
        Assert.Equal(50m, fae.Brokerage);
        Assert.Equal(32.50m, fae.Emolumentos);
        Assert.Equal(27.50m, fae.Liquidacao);
        Assert.Equal(110m, fae.Total);

        var day = DateOnly.FromDateTime(fae.TimestampUtc.UtcDateTime);
        Assert.Equal(110m, keeper.GetDayTotal("alice", day));
    }

    [Fact]
    public void ReplayPath_IsReplayTrue_DoesNotAppendFeeEvent()
    {
        // EventReplayer invokes Apply with isReplay: true so the fee
        // event is NOT re-appended (the replayed FeeAccruedEvent itself
        // is fed to FeeKeeper directly via the replayer's switch case).
        var (proc, _, store, keeper, ownership, book, _, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(2UL, owner, "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 100, 1_000m);
        book.TryAdd(order);
        ownership.Register(2UL, owner);

        proc.Apply(2UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 1_000m,
            rejectReason: null, origClOrdId: 0, fanOut: null, isReplay: true);

        Assert.Empty(store.Recorded);
        Assert.Equal(0m, keeper.GetDayTotal("alice", DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    [Fact]
    public void LiveBackpressureFallbackPath_NoFanOutNoReplay_StillAppendsFeeEvent()
    {
        // P1 regression for #277 pass-1: the live ER router falls back
        // to a fanOut-less direct Apply when WAL append for the ER
        // itself hits backpressure (router's `catch
        // (WalBackpressureException)` branch). That path is NOT replay,
        // so fees MUST still be accrued — gate must be `!isReplay`,
        // not `fanOut != null`.
        var (proc, _, store, keeper, ownership, book, _, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(4UL, owner, "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 100, 1_000m);
        book.TryAdd(order);
        ownership.Register(4UL, owner);

        proc.Apply(4UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 1_000m,
            rejectReason: null, origClOrdId: 0, fanOut: null, isReplay: false);

        var recorded = store.Recorded.ToArray();
        var fae = Assert.IsType<FeeAccruedEvent>(Assert.Single(recorded.Select(r => r.Event).OfType<FeeAccruedEvent>()));
        Assert.Equal(4UL, fae.ClOrdId);
        Assert.True(fae.Total > 0m);
        var day = DateOnly.FromDateTime(fae.TimestampUtc.UtcDateTime);
        Assert.Equal(fae.Total, keeper.GetDayTotal("alice", day));
    }

    [Fact]
    public void DuplicateFill_ProducesNoSecondFeeEvent()
    {
        // Order.ApplyCumulativeFill returns delta=0 on a re-applied
        // same-cum fill, so the entire fill branch (positions, cash,
        // fees) is skipped. Defense in depth: even if it weren't,
        // FeeKeeper.Apply dedupes on ExecutionId.
        var (proc, dispatcher, store, keeper, ownership, book, _, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(3UL, owner, "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 100, 1_000m);
        book.TryAdd(order);
        ownership.Register(3UL, owner);

        for (var i = 0; i < 2; i++)
        {
            var er = new ExecutionReportReceivedEvent
            {
                ClOrdId = 3UL,
                ExecKind = nameof(ExecKind.Fill),
                LeavesQuantity = 0,
                CumulativeQuantity = 100,
                LastQuantity = 100,
                LastPrice = 1_000m,
                RejectReason = null,
                Synthetic = false,
                OrigClOrdId = 0,
            };
            dispatcher.Dispatch(er, fanOut => proc.Apply(
                3UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 1_000m,
                rejectReason: null, origClOrdId: 0, fanOut: fanOut));
        }

        var feeEvents = store.Recorded.Where(r => r.Event is FeeAccruedEvent).ToArray();
        Assert.Single(feeEvents);
        var day = DateOnly.FromDateTime(((FeeAccruedEvent)feeEvents[0].Event).TimestampUtc.UtcDateTime);
        Assert.Equal(110m, keeper.GetDayTotal("alice", day));
    }

    [Fact]
    public void PartialFill_ThenFill_ProducesTwoFeeEvents_OnDeltas()
    {
        var (proc, dispatcher, store, keeper, ownership, book, _, _) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(4UL, owner, "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 100, 1_000m);
        book.TryAdd(order);
        ownership.Register(4UL, owner);

        // Partial: 40 @ 1000 → notional 40k, fees 5+3.25+2.75 bps = 11 bps = R$44.
        DispatchEr(dispatcher, proc, 4UL, ExecKind.PartialFill, leaves: 60, cumQty: 40, lastQty: 40, lastPx: 1_000m);
        // Final: 60 @ 1000 → notional 60k, fees 11 bps = R$66.
        DispatchEr(dispatcher, proc, 4UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 60, lastPx: 1_000m);

        var feeEvents = store.Recorded.Where(r => r.Event is FeeAccruedEvent)
            .Select(r => (FeeAccruedEvent)r.Event).ToArray();
        Assert.Equal(2, feeEvents.Length);
        Assert.Equal("4:40", feeEvents[0].ExecutionId);
        Assert.Equal(40, feeEvents[0].FillQuantity);
        Assert.Equal(44m, feeEvents[0].Total);
        Assert.Equal("4:100", feeEvents[1].ExecutionId);
        Assert.Equal(60, feeEvents[1].FillQuantity);
        Assert.Equal(66m, feeEvents[1].Total);

        var day = DateOnly.FromDateTime(feeEvents[0].TimestampUtc.UtcDateTime);
        Assert.Equal(110m, keeper.GetDayTotal("alice", day));
    }

    private static void DispatchEr(EventDispatcher d, ExecutionReportProcessor p,
        ulong clOrdId, ExecKind kind, long leaves, long cumQty, long lastQty, decimal lastPx)
    {
        var er = new ExecutionReportReceivedEvent
        {
            ClOrdId = clOrdId,
            ExecKind = kind.ToString(),
            LeavesQuantity = leaves,
            CumulativeQuantity = cumQty,
            LastQuantity = lastQty,
            LastPrice = lastPx,
            RejectReason = null,
            Synthetic = false,
            OrigClOrdId = 0,
        };
        d.Dispatch(er, fanOut => p.Apply(clOrdId, kind, leaves, cumQty, lastQty, lastPx, null, 0, fanOut));
    }
}
