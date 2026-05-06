using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS0618 // legacy Margin.Initial used to seed capacity in tests

namespace B3.Trading.Application.Tests;

/// <summary>
/// Slice 2 of #122 — order modify lifecycle wiring:
/// <see cref="OrderReplacementIntent"/> + <see cref="PendingReplacementRegistry"/>,
/// <see cref="IReplaceMarginCoordinator"/> on
/// <see cref="ReserveOnSubmitMarginProvider"/>, and the
/// <see cref="ExecutionReportProcessor"/> Replaced / replace-reject
/// branches.
/// </summary>
public class OrderModifyMarginAndProcessorTests
{
    private static OrderReplacementIntent BuyLimitIntent(
        ulong origId, ulong newId, string owner, long newQty, decimal newPrice) =>
        new(
            OriginalClOrdId: origId,
            NewClOrdId: newId,
            Owner: new EndClientId(owner),
            Symbol: "PETR4",
            SecurityId: 4321UL,
            Side: OrderSide.Buy,
            Type: OrderType.Limit,
            NewQuantity: newQty,
            NewPrice: newPrice,
            FirmId: "FIRM",
            ParentAlgoId: null,
            AlgoSliceSeq: null);

    // ---------------- PendingReplacementRegistry ----------------

    [Fact]
    public void Registry_TryAdd_thenTryConsume_returnsIntentOnce()
    {
        var reg = new PendingReplacementRegistry();
        var intent = BuyLimitIntent(1UL, 2UL, "alice", 100, 30m);
        Assert.True(reg.TryAdd(intent));
        Assert.True(reg.TryConsume(2UL, out var first));
        Assert.NotNull(first);
        Assert.Equal(1UL, first!.OriginalClOrdId);
        Assert.False(reg.TryConsume(2UL, out _));
    }

    [Fact]
    public void Registry_TryAdd_rejectsDuplicateNewClOrdId()
    {
        var reg = new PendingReplacementRegistry();
        Assert.True(reg.TryAdd(BuyLimitIntent(1UL, 2UL, "alice", 100, 30m)));
        Assert.False(reg.TryAdd(BuyLimitIntent(1UL, 2UL, "alice", 200, 31m)));
    }

    [Fact]
    public void Registry_TryGet_doesNotRemove()
    {
        var reg = new PendingReplacementRegistry();
        reg.TryAdd(BuyLimitIntent(1UL, 2UL, "alice", 100, 30m));
        Assert.True(reg.TryGet(2UL, out var peek));
        Assert.NotNull(peek);
        Assert.True(reg.TryConsume(2UL, out _));
    }

    [Fact]
    public void Registry_IsOriginalInFlight_reflectsAddAndConsume()
    {
        var reg = new PendingReplacementRegistry();
        Assert.False(reg.IsOriginalInFlight(1UL));
        Assert.True(reg.TryAdd(BuyLimitIntent(1UL, 2UL, "alice", 100, 30m)));
        Assert.True(reg.IsOriginalInFlight(1UL));
        Assert.True(reg.TryConsume(2UL, out _));
        Assert.False(reg.IsOriginalInFlight(1UL));
    }

    [Fact]
    public void Registry_TryAdd_rejectsSecondModifyForSameOriginal()
    {
        // Slice-4 guard: only one in-flight modify per original ClOrdID.
        // Second TryAdd with same OriginalClOrdId but a fresh new
        // ClOrdID must fail (prevents stacked modifies racing the venue).
        var reg = new PendingReplacementRegistry();
        Assert.True(reg.TryAdd(BuyLimitIntent(1UL, 2UL, "alice", 100, 30m)));
        Assert.False(reg.TryAdd(BuyLimitIntent(1UL, 3UL, "alice", 200, 31m)));
        // After consuming the first, a second modify for the same orig
        // is now allowed.
        Assert.True(reg.TryConsume(2UL, out _));
        Assert.True(reg.TryAdd(BuyLimitIntent(1UL, 3UL, "alice", 200, 31m)));
    }

