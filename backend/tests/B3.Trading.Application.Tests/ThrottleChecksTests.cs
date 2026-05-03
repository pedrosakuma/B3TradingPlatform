using B3.Trading.Application;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Application.Risk.Checks;
using B3.Trading.Domain;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests;

public class ThrottleChecksTests
{
    private static IOptionsMonitor<RiskOptions> Wrap(RiskOptions o) => new StaticOptionsMonitor<RiskOptions>(o);

    private static RiskContext Ctx(
        string owner = "alice", string firm = "default", string symbol = "PETR4",
        OrderSide side = OrderSide.Buy, OrderType type = OrderType.Limit,
        long qty = 100, decimal? price = 30m) =>
        new(new EndClientId(owner), firm, symbol, side, type, qty, price);

    private sealed class StubRef : IReferencePrice
    {
        private readonly Dictionary<string, decimal> _prices;
        public StubRef(params (string, decimal)[] entries) =>
            _prices = entries.ToDictionary(e => e.Item1, e => e.Item2, StringComparer.OrdinalIgnoreCase);
        public bool TryGet(string symbol, out decimal price) => _prices.TryGetValue(symbol, out price);
    }

    // ────────────────── SlidingWindowLedger ──────────────────

    [Fact]
    public void Ledger_SumIsZeroForUnknownKey()
    {
        var ledger = new SlidingWindowLedger(new TestClock(DateTimeOffset.UtcNow));
        Assert.Equal(0m, ledger.Sum("missing", TimeSpan.FromSeconds(60)));
        Assert.Equal(0, ledger.Count("missing", TimeSpan.FromSeconds(60)));
        Assert.Equal(0, ledger.ActiveBucketCount); // no allocation
    }

    [Fact]
    public void Ledger_PrunesEntriesOutsideWindow()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var ledger = new SlidingWindowLedger(clock);
        ledger.Append("k", 100m);
        clock.Advance(TimeSpan.FromSeconds(30));
        ledger.Append("k", 50m);
        Assert.Equal(150m, ledger.Sum("k", TimeSpan.FromSeconds(60)));
        // Advance so the first entry falls out (it was at t0; cutoff = t0+61 - 60 = t0+1).
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(50m, ledger.Sum("k", TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void Ledger_SweepRemovesEmptyBuckets()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var ledger = new SlidingWindowLedger(clock);
        ledger.Append("k", 1m);
        Assert.Equal(1, ledger.ActiveBucketCount);
        clock.Advance(TimeSpan.FromSeconds(120));
        var removed = ledger.SweepEmptyBuckets(TimeSpan.FromSeconds(60));
        Assert.Equal(1, removed);
        Assert.Equal(0, ledger.ActiveBucketCount);
    }

    [Fact]
    public void Ledger_SweepLeavesActiveBuckets()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var ledger = new SlidingWindowLedger(clock);
        ledger.Append("active", 1m);
        ledger.Append("stale", 1m);
        clock.Advance(TimeSpan.FromSeconds(120));
        ledger.Append("active", 1m); // still active
        var removed = ledger.SweepEmptyBuckets(TimeSpan.FromSeconds(60));
        Assert.Equal(1, removed);
        Assert.Equal(1, ledger.ActiveBucketCount);
    }

    // ────────────────── RollingNotionalCheck ──────────────────

    [Fact]
    public void RollingNotional_NoCap_Approves()
    {
        var (check, _, _) = BuildRollingNotional(new RiskOptions());
        Assert.True(check.Check(Ctx()).Approved);
    }

    [Fact]
    public void RollingNotional_RejectsWhenCumulativeExceedsCap()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var opts = new RiskOptions
        {
            RollingNotional = new RollingNotionalOptions
            {
                WindowSeconds = 60,
                Default = new RollingNotionalLimit { Cap = 5_000m },
            },
        };
        var (check, accountant, _) = BuildRollingNotional(opts, clock);

        // First 100 @ 30 = 3000. Approve + record.
        Assert.True(check.Check(Ctx(qty: 100)).Approved);
        accountant.RecordAccepted(Ctx(qty: 100));

