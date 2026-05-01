using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Up = B3.EntryPoint.Client.Models;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Pure translation tests for <see cref="B3EntryPointClientGateway.Translate"/>.
/// No TCP, no upstream client construction — just feed each
/// <see cref="Up.EntryPointEvent"/> subtype and assert the resulting
/// <see cref="ExecutionReportEnvelope"/> shape.
/// </summary>
public class B3EntryPointClientGatewayTranslationTests
{
    private static T WithBase<T>(T ev) where T : Up.EntryPointEvent => ev;

    [Fact]
    public void OrderAccepted_TranslatesToNew()
    {
        var ev = new Up.OrderAccepted
        {
            SeqNum = 1, SendingTime = DateTimeOffset.UtcNow,
            ClOrdID = new Up.ClOrdID(42UL),
            OrderId = 999UL,
            OrderStatus = Up.OrderStatus.New,
            SecurityId = 4321UL,
            Side = Up.Side.Buy,
            LeavesQty = 100UL,
            CumQty = 0UL,
        };

        var env = B3EntryPointClientGateway.Translate(ev);

        Assert.NotNull(env);
        Assert.Equal(EpExecType.New, env!.ExecType);
        Assert.Equal(42UL, env.ClOrdId);
        Assert.Equal(0UL, env.OrigClOrdId);
        Assert.Equal(100, env.LeavesQuantity);
        Assert.Equal(0, env.CumulativeQuantity);
    }

    [Fact]
    public void OrderTrade_PartialAndFull_DiscriminatedByStatus()
    {
        var partial = new Up.OrderTrade
        {
            SeqNum = 1, SendingTime = DateTimeOffset.UtcNow,
            ClOrdID = new Up.ClOrdID(5UL), OrderId = 1, TradeId = 1,
            OrderStatus = Up.OrderStatus.PartiallyFilled,
            LastPx = 30.5m, LastQty = 30UL,
            LeavesQty = 70UL, CumQty = 30UL,
        };
        var full = partial with { OrderStatus = Up.OrderStatus.Filled, LeavesQty = 0UL, CumQty = 100UL };

        var p = B3EntryPointClientGateway.Translate(partial);
        var f = B3EntryPointClientGateway.Translate(full);

        Assert.Equal(EpExecType.PartialFill, p!.ExecType);
        Assert.Equal(30, p.LastQuantity);
        Assert.Equal(30.5m, p.LastPrice);
        Assert.Equal(EpExecType.Fill, f!.ExecType);
    }

    [Fact]
    public void OrderCancelled_CarriesOrigClOrdId()
    {
        var ev = new Up.OrderCancelled
        {
            SeqNum = 1, SendingTime = DateTimeOffset.UtcNow,
            ClOrdID = new Up.ClOrdID(99UL),
            OrigClOrdID = new Up.ClOrdID(42UL),
            OrderId = 1, OrderStatus = Up.OrderStatus.Cancelled,
        };

        var env = B3EntryPointClientGateway.Translate(ev);

        Assert.NotNull(env);
        Assert.Equal(EpExecType.Canceled, env!.ExecType);
        Assert.Equal(99UL, env.ClOrdId);
        Assert.Equal(42UL, env.OrigClOrdId);
    }

    [Fact]
    public void OrderModified_CarriesOrigClOrdId()
    {
        var ev = new Up.OrderModified
        {
            SeqNum = 1, SendingTime = DateTimeOffset.UtcNow,
            ClOrdID = new Up.ClOrdID(101UL),
            OrigClOrdID = new Up.ClOrdID(42UL),
            OrderId = 1, OrderStatus = Up.OrderStatus.Replaced,
            LeavesQty = 200UL, CumQty = 0UL,
        };

        var env = B3EntryPointClientGateway.Translate(ev);

        Assert.NotNull(env);
        Assert.Equal(EpExecType.Replaced, env!.ExecType);
        Assert.Equal(101UL, env.ClOrdId);
        Assert.Equal(42UL, env.OrigClOrdId);
        Assert.Equal(200, env.LeavesQuantity);
    }

    [Fact]
    public void OrderRejected_PreservesReason_FallsBackToCode()
    {
        var withReason = new Up.OrderRejected
        {
            SeqNum = 1, SendingTime = DateTimeOffset.UtcNow,
            ClOrdID = new Up.ClOrdID(7UL), OrderId = 0,
            RejectCode = 99, Reason = "limit breached",
        };
        var noReason = withReason with { Reason = null };

        var a = B3EntryPointClientGateway.Translate(withReason);
        var b = B3EntryPointClientGateway.Translate(noReason);

        Assert.Equal("limit breached", a!.RejectReason);
        Assert.Equal("reject_code=99", b!.RejectReason);
        Assert.Equal(EpExecType.Rejected, a.ExecType);
    }

    [Fact]
    public void BusinessReject_TranslatesToNullEnvelope()
    {
        var ev = new Up.BusinessReject
        {
            SeqNum = 1, SendingTime = DateTimeOffset.UtcNow,
            RefSeqNum = 12345UL, RejectReason = 1, Text = "unknown ClOrdID",
        };

        Assert.Null(B3EntryPointClientGateway.Translate(ev));
    }

    [Fact]
    public void ProcessorAppliesCancelToOriginalOrder()
    {
        // End-to-end: simulate the processor receiving an envelope with
        // OrigClOrdId set (as the real adapter would emit for a cancel).
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var owner = new EndClientId("alice");
        var orig = new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM-A");
        book.TryAdd(orig);
        ownership.Register(42UL, owner);

        var sink = new CapturingSink();
        var proc = new ExecutionReportProcessor(ownership, book, positions, sink, NullLogger<ExecutionReportProcessor>.Instance);

        // ER carries the cancel-side ClOrdID 99 plus OrigClOrdId 42.
        proc.Apply(clOrdId: 99UL, ExecKind.Canceled, 0, 0, 0, 0m, null, origClOrdId: 42UL);

        Assert.Equal(OrderStatus.Cancelled, orig.Status);
        var ev = Assert.Single(sink.Events);
        Assert.Equal(42UL, ev.ClOrdId); // event surfaces the *original* ClOrdID, not the cancel side
    }

    private sealed class CapturingSink : IExecutionEventSink
    {
        public readonly List<ExecutionEvent> Events = new();
        public void Publish(ExecutionEvent ev) => Events.Add(ev);
    }
}
