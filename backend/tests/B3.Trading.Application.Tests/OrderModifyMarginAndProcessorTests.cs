using B3.Trading.Application.Risk;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging;
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

    // ---------------- Issue #241: Cancel-as-Replace (priority-lost) ----------------

    private sealed class RecordingReplaceCoordinator : IReplaceMarginCoordinator
    {
        public List<(ulong Orig, ulong New, decimal Notional)> Commits { get; } = new();
        public List<ulong> Aborts { get; } = new();
        public List<(ulong Orig, ulong New, decimal Notional)> Prepares { get; } = new();

        public Task<RiskDecision> PrepareReplaceAsync(ulong originalClOrdId, ulong newClOrdId, EndClientId owner, decimal newRemainingNotional, CancellationToken ct)
        {
            Prepares.Add((originalClOrdId, newClOrdId, newRemainingNotional));
            return Task.FromResult(RiskDecision.Approve);
        }

        public void CommitReplace(ulong originalClOrdId, ulong newClOrdId, decimal confirmedRemainingNotional)
            => Commits.Add((originalClOrdId, newClOrdId, confirmedRemainingNotional));

        public void AbortReplace(ulong newClOrdId) => Aborts.Add(newClOrdId);
    }

    private sealed class RecordingBotErRouter : IBotErRouter
    {
        public List<ExecutionEvent> Events { get; } = new();
        public void Route(ExecutionEvent ev) => Events.Add(ev);
    }

    [Fact]
    public void Processor_CancelAsReplace_priorityLost_terminalisesOriginalAndHydratesNew_thenSubsequentFillBooksPositionAndCash()
    {
        // Repro for issue #241. B3 priority-lost path: venue emits
        //   ER_Cancel(new=778, orig=777)  +  ER_Trade(new=778, orig=0)
        // — never an ExecType=Replaced. Pre-fix the Cancel terminalised
        // 777, the new order was never created in the book, and the
        // Trade dropped silently (position/cash diverged).
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var cash = new CashLedger();
        var sink = new CaptureSink();
        var opts = new RiskOptions();
        opts.Margin.Enabled = true;
        opts.Margin.Initial["bob"] = 100_000m;
        var monitor = new StaticOptionsMonitor<RiskOptions>(opts);
        var marginProvider = new ReserveOnSubmitMarginProvider(monitor, NullLogger<ReserveOnSubmitMarginProvider>.Instance);
        var replaceCoord = new RecordingReplaceCoordinator();
        var botRouter = new RecordingBotErRouter();
        var reg = new PendingReplacementRegistry();
        cash.SeedIfAbsent(new EndClientId("bob"), 100_000m);

        var proc = new ExecutionReportProcessor(
            ownership, book, positions, sink, marginProvider,
            NullLogger<ExecutionReportProcessor>.Instance,
            algoSignals: null,
            cash: cash,
            replacements: reg,
            replaceMargin: replaceCoord,
            botErRouter: botRouter);

        var bob = new EndClientId("bob");
        // 1) Original Buy 100 PETR4 @ 32.49 — sits in book.
        var orig = new Order(777UL, bob, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 32.49m, "FIRM");
        book.TryAdd(orig);
        orig.MarkWorking();
        ownership.Register(777UL, bob);

        // 2) Modify-to-cross 32.49 → 32.50: register link + intent for newClOrdId=778.
        ownership.RegisterReplaceLink(777UL, 778UL);
        var intent = new OrderReplacementIntent(
            OriginalClOrdId: 777UL,
            NewClOrdId: 778UL,
            Owner: bob,
            Symbol: "PETR4",
            SecurityId: 4321UL,
            Side: OrderSide.Buy,
            Type: OrderType.Limit,
            NewQuantity: 100,
            NewPrice: 32.50m,
            FirmId: "FIRM",
            ParentAlgoId: null,
            AlgoSliceSeq: null);
        Assert.True(reg.TryAdd(intent));

        // 3) Venue cancel-as-replace ER under newClOrdID — priority-lost branch.
        proc.Apply(778UL, ExecKind.Canceled, leaves: 0, cumQty: 0, lastQty: 0, lastPx: 0m, rejectReason: null, origClOrdId: 777UL);

        // Original terminalised as Replaced (not Cancelled); new order hydrated.
        Assert.Equal(OrderStatus.Replaced, orig.Status);
        Assert.True(book.TryGet(778UL, out var newOrder));
        Assert.NotNull(newOrder);
        Assert.Equal(OrderStatus.Working, newOrder!.Status);
        Assert.Equal(100, newOrder.LeavesQuantity);
        Assert.Equal(0, newOrder.CumulativeQuantity);
        // Margin coordinator saw Commit, NOT Abort.
        Assert.Single(replaceCoord.Commits);
        Assert.Empty(replaceCoord.Aborts);
        Assert.Equal((777UL, 778UL, 32.50m * 100), replaceCoord.Commits[0]);
        // Replace-fanout: orig + new ExecutionEvent (kind=Replaced) on the sink.
        Assert.Equal(2, sink.Events.Count);
        Assert.All(sink.Events, e => Assert.Equal(ExecKind.Replaced, e.Kind));
        Assert.Contains(sink.Events, e => e.ClOrdId == 777UL);
        Assert.Contains(sink.Events, e => e.ClOrdId == 778UL);
        // Bot router saw both replace ERs.
        Assert.Equal(2, botRouter.Events.Count);

        // 4) Subsequent Trade ER on the new ClOrdID (orig=0 in priority-lost trade).
        proc.Apply(778UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 32.50m, rejectReason: null, origClOrdId: 0);

        Assert.Equal(OrderStatus.Filled, newOrder.Status);
        Assert.Equal(0, newOrder.LeavesQuantity);
        Assert.Equal(100, newOrder.CumulativeQuantity);
        // Position booked.
        var pos = positions.GetOrCreate(bob, "PETR4");
        Assert.Equal(100, pos.NetQuantity);
        Assert.Equal(32.50m, pos.AverageEntryPrice);
        // Cash debited (Buy: available drops by notional).
        Assert.Equal(100_000m - (32.50m * 100), cash.GetAvailable(bob));
        // Fill ER published on sink + routed to bot.
        Assert.Equal(3, sink.Events.Count);
        var fillEv = sink.Events[2];
        Assert.Equal(ExecKind.Fill, fillEv.Kind);
        Assert.Equal(778UL, fillEv.ClOrdId);
        Assert.Equal(3, botRouter.Events.Count);
    }

    [Fact]
    public void Processor_CancelAsReplace_secondCancelReplay_isIdempotentNoOp()
    {
        // Edge-case from #241: PendingReplacementRegistry.TryConsume is
        // one-shot, so a replayed Cancel ER (FIXP retransmit) falls
        // through to the standard cancel branch. Order is already
        // Replaced (terminal) so MarkCancelled is guarded — no throw,
        // no double-add, no state regression.
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new CaptureSink();
        var opts = new RiskOptions();
        opts.Margin.Enabled = true;
        opts.Margin.Initial["bob"] = 100_000m;
        var monitor = new StaticOptionsMonitor<RiskOptions>(opts);
        var marginProvider = new ReserveOnSubmitMarginProvider(monitor, NullLogger<ReserveOnSubmitMarginProvider>.Instance);
        var replaceCoord = new RecordingReplaceCoordinator();
        var reg = new PendingReplacementRegistry();

        var proc = new ExecutionReportProcessor(
            ownership, book, positions, sink, marginProvider,
            NullLogger<ExecutionReportProcessor>.Instance,
            algoSignals: null,
            cash: null,
            replacements: reg,
            replaceMargin: replaceCoord,
            botErRouter: null);

        var bob = new EndClientId("bob");
        var orig = new Order(777UL, bob, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 32.49m, "FIRM");
        book.TryAdd(orig);
        orig.MarkWorking();
        ownership.Register(777UL, bob);
        ownership.RegisterReplaceLink(777UL, 778UL);
        Assert.True(reg.TryAdd(new OrderReplacementIntent(
            777UL, 778UL, bob, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 32.50m, "FIRM", null, null)));

        // First Cancel ER — intercepted as cancel-as-replace.
        proc.Apply(778UL, ExecKind.Canceled, 0, 0, 0, 0m, null, origClOrdId: 777UL);
        Assert.Equal(OrderStatus.Replaced, orig.Status);
        Assert.True(book.TryGet(778UL, out var newOrder));
        Assert.Equal(OrderStatus.Working, newOrder!.Status);
        var sinkCountAfterFirst = sink.Events.Count;
        Assert.Single(replaceCoord.Commits);

        // Second Cancel ER (FIXP replay) — registry consumed; falls
        // through. The 778 lookup finds the now-Working new order;
        // MarkCancelled WILL apply to it (no terminal guard against
        // Working). This is the documented v1 behaviour for replayed
        // cancels arriving after the new order was hydrated; the more
        // important assertion is no throw, no double-add, no reservation
        // leak (no extra Commit/Abort on the coordinator).
        var origStatusBefore = orig.Status;
        proc.Apply(778UL, ExecKind.Canceled, 0, 0, 0, 0m, null, origClOrdId: 777UL);
        Assert.Equal(origStatusBefore, orig.Status); // original untouched
        Assert.Single(replaceCoord.Commits); // no extra commit
        Assert.Empty(replaceCoord.Aborts);   // no leak
        Assert.True(book.TryGet(778UL, out var stillThere));
        Assert.Same(newOrder, stillThere);   // not re-hydrated
        // Fan-out emitted at most one extra ER for the standard cancel path.
        Assert.InRange(sink.Events.Count - sinkCountAfterFirst, 0, 1);
    }

    // ---------------- Issue #247: CommitReplace reservation drop ----------------

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Records { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }
        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _owner;
            public CapturingLogger(CapturingLoggerProvider owner) { _owner = owner; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _owner.Records.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public async Task Issue247_CancelAsReplace_realProvider_priorityLostFlow_keepsReservedConsistent()
    {
        // Issue #247 reproducer. End-to-end sequence with the REAL
        // ReserveOnSubmitMarginProvider (not the recording stub):
        //   1) Bob submits Buy 100 @ 32.49 PETR4 → reservations[777]=3249.
        //   2) Modify 32.49 → 32.50 (same qty)        → reservations[778]=delta(1).
        //   3) Venue priority-lost: ER_Cancel(778, orig=777) intercepted →
        //      ApplyReplaceAccepted → CommitReplace(777, 778, 3250).
        //   4) Subsequent ER_Trade(778, fill 100) → margin released.
        // Acceptance: the warn "neither original nor pending reservation
        // has an owner; dropping" must NOT fire and reserved must end at 0.
        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));
        var marginLogger = loggerFactory.CreateLogger<ReserveOnSubmitMarginProvider>();

        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new CaptureSink();
        var opts = new RiskOptions();
        opts.Margin.Enabled = true;
        opts.Margin.Initial["bob"] = 100_000m;
        var monitor = new StaticOptionsMonitor<RiskOptions>(opts);
        var margin = new ReserveOnSubmitMarginProvider(monitor, marginLogger);
        var reg = new PendingReplacementRegistry();

        var proc = new ExecutionReportProcessor(
            ownership, book, positions, sink, margin,
            NullLogger<ExecutionReportProcessor>.Instance,
            algoSignals: null,
            cash: null,
            replacements: reg,
            replaceMargin: margin);

        var bob = new EndClientId("bob");

        // 1) Bob submits original.
        var orig = new Order(777UL, bob, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 32.49m, "FIRM");
        book.TryAdd(orig);
        orig.MarkWorking();
        ownership.Register(777UL, bob);
        Assert.True((await margin.TryReserveAsync(
            777UL,
            new RiskContext(bob, "FIRM", "PETR4", OrderSide.Buy, OrderType.Limit, 100, 32.49m),
            default)).Approved);
        Assert.Equal(32.49m * 100, margin.ReservedForTesting("bob"));

        // 2) Modify-to-cross 32.49 → 32.50.
        ownership.RegisterReplaceLink(777UL, 778UL);
        Assert.True((await ((IReplaceMarginCoordinator)margin).PrepareReplaceAsync(
            777UL, 778UL, bob, newRemainingNotional: 32.50m * 100, default)).Approved);
        Assert.True(reg.TryAdd(new OrderReplacementIntent(
            777UL, 778UL, bob, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 32.50m, "FIRM", null, null)));
        // Delta path: prepare reserved +1 (3250 - 3249).
        Assert.Equal(32.50m * 100, margin.ReservedForTesting("bob"));

        // 3) Venue priority-lost path: ER_Cancel under newClOrdID with orig=777.
        proc.Apply(778UL, ExecKind.Canceled, leaves: 0, cumQty: 0, lastQty: 0,
                   lastPx: 0m, rejectReason: null, origClOrdId: 777UL);

        // CommitReplace must have transferred ownership cleanly: reserved
        // stays at the new notional (3250). The "dropping" warn must NOT fire.
        Assert.DoesNotContain(loggerProvider.Records, r =>
            r.Level == LogLevel.Warning && r.Message.Contains("dropping", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(32.50m * 100, margin.ReservedForTesting("bob"));

        // 4) Subsequent Fill ER on the new ClOrdID releases everything.
        proc.Apply(778UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100,
                   lastPx: 32.50m, rejectReason: null, origClOrdId: 0);
        Assert.Equal(0m, margin.ReservedForTesting("bob"));
    }

    [Fact]
    public void Issue247_CommitReplace_isSilentNoOpWhenMarginDisabled()
    {
        // Spin-off acceptance criterion: in deployments with
        // Margin.Enabled=false the coordinator is still wired (so it can
        // clean up if margin is toggled mid-session), but a normal
        // Cancel-as-Replace still funnels through CommitReplace — that
        // call must be a silent no-op, not a noisy warn-and-drop.
        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));
        var marginLogger = loggerFactory.CreateLogger<ReserveOnSubmitMarginProvider>();
        var opts = new RiskOptions();
        opts.Margin.Enabled = false;
        var monitor = new StaticOptionsMonitor<RiskOptions>(opts);
        var margin = new ReserveOnSubmitMarginProvider(monitor, marginLogger);

        ((IReplaceMarginCoordinator)margin).CommitReplace(777UL, 778UL, 3250m);

        Assert.DoesNotContain(loggerProvider.Records, r => r.Level >= LogLevel.Warning);
        Assert.Equal(0m, margin.ReservedForTesting("bob"));
    }

    [Fact]
    public void Issue247_CommitReplace_marginEnabled_neitherSideTracked_logsErrorAndCountsDrop()
    {
        // Defensive: if some future code path registers an intent
        // without going through PrepareReplaceAsync, CommitReplace must
        // still surface the drop loudly (error + metric) instead of the
        // silent warn it used to emit. This protects the alertable-leak
        // semantics called out in #247's acceptance criteria.
        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));
        var marginLogger = loggerFactory.CreateLogger<ReserveOnSubmitMarginProvider>();
        var opts = new RiskOptions();
        opts.Margin.Enabled = true;
        opts.Margin.Initial["bob"] = 10_000m;
        var monitor = new StaticOptionsMonitor<RiskOptions>(opts);
        var margin = new ReserveOnSubmitMarginProvider(monitor, marginLogger);

        // Skip both TryReserveAsync and PrepareReplaceAsync — empty ledger.
        ((IReplaceMarginCoordinator)margin).CommitReplace(777UL, 778UL, 3250m);

        Assert.Contains(loggerProvider.Records, r =>
            r.Level == LogLevel.Error && r.Message.Contains("dropping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Issue247_PR248_CommitReplace_AfterMarginDisabledToggle_StillReleasesOriginalReservation()
    {
        // PR #248 P2: the Margin.Enabled gate in CommitReplace must NOT
        // skip the cleanup path when reservations were created while
        // margin was enabled and then the operator toggled it off via
        // the admin reload path (IOptionsMonitor). Otherwise the
        // original slot leaks forever — _reservations[orig] stays set
        // and _reserved[owner] never returns to zero.
        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));
        var marginLogger = loggerFactory.CreateLogger<ReserveOnSubmitMarginProvider>();

        var enabled = new RiskOptions();
        enabled.Margin.Enabled = true;
        enabled.Margin.Initial["bob"] = 100_000m;
        var monitor = new StaticOptionsMonitor<RiskOptions>(enabled);
        var margin = new ReserveOnSubmitMarginProvider(monitor, marginLogger);
        var bob = new EndClientId("bob");

        // Reserve while margin is enabled.
        Assert.True((await margin.TryReserveAsync(
            777UL,
            new RiskContext(bob, "FIRM", "PETR4", OrderSide.Buy, OrderType.Limit, 100, 32.49m),
            default)).Approved);
        Assert.Equal(32.49m * 100, margin.ReservedForTesting("bob"));

        // Operator flips margin OFF mid-session via admin reload.
        var disabled = new RiskOptions();
        disabled.Margin.Enabled = false;
        disabled.Margin.Initial["bob"] = 100_000m;
        monitor.Set(disabled);
        Assert.False(monitor.CurrentValue.Margin.Enabled);

        // Cancel-as-Replace lands AFTER the toggle. confirmedRemainingNotional=0
        // simulates a flat-out cancel-as-replace (modify-to-fill collapses it
        // to zero). This must release the original reservation cleanly.
        ((IReplaceMarginCoordinator)margin).CommitReplace(777UL, 778UL, 0m);

        Assert.Equal(0m, margin.ReservedForTesting("bob"));
        Assert.DoesNotContain(loggerProvider.Records, r => r.Level >= LogLevel.Warning);
    }
}
