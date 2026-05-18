using B3.Trading.Application.MarketData;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests;

/// <summary>
/// Q4.7 (#307). Capture-side coverage for the best-execution book-touch
/// evidence flow: the <see cref="EntryPointExecutionReportRouter"/>
/// reads the top-of-book from <see cref="PegBookTopCache"/> at the
/// instant a Fill / PartialFill lands, the snapshot is threaded through
/// the WAL + <see cref="ExecutionEvent"/>, and the
/// <see cref="FillProjection"/> indexes the record by
/// <c>{ClOrdId}:{cumQty}</c> for the REST + WS read paths.
/// </summary>
public class BookTouchCaptureTests
{
    private static readonly DateTimeOffset FillNow =
        new(2024, 11, 1, 13, 30, 0, TimeSpan.Zero);

    private static (PegBookTopCache Cache, FillProjection Fills, ExecutionReportProcessor Proc,
        WorkingOrderBook Book, OrderOwnershipMap Own, MockEntryPointClient Client,
        EntryPointExecutionReportRouter Router) Wire()
    {
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var sink = new NullSink();
        var cache = new PegBookTopCache();
        var fills = new FillProjection();
        var proc = new ExecutionReportProcessor(
            ownership, book, positions, sink, new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            fillProjection: fills);
        var dispatcher = new EventDispatcher(new NullEventStore());
        var client = new MockEntryPointClient();
        var router = new EntryPointExecutionReportRouter(client, proc, dispatcher, book, cache);
        return (cache, fills, proc, book, ownership, client, router);
    }

    [Fact]
    public void Capture_CacheFresh_PopulatesPricesAndStaleFalse()
    {
        var top = new PegBookTopCache();
        top.UpdateBookTop("PETR4", 29.95m, 30.05m, FillNow.AddMilliseconds(-50));
        top.UpdateLast("PETR4", 30.00m, FillNow.AddMilliseconds(-50));

        var snap = BookTouchSnapshot.Capture(top, "PETR4", FillNow);

        Assert.NotNull(snap);
        Assert.False(snap.Stale);
        Assert.Equal(29.95m, snap.BestBid);
        Assert.Equal(30.05m, snap.BestAsk);
        Assert.Equal(30.00m, snap.MidPrice);
        Assert.Equal(30.00m, snap.LastTradePrice);
        Assert.Equal(FillNow, snap.CapturedAtUtc);
    }

    [Fact]
    public void Capture_CacheOlderThan500ms_FlagsStaleButKeepsPrices()
    {
        var top = new PegBookTopCache();
        top.UpdateBookTop("PETR4", 29.95m, 30.05m, FillNow.AddSeconds(-2));

        var snap = BookTouchSnapshot.Capture(top, "PETR4", FillNow);

        Assert.True(snap.Stale);
        Assert.Equal(29.95m, snap.BestBid);
        Assert.Equal(30.05m, snap.BestAsk);
    }

    [Fact]
    public void Capture_CacheMiss_ReturnsNullPricesAndStaleTrue()
    {
        var top = new PegBookTopCache();

        var snap = BookTouchSnapshot.Capture(top, "PETR4", FillNow);

        Assert.True(snap.Stale);
        Assert.Null(snap.BestBid);
        Assert.Null(snap.BestAsk);
        Assert.Null(snap.MidPrice);
        Assert.Null(snap.LastTradePrice);
        Assert.Equal(FillNow, snap.CapturedAtUtc);
    }

    [Fact]
    public void RouterFill_FreshCache_RecordsFillWithTouch()
    {
        var (cache, fills, _, book, ownership, client, router) = Wire();
        using var _r = router;
        var owner = new EndClientId("alice");
        book.TryAdd(new Order(1UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m));
        ownership.Register(1UL, owner);
        // Freshly-updated cache — within the 500ms freshness window.
        cache.UpdateBookTop("PETR4", 29.99m, 30.01m, DateTimeOffset.UtcNow);
        cache.UpdateLast("PETR4", 30.00m, DateTimeOffset.UtcNow);

        client.EmitExecutionReport(new ExecutionReportEnvelope(1UL, EpExecType.Fill, 0, 100, 100, 30m, null));

        Assert.True(fills.TryGet(FillProjection.BuildId(1UL, 100), out var rec));
        Assert.NotNull(rec.BookTouch);
        Assert.False(rec.BookTouch!.Stale);
        Assert.Equal(29.99m, rec.BookTouch.BestBid);
        Assert.Equal(30.01m, rec.BookTouch.BestAsk);
        Assert.Equal(30.00m, rec.BookTouch.LastTradePrice);
    }

