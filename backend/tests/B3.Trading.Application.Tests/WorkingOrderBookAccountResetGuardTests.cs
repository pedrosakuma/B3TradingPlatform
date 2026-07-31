using B3.Trading.Application;
using B3.Trading.Domain;
using Xunit;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #671/#753 (RFC: admin account reset, PR 3, code-review addendum
/// #1). Coverage for <see cref="WorkingOrderBook.CountNonTerminalForOwnerAndFirmIncludingStale"/>
/// — the reset-specific guard query that, unlike
/// <see cref="WorkingOrderBook.CountOpenForOwnerAndFirm"/> (used by
/// <c>MaxOpenOrdersCheck</c>'s risk budget), must NOT exempt stale
/// orders: a stale order's true venue-side disposition can no longer
/// be positively confirmed, so <c>AdminEndpoints.HandleAccountReset</c>
/// must fail closed on it exactly like any other working order.
/// </summary>
public class WorkingOrderBookAccountResetGuardTests
{
    private static Order NewOrder(ulong clOrdId, EndClientId owner, string firmId, string symbol = "PETR4") =>
        new(clOrdId, owner, symbol, securityId: clOrdId, side: OrderSide.Buy, type: OrderType.Limit,
            quantity: 100, price: 20m, firmId: firmId);

    [Fact]
    public void CountNonTerminalIncludingStale_StaleWorkingOrder_IsCounted()
    {
        var book = new WorkingOrderBook();
        var alice = new EndClientId("alice");
        var order = NewOrder(1, alice, "FIRM01");
        Assert.True(book.TryAdd(order));
        order.MarkWorking();
        Assert.True(order.MarkStale("inbound_gap:1-2", DateTimeOffset.UtcNow));

        // The reset-specific guard MUST see the stale order...
        Assert.Equal(1, book.CountNonTerminalForOwnerAndFirmIncludingStale("FIRM01", alice));

        // ...while the pre-existing risk-budget count (unchanged
        // semantics — must not regress) continues to EXCLUDE it.
        Assert.Equal(0, book.CountOpenForOwnerAndFirm("FIRM01", alice));
    }

    [Fact]
    public void CountNonTerminalIncludingStale_NonStaleWorkingOrder_IsCountedByBothMethods()
    {
        var book = new WorkingOrderBook();
        var alice = new EndClientId("alice");
        Assert.True(book.TryAdd(NewOrder(2, alice, "FIRM01")));

        Assert.Equal(1, book.CountNonTerminalForOwnerAndFirmIncludingStale("FIRM01", alice));
        Assert.Equal(1, book.CountOpenForOwnerAndFirm("FIRM01", alice));
    }

    [Fact]
    public void CountNonTerminalIncludingStale_TerminalOrder_IsNeverCounted()
    {
        var book = new WorkingOrderBook();
        var alice = new EndClientId("alice");
        var order = NewOrder(3, alice, "FIRM01");
        Assert.True(book.TryAdd(order));
        order.MarkWorking();
        Assert.True(order.MarkStale("inbound_gap:3-4", DateTimeOffset.UtcNow));
        order.ApplyCumulativeFill(100); // fully filled -> terminal, even though also stale

        Assert.Equal(0, book.CountNonTerminalForOwnerAndFirmIncludingStale("FIRM01", alice));
        Assert.Equal(0, book.CountOpenForOwnerAndFirm("FIRM01", alice));
    }

    [Fact]
    public void CountNonTerminalIncludingStale_PartiallyFilledStaleOrder_IsCounted()
    {
        var book = new WorkingOrderBook();
        var alice = new EndClientId("alice");
        var order = NewOrder(4, alice, "FIRM01");
        Assert.True(book.TryAdd(order));
        order.ApplyCumulativeFill(40); // partial -> PartiallyFilled, non-terminal
        Assert.True(order.MarkStale("inbound_gap:5-6", DateTimeOffset.UtcNow));

        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
        Assert.Equal(1, book.CountNonTerminalForOwnerAndFirmIncludingStale("FIRM01", alice));
        Assert.Equal(0, book.CountOpenForOwnerAndFirm("FIRM01", alice));
    }

    /// <summary>
    /// Tenant isolation: a stale order under a DIFFERENT firm (same
    /// end-client id, same login) or a DIFFERENT owner must never
    /// count against another tenant's reset guard.
    /// </summary>
    [Fact]
    public void CountNonTerminalIncludingStale_FirmIsolation_DoesNotLeakAcrossFirms()
    {
        var book = new WorkingOrderBook();
        var shared = new EndClientId("shared-name");
        var order1 = NewOrder(5, shared, "FIRM01");
        var order2 = NewOrder(6, shared, "FIRM02");
        Assert.True(book.TryAdd(order1));
        Assert.True(book.TryAdd(order2));
        order1.MarkWorking();
        order2.MarkWorking();
        Assert.True(order1.MarkStale("inbound_gap:7-8", DateTimeOffset.UtcNow));
        Assert.True(order2.MarkStale("inbound_gap:9-10", DateTimeOffset.UtcNow));

        Assert.Equal(1, book.CountNonTerminalForOwnerAndFirmIncludingStale("FIRM01", shared));
        Assert.Equal(1, book.CountNonTerminalForOwnerAndFirmIncludingStale("FIRM02", shared));

        // A third firm with no orders at all for this end-client sees zero.
        Assert.Equal(0, book.CountNonTerminalForOwnerAndFirmIncludingStale("FIRM03", shared));
    }

    /// <summary>
    /// Tenant isolation: a stale order for a DIFFERENT end-client
    /// (same firm) must not count against this owner's reset guard.
    /// Alice's own (non-stale, still-PendingNew) order continues to
    /// count against her own guard regardless of Bob's staleness —
    /// isolation means "no cross-owner leakage", not "no self count".
    /// </summary>
    [Fact]
    public void CountNonTerminalIncludingStale_OwnerIsolation_DoesNotLeakAcrossEndClients()
    {
        var book = new WorkingOrderBook();
        var alice = new EndClientId("alice");
        var bob = new EndClientId("bob");
        var carol = new EndClientId("carol"); // no orders at all
        var aliceOrder = NewOrder(7, alice, "FIRM01");
        var bobOrder = NewOrder(8, bob, "FIRM01");
        Assert.True(book.TryAdd(aliceOrder));
        Assert.True(book.TryAdd(bobOrder));
        bobOrder.MarkWorking();
        Assert.True(bobOrder.MarkStale("inbound_gap:11-12", DateTimeOffset.UtcNow));

        // Alice's own (unrelated, non-stale) order still counts for
        // her own guard...
        Assert.Equal(1, book.CountNonTerminalForOwnerAndFirmIncludingStale("FIRM01", alice));
        // ...but Bob's staleness does not inflate it further, and an
        // end-client with no orders at all sees zero.
        Assert.Equal(1, book.CountNonTerminalForOwnerAndFirmIncludingStale("FIRM01", bob));
        Assert.Equal(0, book.CountNonTerminalForOwnerAndFirmIncludingStale("FIRM01", carol));
    }
}
