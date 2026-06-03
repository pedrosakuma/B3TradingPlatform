using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #512. The runtime post-connect session-roll reactor reaps un-acked
/// PendingNew for a firm whose venue session rolled (cold-resume fallback
/// bump), while leaving Working orders and other firms untouched. Mirrors
/// the boot-time #380/#504 baseline reconcile via the shared
/// <see cref="FirmSessionRollReconciliation"/> helper.
/// </summary>
public class ConnectSessionRollReactorTests
{
    private static Order MakeOrder(ulong clOrdId, string firmId, string owner = "alice") =>
        new(clOrdId, new EndClientId(owner), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 10, 1m, firmId);

    private static EventDispatcher Dispatcher() => new(new NullEventStore());

    [Fact]
    public void OnSessionRolledAtConnect_CancelsPendingNew_ForFirm_KeepsWorking()
    {
        var book = new WorkingOrderBook();

        var pendingA1 = MakeOrder(1UL, "FIRM_A");
        var pendingA2 = MakeOrder(2UL, "FIRM_A");
        var workingA = MakeOrder(3UL, "FIRM_A");
        workingA.MarkWorking();
        book.TryAdd(pendingA1);
        book.TryAdd(pendingA2);
        book.TryAdd(workingA);

        var reactor = new PendingNewReapingConnectRollReactor(
            book, Dispatcher(), NullLogger<PendingNewReapingConnectRollReactor>.Instance);

        reactor.OnSessionRolledAtConnect("FIRM_A", fromVerId: 7, toVerId: 8);

        Assert.Equal(OrderStatus.Cancelled, pendingA1.Status);
        Assert.Equal(OrderStatus.Cancelled, pendingA2.Status);
        Assert.Equal(OrderStatus.Working, workingA.Status);
    }

    [Fact]
    public void OnSessionRolledAtConnect_DoesNotTouchOtherFirms()
    {
        var book = new WorkingOrderBook();
        var pendingA = MakeOrder(1UL, "FIRM_A");
        var pendingB = MakeOrder(2UL, "FIRM_B");
        book.TryAdd(pendingA);
        book.TryAdd(pendingB);

        var reactor = new PendingNewReapingConnectRollReactor(
            book, Dispatcher(), NullLogger<PendingNewReapingConnectRollReactor>.Instance);

        reactor.OnSessionRolledAtConnect("FIRM_A", 5, 6);

        Assert.Equal(OrderStatus.Cancelled, pendingA.Status);
        Assert.Equal(OrderStatus.PendingNew, pendingB.Status);
    }

    [Fact]
    public void Helper_ReturnsCancelledCount()
    {
        var book = new WorkingOrderBook();
        book.TryAdd(MakeOrder(1UL, "FIRM_A"));
        book.TryAdd(MakeOrder(2UL, "FIRM_A"));
        var working = MakeOrder(3UL, "FIRM_A");
        working.MarkWorking();
        book.TryAdd(working);

        var count = FirmSessionRollReconciliation.CancelPendingNewForRolledFirm(
            book, "FIRM_A", 1, 2, NullLogger.Instance);

        Assert.Equal(2, count);
    }

    [Fact]
    public void Helper_NoPendingNew_ReturnsZero_AndKeepsWorking()
    {
        var book = new WorkingOrderBook();
        var working = MakeOrder(1UL, "FIRM_A");
        working.MarkWorking();
        book.TryAdd(working);

        var count = FirmSessionRollReconciliation.CancelPendingNewForRolledFirm(
            book, "FIRM_A", 1, 2, NullLogger.Instance);

        Assert.Equal(0, count);
        Assert.Equal(OrderStatus.Working, working.Status);
    }

    [Fact]
    public void Helper_UnknownFirm_ReturnsZero()
    {
        var book = new WorkingOrderBook();
        book.TryAdd(MakeOrder(1UL, "FIRM_A"));

        var count = FirmSessionRollReconciliation.CancelPendingNewForRolledFirm(
            book, "FIRM_X", 1, 2, NullLogger.Instance);

        Assert.Equal(0, count);
    }
}
