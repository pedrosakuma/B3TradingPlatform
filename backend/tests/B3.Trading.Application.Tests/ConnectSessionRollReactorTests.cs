using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #512/#644. The runtime post-connect session-roll reactor preserves un-acked
/// PendingNew for a firm whose venue session rolled while staling working
/// projections and leaving other firms untouched. Mirrors
/// the boot-time #380/#504 baseline reconcile via the shared
/// <see cref="FirmSessionRollReconciliation"/> helper.
/// </summary>
public class ConnectSessionRollReactorTests
{
    private static Order MakeOrder(ulong clOrdId, string firmId, string owner = "alice") =>
        new(clOrdId, new EndClientId(owner), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 10, 1m, firmId);

    private static EventDispatcher Dispatcher() => new(new NullEventStore());

    [Fact]
    public void OnSessionRolled_WithStaleness_PreservesPendingNew_AndStalesWorkingAndPartiallyFilled()
    {
        var book = new WorkingOrderBook();
        var pending = MakeOrder(1UL, "FIRM_A");
        var working = MakeOrder(2UL, "FIRM_A");
        working.MarkWorking();
        var partial = MakeOrder(3UL, "FIRM_A");
        partial.MarkWorking();
        partial.ApplyCumulativeFill(5); // 5 of 10 → PartiallyFilled
        var otherFirm = MakeOrder(4UL, "FIRM_B");
        otherFirm.MarkWorking();
        book.TryAdd(pending);
        book.TryAdd(working);
        book.TryAdd(partial);
        book.TryAdd(otherFirm);

        var dispatcher = Dispatcher();
        var staleness = new OrderStalenessService(dispatcher, book);
        var reactor = new PendingNewReapingConnectRollReactor(
            book, dispatcher, NullLogger<PendingNewReapingConnectRollReactor>.Instance, staleness);

        reactor.OnSessionRolled("FIRM_A", fromVerId: 7, toVerId: 8);

        Assert.Equal(OrderStatus.PendingNew, pending.Status);
        Assert.False(pending.IsStale);
        // Working + PartiallyFilled flagged stale (non-destructive — status kept).
        Assert.Equal(OrderStatus.Working, working.Status);
        Assert.True(working.IsStale);
        Assert.Equal("session_rolled:7-8", working.StaleReason);
        Assert.Equal(OrderStatus.PartiallyFilled, partial.Status);
        Assert.True(partial.IsStale);
        Assert.Equal("session_rolled:7-8", partial.StaleReason);
        // Other firm untouched.
        Assert.False(otherFirm.IsStale);
        Assert.Equal(OrderStatus.Working, otherFirm.Status);
    }

    [Fact]
    public void OnSessionRolled_NoStalenessService_PreservesPendingNew_KeepsWorking()
    {
        var book = new WorkingOrderBook();
        var pending = MakeOrder(1UL, "FIRM_A");
        var working = MakeOrder(2UL, "FIRM_A");
        working.MarkWorking();
        book.TryAdd(pending);
        book.TryAdd(working);

        var reactor = new PendingNewReapingConnectRollReactor(
            book, Dispatcher(), NullLogger<PendingNewReapingConnectRollReactor>.Instance, staleness: null);

        reactor.OnSessionRolled("FIRM_A", 7, 8);

        Assert.Equal(OrderStatus.PendingNew, pending.Status);
        Assert.Equal(OrderStatus.Working, working.Status);
        Assert.False(working.IsStale);
    }

    [Fact]
    public void OnSessionRolled_StalingPhaseThrows_RethrowsAfterPreservingPendingNew()
    {
        var book = new WorkingOrderBook();
        var pending = MakeOrder(1UL, "FIRM_A");
        var working = MakeOrder(2UL, "FIRM_A");
        working.MarkWorking();
        book.TryAdd(pending);
        book.TryAdd(working);

        // Shared dispatcher whose WAL append throws — Phase 1 reap (in-memory,
        // under RunExclusive, no append) still completes; Phase 2 staling
        // (per-order Dispatch → Append) fails.
        var dispatcher = new EventDispatcher(new ThrowingEventStore());
        var staleness = new OrderStalenessService(dispatcher, book);
        var reactor = new PendingNewReapingConnectRollReactor(
            book, dispatcher, NullLogger<PendingNewReapingConnectRollReactor>.Instance, staleness);

        Assert.ThrowsAny<Exception>(() => reactor.OnSessionRolled("FIRM_A", 7, 8));

        Assert.Equal(OrderStatus.PendingNew, pending.Status);
    }

