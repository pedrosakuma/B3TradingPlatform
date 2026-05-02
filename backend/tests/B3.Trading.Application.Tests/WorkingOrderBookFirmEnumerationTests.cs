using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application.Tests;

public class WorkingOrderBookFirmEnumerationTests
{
    private static Order MakeOrder(ulong clOrdId, string firmId, string owner = "alice") =>
        new(clOrdId, new EndClientId(owner), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 10, 1m, firmId);

    [Fact]
    public void EnumerateForFirm_ReturnsOnlyMatchingFirm()
    {
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(MakeOrder(1UL, "FIRM_A")));
        Assert.True(book.TryAdd(MakeOrder(2UL, "FIRM_A")));
        Assert.True(book.TryAdd(MakeOrder(3UL, "FIRM_B")));

        var a = book.EnumerateForFirm("FIRM_A");
        var b = book.EnumerateForFirm("FIRM_B");

        Assert.Equal(new ulong[] { 1, 2 }, a.Select(o => o.ClOrdId).OrderBy(x => x));
        Assert.Equal(new ulong[] { 3 }, b.Select(o => o.ClOrdId));
    }

    [Fact]
    public void EnumerateForFirm_UnknownFirm_ReturnsEmpty()
    {
        var book = new WorkingOrderBook();
        book.TryAdd(MakeOrder(1UL, "FIRM_A"));

        Assert.Empty(book.EnumerateForFirm("FIRM_X"));
    }

    [Fact]
    public void EnumerateForFirm_ExcludesTerminalByDefault_IncludesWhenRequested()
    {
        var book = new WorkingOrderBook();
        var working = MakeOrder(1UL, "FIRM_A");
        var filled = MakeOrder(2UL, "FIRM_A");
        var cancelled = MakeOrder(3UL, "FIRM_A");
        var rejected = MakeOrder(4UL, "FIRM_A");
        book.TryAdd(working);
        book.TryAdd(filled);
        book.TryAdd(cancelled);
        book.TryAdd(rejected);

        filled.ApplyFill(10);     // -> Filled
        cancelled.MarkCancelled();
        rejected.MarkRejected();

        var active = book.EnumerateForFirm("FIRM_A");
        var all = book.EnumerateForFirm("FIRM_A", includeTerminal: true);

        Assert.Equal(new ulong[] { 1 }, active.Select(o => o.ClOrdId));
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public void Restore_RebuildsFirmIndex()
    {
        var book = new WorkingOrderBook();
        // Pre-populate something to ensure Restore clears the index too.
        book.TryAdd(MakeOrder(99UL, "STALE_FIRM"));

        var snaps = new[]
        {
            new OrderSnapshot(10UL, "alice", "PETR4", 4321UL, "Buy", "Limit", 5, 1m, 5, 0, "Working", "FIRM_A"),
            new OrderSnapshot(11UL, "bob",   "PETR4", 4321UL, "Sell","Limit", 7, 2m, 7, 0, "PendingNew", "FIRM_B"),
        };
        book.Restore(snaps);

        Assert.Empty(book.EnumerateForFirm("STALE_FIRM"));
        Assert.Equal(new ulong[] { 10 }, book.EnumerateForFirm("FIRM_A").Select(o => o.ClOrdId));
        Assert.Equal(new ulong[] { 11 }, book.EnumerateForFirm("FIRM_B").Select(o => o.ClOrdId));
    }

    [Fact]
    public void EnumerateForFirm_StableUnderConcurrentAdds()
    {
        var book = new WorkingOrderBook();
        for (ulong i = 1; i <= 100; i++)
            book.TryAdd(MakeOrder(i, "FIRM_A"));

        // Snapshot, then add more. Snapshot must not throw and must not include
        // post-snapshot additions deterministically — but at minimum, enumeration
        // should never throw and the snapshot should be reasonable.
        var snapshot = book.EnumerateForFirm("FIRM_A");
        Parallel.For(101, 201, i => book.TryAdd(MakeOrder((ulong)i, "FIRM_A")));

        // The snapshot returned earlier is a materialized list, so its count is fixed.
        Assert.Equal(100, snapshot.Count);

        // A fresh enumeration after the parallel adds sees all 200.
        var after = book.EnumerateForFirm("FIRM_A");
        Assert.Equal(200, after.Count);
    }

    [Fact]
    public void EnumerateForFirm_NullOrEmpty_Throws()
    {
        var book = new WorkingOrderBook();
        Assert.Throws<ArgumentException>(() => book.EnumerateForFirm(""));
        Assert.Throws<ArgumentException>(() => book.EnumerateForFirm("   "));
        Assert.Throws<ArgumentNullException>(() => book.EnumerateForFirm(null!));
    }
}
