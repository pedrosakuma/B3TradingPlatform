using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

public class ExecutionReportProcessorCashLedgerTests
{
    private sealed class NullSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent ev) { }
    }

    private static (ExecutionReportProcessor Proc, OrderOwnershipMap Own, WorkingOrderBook Book, PositionKeeper Pos, CashLedger Cash) Build()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var cash = new CashLedger();
        var proc = new ExecutionReportProcessor(
            ownership, book, positions, new NullSink(), new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            algoSignals: null,
            cash: cash);
        return (proc, ownership, book, positions, cash);
    }

    [Fact]
    public void BuyFill_DebitsCash()
    {
        var (proc, ownership, book, _, cash) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        proc.Apply(1UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 30m, rejectReason: null);

        Assert.Equal(-3000m, cash.GetAvailable(owner));
    }

    [Fact]
    public void SellFill_CreditsCash()
    {
        var (proc, ownership, book, _, cash) = Build();
        var owner = new EndClientId("bob");
        var order = new Order(2UL, owner, "VALE3", 2UL, OrderSide.Sell, OrderType.Limit, 50, 65m);
        book.TryAdd(order);
        ownership.Register(2UL, owner);

        proc.Apply(2UL, ExecKind.Fill, leaves: 0, cumQty: 50, lastQty: 50, lastPx: 65m, rejectReason: null);

        Assert.Equal(3250m, cash.GetAvailable(owner));
    }

    [Fact]
    public void PartialFill_BooksOnDelta()
    {
        var (proc, ownership, book, _, cash) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(3UL, owner, "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(3UL, owner);

        proc.Apply(3UL, ExecKind.PartialFill, leaves: 60, cumQty: 40, lastQty: 40, lastPx: 30m, rejectReason: null);
        proc.Apply(3UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 60, lastPx: 31m, rejectReason: null);

        // 40@30 + 60@31 = 1200 + 1860 = 3060 debit
        Assert.Equal(-3060m, cash.GetAvailable(owner));
    }

    [Fact]
    public void DuplicateFill_IsIdempotent()
    {
        // ER processor dedups by cumulative quantity — replaying the
        // same Fill twice must not double-debit cash.
        var (proc, ownership, book, _, cash) = Build();
        var owner = new EndClientId("alice");
        var order = new Order(4UL, owner, "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        book.TryAdd(order);
        ownership.Register(4UL, owner);

        proc.Apply(4UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 30m, rejectReason: null);
        proc.Apply(4UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 30m, rejectReason: null);

        Assert.Equal(-3000m, cash.GetAvailable(owner));
    }

    [Fact]
    public void NullCashLedger_DoesNotThrow()
    {
        // Test contexts that omit the CashLedger should still process
        // fills exactly as before — production DI always injects it.
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var proc = new ExecutionReportProcessor(
            ownership, book, positions, new NullSink(), new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        var owner = new EndClientId("alice");
        var order = new Order(5UL, owner, "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 10, 1m);
        book.TryAdd(order);
        ownership.Register(5UL, owner);

        proc.Apply(5UL, ExecKind.Fill, leaves: 0, cumQty: 10, lastQty: 10, lastPx: 1m, rejectReason: null);

        Assert.Equal(10, positions.GetOrCreate(owner, "PETR4").NetQuantity);
    }
}