        // Next 100 @ 30 = 3000 → cumulative 6000 > 5000 → reject.
        Assert.False(check.Check(Ctx(qty: 100)).Approved);

        // After window passes, the ledger is clear again.
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True(check.Check(Ctx(qty: 100)).Approved);
    }

    [Fact]
    public void RollingNotional_PerEndClientAndPerFirmAreIndependent()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var opts = new RiskOptions
        {
            RollingNotional = new RollingNotionalOptions
            {
                WindowSeconds = 60,
                PerEndClient = { ["alice"] = new RollingNotionalLimit { Cap = 10_000m } },
                PerFirm = { ["acme"] = new RollingNotionalLimit { Cap = 1_000m } },
            },
        };
        var (check, accountant, _) = BuildRollingNotional(opts, clock);

        // 1500 notional > firm cap 1000 → reject even though end-client cap is 10000.
        Assert.False(check.Check(Ctx(firm: "acme", qty: 50)).Approved);
    }

    [Fact]
    public void RollingNotional_MarketOrderUsesReferencePrice()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var opts = new RiskOptions
        {
            RollingNotional = new RollingNotionalOptions
            {
                WindowSeconds = 60,
                Default = new RollingNotionalLimit { Cap = 1_000m },
            },
        };
        var refPx = new StubRef(("PETR4", 30m));
        var (check, accountant, _) = BuildRollingNotional(opts, clock, refPx);

        // Market order 100 * ref 30 = 3000 > 1000 → reject.
        Assert.False(check.Check(Ctx(qty: 100, price: null, type: OrderType.Market)).Approved);
    }

    [Fact]
    public void RollingNotional_MarketOrderApprovesWhenNoReference()
    {
        // Fail-open posture (mirrors PriceCollarCheck) — bypass metric is bumped
        // by the accountant on its NotionalFor() path.
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var opts = new RiskOptions
        {
            RollingNotional = new RollingNotionalOptions
            {
                Default = new RollingNotionalLimit { Cap = 1_000m },
            },
        };
        var (check, _, _) = BuildRollingNotional(opts, clock, new StubRef());
        Assert.True(check.Check(Ctx(qty: 100, price: null, type: OrderType.Market)).Approved);
    }

    // ────────────────── OrderRateLimitCheck ──────────────────

    [Fact]
    public void OrderRate_RejectsWhenRateExceeded()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var opts = new RiskOptions
        {
            OrderRate = new OrderRateOptions
            {
                WindowSeconds = 1,
                Default = new OrderRateLimit { Max = 2 },
            },
        };
        var (check, accountant) = BuildOrderRate(opts, clock);

        Assert.True(check.Check(Ctx()).Approved);
        accountant.RecordAccepted(Ctx());
        Assert.True(check.Check(Ctx()).Approved);
        accountant.RecordAccepted(Ctx());
        Assert.False(check.Check(Ctx()).Approved); // 3rd would exceed cap of 2

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(check.Check(Ctx()).Approved); // window cleared
    }

    [Fact]
    public void OrderRate_PerFirmCapAppliesAcrossClients()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var opts = new RiskOptions
        {
            OrderRate = new OrderRateOptions
            {
                WindowSeconds = 1,
                PerFirm = { ["acme"] = new OrderRateLimit { Max = 1 } },
            },
        };
        var (check, accountant) = BuildOrderRate(opts, clock);

        Assert.True(check.Check(Ctx(owner: "alice", firm: "acme")).Approved);
        accountant.RecordAccepted(Ctx(owner: "alice", firm: "acme"));
        // Different end-client, same firm — firm cap blocks it.
        Assert.False(check.Check(Ctx(owner: "bob", firm: "acme")).Approved);
    }

    // ────────────────── MaxOpenOrdersCheck ──────────────────

    [Fact]
    public void MaxOpenOrders_RejectsWhenIncludeSelfExceedsCap()
    {
        // The order is added to the book *before* risk runs, so the
        // count includes the current order. Cap=2 means we accept up
        // to two open orders for this owner.
        var book = new WorkingOrderBook();
        AddOpen(book, "alice", clOrdId: 1);
        AddOpen(book, "alice", clOrdId: 2);
        AddOpen(book, "alice", clOrdId: 3); // current submit
        var opts = Wrap(new RiskOptions { Default = new RiskLimits { MaxOpenOrders = 2 } });
        var check = new MaxOpenOrdersCheck(opts, book);

        Assert.False(check.Check(Ctx()).Approved);
    }

    [Fact]
    public void MaxOpenOrders_ApprovesWhenAtCap()
    {
        var book = new WorkingOrderBook();
        AddOpen(book, "alice", clOrdId: 1);
        AddOpen(book, "alice", clOrdId: 2); // current submit, count == cap
        var opts = Wrap(new RiskOptions { Default = new RiskLimits { MaxOpenOrders = 2 } });
        Assert.True(new MaxOpenOrdersCheck(opts, book).Check(Ctx()).Approved);
    }

    [Fact]
    public void MaxOpenOrders_NoCap_Approves()
    {
        var book = new WorkingOrderBook();
        AddOpen(book, "alice", clOrdId: 1);
        Assert.True(new MaxOpenOrdersCheck(Wrap(new RiskOptions()), book).Check(Ctx()).Approved);
    }

    [Fact]
    public void MaxOpenOrders_ExcludesTerminalOrders()
    {
        var book = new WorkingOrderBook();
        var o1 = NewOrder("alice", 1);
        book.TryAdd(o1);
        o1.MarkRejected(); // terminal — should not count
        AddOpen(book, "alice", clOrdId: 2); // current submit
        var opts = Wrap(new RiskOptions { Default = new RiskLimits { MaxOpenOrders = 1 } });
        // Open count (excluding terminal) = 1 (only o2). Cap = 1, count > cap is false → approve.
        Assert.True(new MaxOpenOrdersCheck(opts, book).Check(Ctx()).Approved);
    }

    // ────────────────── CompositeRiskAccountant ──────────────────

    [Fact]
    public void Composite_FansOutToAllAccountants()
    {
        var a = new CountingAccountant();
        var b = new CountingAccountant();
        var composite = new CompositeRiskAccountant(new IRiskAccountant[] { a, b });
        composite.RecordAccepted(Ctx());
        Assert.Equal(1, a.Count);
        Assert.Equal(1, b.Count);
    }

    [Fact]
    public void Composite_DoesNotIncludeItself()
    {
        // DI registers the composite as well; ensure the constructor
        // filters it out so RecordAccepted doesn't recurse infinitely.
        var inner = new CountingAccountant();
        var composite = new CompositeRiskAccountant(Array.Empty<IRiskAccountant>());
        var outer = new CompositeRiskAccountant(new IRiskAccountant[] { composite, inner });
        outer.RecordAccepted(Ctx());
        Assert.Equal(1, inner.Count);
    }

    private sealed class CountingAccountant : IRiskAccountant
    {
        public int Count;
        public void RecordAccepted(RiskContext ctx) => Count++;
    }

    // ────────────────── helpers ──────────────────

    private static (RollingNotionalCheck check, RollingNotionalAccountant accountant, IOptionsMonitor<RiskOptions> opts)
        BuildRollingNotional(RiskOptions o, TestClock? clock = null, IReferencePrice? refPx = null)
    {
        clock ??= new TestClock(DateTimeOffset.UtcNow);
        refPx ??= new StubRef();
        var monitor = Wrap(o);
        var accountant = new RollingNotionalAccountant(monitor, refPx, clock);
        return (new RollingNotionalCheck(monitor, accountant), accountant, monitor);
    }

    private static (OrderRateLimitCheck check, OrderRateAccountant accountant)
        BuildOrderRate(RiskOptions o, TestClock? clock = null)
    {
        clock ??= new TestClock(DateTimeOffset.UtcNow);
        var monitor = Wrap(o);
        var accountant = new OrderRateAccountant(monitor, clock);
        return (new OrderRateLimitCheck(monitor, accountant), accountant);
    }

    private static void AddOpen(WorkingOrderBook book, string owner, ulong clOrdId) =>
        book.TryAdd(NewOrder(owner, clOrdId));

    private static Order NewOrder(string owner, ulong clOrdId) =>
        new(clOrdId, new EndClientId(owner), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            quantity: 100, price: 30m, firmId: "default");
}