    [Fact]
    public void Helper_SessionRolledStaleReason_FormatsFromTo()
    {
        Assert.Equal("session_rolled:7-8",
            FirmSessionRollReconciliation.SessionRolledStaleReason(7, 8));
    }

    [Fact]
    public void OnSessionRolled_PreservesPendingNew_ForFirm_KeepsWorking()
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

        reactor.OnSessionRolled("FIRM_A", fromVerId: 7, toVerId: 8);

        Assert.Equal(OrderStatus.PendingNew, pendingA1.Status);
        Assert.Equal(OrderStatus.PendingNew, pendingA2.Status);
        Assert.Equal(OrderStatus.Working, workingA.Status);
    }

    [Fact]
    public void OnSessionRolled_DoesNotTouchOtherFirms()
    {
        var book = new WorkingOrderBook();
        var pendingA = MakeOrder(1UL, "FIRM_A");
        var pendingB = MakeOrder(2UL, "FIRM_B");
        book.TryAdd(pendingA);
        book.TryAdd(pendingB);

        var reactor = new PendingNewReapingConnectRollReactor(
            book, Dispatcher(), NullLogger<PendingNewReapingConnectRollReactor>.Instance);

        reactor.OnSessionRolled("FIRM_A", 5, 6);

        Assert.Equal(OrderStatus.PendingNew, pendingA.Status);
        Assert.Equal(OrderStatus.PendingNew, pendingB.Status);
    }

    [Fact]
    public async Task OnSessionRolled_PendingNewReservationRemainsHeldWithoutVenueEvidence()
    {
        var options = new RiskOptions();
        options.Margin.Enabled = true;
#pragma warning disable CS0618
        options.Margin.Initial["alice"] = 1_000m;
#pragma warning restore CS0618
        var margin = new ReserveOnSubmitMarginProvider(
            new StaticOptionsMonitor<RiskOptions>(options),
            NullLogger<ReserveOnSubmitMarginProvider>.Instance);
        Assert.True((await margin.TryReserveAsync(
            1,
            new RiskContext(
                new EndClientId("alice"),
                "FIRM_A",
                "PETR4",
                OrderSide.Buy,
                OrderType.Limit,
                100,
                10m),
            CancellationToken.None)).Approved);

        var book = new WorkingOrderBook();
        var pending = MakeOrder(1, "FIRM_A");
        book.TryAdd(pending);
        var reactor = new PendingNewReapingConnectRollReactor(
            book,
            Dispatcher(),
            NullLogger<PendingNewReapingConnectRollReactor>.Instance);

        reactor.OnSessionRolled("FIRM_A", 7, 8);

        Assert.Equal(OrderStatus.PendingNew, pending.Status);
        Assert.Equal(0m, margin.AvailableForTesting("alice"));
    }

    [Fact]
    public void Helper_ReturnsPreservedCount()
    {
        var book = new WorkingOrderBook();
        book.TryAdd(MakeOrder(1UL, "FIRM_A"));
        book.TryAdd(MakeOrder(2UL, "FIRM_A"));
        var working = MakeOrder(3UL, "FIRM_A");
        working.MarkWorking();
        book.TryAdd(working);

        var count = FirmSessionRollReconciliation.PreservePendingNewForRolledFirm(
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

        var count = FirmSessionRollReconciliation.PreservePendingNewForRolledFirm(
            book, "FIRM_A", 1, 2, NullLogger.Instance);

        Assert.Equal(0, count);
        Assert.Equal(OrderStatus.Working, working.Status);
    }

    [Fact]
    public void Helper_UnknownFirm_ReturnsZero()
    {
        var book = new WorkingOrderBook();
        book.TryAdd(MakeOrder(1UL, "FIRM_A"));

        var count = FirmSessionRollReconciliation.PreservePendingNewForRolledFirm(
            book, "FIRM_X", 1, 2, NullLogger.Instance);

        Assert.Equal(0, count);
    }

    private sealed class ThrowingEventStore : IEventStore
    {
        public long CurrentSeq => 0;
        public long Append(WalEvent evt) => throw new IOException("wal down");
        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
            => throw new IOException("wal down");
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
