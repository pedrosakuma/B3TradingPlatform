using System.Diagnostics.Metrics;

using B3.Trading.Application.Risk;
using B3.Trading.Application;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

public class EntryPointGatewayAndRouterTests
{
    [Fact]
    public async Task Gateway_Submit_ForwardsToClient_WithCorrectFirmAndFields()
    {
        var client = new MockEntryPointClient();
        var gateway = new EntryPointClientGateway(client, "FIRM-A");
        var order = new Order(42UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Sell, OrderType.Limit, 50, 31.25m);

        await gateway.SubmitAsync(order, CancellationToken.None);

        var sent = Assert.Single(client.SubmittedNewOrders);
        Assert.Equal(42UL, sent.ClOrdId);
        Assert.Equal(4321UL, sent.SecurityId);
        Assert.Equal("FIRM-A", sent.FirmId);
        Assert.Equal(EpSide.Sell, sent.Side);
        Assert.Equal(EpOrderType.Limit, sent.Type);
        Assert.Equal(50, sent.Quantity);
        Assert.Equal(31.25m, sent.Price);
        // Q3.4 (#284). Plain (no-reserve) orders must not surface a
        // MaxFloor on the wire — a non-null value would cause the
        // venue to expose only a slice of the order.
        Assert.Null(sent.MaxFloor);
    }

