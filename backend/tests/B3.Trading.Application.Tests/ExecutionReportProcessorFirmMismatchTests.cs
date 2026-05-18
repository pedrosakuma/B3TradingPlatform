using System.Diagnostics.Metrics;
using B3.Trading.Application;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

/// <summary>
/// PR #317 P1. Inbound ER cross-firm guard in
/// <see cref="ExecutionReportProcessor.Apply"/>.
///
/// <para>An ER whose envelope <c>FirmId</c> mismatches the resolved
/// order's <c>FirmId</c> must be rejected without state mutation and
/// counted on <c>trading.er.firm_mismatch_total</c>. Legacy WAL events
/// (envelope <c>FirmId == null</c>) bypass the check so existing logs
/// hydrate identically.</para>
/// </summary>
public class ExecutionReportProcessorFirmMismatchTests
{
    private sealed class CapturingSink : IExecutionEventSink
    {
        public List<ExecutionEvent> Events { get; } = new();
        public void Publish(ExecutionEvent evt) => Events.Add(evt);
    }

    [Fact]
    public void Fill_WithMismatchingFirm_IsRejected_NoStateMutation_AndCounted()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new CapturingSink();
        var proc = new ExecutionReportProcessor(ownership, book, positions, sink,
            new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);

        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: "FIRM01");
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        // Subscribe to the firm-mismatch counter via a meter listener so we
        // can assert exactly one increment without relying on global state.
        long mismatchCount = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "B3.Trading" && inst.Name == "trading.er.firm_mismatch_total")
                l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref mismatchCount, value));
        listener.Start();

        // ER arrives on FIRM02 for an order placed under FIRM01 — reject.
        proc.Apply(
            clOrdId: 1UL, kind: ExecKind.Fill,
            leaves: 0, cumQty: 100, lastQty: 100, lastPx: 30m,
            rejectReason: null, origClOrdId: 0,
            fanOut: null, isReplay: false, eventTimestampUtc: null,
            envelopeFirmId: "FIRM02");

        listener.Dispose();

        Assert.Equal(1, Interlocked.Read(ref mismatchCount));
        // Order untouched: still PendingNew, no fills booked.
        Assert.Equal(OrderStatus.PendingNew, order.Status);
        Assert.Equal(0, order.CumulativeQuantity);
        Assert.Equal(0, positions.GetOrCreate("FIRM01", owner, "PETR4").NetQuantity);
        // No ExecutionEvent fan-out for a rejected ER — also implies no
        // nested Dispatch into a WAL state-mutation event (fee/PnL) since
        // those flow off the same fill path the processor short-circuits
        // before reaching.
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void Fill_WithMatchingFirm_IsApplied()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new CapturingSink();
        var proc = new ExecutionReportProcessor(ownership, book, positions, sink,
            new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);

        var owner = new EndClientId("alice");
        var order = new Order(2UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: "FIRM01");
        book.TryAdd(order);
        ownership.Register(2UL, owner);

        proc.Apply(
            clOrdId: 2UL, kind: ExecKind.Fill,
            leaves: 0, cumQty: 100, lastQty: 100, lastPx: 30m,
            rejectReason: null, origClOrdId: 0,
            fanOut: null, isReplay: false, eventTimestampUtc: null,
            envelopeFirmId: "FIRM01");

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(100, positions.GetOrCreate("FIRM01", owner, "PETR4").NetQuantity);
    }

    [Fact]
    public void Fill_WithNullEnvelopeFirm_BypassesCheck_LegacyReplayBackCompat()
    {
        // Legacy WAL events written before #317 carry a null FirmId on
        // ExecutionReportReceivedEvent. Replay must apply them unchanged
        // (no firm guard, no counter increment) so historical segments
        // hydrate identically. We model this by calling Apply with
        // envelopeFirmId == null.
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new CapturingSink();
        var proc = new ExecutionReportProcessor(ownership, book, positions, sink,
            new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance);

        var owner = new EndClientId("alice");
        var order = new Order(3UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: "FIRM01");
        book.TryAdd(order);
        ownership.Register(3UL, owner);

        proc.Apply(3UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 30m, rejectReason: null);

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(100, positions.GetOrCreate("FIRM01", owner, "PETR4").NetQuantity);
    }

    // ---------------- PR #317 P1: replace-lifecycle bypass guard ----------------

    private static OrderReplacementIntent BuildIntent(ulong origId, ulong newId, string owner, string firmId, long newQty = 200, decimal newPrice = 30m) =>
        new(
            OriginalClOrdId: origId,
            NewClOrdId: newId,
            Owner: new EndClientId(owner),
            Symbol: "PETR4",
            SecurityId: 4321UL,
            Side: OrderSide.Buy,
            Type: OrderType.Limit,
            NewQuantity: newQty,
            NewPrice: newPrice,
            FirmId: firmId,
            ParentAlgoId: null,
            AlgoSliceSeq: null);

    private static (ExecutionReportProcessor proc, OrderOwnershipMap own, WorkingOrderBook book,
                    PendingReplacementRegistry reg, CapturingSink sink) BuildProcWithReg()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new CapturingSink();
        var reg = new PendingReplacementRegistry();
        var proc = new ExecutionReportProcessor(
            ownership, book, positions, sink,
            new NoOpMarginProvider(), NullLogger<ExecutionReportProcessor>.Instance,
            replacements: reg);
        return (proc, ownership, book, reg, sink);
    }

    private static (MeterListener listener, Func<long> read) ListenMismatch()
    {
        long count = 0;
        var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "B3.Trading" && inst.Name == "trading.er.firm_mismatch_total")
                l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref count, value));
        listener.Start();
        return (listener, () => Interlocked.Read(ref count));
    }

    [Fact]
    public void Rejected_forPendingReplacement_withMismatchingFirm_doesNotConsumeIntent_orMutateOriginal()
    {
        // ER_Rejected for the new ClOrdID of an in-flight modify
        // normally drives the replace-reject branch via TryConsume.
        // With a cross-firm envelope, the hoisted guard must short-
        // circuit BEFORE the consume so the intent stays parked and
        // the still-Working original is left untouched.
        var (proc, ownership, book, reg, sink) = BuildProcWithReg();
        var owner = new EndClientId("alice");
        var orig = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 10m, firmId: "FIRM01");
        book.TryAdd(orig);
        ownership.Register(1UL, owner);
        Assert.True(reg.TryAdd(BuildIntent(1UL, 2UL, "alice", "FIRM01")));

        var (listener, read) = ListenMismatch();
        proc.Apply(2UL, ExecKind.Rejected, leaves: 0, cumQty: 0, lastQty: 0, lastPx: 0m,
            rejectReason: "wrong-firm", envelopeFirmId: "FIRM02");
        listener.Dispose();

        Assert.Equal(1, read());
        // Intent untouched — second consume still succeeds.
        Assert.True(reg.TryConsume(2UL, out var stillThere));
        Assert.NotNull(stillThere);
        Assert.Equal(OrderStatus.PendingNew, orig.Status);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void Replaced_forPendingReplacement_withMismatchingFirm_doesNotConsumeIntent_orHydrateNew()
    {
        var (proc, ownership, book, reg, sink) = BuildProcWithReg();
        var owner = new EndClientId("alice");
        var orig = new Order(10UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 10m, firmId: "FIRM01");
        book.TryAdd(orig);
        ownership.Register(10UL, owner);
        Assert.True(reg.TryAdd(BuildIntent(10UL, 11UL, "alice", "FIRM01")));

        var (listener, read) = ListenMismatch();
        proc.Apply(11UL, ExecKind.Replaced, leaves: 200, cumQty: 0, lastQty: 0, lastPx: 0m,
            rejectReason: null, origClOrdId: 10UL, envelopeFirmId: "FIRM02");
        listener.Dispose();

        Assert.Equal(1, read());
        // Intent NOT consumed.
        Assert.True(reg.TryConsume(11UL, out var stillThere));
        Assert.NotNull(stillThere);
        // Original NOT terminalised.
        Assert.Equal(OrderStatus.PendingNew, orig.Status);
        // Replacement NOT booked.
        Assert.False(book.TryGet(11UL, out _));
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void CancelAsReplace_withMismatchingFirm_doesNotConsumeIntent_orMutateOriginal()
    {
        // Issue #241 priority-lost: venue sends Canceled under the new
        // ClOrdID; processor would normally funnel through
        // ApplyReplaceAccepted via the third intercept. Cross-firm
        // envelope must short-circuit BEFORE TryConsume.
        var (proc, ownership, book, reg, sink) = BuildProcWithReg();
        var owner = new EndClientId("alice");
        var orig = new Order(777UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 10m, firmId: "FIRM01");
        book.TryAdd(orig);
        ownership.Register(777UL, owner);
        Assert.True(reg.TryAdd(BuildIntent(777UL, 778UL, "alice", "FIRM01", newQty: 150)));

        var (listener, read) = ListenMismatch();
        proc.Apply(778UL, ExecKind.Canceled, leaves: 0, cumQty: 0, lastQty: 0, lastPx: 0m,
            rejectReason: null, origClOrdId: 777UL, envelopeFirmId: "FIRM02");
        listener.Dispose();

        Assert.Equal(1, read());
        Assert.True(reg.TryConsume(778UL, out var stillThere));
        Assert.NotNull(stillThere);
        Assert.Equal(OrderStatus.PendingNew, orig.Status);
        Assert.False(book.TryGet(778UL, out _));
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void Rejected_forPendingReplacement_withMatchingFirm_stillFlowsThroughReplaceReject()
    {
        // Happy-path regression: matching FirmId on a Rejected ER for
        // an in-flight modify still consumes the intent and routes to
        // ApplyReplaceRejected. Confirms the hoisted guard is permissive
        // when the firms agree.
        var (proc, ownership, book, reg, sink) = BuildProcWithReg();
        var owner = new EndClientId("alice");
        var orig = new Order(20UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 10m, firmId: "FIRM01");
        book.TryAdd(orig);
        ownership.Register(20UL, owner);
        Assert.True(reg.TryAdd(BuildIntent(20UL, 21UL, "alice", "FIRM01")));

        proc.Apply(21UL, ExecKind.Rejected, leaves: 0, cumQty: 0, lastQty: 0, lastPx: 0m,
            rejectReason: "tick-out-of-range", envelopeFirmId: "FIRM01");

        // Intent consumed by the replace-reject branch.
        Assert.False(reg.TryGet(21UL, out _));
        Assert.Equal(OrderStatus.PendingNew, orig.Status);
    }
}