    [Fact]
    public void RouterFill_StaleCache_RecordsTouchFlaggedStale()
    {
        var (cache, fills, _, book, ownership, client, router) = Wire();
        using var _r = router;
        var owner = new EndClientId("alice");
        book.TryAdd(new Order(2UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 50, 30m));
        ownership.Register(2UL, owner);
        // Two-second old book-top — comfortably outside the 500ms window.
        cache.UpdateBookTop("PETR4", 29.50m, 30.50m, DateTimeOffset.UtcNow.AddSeconds(-2));

        client.EmitExecutionReport(new ExecutionReportEnvelope(2UL, EpExecType.PartialFill, 20, 30, 30, 30m, null));

        Assert.True(fills.TryGet(FillProjection.BuildId(2UL, 30), out var rec));
        Assert.NotNull(rec.BookTouch);
        Assert.True(rec.BookTouch!.Stale);
        Assert.Equal(29.50m, rec.BookTouch.BestBid);
        Assert.Equal(30.50m, rec.BookTouch.BestAsk);
    }

    [Fact]
    public void RouterFill_CacheMiss_RecordsNullPricesStaleTrue()
    {
        var (_, fills, _, book, ownership, client, router) = Wire();
        using var _r = router;
        var owner = new EndClientId("alice");
        book.TryAdd(new Order(3UL, owner, "VALE3", 5678UL, OrderSide.Sell, OrderType.Limit, 10, 60m));
        ownership.Register(3UL, owner);
        // Note: no UpdateBookTop / UpdateLast for VALE3 → cache miss.

        client.EmitExecutionReport(new ExecutionReportEnvelope(3UL, EpExecType.Fill, 0, 10, 10, 60m, null));

        Assert.True(fills.TryGet(FillProjection.BuildId(3UL, 10), out var rec));
        Assert.NotNull(rec.BookTouch);
        Assert.True(rec.BookTouch!.Stale);
        Assert.Null(rec.BookTouch.BestBid);
        Assert.Null(rec.BookTouch.BestAsk);
        Assert.Null(rec.BookTouch.MidPrice);
        Assert.Null(rec.BookTouch.LastTradePrice);
    }

    [Fact]
    public void RouterCancel_NoFill_NoProjectionEntry()
    {
        var (cache, fills, _, book, ownership, client, router) = Wire();
        using var _r = router;
        var owner = new EndClientId("alice");
        book.TryAdd(new Order(4UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m));
        ownership.Register(4UL, owner);
        cache.UpdateBookTop("PETR4", 29.99m, 30.01m, DateTimeOffset.UtcNow);

        client.EmitExecutionReport(new ExecutionReportEnvelope(4UL, EpExecType.Canceled, 0, 0, 0, 0m, null));

        Assert.Equal(0, fills.Count);
    }

    [Fact]
    public void ExecutionEvent_AdditiveBookTouch_DefaultsToNull()
    {
        // Smoke: every legacy ExecutionEvent construction path (without
        // BookTouch) keeps deserializing / equating to "no touch" so
        // existing consumers (drop-copy, executions.me) aren't broken.
        var ev = new ExecutionEvent(
            new EndClientId("alice"), 99UL, "PETR4", OrderSide.Buy, OrderStatus.Filled, ExecKind.Fill,
            0, 10, 10, 30m, null, DateTimeOffset.UtcNow);
        Assert.Null(ev.BookTouch);
    }

