using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;

namespace B3.Trading.Application.Tests;

public class OrderStalenessServiceBulkTests
{
    private static (OrderStalenessService svc, WorkingOrderBook book) Build()
    {
        var book = new WorkingOrderBook();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var svc = new OrderStalenessService(dispatcher, book);
        return (svc, book);
    }

    private static Order AddWorking(WorkingOrderBook book, ulong clOrdId, string firmId = "FIRM01")
    {
        var o = new Order(clOrdId, new EndClientId("alice"), "PETR4", 1UL,
            OrderSide.Buy, OrderType.Limit, 100, 30m, firmId);
        o.MarkWorking();
        book.TryAdd(o);
        return o;
    }

    [Fact]
    public void MarkAllWorkingByFirm_FlagsEveryWorkingOrderForFirm()
    {
        var (svc, book) = Build();
        var a = AddWorking(book, 1UL, "FIRM01");
        var b = AddWorking(book, 2UL, "FIRM01");
        var other = AddWorking(book, 3UL, "FIRM02");

        var n = svc.MarkAllWorkingByFirm("FIRM01", "venue restart", DateTimeOffset.UtcNow, "admin");

        Assert.Equal(2, n);
        Assert.True(a.IsStale);
        Assert.True(b.IsStale);
        Assert.False(other.IsStale);
    }

    [Fact]
    public void MarkAllWorkingByFirm_SkipsAlreadyStale()
    {
        var (svc, book) = Build();
        var a = AddWorking(book, 1UL);
        AddWorking(book, 2UL);
        svc.MarkStale("FIRM01", 1UL, "manual", DateTimeOffset.UtcNow, "admin");

        var n = svc.MarkAllWorkingByFirm("FIRM01", "auto", DateTimeOffset.UtcNow, null);

        Assert.Equal(1, n); // only the second one
        Assert.Equal("manual", a.StaleReason); // original reason preserved
    }

