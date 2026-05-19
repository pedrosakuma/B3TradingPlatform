using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #337 — coverage for the OrderReplaceRejectedEvent audit row the
/// modify pipeline now writes when the risk pipeline or the margin
/// coordinator rejects a cancel-replace pre-WAL. Closes the audit gap
/// flagged by the risk-pipeline-ordering RFC (#262).
/// </summary>
public class OrderReplaceRejectedEventTests
{
    [Fact]
    public async Task RiskReject_writes_OrderReplaceRejectedEvent_with_source_risk_and_burned_clordid()
    {
        var (svc, store, sink, _, seed) = Build(riskRejects: true, marginRejects: false);

        var result = await svc.ModifyAsync(
            new OrderModifyRequest(seed.Owner, seed.ClOrdId, NewQuantity: 200, NewPrice: 30m),
            CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.RiskRejected, result.Kind);

        var rejected = store.Recorded
            .Select(t => t.Event)
            .OfType<OrderReplaceRejectedEvent>()
            .Single();
        Assert.Equal(seed.ClOrdId, rejected.OriginalClOrdId);
        Assert.NotEqual(0UL, rejected.NewClOrdId);
        Assert.Equal("risk", rejected.Source);
        Assert.Equal("test_risk_reject", rejected.Reason);
        Assert.Equal(seed.FirmId, rejected.FirmId);
        Assert.Equal(seed.Owner.Value, rejected.EndClientId);
        Assert.Equal(seed.Symbol, rejected.Symbol);
        Assert.Equal(seed.SecurityId, rejected.SecurityId);
        Assert.Equal(200, rejected.RequestedQuantity);
        Assert.Equal(30m, rejected.RequestedPrice);

        // Live FE notification mirrors submit-side: ExecKind.Rejected
        // under the burned ClOrdId.
        var live = Assert.Single(sink.Events);
        Assert.Equal(ExecKind.Rejected, live.Kind);
        Assert.Equal(rejected.NewClOrdId, live.ClOrdId);
        Assert.Equal("test_risk_reject", live.RejectReason);
    }

    [Fact]
    public async Task MarginReject_writes_OrderReplaceRejectedEvent_with_source_margin()
    {
        var (svc, store, _, _, seed) = Build(riskRejects: false, marginRejects: true);

        var result = await svc.ModifyAsync(
            new OrderModifyRequest(seed.Owner, seed.ClOrdId, NewQuantity: 200, NewPrice: 30m),
            CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.RiskRejected, result.Kind);
        var rejected = store.Recorded
            .Select(t => t.Event)
            .OfType<OrderReplaceRejectedEvent>()
            .Single();
        Assert.Equal("margin", rejected.Source);
        Assert.Equal("test_margin_reject", rejected.Reason);
    }

    [Fact]
    public async Task RiskReject_does_not_write_OrderReplaceRequestedEvent_or_advance_intent()
    {
        // The rejected ClOrdId must NOT appear as an OrderReplaceRequestedEvent
        // (no intent registration, no gateway dispatch). Only the
        // OrderReplaceRejectedEvent audit row exists for it.
        var (svc, store, _, _, seed) = Build(riskRejects: true, marginRejects: false);

        await svc.ModifyAsync(
            new OrderModifyRequest(seed.Owner, seed.ClOrdId, NewQuantity: 200, NewPrice: 30m),
            CancellationToken.None);

        Assert.Empty(store.Recorded.Select(t => t.Event).OfType<OrderReplaceRequestedEvent>());
        Assert.Single(store.Recorded.Select(t => t.Event).OfType<OrderReplaceRejectedEvent>());
    }

    [Fact]
    public async Task RiskReject_gateway_is_not_called()
    {
        var (svc, _, _, gw, seed) = Build(riskRejects: true, marginRejects: false);

        await svc.ModifyAsync(
            new OrderModifyRequest(seed.Owner, seed.ClOrdId, NewQuantity: 200, NewPrice: 30m),
            CancellationToken.None);

        Assert.Empty(gw.Replaces);
    }