    [Fact]
    public void WalEvent_BookTouchField_RoundTripsThroughJson()
    {
        // The WAL event is serialised via the source-gen polymorphic
        // context. Verifying round-trip catches any forgotten
        // [JsonSerializable] entry for BookTouchSnapshot.
        var evt = new ExecutionReportReceivedEvent
        {
            ClOrdId = 7UL,
            ExecKind = "Fill",
            LeavesQuantity = 0,
            CumulativeQuantity = 50,
            LastQuantity = 50,
            LastPrice = 31m,
            Synthetic = false,
            BookTouch = new BookTouchSnapshot
            {
                BestBid = 30.95m,
                BestAsk = 31.05m,
                MidPrice = 31.00m,
                LastTradePrice = 31.00m,
                CapturedAtUtc = FillNow,
                Stale = false,
            },
        };
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            (WalEvent)evt, WalEventJsonContext.Default.WalEvent);
        var roundTripped = (ExecutionReportReceivedEvent)System.Text.Json.JsonSerializer.Deserialize(
            bytes, WalEventJsonContext.Default.WalEvent)!;
        Assert.NotNull(roundTripped.BookTouch);
        Assert.Equal(30.95m, roundTripped.BookTouch!.BestBid);
        Assert.False(roundTripped.BookTouch.Stale);
    }

    [Fact]
    public void WalEvent_LegacyPayload_WithoutBookTouch_ReplaysAsNull()
    {
        // Older WAL records have no "bookTouch" property at all. The
        // additive field must default to null — never throw — so the
        // replay path is forward + backward compatible.
        const string legacyJson =
            "{\"kind\":\"er.received\",\"clOrdId\":1,\"execKind\":\"Fill\",\"leavesQuantity\":0," +
            "\"cumulativeQuantity\":10,\"lastQuantity\":10,\"lastPrice\":30,\"synthetic\":false," +
            "\"timestampUtc\":\"2024-01-01T00:00:00+00:00\"}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(legacyJson);
        var evt = (ExecutionReportReceivedEvent)System.Text.Json.JsonSerializer.Deserialize(
            bytes, WalEventJsonContext.Default.WalEvent)!;
        Assert.Null(evt.BookTouch);
    }

    [Fact]
    public void EventReplayer_PreservesBookTouch_OnFullWalReplay()
    {
        // Pass-1 P1 regression: post-snapshot tail and no-snapshot WAL
        // replay paths must thread the additive BookTouch field. Prior
        // to the fix, EventReplayer.Apply(ExecutionReportReceivedEvent)
        // dropped er.BookTouch on the floor, leaving FillProjection
        // with a null touch even though the WAL carried one.
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var positions = new PositionKeeper();
        var sink = new NullSink();
        var fills = new FillProjection();
        var processor = new ExecutionReportProcessor(
            ownership, book, positions, sink, new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            fillProjection: fills);
        var owner = new EndClientId("alice");
        book.TryAdd(new Order(42UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m));
        ownership.Register(42UL, owner);

        var replayer = new EventReplayer(
            book, ownership, new Risk.KillSwitchService(), new Risk.SymbolHaltService(),
            new Risk.SessionPhaseService(), processor, new AlgoBook(),
            new ClOrdIdPrefixRegistry(), new AlgoIdRegistry());

        var touch = new BookTouchSnapshot
        {
            BestBid = 29.95m,
            BestAsk = 30.05m,
            MidPrice = 30.00m,
            LastTradePrice = 30.00m,
            CapturedAtUtc = FillNow,
            Stale = false,
        };
        replayer.Apply(new ExecutionReportReceivedEvent
        {
            TimestampUtc = FillNow,
            ClOrdId = 42UL,
            ExecKind = "Fill",
            LeavesQuantity = 0,
            CumulativeQuantity = 100,
            LastQuantity = 100,
            LastPrice = 30m,
            Synthetic = false,
            BookTouch = touch,
        });

        Assert.True(fills.TryGet(FillProjection.BuildId(42UL, 100), out var rec));
        Assert.NotNull(rec.BookTouch);
        Assert.Equal(29.95m, rec.BookTouch!.BestBid);
        Assert.Equal(30.05m, rec.BookTouch.BestAsk);
        Assert.False(rec.BookTouch.Stale);
    }

    [Fact]
    public void FillProjection_EvictsOldestPastCapacity()
    {
        // Pass-1 P2 regression: in-memory projection must not grow
        // unbounded. FillProjectionOptions.Capacity caps the dictionary
        // size; once exceeded, the oldest insertion is evicted FIFO so
        // memory stays bounded regardless of WAL retention.
        var opts = Microsoft.Extensions.Options.Options.Create(
            new FillProjectionOptions { Capacity = 3 });
        var fills = new FillProjection(opts);
        var owner = new EndClientId("alice");

        for (ulong i = 1; i <= 5; i++)
        {
            fills.Record(i, 10, owner, "FIRM01", "PETR4", OrderSide.Buy, 10, 30m, FillNow, bookTouch: null);
        }

        Assert.Equal(3, fills.Count);
        Assert.False(fills.TryGet(FillProjection.BuildId(1UL, 10), out _));
        Assert.False(fills.TryGet(FillProjection.BuildId(2UL, 10), out _));
        Assert.True(fills.TryGet(FillProjection.BuildId(3UL, 10), out _));
        Assert.True(fills.TryGet(FillProjection.BuildId(4UL, 10), out _));
        Assert.True(fills.TryGet(FillProjection.BuildId(5UL, 10), out _));
    }

    [Fact]
    public void FillProjection_RecordIfAbsent_PreservesOriginalTouch()
    {
        // Pass-2 P1 regression: SnapshotService pre-pass rehydrates
        // every historical Fill ER directly into FillProjection,
        // bypassing the ExecutionReportProcessor duplicate-fill
        // suppression. A duplicate / retransmit ER persisted later in
        // the WAL must NOT overwrite the original touch evidence.
        var fills = new FillProjection();
        var owner = new EndClientId("alice");
        var original = new BookTouchSnapshot
        {
            BestBid = 29.95m,
            BestAsk = 30.05m,
            CapturedAtUtc = FillNow,
            Stale = false,
        };
        var retransmit = new BookTouchSnapshot
        {
            BestBid = 31.00m,
            BestAsk = 31.10m,
            CapturedAtUtc = FillNow.AddMinutes(5),
            Stale = false,
        };

        fills.RecordIfAbsent(7UL, 100, owner, "FIRM01", "PETR4", OrderSide.Buy,
            100, 30m, FillNow, bookTouch: original);
        var second = fills.RecordIfAbsent(7UL, 100, owner, "FIRM01", "PETR4", OrderSide.Buy,
            100, 30m, FillNow.AddMinutes(5), bookTouch: retransmit);

        Assert.Null(second);
        Assert.True(fills.TryGet(FillProjection.BuildId(7UL, 100), out var rec));
        Assert.Equal(29.95m, rec.BookTouch!.BestBid);
        Assert.Equal(FillNow, rec.TimestampUtc);
    }

    [Fact]
    public void EventReplayer_HonoursDurableTimestamp_OnLegacyNullTouchFill()
    {
        // Pass-2 P2 regression: ExecutionEvent.TimestampUtc must come
        // from the durable WAL timestamp on replay, not UtcNow, so a
        // legacy ER (no BookTouch) projected via the live processor's
        // FillProjection.Record call retains its original execution
        // time. The REST surface falls back to record.TimestampUtc as
        // capturedAtUtc when bookTouch is null.
        var book = new WorkingOrderBook();
        var ownership = new OrderOwnershipMap();
        var positions = new PositionKeeper();
        var sink = new NullSink();
        var fills = new FillProjection();
        var processor = new ExecutionReportProcessor(
            ownership, book, positions, sink, new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            fillProjection: fills);
        var owner = new EndClientId("alice");
        book.TryAdd(new Order(99UL, owner, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 50, 30m));
        ownership.Register(99UL, owner);

        var replayer = new EventReplayer(
            book, ownership, new Risk.KillSwitchService(), new Risk.SymbolHaltService(),
            new Risk.SessionPhaseService(), processor, new AlgoBook(),
            new ClOrdIdPrefixRegistry(), new AlgoIdRegistry());

        var execTime = FillNow.AddHours(-3);
        replayer.Apply(new ExecutionReportReceivedEvent
        {
            TimestampUtc = execTime,
            ClOrdId = 99UL,
            ExecKind = "Fill",
            LeavesQuantity = 0,
            CumulativeQuantity = 50,
            LastQuantity = 50,
            LastPrice = 30m,
            Synthetic = false,
            BookTouch = null,
        });

        Assert.True(fills.TryGet(FillProjection.BuildId(99UL, 50), out var rec));
        Assert.Equal(execTime, rec.TimestampUtc);
        Assert.Null(rec.BookTouch);
    }

    private sealed class NullSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent ev) { }
    }
}