    [Fact]
    public void MarkAllWorkingByFirm_Idempotent()
    {
        var (svc, book) = Build();
        AddWorking(book, 1UL);
        AddWorking(book, 2UL);

        var first = svc.MarkAllWorkingByFirm("FIRM01", "x", DateTimeOffset.UtcNow, null);
        var second = svc.MarkAllWorkingByFirm("FIRM01", "x", DateTimeOffset.UtcNow, null);

        Assert.Equal(2, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public void MarkAllWorkingByFirm_SkipsTerminalAndPendingNew()
    {
        var (svc, book) = Build();
        // PendingNew (not yet MarkWorking)
        var pending = new Order(10UL, new EndClientId("alice"), "PETR4", 1UL,
            OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM01");
        book.TryAdd(pending);

        // Filled
        var filled = AddWorking(book, 11UL);
        filled.ApplyCumulativeFill(100);

        // Working — eligible
        var working = AddWorking(book, 12UL);

        var n = svc.MarkAllWorkingByFirm("FIRM01", "x", DateTimeOffset.UtcNow, null);

        Assert.Equal(1, n);
        Assert.False(pending.IsStale);
        Assert.False(filled.IsStale);
        Assert.True(working.IsStale);
    }

    [Fact]
    public void MarkAllWorkingByFirm_UnknownFirm_ReturnsZero()
    {
        var (svc, book) = Build();
        AddWorking(book, 1UL, "FIRM01");
        Assert.Equal(0, svc.MarkAllWorkingByFirm("NOPE", "x", DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public void MarkAllWorkingByFirm_BlankFirm_Throws()
    {
        var (svc, _) = Build();
        Assert.Throws<ArgumentException>(() => svc.MarkAllWorkingByFirm(" ", "x", DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public void MarkAllWorkingByFirm_BlankReason_Throws()
    {
        var (svc, _) = Build();
        Assert.Throws<ArgumentException>(() => svc.MarkAllWorkingByFirm("FIRM01", " ", DateTimeOffset.UtcNow, null));
    }
}

public class OrderStaleningVenueReactorTests
{
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static (OrderStaleningVenueReactor reactor, WorkingOrderBook book, FixedClock clock)
        Build(bool onPeerTerminate = false)
    {
        var book = new WorkingOrderBook();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var svc = new OrderStalenessService(dispatcher, book);
        var clock = new FixedClock(DateTimeOffset.Parse("2026-05-07T20:00:00Z"));
        var reactor = new OrderStaleningVenueReactor(
            svc, new AutoStaleOptions { OnPeerTerminate = onPeerTerminate }, clock);
        return (reactor, book, clock);
    }

    private static Order AddWorking(WorkingOrderBook book, ulong clOrdId, string firmId = "FIRM01")
    {
        var o = new Order(clOrdId, new EndClientId("alice"), "PETR4", 1UL,
            OrderSide.Buy, OrderType.Limit, 100, 30m, firmId);
        o.MarkWorking();
        book.TryAdd(o);
        return o;
    }

    [Fact]
    public void OnPeerReconnected_InboundGap_MarksAllWorkingForFirm()
    {
        var (reactor, book, _) = Build();
        var a = AddWorking(book, 1UL);
        var b = AddWorking(book, 2UL);

        reactor.OnPeerReconnected("FIRM01",
            new ReconnectOutcome(HadInboundGap: true, GapFromSeq: 100UL, GapCount: 5u, PriorSessionVerId: 7UL,
                PriorTerminationCode: "FINISHED"));

        Assert.True(a.IsStale);
        Assert.True(b.IsStale);
        Assert.Equal("inbound_gap:100-104", a.StaleReason);
    }

    [Fact]
    public void OnPeerReconnected_NoGap_DefaultPolicy_DoesNothing()
    {
        var (reactor, book, _) = Build(onPeerTerminate: false);
        var a = AddWorking(book, 1UL);

        reactor.OnPeerReconnected("FIRM01",
            new ReconnectOutcome(HadInboundGap: false, null, null, null, PriorTerminationCode: "UnspecifiedError"));

        Assert.False(a.IsStale);
    }

    [Fact]
    public void OnPeerReconnected_NoGap_FlagOn_MarksWithPeerTerminatedReason()
    {
        var (reactor, book, _) = Build(onPeerTerminate: true);
        var a = AddWorking(book, 1UL);

        reactor.OnPeerReconnected("FIRM01",
            new ReconnectOutcome(HadInboundGap: false, null, null, null, PriorTerminationCode: "UnspecifiedError"));

        Assert.True(a.IsStale);
        Assert.Equal("peer_terminated:UnspecifiedError", a.StaleReason);
    }

    [Fact]
    public void OnPeerReconnected_NoGap_FlagOn_NoCode_DoesNothing()
    {
        // Reconnect that wasn't preceded by a peer-terminate (e.g. operator-driven).
        // Without a termination code we have no diagnostic to propagate, so skip.
        var (reactor, book, _) = Build(onPeerTerminate: true);
        var a = AddWorking(book, 1UL);

        reactor.OnPeerReconnected("FIRM01",
            new ReconnectOutcome(HadInboundGap: false, null, null, null, PriorTerminationCode: null));

        Assert.False(a.IsStale);
    }

    [Fact]
    public void OnPeerReconnected_GapWinsOverPeerTerminateFlag()
    {
        // Both signals present: gap takes precedence (more specific reason).
        var (reactor, book, _) = Build(onPeerTerminate: true);
        var a = AddWorking(book, 1UL);

        reactor.OnPeerReconnected("FIRM01",
            new ReconnectOutcome(true, 50UL, 3u, 2UL, "UnspecifiedError"));

        Assert.True(a.IsStale);
        Assert.Equal("inbound_gap:50-52", a.StaleReason);
    }

    [Fact]
    public void OnPeerReconnected_BlankFirm_Throws()
    {
        var (reactor, _, _) = Build();
        Assert.Throws<ArgumentException>(() =>
            reactor.OnPeerReconnected("", new ReconnectOutcome(false, null, null, null, null)));
    }
}