    [Fact]
    public async Task Gateway_Submit_Iceberg_ForwardsDisplayQtyAsMaxFloor()
    {
        // Q3.4 (#284) pass-1 (#297). Pin DisplayQty → MaxFloor wire
        // mapping through the IEntryPointClient seam (the real SDK
        // path is pinned by B3EntryPointClientGatewayMapTests).
        var client = new MockEntryPointClient();
        var gateway = new EntryPointClientGateway(client, "FIRM-A");
        var order = new Order(42UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A", displayQty: 10, displayResetPolicy: DisplayResetPolicy.Always);

        await gateway.SubmitAsync(order, CancellationToken.None);

        var sent = Assert.Single(client.SubmittedNewOrders);
        Assert.Equal(10L, sent.MaxFloor);
        Assert.Equal(100, sent.Quantity);
    }

    [Fact]
    public async Task Gateway_CancelReplace_ForwardsToClient()
    {
        var client = new MockEntryPointClient();
        var gateway = new EntryPointClientGateway(client, "FIRM-A");
        var original = new Order(100UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);

        await gateway.CancelReplaceAsync(original, 101UL, 200, 30m, null, null, null, CancellationToken.None);

        var sent = Assert.Single(client.SubmittedReplaces);
        Assert.Equal(100UL, sent.OriginalClOrdId);
        Assert.Equal(101UL, sent.NewClOrdId);
        Assert.Equal(4321UL, sent.SecurityId);
        Assert.Equal(EpSide.Buy, sent.Side);
        Assert.Equal(200, sent.NewQuantity);
        // Q3.4 (#284). Plain (no-reserve) replace must not surface MaxFloor.
        Assert.Null(sent.MaxFloor);
        // #437. With no override the replace inherits the original's
        // TimeInForce (Day default) and carries no Stop/GTD because
        // OrderType is Limit and TIF != GoodTillDate.
        Assert.Equal(TimeInForce.Day, sent.TimeInForce);
        Assert.Null(sent.StopPrice);
        Assert.Null(sent.GoodTillDate);
    }

    [Fact]
    public async Task Gateway_CancelReplace_PropagatesTifStopAndGtd()
    {
        // #437. Mock seam parity with B3EntryPointClientGateway: when
        // the modify pipeline asks to switch TIF to GoodTillDate it
        // must also supply a GoodTillDate; Stop* order types must
        // surface their StopPrice. The domain merge enforces these
        // invariants; the gateway just plumbs the merged values onto
        // the wire request so test wire-mapping pins both adapter
        // paths to the same shape.
        var client = new MockEntryPointClient();
        var gateway = new EntryPointClientGateway(client, "FIRM-A");

        var stopOrig = new Order(200UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy,
            OrderType.StopLimit, 100, price: 30m, firmId: "FIRM-A", stopPrice: 29.5m);
        await gateway.CancelReplaceAsync(stopOrig, 201UL, 100, 31m,
            requestedTimeInForce: null, requestedStopPrice: 29m, requestedGoodTillDate: null,
            CancellationToken.None);
        var withStop = client.SubmittedReplaces.Last();
        Assert.Equal(29m, withStop.StopPrice);

        var gtdMoment = new DateTimeOffset(2030, 1, 2, 17, 0, 0, TimeSpan.Zero);
        var dayOrig = new Order(300UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy,
            OrderType.Limit, 100, 30m);
        await gateway.CancelReplaceAsync(dayOrig, 301UL, 150, 30m,
            requestedTimeInForce: TimeInForce.GTD, requestedStopPrice: null, requestedGoodTillDate: gtdMoment,
            CancellationToken.None);
        var gtd = client.SubmittedReplaces.Last();
        Assert.Equal(TimeInForce.GTD, gtd.TimeInForce);
        Assert.Equal(gtdMoment, gtd.GoodTillDate);
    }

    [Fact]
    public async Task Gateway_CancelReplace_Iceberg_InheritsAndClampsMaxFloor()
    {
        // Q3.4 (#284) pass-1 (#297). Replace inherits the original's
        // visible portion (MaxFloor). When the new order qty shrinks
        // below the original DisplayQty, MaxFloor must clamp to the
        // new qty so the venue invariant (MaxFloor <= OrderQty) holds.
        var client = new MockEntryPointClient();
        var gateway = new EntryPointClientGateway(client, "FIRM-A");
        var original = new Order(100UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM-A", displayQty: 50, displayResetPolicy: DisplayResetPolicy.Always);

        // 1) Replace grows the qty — MaxFloor stays at the original 50.
        await gateway.CancelReplaceAsync(original, 101UL, 200, 30m, null, null, null, CancellationToken.None);
        var grown = client.SubmittedReplaces.Last();
        Assert.Equal(50L, grown.MaxFloor);

        // 2) Replace shrinks below DisplayQty — MaxFloor clamps to newQty.
        await gateway.CancelReplaceAsync(original, 102UL, 20, 30m, null, null, null, CancellationToken.None);
        var shrunk = client.SubmittedReplaces.Last();
        Assert.Equal(20L, shrunk.MaxFloor);
    }

    [Fact]
    public void Router_DeliversERToProcessor()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        var sink = new TestSink();
        var proc = new ExecutionReportProcessor(ownership, book, positions, sink, new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);
        var client = new MockEntryPointClient();
        // RFC §5.2 (F2). Wire the test sink as a fan-out sink so the
        // dispatcher's per-sink-channel fan-out (under the lock) routes
        // captured ERs into it. The legacy synchronous sink.Publish
        // path is no longer invoked from inside Apply when an
        // ExecutionFanOut writer is supplied.
        var dispatcher = new EventDispatcher(new NullEventStore(), new[] { (IExecutionFanOutSink)sink });
        using var router = new EntryPointExecutionReportRouter(client, proc, dispatcher);

        client.EmitExecutionReport(new ExecutionReportEnvelope(1UL, EpExecType.Fill, 0, 100, 100, 30m, null));

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Single(sink.Events);
    }

    [Fact]
    public void Router_WalBackpressure_DoesNotApplyFillInMemory()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        var sink = new TestSink();
        var proc = new ExecutionReportProcessor(
            ownership, book, positions, sink, new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        var client = new MockEntryPointClient();
        var dispatcher = new EventDispatcher(new BackpressureStore(), new[] { (IExecutionFanOutSink)sink });
        using var router = new EntryPointExecutionReportRouter(client, proc, dispatcher);

        Assert.Throws<WalBackpressureException>(() =>
            client.EmitExecutionReport(new ExecutionReportEnvelope(
                1UL, EpExecType.Fill, 0, 100, 100, 30m, null)));

        Assert.Equal(OrderStatus.PendingNew, order.Status);
        Assert.Equal(0, order.CumulativeQuantity);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void Router_OnBusinessReject_AppendsWalEvent_WithGatewayStampedFirm()
    {
        // #432. BusinessReject from the venue must reach the WAL so the
        // operator can reconcile "request sent but no ER" gaps and so the
        // history projection / replay can surface it. The router stamps
        // FirmId from the envelope (which the gateway itself stamps from
        // its own configured firm) — defending against a future refactor
        // that forgets to plumb FirmId through.
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new TestSink();
        var proc = new ExecutionReportProcessor(ownership, book, positions, sink, new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);
        var client = new MockEntryPointClient();
        var store = new BrRecordingStore();
        var dispatcher = new EventDispatcher(store, Array.Empty<IExecutionFanOutSink>());
        using var router = new EntryPointExecutionReportRouter(client, proc, dispatcher);

        var sendingTime = new DateTimeOffset(2026, 5, 24, 14, 30, 0, TimeSpan.Zero);
        client.EmitBusinessReject(new BusinessRejectEnvelope(
            FirmId: "FIRM-A",
            RefSeqNum: 4242UL,
            RejectReason: 3,
            Text: "Unknown SecurityID",
            SeqNum: 5000UL,
            SendingTime: sendingTime));

        var (_, evt) = Assert.Single(store.Recorded);
        var br = Assert.IsType<BusinessRejectReceivedEvent>(evt);
        Assert.Equal("FIRM-A", br.FirmId);
        Assert.Equal(4242UL, br.RefSeqNum);
        Assert.Equal(3, br.RejectReason);
        Assert.Equal("Unknown SecurityID", br.Text);
        Assert.Equal(5000UL, br.SeqNum);
        Assert.Equal(sendingTime, br.SendingTime);

        // BR is replay-inert — it must not touch order state.
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void Router_OnBusinessReject_NullFirm_DefaultsToDefaultFirm()
    {
        // Back-compat: legacy gateways / mocks that don't stamp FirmId
        // still produce a recoverable WAL row rather than crashing.
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new TestSink();
        var proc = new ExecutionReportProcessor(ownership, book, positions, sink, new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);
        var client = new MockEntryPointClient();
        var store = new BrRecordingStore();
        var dispatcher = new EventDispatcher(store, Array.Empty<IExecutionFanOutSink>());
        using var router = new EntryPointExecutionReportRouter(client, proc, dispatcher);

        client.EmitBusinessReject(new BusinessRejectEnvelope(
            FirmId: null,
            RefSeqNum: 1UL,
            RejectReason: 0,
            Text: null,
            SeqNum: 2UL,
            SendingTime: DateTimeOffset.UtcNow));

        var (_, evt) = Assert.Single(store.Recorded);
        var br = Assert.IsType<BusinessRejectReceivedEvent>(evt);
        Assert.Equal("default", br.FirmId);
    }

    [Fact]
    public void Router_OnBusinessReject_DuplicateSeqNum_AppendsAndCountsOnce()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new TestSink();
        var proc = new ExecutionReportProcessor(ownership, book, positions, sink, new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);
        var client = new MockEntryPointClient();
        var store = new BrRecordingStore();
        var dispatcher = new EventDispatcher(store, Array.Empty<IExecutionFanOutSink>());
        using var listener = ListenBusinessRejectCounter("FIRM-DUP", 3, out var readBusinessRejects);
        using var router = new EntryPointExecutionReportRouter(client, proc, dispatcher);

        var reject = new BusinessRejectEnvelope(
            FirmId: "FIRM-DUP",
            RefSeqNum: 4242UL,
            RejectReason: 3,
            Text: "Unknown SecurityID",
            SeqNum: 5000UL,
            SendingTime: new DateTimeOffset(2026, 5, 24, 14, 30, 0, TimeSpan.Zero));

        client.EmitBusinessReject(reject);
        client.EmitBusinessReject(reject);

        var (_, evt) = Assert.Single(store.Recorded);
        var br = Assert.IsType<BusinessRejectReceivedEvent>(evt);
        Assert.Equal("FIRM-DUP", br.FirmId);
        Assert.Equal(5000UL, br.SeqNum);
        Assert.Equal(1, readBusinessRejects());
        Assert.Empty(sink.Events);
    }

    private sealed class BrRecordingStore : IEventStore
    {
        public System.Collections.Concurrent.ConcurrentQueue<(long Seq, WalEvent Event)> Recorded { get; } = new();
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
        public async System.Collections.Generic.IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BackpressureStore : IEventStore
    {
        public long CurrentSeq => 0;
        public long Append(WalEvent evt) =>
            throw new WalBackpressureException("forced saturation");
        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload) =>
            throw new WalBackpressureException("forced saturation");
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async System.Collections.Generic.IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestSink : IExecutionEventSink, IExecutionFanOutSink
    {
        public readonly List<ExecutionEvent> Events = new();
        public ExecutionFanOutTargets Target => ExecutionFanOutTargets.All;
        public void Publish(ExecutionEvent ev) { lock (Events) Events.Add(ev); }
        public void Enqueue(long seq, ExecutionEvent ev) { lock (Events) Events.Add(ev); }
    }

    private static MeterListener ListenBusinessRejectCounter(string firmId, int rejectReason, out Func<long> read)
    {
        long total = 0;
        var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "B3.Trading" && inst.Name == MetricsRegistry.EntryPointBusinessRejects.Name)
                l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? seenFirm = null;
            int? seenReason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "firm")
                    seenFirm = tag.Value as string;
                else if (tag.Key == "reason" && tag.Value is int reason)
                    seenReason = reason;
            }

            if (seenFirm == firmId && seenReason == rejectReason)
                Interlocked.Add(ref total, value);
        });
        listener.Start();
        read = () => Interlocked.Read(ref total);
        return listener;
    }
}
