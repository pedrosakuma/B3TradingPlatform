using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
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

    // ---------------- Q1.1 (#253) optionals through replace pipeline ----------------

    [Fact]
    public void HydrateReplacement_inheritsTifStopGtdWhenAllNull()
    {
        // Default behaviour: caller passed no Q1.1 overrides → the
        // replacement Order carries the original's TIF/StopPrice/GTD.
        var owner = new EndClientId("alice");
        var orig = new Order(
            1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.StopLimit,
            100, 30m, "FIRM",
            timeInForce: TimeInForce.GTD,
            stopPrice: 29m,
            goodTillDate: new DateTimeOffset(2030, 1, 2, 18, 0, 0, TimeSpan.Zero));
        var replacement = Order.HydrateReplacement(orig, 2UL, 200, 31m, erLeaves: 200, erCumulative: 0);
        Assert.Equal(TimeInForce.GTD, replacement.TimeInForce);
        Assert.Equal(29m, replacement.StopPrice);
        Assert.Equal(orig.GoodTillDate, replacement.GoodTillDate);
    }

    [Fact]
    public void HydrateReplacement_overridesStopPriceOnExistingStopLimit()
    {
        var owner = new EndClientId("alice");
        var orig = new Order(
            1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.StopLimit,
            100, 30m, "FIRM",
            timeInForce: TimeInForce.Day, stopPrice: 29m);
        var replacement = Order.HydrateReplacement(
            orig, 2UL, 100, 30m, erLeaves: 100, erCumulative: 0,
            requestedStopPrice: 28.5m);
        Assert.Equal(28.5m, replacement.StopPrice);
        Assert.Equal(TimeInForce.Day, replacement.TimeInForce);
    }

    [Fact]
    public void Merge_changesTifDayToGtc()
    {
        var (tif, stop, gtd) = Order.MergeReplacementOptionals(
            OrderType.Limit, TimeInForce.Day, originalStopPrice: null, originalGoodTillDate: null,
            requestedTimeInForce: TimeInForce.GTC, requestedStopPrice: null, requestedGoodTillDate: null);
        Assert.Equal(TimeInForce.GTC, tif);
        Assert.Null(stop);
        Assert.Null(gtd);
    }

    [Fact]
    public void Merge_changesTifDayToGtdRequiresGoodTillDate()
    {
        var future = DateTimeOffset.UtcNow.AddDays(1);
        var (tif, _, gtd) = Order.MergeReplacementOptionals(
            OrderType.Limit, TimeInForce.Day, null, null,
            TimeInForce.GTD, null, future);
        Assert.Equal(TimeInForce.GTD, tif);
        Assert.Equal(future, gtd);

        // Without supplying GoodTillDate the merge rejects (caller must
        // explicitly carry the expiry when transitioning into GTD).
        Assert.Throws<ArgumentException>(() => Order.MergeReplacementOptionals(
            OrderType.Limit, TimeInForce.Day, null, null,
            TimeInForce.GTD, null, null));
    }

    [Fact]
    public void Merge_tifAwayFromGtdAutoClearsGoodTillDate()
    {
        // Documented semantic: changing TIF away from GTD without
        // explicitly nulling GoodTillDate auto-clears the inherited
        // expiry rather than forcing a redundant null in the request.
        var origGtd = new DateTimeOffset(2030, 6, 1, 18, 0, 0, TimeSpan.Zero);
        var (tif, _, gtd) = Order.MergeReplacementOptionals(
            OrderType.Limit, TimeInForce.GTD, null, origGtd,
            TimeInForce.Day, null, null);
        Assert.Equal(TimeInForce.Day, tif);
        Assert.Null(gtd);
    }

    [Fact]
    public void Merge_rejectsGoodTillDateWhenEffectiveTifIsNotGtd()
    {
        // Caller asked TIF=Day but supplied a GoodTillDate. Auto-clearing
        // would silently discard the value; reject loudly instead so the
        // caller fixes their request.
        var fut = DateTimeOffset.UtcNow.AddDays(1);
        Assert.Throws<ArgumentException>(() => Order.MergeReplacementOptionals(
            OrderType.Limit, TimeInForce.GTD, null, fut,
            TimeInForce.Day, null, fut));
    }

    [Fact]
    public void Merge_rejectsStopPriceForNonStopOrder()
    {
        Assert.Throws<ArgumentException>(() => Order.MergeReplacementOptionals(
            OrderType.Limit, TimeInForce.Day, null, null,
            null, requestedStopPrice: 10m, null));
    }

    [Fact]
    public void Merge_rejectsNonPositiveStopPriceForStopOrder()
    {
        // Original StopLimit has StopPrice=29; caller "overrides" with 0.
        Assert.Throws<ArgumentException>(() => Order.MergeReplacementOptionals(
            OrderType.StopLimit, TimeInForce.Day, originalStopPrice: 29m, null,
            null, requestedStopPrice: 0m, null));
    }

    [Fact]
    public void HydrateReplacement_changesTifDayToGtdWithExpiry()
    {
        var owner = new EndClientId("alice");
        var orig = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM");
        var future = new DateTimeOffset(2030, 12, 31, 18, 0, 0, TimeSpan.Zero);
        var replacement = Order.HydrateReplacement(
            orig, 2UL, 100, 30m, erLeaves: 100, erCumulative: 0,
            requestedTimeInForce: TimeInForce.GTD,
            requestedGoodTillDate: future);
        Assert.Equal(TimeInForce.GTD, replacement.TimeInForce);
        Assert.Equal(future, replacement.GoodTillDate);
    }

    [Fact]
    public void HydrateReplacement_changesTifGtdToDayClearsGoodTillDate()
    {
        var owner = new EndClientId("alice");
        var orig = new Order(
            1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, "FIRM",
            timeInForce: TimeInForce.GTD,
            goodTillDate: new DateTimeOffset(2030, 6, 1, 18, 0, 0, TimeSpan.Zero));
        var replacement = Order.HydrateReplacement(
            orig, 2UL, 100, 30m, erLeaves: 100, erCumulative: 0,
            requestedTimeInForce: TimeInForce.Day);
        Assert.Equal(TimeInForce.Day, replacement.TimeInForce);
        Assert.Null(replacement.GoodTillDate);
    }

    [Fact]
    public void OrderReplaceRequestedEvent_OldPayloadDeserializesWithNullOptionals()
    {
        // Backward-compat: WAL segments written before the Q1.1
        // overrides existed lack the three Requested* fields. The
        // source-generated context must hydrate them as null so the
        // ER-replay path inherits everything from the original Order
        // (preserving exact pre-Q1.1 behaviour).
        const string oldJson = """
        {
            "OriginalClOrdId": 1,
            "NewClOrdId": 2,
            "EndClientId": "alice",
            "FirmId": "FIRM",
            "Symbol": "PETR4",
            "SecurityId": 4321,
            "Side": "Buy",
            "Type": "Limit",
            "NewQuantity": 200,
            "NewPrice": 30.5
        }
        """;
        var ev = System.Text.Json.JsonSerializer.Deserialize(
            oldJson,
            B3.Trading.Application.Persistence.WalEventJsonContext.Default.OrderReplaceRequestedEvent);
        Assert.NotNull(ev);
        Assert.Equal(1UL, ev!.OriginalClOrdId);
        Assert.Equal(2UL, ev.NewClOrdId);
        Assert.Null(ev.RequestedTimeInForce);
        Assert.Null(ev.RequestedStopPrice);
        Assert.Null(ev.RequestedGoodTillDate);
    }

    [Fact]
    public void OrderReplaceRequestedEvent_RoundTripsOptionalsWhenSet()
    {
        var future = new DateTimeOffset(2030, 6, 1, 18, 0, 0, TimeSpan.Zero);
        var ev = new OrderReplaceRequestedEvent
        {
            OriginalClOrdId = 1UL,
            NewClOrdId = 2UL,
            EndClientId = "alice",
            FirmId = "FIRM",
            Symbol = "PETR4",
            SecurityId = 4321UL,
            Side = "Buy",
            Type = "Limit",
            NewQuantity = 100,
            NewPrice = 30m,
            RequestedTimeInForce = nameof(TimeInForce.GTD),
            RequestedStopPrice = null,
            RequestedGoodTillDate = future,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            ev, B3.Trading.Application.Persistence.WalEventJsonContext.Default.OrderReplaceRequestedEvent);
        var roundtrip = System.Text.Json.JsonSerializer.Deserialize(
            json, B3.Trading.Application.Persistence.WalEventJsonContext.Default.OrderReplaceRequestedEvent);
        Assert.Equal(nameof(TimeInForce.GTD), roundtrip!.RequestedTimeInForce);
        Assert.Equal(future, roundtrip.RequestedGoodTillDate);
    }

    // ---------------- OrderModifyService end-to-end (Q1.1 dispatch) ----------------

    private sealed class CapturingGateway : IExchangeGateway
    {
        public List<(Order Original, ulong NewClOrdId, long NewQty, decimal? NewPrice,
                     TimeInForce? Tif, decimal? Stop, DateTimeOffset? Gtd)> Replaces
        { get; } = new();

        public Task SubmitAsync(Order order, CancellationToken ct) => Task.CompletedTask;
        public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken ct) => Task.CompletedTask;
        public Task CancelReplaceAsync(
            Order original, ulong newClOrdId, long newQty, decimal? newPrice,
            TimeInForce? tif, decimal? stop, DateTimeOffset? gtd, CancellationToken ct)
        {
            Replaces.Add((original, newClOrdId, newQty, newPrice, tif, stop, gtd));
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpReplaceMargin : IReplaceMarginCoordinator
    {
        public Task<RiskDecision> PrepareReplaceAsync(ulong _, ulong __, EndClientId ___, decimal ____, CancellationToken _____)
            => Task.FromResult(RiskDecision.Approve);
        public void CommitReplace(ulong _, ulong __, decimal ___) { }
        public void AbortReplace(ulong _) { }
    }

    private sealed class CapturingSink : IExecutionEventSink
    {
        public readonly List<ExecutionEvent> Events = new();
        public void Publish(ExecutionEvent ev) => Events.Add(ev);
    }

    private sealed class NeverDrain : Lifecycle.IDrainGate { public bool IsDraining => false; }

    private static (OrderModifyService svc, CapturingGateway gw, WorkingOrderBook book, PendingReplacementRegistry reg)
        BuildModifyService(Order seedOrder)
    {
        var owner = seedOrder.Owner;
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(seedOrder));
        ownership.Register(seedOrder.ClOrdId, owner);
        var gateway = new CapturingGateway();
        var sink = new CapturingSink();
        var risk = new Risk.RiskPipeline(Array.Empty<Risk.IRiskCheck>());
        var margin = new NoOpReplaceMargin();
        var replacements = new PendingReplacementRegistry();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var svc = new OrderModifyService(
            clOrdIds, ownership, book, gateway, sink, risk, margin, replacements, dispatcher,
            new NeverDrain(), NullLogger<OrderModifyService>.Instance);
        return (svc, gateway, book, replacements);
    }

    [Fact]
    public async Task Modify_changesStopPriceOnExistingStopLimit_outboundCarriesNewStopPrice()
    {
        var owner = new EndClientId("alice");
        var stopLimit = new Order(
            1_000_001UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.StopLimit,
            100, 30m, "FIRM", timeInForce: TimeInForce.Day, stopPrice: 29m);
        var (svc, gw, _, _) = BuildModifyService(stopLimit);

        var result = await svc.ModifyAsync(
            new OrderModifyRequest(owner, stopLimit.ClOrdId, NewQuantity: 100, NewPrice: 30m,
                NewTimeInForce: null, NewStopPrice: 28.25m, NewGoodTillDate: null),
            CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.Accepted, result.Kind);
        var call = Assert.Single(gw.Replaces);
        Assert.Equal(28.25m, call.Stop);
        Assert.Null(call.Tif);
        Assert.Null(call.Gtd);
    }

    [Fact]
    public async Task Modify_changesTifDayToGtc_outboundCarriesGtc()
    {
        var owner = new EndClientId("alice");
        var orig = new Order(1_000_002UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM");
        var (svc, gw, _, _) = BuildModifyService(orig);

        var result = await svc.ModifyAsync(
            new OrderModifyRequest(owner, orig.ClOrdId, 100, 30m, NewTimeInForce: TimeInForce.GTC),
            CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.Accepted, result.Kind);
        Assert.Equal(TimeInForce.GTC, gw.Replaces.Single().Tif);
    }

    [Fact]
    public async Task Modify_changesTifDayToGtdWithExpiry_outboundCarriesBoth()
    {
        var owner = new EndClientId("alice");
        var orig = new Order(1_000_003UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM");
        var (svc, gw, _, _) = BuildModifyService(orig);
        var future = new DateTimeOffset(2030, 12, 31, 18, 0, 0, TimeSpan.Zero);

        var result = await svc.ModifyAsync(
            new OrderModifyRequest(owner, orig.ClOrdId, 100, 30m,
                NewTimeInForce: TimeInForce.GTD, NewGoodTillDate: future),
            CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.Accepted, result.Kind);
        var call = gw.Replaces.Single();
        Assert.Equal(TimeInForce.GTD, call.Tif);
        Assert.Equal(future, call.Gtd);
    }

    [Fact]
    public async Task Modify_invariantViolation_isRejectedBeforeWalAndGateway()
    {
        var owner = new EndClientId("alice");
        var orig = new Order(1_000_004UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM");
        var (svc, gw, book, reg) = BuildModifyService(orig);

        var result = await svc.ModifyAsync(
            new OrderModifyRequest(owner, orig.ClOrdId, 100, 30m, NewTimeInForce: TimeInForce.GTD),
            CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.BadRequest, result.Kind);
        Assert.NotNull(result.Reason);
        Assert.Contains("GoodTillDate", result.Reason);
        Assert.Empty(gw.Replaces);
        Assert.False(reg.IsOriginalInFlight(orig.ClOrdId));
        Assert.True(book.TryGet(orig.ClOrdId, out var still));
        Assert.NotNull(still);
        Assert.NotEqual(OrderStatus.Replaced, still!.Status);
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
        // #381. The new contract: replace-reject DOES surface a single
        // ExecKind.ReplaceRejected event scoped to OriginalClOrdId so the
        // operator's UI can release the optimistic Modify-inflight flag.
        // The replace-side Rejected event is BotRouter-only (#172 F) and
        // does not reach _sink in this test wiring (no botErRouter wired).
        var ev = Assert.Single(sink.Events);
        Assert.Equal(1UL, ev.ClOrdId);
        Assert.Equal(ExecKind.ReplaceRejected, ev.Kind);
        Assert.Equal(OrderStatus.PendingNew, ev.Status);
        Assert.Equal(100, ev.LeavesQuantity);
        Assert.Equal("tick-out-of-range", ev.RejectReason);
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
        // Replace-fanout: orig (kind=Replaced) + new (kind=ReplacedNew, #417) on the sink.
        Assert.Equal(2, sink.Events.Count);
        Assert.Equal(ExecKind.Replaced, sink.Events.Single(e => e.ClOrdId == 777UL).Kind);
        Assert.Equal(ExecKind.ReplacedNew, sink.Events.Single(e => e.ClOrdId == 778UL).Kind);
        // #417: bot router is wire-faithful — sees ONE ExecutionReport
        // for the replace ack (the new leg, carrying OrigClOrdID via
        // the FIXP encoder), not two. The orig-leg terminalisation is
        // an internal WS projection only.
        Assert.Single(botRouter.Events);
        Assert.Equal(778UL, botRouter.Events[0].ClOrdId);
        Assert.Equal(ExecKind.ReplacedNew, botRouter.Events[0].Kind);

        // 4) Subsequent Trade ER on the new ClOrdID (orig=0 in priority-lost trade).
        proc.Apply(778UL, ExecKind.Fill, leaves: 0, cumQty: 100, lastQty: 100, lastPx: 32.50m, rejectReason: null, origClOrdId: 0);

        Assert.Equal(OrderStatus.Filled, newOrder.Status);
        Assert.Equal(0, newOrder.LeavesQuantity);
        Assert.Equal(100, newOrder.CumulativeQuantity);
        // Position booked.
        var pos = positions.GetOrCreate("FIRM", bob, "PETR4");
        Assert.Equal(100, pos.NetQuantity);
        Assert.Equal(32.50m, pos.AverageEntryPrice);
        // Cash debited (Buy: available drops by notional).
        Assert.Equal(100_000m - (32.50m * 100), cash.GetAvailable(bob));
        // Fill ER published on sink + routed to bot.
        Assert.Equal(3, sink.Events.Count);
        var fillEv = sink.Events[2];
        Assert.Equal(ExecKind.Fill, fillEv.Kind);
        Assert.Equal(778UL, fillEv.ClOrdId);
        // #417: bot saw 1 replace ER (new leg only) + 1 fill = 2 total.
        Assert.Equal(2, botRouter.Events.Count);
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

        // Second Cancel ER using the same (new, orig) shape — registry
        // is already consumed, but the standard path still resolves
        // Canceled(new, orig) back to the ORIGINAL order. That means
        // this replay/audit-trail shape is a no-op against the already-
        // Replaced original; it does NOT cancel the hydrated new order.
        // The more important assertion here is still no throw, no
        // double-add, no reservation leak (no extra Commit/Abort on the
        // coordinator).
        var origStatusBefore = orig.Status;
        proc.Apply(778UL, ExecKind.Canceled, 0, 0, 0, 0m, null, origClOrdId: 777UL);
        Assert.Equal(origStatusBefore, orig.Status); // original untouched
        Assert.Single(replaceCoord.Commits); // no extra commit
        Assert.Empty(replaceCoord.Aborts);   // no leak
        Assert.True(book.TryGet(778UL, out var stillThere));
        Assert.Same(newOrder, stillThere);   // hydrated replacement untouched
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

    // ---------------- PR #260 P2: IsMarginBearing predicate covers StopLimit / MarketWithLeftover ----------------

    private static (OrderModifyService svc, CapturingGateway gw, RecordingReplaceCoordinator coord)
        BuildModifyServiceWithCoordinator(Order seedOrder)
    {
        var owner = seedOrder.Owner;
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(seedOrder));
        ownership.Register(seedOrder.ClOrdId, owner);
        var gateway = new CapturingGateway();
        var sink = new CapturingSink();
        var risk = new Risk.RiskPipeline(Array.Empty<Risk.IRiskCheck>());
        var coord = new RecordingReplaceCoordinator();
        var replacements = new PendingReplacementRegistry();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var svc = new OrderModifyService(
            clOrdIds, ownership, book, gateway, sink, risk, coord, replacements, dispatcher,
            new NeverDrain(), NullLogger<OrderModifyService>.Instance);
        return (svc, gateway, coord);
    }

    [Fact]
    public async Task Modify_StopLimit_downsize_passesNewNotionalToCoordinator_notZero()
    {
        // Pre-fix the gate was `orig.Type == OrderType.Limit`, so a
        // buy StopLimit replace silently passed 0 to PrepareReplaceAsync
        // (and later 0 to CommitReplace), freeing the cash-reservation
        // even though the new working order still consumed margin.
        var owner = new EndClientId("alice");
        var stopLimit = new Order(
            500_001UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.StopLimit,
            100, 30m, "FIRM", timeInForce: TimeInForce.Day, stopPrice: 29m);
        var (svc, _, coord) = BuildModifyServiceWithCoordinator(stopLimit);

        // Downsize 100 → 60 at the same limit price.
        var result = await svc.ModifyAsync(
            new OrderModifyRequest(owner, stopLimit.ClOrdId, NewQuantity: 60, NewPrice: 30m),
            CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.Accepted, result.Kind);
        var prep = Assert.Single(coord.Prepares);
        Assert.Equal(30m * 60, prep.Notional);
    }

    [Fact]
    public async Task Modify_StopLimit_upsize_deltaCheckEnforced_rejectsWhenInsufficient()
    {
        // Real provider so the upsize delta check actually fires. Pre-fix
        // a buy StopLimit upsize sent 0 to PrepareReplaceAsync, so the
        // capacity check was bypassed and an over-margin upsize would
        // sneak through.
        var owner = new EndClientId("alice");
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var stopLimit = new Order(
            500_002UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.StopLimit,
            100, 30m, "FIRM", timeInForce: TimeInForce.Day, stopPrice: 29m);
        Assert.True(book.TryAdd(stopLimit));
        ownership.Register(stopLimit.ClOrdId, owner);

        var opts = new RiskOptions();
        opts.Margin.Enabled = true;
        opts.Margin.Initial["alice"] = 3_500m; // room for 100*30=3000 + only 500 headroom
        var monitor = new StaticOptionsMonitor<RiskOptions>(opts);
        var margin = new ReserveOnSubmitMarginProvider(monitor, NullLogger<ReserveOnSubmitMarginProvider>.Instance);
        Assert.True((await margin.TryReserveAsync(
            stopLimit.ClOrdId,
            new RiskContext(owner, "FIRM", "PETR4", OrderSide.Buy, OrderType.StopLimit, 100, 30m),
            default)).Approved);
        Assert.Equal(3000m, margin.ReservedForTesting("alice"));

        var svc = new OrderModifyService(
            clOrdIds, ownership, book, new CapturingGateway(), new CapturingSink(),
            new Risk.RiskPipeline(Array.Empty<Risk.IRiskCheck>()),
            margin, new PendingReplacementRegistry(), new EventDispatcher(new NullEventStore()),
            new NeverDrain(), NullLogger<OrderModifyService>.Instance);

        // Upsize 100 → 200 @ 30 = 6000, delta = +3000 against 500 headroom → reject.
        var result = await svc.ModifyAsync(
            new OrderModifyRequest(owner, stopLimit.ClOrdId, NewQuantity: 200, NewPrice: 30m),
            CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.RiskRejected, result.Kind);
        Assert.Contains("insufficient margin", result.Reason);
        // Original reservation untouched.
        Assert.Equal(3000m, margin.ReservedForTesting("alice"));
    }

    [Fact]
    public async Task Modify_MarketWithLeftover_downsize_passesNewNotionalToCoordinator_notZero()
    {
        // MarketWithLeftover carries a Price (the leftover-as-limit
        // price) and is therefore reserved on submit by the
        // ReserveOnSubmitMarginProvider — so the same gate fix applies
        // here, otherwise replace silently frees the cash.
        var owner = new EndClientId("alice");
        var mwl = new Order(
            500_003UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.MarketWithLeftover,
            100, 30m, "FIRM");
        var (svc, _, coord) = BuildModifyServiceWithCoordinator(mwl);

        var result = await svc.ModifyAsync(
            new OrderModifyRequest(owner, mwl.ClOrdId, NewQuantity: 50, NewPrice: 30m),
            CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.Accepted, result.Kind);
        var prep = Assert.Single(coord.Prepares);
        Assert.Equal(30m * 50, prep.Notional);
    }

    [Fact]
    public async Task Processor_Replaced_StopLimit_commitsAtNewNotional_notZero()
    {
        // Processor-side mirror of the same predicate fix: confirmedRemaining
        // for StopLimit must reflect price * leaves so CommitReplace
        // rebalances the ledger. Pre-fix it was 0, releasing the original
        // reservation while the new order was still alive in the book.
        var (proc, ownership, book, reg, margin, _) = BuildProcessor();
        var owner = new EndClientId("alice");
        var orig = new Order(
            10UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.StopLimit,
            100, 10m, "FIRM", timeInForce: TimeInForce.Day, stopPrice: 9m);
        book.TryAdd(orig);
        ownership.Register(10UL, owner);
        Assert.True((await margin.TryReserveAsync(
            10UL,
            new RiskContext(owner, "FIRM", "PETR4", OrderSide.Buy, OrderType.StopLimit, 100, 10m),
            default)).Approved);

        // Downsize 100 → 60 @ 10 → new reservation should be 600.
        Assert.True(reg.TryAdd(new OrderReplacementIntent(
            10UL, 11UL, owner, "PETR4", 4321UL,
            OrderSide.Buy, OrderType.StopLimit, 60, 10m, "FIRM", null, null)));
        await ((IReplaceMarginCoordinator)margin).PrepareReplaceAsync(10UL, 11UL, owner, 600m, default);

        proc.Apply(11UL, ExecKind.Replaced, leaves: 60, cumQty: 0, lastQty: 0,
                   lastPx: 0m, rejectReason: null, origClOrdId: 10UL);

        Assert.Equal(600m, margin.ReservedForTesting("alice"));
    }

    // ───── Pass-4 review (#299) P1 — Canceled ER for orig clears ambiguous intent ─────

    [Fact]
    public async Task Processor_CancelledOrigWithPendingReplaceIntent_ReleasesHeldReservationAndClearsIntent()
    {
        // Pass-4 review (#299) P1. Scenario: AlgoEngine modify
        // dispatched ambiguous (gateway threw), so the engine KEEPS
        // both the PendingReplacementRegistry intent AND the held
        // upsize-delta margin reservation. The venue ultimately
        // emits a Cancelled ER for the ORIG (i.e. the venue
        // accepted the cancel half of the cancel-replace but
        // dropped the replacement — or never registered the
        // replacement at all). The ER processor must now release
        // the still-held upsize delta + clear the pending intent
        // so a stray late ER under the never-created new ClOrdID
        // is not misinterpreted as a replace confirmation.
        var (proc, ownership, book, reg, margin, _) = BuildProcessor();
        var owner = new EndClientId("alice");
        var orig = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM");
        book.TryAdd(orig);
        orig.MarkWorking();
        ownership.Register(1UL, owner);
        Assert.True((await margin.TryReserveAsync(1UL, BuyCtx("alice", 30m, 100), default)).Approved);
        Assert.Equal(3000m, margin.ReservedForTesting("alice"));

        // Pass-1 ambiguous-send state: intent in the registry +
        // Prepare-held upsize delta + AmbiguousMarginHeld marker.
        Assert.True(reg.TryAdd(BuyLimitIntent(1UL, 2UL, "alice", 100, 30.5m), DateTimeOffset.UtcNow));
        Assert.True((await ((IReplaceMarginCoordinator)margin).PrepareReplaceAsync(
            1UL, 2UL, owner, 3050m, default)).Approved);
        Assert.True(reg.MarkAmbiguousMarginHeld(2UL));
        Assert.Equal(3050m, margin.ReservedForTesting("alice"));

        // Venue Cancelled the orig — the cancel-replace's "replace"
        // half never materialised. Drive a Canceled ER for the orig.
        proc.Apply(1UL, ExecKind.Canceled, leaves: 0, cumQty: 0,
                   lastQty: 0, lastPx: 0m, rejectReason: null);

        // Original transitioned + upsize delta released back to 0.
        Assert.Equal(OrderStatus.Cancelled, orig.Status);
        Assert.Equal(0m, margin.ReservedForTesting("alice"));
        // Intent cleared from both the new-ClOrdID and orig indexes,
        // so neither a stray ER nor a follow-up modify on the same
        // orig is blocked by phantom in-flight state.
        Assert.False(reg.TryGet(2UL, out _));
        Assert.False(reg.IsOriginalInFlight(1UL));
    }

    // ─────────── Pass-5 review (#299) P1 — restart re-establishes held reservation ───────────

    [Fact]
    public async Task EventReplayer_AmbiguousMarginHeldEvent_ReEstablishesReservation_BlocksCompetingOrderPostRestart()
    {
        // Pass-5 review (#299) P1. The pass-4 fix kept the upsize-
        // delta margin reservation tied to the in-memory ambiguous-
        // held flag so a late Replaced ER could converge without
        // breaking the over-allocation invariant. But the flag lived
        // ONLY in memory: a crash between the OrderReplaceRequestedEvent
        // append and the next periodic snapshot lost it, replay re-
        // added the intent without the flag (so the TTL sweep never
        // reaped it), and capacity returned post-restart while the
        // venue might still send Replaced — at which point
        // CommitReplace landed in the "neither side has owner" branch
        // and dropped the reservation entirely, or (after Prepare ran
        // again) double-added it.
        //
        // Pass-5 fixes the durability hole with a dedicated WAL event
        // that the EventReplayer translates back into both the
        // PrepareReplaceAsync re-reservation and the in-memory flag
        // mark. This test exercises the replay path directly and
        // asserts that a competing order placed POST-replay cannot
        // consume the held capacity — i.e. the over-allocation
        // invariant is preserved across the crash.
        //
        // Owner cap = 3050 (matches the pass-4 endpoint test); held
        // delta = full new notional of 3050 because the original's
        // pre-crash reservation is NOT replayed (per the documented
        // "reservations are not durable across restart" semantics of
        // OrderSubmittedEvent — only the held ambiguous slot is).
        var (_, ownership, book, reg, margin, _) = BuildProcessor(initial: 3050m);
        var processor = new ExecutionReportProcessor(
            ownership, book, new PositionKeeper(), new CaptureSink(), margin,
            NullLogger<ExecutionReportProcessor>.Instance,
            algoSignals: null, cash: null,
            replacements: reg, replaceMargin: margin);

        // POST-RESTART world: nothing reserved yet. The intent has
        // been re-registered by replay of the OrderReplaceRequestedEvent
        // (mirror that shape here directly).
        var heldAt = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        Assert.True(reg.TryAdd(BuyLimitIntent(1UL, 2UL, "alice", 100, 30.5m), heldAt));
        Assert.Equal(0m, margin.ReservedForTesting("alice"));

        var replayer = new EventReplayer(book, ownership,
            new KillSwitchService(), new SymbolHaltService(), new SessionPhaseService(),
            processor, new AlgoBook(),
            new ClOrdIdPrefixRegistry(), new AlgoIdRegistry(),
            replacements: reg,
            replaceMargin: margin);

        // Replay the durable ambiguous-held event.
        replayer.Apply(new OrderReplaceAmbiguousMarginHeldEvent
        {
            NewClOrdId = 2UL,
            OriginalClOrdId = 1UL,
            EndClientId = "alice",
            NewRemainingNotional = 3050m,
            HeldAtUtc = heldAt,
        });

        // Reservation re-established to the full held delta (3050) —
        // exactly the value that would have been held pre-crash.
        Assert.Equal(3050m, margin.ReservedForTesting("alice"));

        // A competing order trying to grab the freed capacity MUST
        // be rejected: only 0 of the 3050 cap is available.
        var owner = new EndClientId("alice");
        var competing = await margin.TryReserveAsync(
            clOrdId: 99UL,
            new RiskContext(owner, "FIRM", "PETR4",
                OrderSide.Buy, OrderType.Limit, 1, 30.0m),
            CancellationToken.None);
        Assert.False(competing.Approved);

        // The intent is back in the ambiguous-held state so the
        // post-restart TTL sweep can converge on the same deadline
        // the pre-crash sweep would have observed.
        Assert.Equal(1, reg.AmbiguousCountForTesting);

        // Sanity: if the persistence is REMOVED (i.e. the replay
        // case branch goes away, simulating pre-pass-5 behaviour),
        // this assertion flips — competing would be approved
        // because reserved stayed at 0. That's the regression the
        // test guards against.
    }

    // ─────────── Pass-6 review (#299) P1 — snapshot capture/restore re-establishes held reservation ───────────

    [Fact]
    public async Task StateSnapshotter_CaptureRestore_AmbiguousMarginHeldEntry_ReEstablishesReservation_BlocksCompetingOrderPostRestart()
    {
        // Pass-6 review (#299) P1. The pass-5 fix made the held
        // reservation durable via OrderReplaceAmbiguousMarginHeldEvent
        // so a snapshot-less / WAL-tail-only recovery could re-call
        // PrepareReplaceAsync. But CaptureRaw never projected the
        // PendingReplacementRegistry into the persisted snapshot:
        // when the periodic snapshotter ran AFTER the ambiguous mark
        // its Seq advanced past the OrderReplaceAmbiguousMarginHeldEvent,
        // recovery loaded the snapshot and started its WAL read past
        // that event, the snapshot didn't carry the entry, and so the
        // post-restart sweep had no ambiguous entry at all — a late
        // Replaced/Rejected ER missed the registry path and the
        // reservation leaked (or worse, was returned to free capacity
        // by a competing order winning the race).
        //
        // The pass-6 fix persists every PendingReplacementRegistry
        // entry — including the AmbiguousMarginHeld flag + HeldAtUtc +
        // NewRemainingNotional — in the snapshot, AND re-invokes
        // PrepareReplaceAsync on restore for every ambiguous-flagged
        // entry. This test exercises the snapshot path end-to-end
        // (no WAL replay) and asserts the same over-allocation
        // invariant the pass-5 WAL-replay test guards.
        //
        // Owner cap = 3050; original Buy 100 @ 30 (3000); modify
        // requests 100 @ 30.5 → new notional 3050 (upsize delta 50).
        // After the ambiguous send, the upsize delta is held under
        // the new ClOrdID alongside the still-reserved original
        // 3000 — total 3050 = full cap. Post-restart we only model
        // the held NEW-side reservation (the original reservation,
        // like every plain OrderSubmittedEvent reservation, is NOT
        // durable across restart per the documented semantics —
        // only the ambiguous-held slot is). So the new-side
        // re-reservation alone must consume 3050 of the 3050 cap.

        // ── Pre-crash world ──
        var pre = new TestWorld(initial: 3050m);
        var owner = new EndClientId("alice");
        var orig = new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m, "FIRM");
        pre.Book.TryAdd(orig);
        orig.MarkWorking();
        pre.Ownership.Register(1UL, owner);
        Assert.True((await pre.Margin.TryReserveAsync(1UL, BuyCtx("alice", 30m, 100), default)).Approved);
        Assert.True(pre.Replacements.TryAdd(BuyLimitIntent(1UL, 2UL, "alice", 100, 30.5m),
            createdAt: new DateTimeOffset(2026, 1, 1, 8, 59, 0, TimeSpan.Zero)));
        Assert.True((await ((IReplaceMarginCoordinator)pre.Margin).PrepareReplaceAsync(
            1UL, 2UL, owner, 3050m, default)).Approved);
        var heldAt = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        Assert.True(pre.Replacements.MarkAmbiguousMarginHeld(2UL, heldAt, newRemainingNotional: 3050m));

        // Periodic snapshot captured AFTER the ambiguous mark.
        var snap = pre.Snapshotter.Capture(seq: 42L);

        // Sanity: snapshot carries the entry (the bug pre-pass-6 was
        // exactly that this list was empty).
        Assert.Single(snap.PendingReplacements);
        var persisted = snap.PendingReplacements[0];
        Assert.Equal(2UL, persisted.NewClOrdId);
        Assert.True(persisted.AmbiguousMarginHeld);
        Assert.Equal(heldAt, persisted.AmbiguousAtUtc);
        Assert.Equal(3050m, persisted.NewRemainingNotional);

        // ── Crash + restart ── fresh world (clean ownership map,
        // empty book, zero-reservation margin provider, empty
        // registry). No WAL tail; recovery is snapshot-only.
        var post = new TestWorld(initial: 3050m);
        Assert.Equal(0m, post.Margin.ReservedForTesting("alice"));
        post.Snapshotter.Restore(snap);

        // Reservation re-established to the full held delta — exactly
        // what the pre-crash dispatch had reserved under the new
        // ClOrdID — and the registry entry is back in the ambiguous
        // state so the post-restart TTL sweep ages from the same
        // wall-clock the pre-crash sweep would have observed.
        Assert.Equal(3050m, post.Margin.ReservedForTesting("alice"));
        Assert.Equal(1, post.Replacements.AmbiguousCountForTesting);
        Assert.True(post.Replacements.TryGet(2UL, out var rehydrated));
        Assert.NotNull(rehydrated);
        Assert.Equal(1UL, rehydrated!.OriginalClOrdId);

        // A competing order trying to grab the (would-be-free)
        // capacity MUST be rejected.
        var competing = await post.Margin.TryReserveAsync(
            clOrdId: 99UL,
            new RiskContext(owner, "FIRM", "PETR4",
                OrderSide.Buy, OrderType.Limit, 1, 30.0m),
            CancellationToken.None);
        Assert.False(competing.Approved);

        // Late Replaced ER under the new ClOrdID converges through
        // the registry path: CommitReplace finalises the reservation
        // at the venue-confirmed leaves (100 @ 30.5 = 3050) so the
        // owner stays fully reserved. Without the snapshot
        // re-hydration above this branch would land in the
        // "no intent" fallback and silently drop the held delta.
        var processor = new ExecutionReportProcessor(
            post.Ownership, post.Book, new PositionKeeper(), new CaptureSink(), post.Margin,
            NullLogger<ExecutionReportProcessor>.Instance,
            algoSignals: null, cash: null,
            replacements: post.Replacements, replaceMargin: post.Margin);
        processor.Apply(2UL, ExecKind.Replaced, leaves: 100, cumQty: 0, lastQty: 0,
                        lastPx: 0m, rejectReason: null, origClOrdId: 1UL);
        Assert.Equal(3050m, post.Margin.ReservedForTesting("alice"));
        Assert.False(post.Replacements.TryGet(2UL, out _));

        // Regression guard: if the snapshot capture or the restore
        // re-hydration is removed (the pre-pass-6 bug), the snapshot
        // would carry no entry, post.Restore would no-op, reserved
        // would stay at 0, competing would be Approved, and the
        // Replaced ER would land outside the registry path. Every
        // assertion above flips — exactly the snapshot-tail
        // recovery hole pass-6 closes.
    }

    private sealed class TestWorld
    {
        public OrderOwnershipMap Ownership { get; } = new();
        public WorkingOrderBook Book { get; } = new();
        public PositionKeeper Positions { get; } = new();
        public ReserveOnSubmitMarginProvider Margin { get; }
        public PendingReplacementRegistry Replacements { get; } = new();
        public StateSnapshotter Snapshotter { get; }

        public TestWorld(decimal initial)
        {
            var opts = new RiskOptions();
            opts.Margin.Enabled = true;
            opts.Margin.Initial["alice"] = initial;
            var monitor = new StaticOptionsMonitor<RiskOptions>(opts);
            Margin = new ReserveOnSubmitMarginProvider(monitor, NullLogger<ReserveOnSubmitMarginProvider>.Instance);
            Snapshotter = new StateSnapshotter(
                Book, Positions, new KillSwitchService(), new SymbolHaltService(), new SessionPhaseService(),
                new ClOrdIdPrefixRegistry(), Ownership, new AlgoBook(), new AlgoIdRegistry(), new CashLedger(),
                replacements: Replacements,
                replaceMargin: Margin);
        }
    }

    // ---------------- PR #316 P1: sub-account risk on modify ----------------

    private static (OrderModifyService svc, CapturingGateway gw, SubAccountPositionKeeper subPos, SubAccountsRegistry reg)
        BuildModifyServiceWithSubAccountRisk(
            Order seedOrder,
            SubAccountRiskOptions opts,
            Action<SubAccountsRegistry>? configureRegistry = null)
    {
        var owner = seedOrder.Owner;
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        Assert.True(book.TryAdd(seedOrder));
        ownership.Register(seedOrder.ClOrdId, owner);
        var gateway = new CapturingGateway();
        var sink = new CapturingSink();
        var subPos = new SubAccountPositionKeeper();
        var registry = new SubAccountsRegistry();
        configureRegistry?.Invoke(registry);
        var subCheck = new Risk.Checks.SubAccountLimitsCheck(
            new StaticOptionsMonitor<SubAccountRiskOptions>(opts), book, subPos, registry);
        var risk = new Risk.RiskPipeline(new Risk.IRiskCheck[] { subCheck });
        var margin = new NoOpReplaceMargin();
        var replacements = new PendingReplacementRegistry();
        var dispatcher = new EventDispatcher(new NullEventStore());
        var svc = new OrderModifyService(
            clOrdIds, ownership, book, gateway, sink, risk, margin, replacements, dispatcher,
            new NeverDrain(), NullLogger<OrderModifyService>.Instance);
        return (svc, gateway, subPos, registry);
    }

    [Fact]
    public async Task Modify_subAccountPositionCap_rejectsWhenReplacementWouldBreach()
    {
        // PR #316 P1. Build a working buy in sub-account A with a
        // pre-existing 60-lot position; sub-account cap is 100. A
        // resize to 50 (delta +50) would project net 110 → reject.
        const string firm = "FIRM01";
        const string sub = "A";
        var owner = new EndClientId("alice");
        var seed = new Order(
            2_000_001UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            10, 30m, firm, subAccountId: new SubAccountId(sub));
        var opts = new SubAccountRiskOptions
        {
            PerFirm = new()
            {
                [firm] = new FirmSubAccountRiskOptions
                {
                    PerSubAccount = new() { [sub] = new SubAccountRiskLimits { PositionLimit = 100 } },
                },
            },
        };
        var (svc, gw, subPos, _) = BuildModifyServiceWithSubAccountRisk(
            seed, opts, reg => reg.ApplyCreated(firm, sub, null));
        // Seed an existing 60-lot net long in the sub-account.
        subPos.ApplyFill(firm, owner, new SubAccountId(sub), "PETR4", OrderSide.Buy, 60, 30m);

        var result = await svc.ModifyAsync(
            new OrderModifyRequest(owner, seed.ClOrdId, NewQuantity: 50, NewPrice: 30m),
            CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.RiskRejected, result.Kind);
        Assert.StartsWith("sub_account_limit_exceeded", result.Reason);
        Assert.Empty(gw.Replaces);
    }

    [Fact]
    public async Task Modify_deactivatedSubAccount_rejectsWithDistinctReason()
    {
        // PR #316 P1. The original is booked against sub-account A;
        // A is then deactivated. A modify must be rejected with the
        // dedicated `sub_account_deactivated` reason (not aliased to
        // the cap-breach reason) so operators and metrics can tell
        // them apart.
        const string firm = "FIRM01";
        const string sub = "A";
        var owner = new EndClientId("alice");
        var seed = new Order(
            2_000_002UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            10, 30m, firm, subAccountId: new SubAccountId(sub));
        var (svc, gw, _, _) = BuildModifyServiceWithSubAccountRisk(
            seed, new SubAccountRiskOptions(),
            reg =>
            {
                reg.ApplyCreated(firm, sub, null);
                reg.ApplyDeactivated(firm, sub);
            });

        var result = await svc.ModifyAsync(
            new OrderModifyRequest(owner, seed.ClOrdId, NewQuantity: 15, NewPrice: 30m),
            CancellationToken.None);

        Assert.Equal(OrderModifyResultKind.RiskRejected, result.Kind);
        Assert.StartsWith("sub_account_deactivated", result.Reason);
        Assert.DoesNotContain("sub_account_limit_exceeded", result.Reason);
        Assert.Empty(gw.Replaces);
    }
}