    // ---------------- HydrateReplacement ----------------

    [Fact]
    public void HydrateReplacement_workingWhenNoCum()
    {
        var owner = new EndClientId("alice");
        var orig = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM");
        var newOrder = Order.HydrateReplacement(orig, 2UL, 200, 31m, erLeaves: 200, erCumulative: 0);
        Assert.Equal(OrderStatus.Working, newOrder.Status);
        Assert.Equal(200, newOrder.LeavesQuantity);
        Assert.Equal(0, newOrder.CumulativeQuantity);
        Assert.Equal(31m, newOrder.Price);
        Assert.Equal("PETR4", newOrder.Symbol);
        Assert.Equal(owner, newOrder.Owner);
        Assert.Equal("FIRM", newOrder.FirmId);
    }

    [Fact]
    public void HydrateReplacement_partiallyFilledWhenCumPositive()
    {
        var orig = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM");
        var newOrder = Order.HydrateReplacement(orig, 2UL, 200, 31m, erLeaves: 150, erCumulative: 50);
        Assert.Equal(OrderStatus.PartiallyFilled, newOrder.Status);
        Assert.Equal(50, newOrder.CumulativeQuantity);
    }

    [Fact]
    public void HydrateReplacement_filledWhenCumGreaterEqualNewQty()
    {
        var orig = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM");
        var newOrder = Order.HydrateReplacement(orig, 2UL, 60, 31m, erLeaves: 0, erCumulative: 60);
        Assert.Equal(OrderStatus.Filled, newOrder.Status);
    }

