using B3.Trading.Application.Risk;
using B3.Trading.Application;
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
    }

    [Fact]
    public async Task Gateway_CancelReplace_ForwardsToClient()
    {
        var client = new MockEntryPointClient();
        var gateway = new EntryPointClientGateway(client, "FIRM-A");
        var original = new Order(100UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);

        await gateway.CancelReplaceAsync(original, 101UL, 200, 30m, CancellationToken.None);

        var sent = Assert.Single(client.SubmittedReplaces);
        Assert.Equal(100UL, sent.OriginalClOrdId);
        Assert.Equal(101UL, sent.NewClOrdId);
        Assert.Equal(4321UL, sent.SecurityId);
        Assert.Equal(EpSide.Buy, sent.Side);
        Assert.Equal(200, sent.NewQuantity);
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
        var dispatcher = new EventDispatcher(new NullEventStore());
        using var router = new EntryPointExecutionReportRouter(client, proc, dispatcher);

        client.EmitExecutionReport(new ExecutionReportEnvelope(1UL, EpExecType.Fill, 0, 100, 100, 30m, null));

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Single(sink.Events);
    }

    private sealed class TestSink : IExecutionEventSink
    {
        public readonly List<ExecutionEvent> Events = new();
        public void Publish(ExecutionEvent ev) => Events.Add(ev);
    }
}