    [Fact]
    public async Task RiskReject_preserves_requested_optionals_on_event()
    {
        var (svc, store, _, _, seed) = Build(riskRejects: true, marginRejects: false);

        var gtd = DateTimeOffset.UtcNow.AddHours(2);
        await svc.ModifyAsync(
            new OrderModifyRequest(
                seed.Owner, seed.ClOrdId, NewQuantity: 150, NewPrice: 31m,
                NewTimeInForce: TimeInForce.GTD, NewStopPrice: null, NewGoodTillDate: gtd),
            CancellationToken.None);

        var evt = store.Recorded.Select(t => t.Event).OfType<OrderReplaceRejectedEvent>().Single();
        Assert.Equal("GTD", evt.RequestedTimeInForce);
        Assert.Equal(gtd, evt.RequestedGoodTillDate);
        Assert.Null(evt.RequestedStopPrice);
    }

    // ----------------------------------------------------------------
    // Builders / fakes
    // ----------------------------------------------------------------

    private static (OrderModifyService Svc, RecordingEventStore Store, CapturingSink Sink,
        CapturingGateway Gw, Order Seed) Build(bool riskRejects, bool marginRejects)
    {
        var owner = new EndClientId("alice");
        var seed = new Order(
            1_337_001UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM01");
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(seed));
        ownership.Register(seed.ClOrdId, owner);
        var gateway = new CapturingGateway();
        var sink = new CapturingSink();
        var checks = riskRejects
            ? new IRiskCheck[] { new AlwaysRejectingCheck("test_risk_reject") }
            : Array.Empty<IRiskCheck>();
        var risk = new RiskPipeline(checks);
        var margin = marginRejects
            ? (IReplaceMarginCoordinator)new AlwaysRejectingReplaceMargin("test_margin_reject")
            : new NoOpReplaceMargin();
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var svc = new OrderModifyService(
            clOrdIds, ownership, book, gateway, sink, risk, margin,
            new PendingReplacementRegistry(), dispatcher,
            new NeverDrain(), NullLogger<OrderModifyService>.Instance);
        return (svc, store, sink, gateway, seed);
    }

    private sealed class AlwaysRejectingCheck(string reason) : IRiskCheck
    {
        public int Order => 0;
        public string Name => "AlwaysReject";
        public RiskDecision Check(RiskContext ctx) => RiskDecision.Reject(reason);
    }

    private sealed class AlwaysRejectingReplaceMargin(string reason) : IReplaceMarginCoordinator
    {
        public Task<RiskDecision> PrepareReplaceAsync(ulong _, ulong __, EndClientId ___, decimal ____, CancellationToken _____)
            => Task.FromResult(RiskDecision.Reject(reason));
        public void CommitReplace(ulong _, ulong __, decimal ___) { }
        public void AbortReplace(ulong _) { }
    }

    private sealed class NoOpReplaceMargin : IReplaceMarginCoordinator
    {
        public Task<RiskDecision> PrepareReplaceAsync(ulong _, ulong __, EndClientId ___, decimal ____, CancellationToken _____)
            => Task.FromResult(RiskDecision.Approve);
        public void CommitReplace(ulong _, ulong __, decimal ___) { }
        public void AbortReplace(ulong _) { }
    }

    private sealed class CapturingSink : IExecutionEventSink
    {
        public readonly List<ExecutionEvent> Events = new();
        public void Publish(ExecutionEvent ev) => Events.Add(ev);
    }

    private sealed class CapturingGateway : IExchangeGateway
    {
        public readonly List<(ulong NewClOrdId, long Qty, decimal? Px, TimeInForce? Tif, decimal? Stop, DateTimeOffset? Gtd)> Replaces = new();
        public Task SubmitAsync(Order o, CancellationToken ct) => Task.CompletedTask;
        public Task CancelAsync(Order o, ulong cancelClOrdId, CancellationToken ct) => Task.CompletedTask;
        public Task CancelReplaceAsync(Order o, ulong newClOrdId, long newQty, decimal? newPx,
            TimeInForce? tif, decimal? stop, DateTimeOffset? gtd, CancellationToken ct)
        {
            Replaces.Add((newClOrdId, newQty, newPx, tif, stop, gtd));
            return Task.CompletedTask;
        }
    }

    private sealed class NeverDrain : Lifecycle.IDrainGate { public bool IsDraining => false; }

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
}