    [Fact]
    public void HydrateReplacement_rejectsZeroClOrdId()
    {
        var orig = new Order(1UL, new EndClientId("alice"), "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Order.HydrateReplacement(orig, 0UL, 200, 31m, 200, 0));
    }

    // ---------------- IReplaceMarginCoordinator ----------------

    private static (ReserveOnSubmitMarginProvider provider, StaticOptionsMonitor<RiskOptions> monitor) BuildProvider(
        decimal initial = 100_000m, string owner = "alice")
    {
        var opts = new RiskOptions();
        opts.Margin.Enabled = true;
        opts.Margin.Initial[owner] = initial;
        var monitor = new StaticOptionsMonitor<RiskOptions>(opts);
        var provider = new ReserveOnSubmitMarginProvider(monitor, NullLogger<ReserveOnSubmitMarginProvider>.Instance);
        return (provider, monitor);
    }

    private static RiskContext BuyCtx(string owner, decimal price, long qty) =>
        new(new EndClientId(owner), "FIRM", "PETR4", OrderSide.Buy, OrderType.Limit, qty, price);

    [Fact]
    public async Task Coordinator_upsize_reservesDelta_thenCommitTransfersOwnership()
    {
        var (p, _) = BuildProvider(initial: 10_000m);
        Assert.True((await p.TryReserveAsync(1UL, BuyCtx("alice", 10m, 100), default)).Approved);
        // reserved = 1000, available = 9000

        // Upsize from 100@10 → 200@10. Delta = 1000.
        var prep = await ((IReplaceMarginCoordinator)p).PrepareReplaceAsync(
            1UL, 2UL, new EndClientId("alice"), newRemainingNotional: 2000m, default);
        Assert.True(prep.Approved);
        Assert.Equal(2000m, p.ReservedForTesting("alice"));

        // Commit: confirmed remaining = 2000.
        ((IReplaceMarginCoordinator)p).CommitReplace(1UL, 2UL, 2000m);
        Assert.Equal(2000m, p.ReservedForTesting("alice"));

        // Now releasing the new order should fully restore the ledger.
        p.ReleaseReservation(2UL);
        Assert.Equal(0m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task Coordinator_downsize_doesNotReserveAtPrepare_releasesAtCommit()
    {
        var (p, _) = BuildProvider(initial: 10_000m);
        Assert.True((await p.TryReserveAsync(1UL, BuyCtx("alice", 10m, 100), default)).Approved);
        Assert.Equal(1000m, p.ReservedForTesting("alice"));

        // Downsize 100@10 → 50@10. Delta = -500. No extra reserve.
        var prep = await ((IReplaceMarginCoordinator)p).PrepareReplaceAsync(
            1UL, 2UL, new EndClientId("alice"), newRemainingNotional: 500m, default);
        Assert.True(prep.Approved);
        Assert.Equal(1000m, p.ReservedForTesting("alice"));

        ((IReplaceMarginCoordinator)p).CommitReplace(1UL, 2UL, 500m);
        Assert.Equal(500m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task Coordinator_upsizeRejected_whenDeltaExceedsAvailable()
    {
        var (p, _) = BuildProvider(initial: 1_500m);
        Assert.True((await p.TryReserveAsync(1UL, BuyCtx("alice", 10m, 100), default)).Approved);
        // available = 500. Try upsize that needs +1000 delta.
        var prep = await ((IReplaceMarginCoordinator)p).PrepareReplaceAsync(
            1UL, 2UL, new EndClientId("alice"), newRemainingNotional: 2000m, default);
        Assert.False(prep.Approved);
        Assert.Contains("insufficient margin", prep.Reason);
        // Original reservation still intact.
        Assert.Equal(1000m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task Coordinator_abort_releasesUpsizeDeltaOnly_originalIntact()
    {
        var (p, _) = BuildProvider(initial: 10_000m);
        Assert.True((await p.TryReserveAsync(1UL, BuyCtx("alice", 10m, 100), default)).Approved);
        await ((IReplaceMarginCoordinator)p).PrepareReplaceAsync(
            1UL, 2UL, new EndClientId("alice"), 2000m, default);
        Assert.Equal(2000m, p.ReservedForTesting("alice"));

        ((IReplaceMarginCoordinator)p).AbortReplace(2UL);
        Assert.Equal(1000m, p.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task Coordinator_sellOrZeroNotional_isApproveNoOp()
    {
        var (p, _) = BuildProvider(initial: 1_000m);
        var prep = await ((IReplaceMarginCoordinator)p).PrepareReplaceAsync(
            999UL, 1000UL, new EndClientId("alice"), newRemainingNotional: 0m, default);
        Assert.True(prep.Approved);
        Assert.Equal(0m, p.ReservedForTesting("alice"));
    }

    // ---------------- ExecutionReportProcessor wiring ----------------

    private sealed class CaptureSink : IExecutionEventSink
    {
        public List<ExecutionEvent> Events { get; } = new();
        public void Publish(ExecutionEvent ev) => Events.Add(ev);
    }

    private static (ExecutionReportProcessor proc, OrderOwnershipMap own, WorkingOrderBook book,
                    PendingReplacementRegistry reg, ReserveOnSubmitMarginProvider margin, CaptureSink sink) BuildProcessor(
        decimal initial = 10_000m)
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new CaptureSink();
        var opts = new RiskOptions();
        opts.Margin.Enabled = true;
        opts.Margin.Initial["alice"] = initial;
        var monitor = new StaticOptionsMonitor<RiskOptions>(opts);
        var margin = new ReserveOnSubmitMarginProvider(monitor, NullLogger<ReserveOnSubmitMarginProvider>.Instance);
        var reg = new PendingReplacementRegistry();
        var proc = new ExecutionReportProcessor(
            ownership, book, positions, sink, margin,
            NullLogger<ExecutionReportProcessor>.Instance,
            algoSignals: null,
            cash: null,
            replacements: reg,
            replaceMargin: margin);
        return (proc, ownership, book, reg, margin, sink);
    }

    [Fact]
    public async Task Processor_Replaced_terminalizesOriginalAndHydratesNew_andTransfersMargin()
    {
        var (proc, ownership, book, reg, margin, sink) = BuildProcessor();
        var owner = new EndClientId("alice");
        var orig = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 10m, "FIRM");
        book.TryAdd(orig);
        ownership.Register(1UL, owner);
        Assert.True((await margin.TryReserveAsync(1UL, BuyCtx("alice", 10m, 100), default)).Approved);
        // upsize 100@10 → 200@10
        reg.TryAdd(BuyLimitIntent(1UL, 2UL, "alice", 200, 10m));
        await ((IReplaceMarginCoordinator)margin).PrepareReplaceAsync(
            1UL, 2UL, owner, 2000m, default);

        proc.Apply(2UL, ExecKind.Replaced, leaves: 200, cumQty: 0, lastQty: 0, lastPx: 0m, rejectReason: null, origClOrdId: 1UL);

        Assert.Equal(OrderStatus.Replaced, orig.Status);
        Assert.True(book.TryGet(2UL, out var newOrder));
        Assert.Equal(OrderStatus.Working, newOrder!.Status);
        Assert.Equal(200, newOrder.LeavesQuantity);
        Assert.Equal(2000m, margin.ReservedForTesting("alice"));
        Assert.Equal(2, sink.Events.Count); // one for orig, one for new
        Assert.False(reg.TryGet(2UL, out _));
    }

    [Fact]
    public void Processor_Rejected_forIntentInRegistry_treatsAsReplaceReject_originalUntouched()
    {
        var (proc, ownership, book, reg, _, sink) = BuildProcessor();
        var owner = new EndClientId("alice");
        var orig = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 10m, "FIRM");
        book.TryAdd(orig);
        ownership.Register(1UL, owner);
        reg.TryAdd(BuyLimitIntent(1UL, 2UL, "alice", 200, 10m));

        proc.Apply(2UL, ExecKind.Rejected, leaves: 0, cumQty: 0, lastQty: 0, lastPx: 0m, rejectReason: "tick-out-of-range");

        Assert.Equal(OrderStatus.PendingNew, orig.Status); // unchanged
        Assert.False(book.TryGet(2UL, out _));
        Assert.False(reg.TryGet(2UL, out _));
        Assert.Empty(sink.Events); // replace-reject does not publish (no new order to surface)
    }

    [Fact]
    public async Task Processor_Replaced_aborts_whenOriginalMissing()
    {
        var (proc, _, _, reg, margin, sink) = BuildProcessor();
        // Reserve under origId but never add to book — simulate desync.
        Assert.True((await margin.TryReserveAsync(1UL, BuyCtx("alice", 10m, 100), default)).Approved);
        reg.TryAdd(BuyLimitIntent(1UL, 2UL, "alice", 200, 10m));
        await ((IReplaceMarginCoordinator)margin).PrepareReplaceAsync(
            1UL, 2UL, new EndClientId("alice"), 2000m, default);

        proc.Apply(2UL, ExecKind.Replaced, leaves: 200, cumQty: 0, lastQty: 0, lastPx: 0m, rejectReason: null, origClOrdId: 1UL);

        // upsize delta released; original reservation still alive.
        Assert.Equal(1000m, margin.ReservedForTesting("alice"));
        Assert.Empty(sink.Events);
        Assert.False(reg.TryGet(2UL, out _)); // intent consumed
    }

    [Fact]
    public void Processor_Rejected_normalRejectionStillFlowsThroughOriginalPath_whenNoIntent()
    {
        // Sanity: a Rejected ER for a ClOrdID NOT in the registry must
        // continue to follow the normal terminate-the-order branch.
        var (proc, ownership, book, _, _, sink) = BuildProcessor();
        var owner = new EndClientId("alice");
        var order = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 10m, "FIRM");
        book.TryAdd(order);
        ownership.Register(1UL, owner);

        proc.Apply(1UL, ExecKind.Rejected, 0, 0, 0, 0m, "venue-down");

        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Single(sink.Events);
    }
}
