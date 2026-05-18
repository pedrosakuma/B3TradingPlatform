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
}
