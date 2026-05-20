using B3.Trading.Application.MarketData;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Infrastructure.Persistence;

namespace B3.Trading.Application.Tests;

/// <summary>
/// #370 Stage A — operator/venue origin semantics for
/// <see cref="SymbolHaltService"/> and the
/// <see cref="VenueHaltSubscriber"/> bridge.
/// </summary>
public sealed class SymbolHaltOriginTests
{
    // ── SymbolHaltService origin semantics ─────────────────────────

    [Fact]
    public void OperatorAndVenueAreIndependentFlags()
    {
        var svc = new SymbolHaltService();
        svc.Halt("PETR4", HaltOrigin.Operator);
        svc.Halt("PETR4", HaltOrigin.Venue);

        Assert.True(svc.IsHalted("PETR4"));
        Assert.True(svc.IsHaltedBy("PETR4", HaltOrigin.Operator));
        Assert.True(svc.IsHaltedBy("PETR4", HaltOrigin.Venue));

        // Venue resume — operator still has it halted.
        var fullyCleared = svc.Resume("PETR4", HaltOrigin.Venue);
        Assert.False(fullyCleared);
        Assert.True(svc.IsHalted("PETR4"));
        Assert.True(svc.IsHaltedBy("PETR4", HaltOrigin.Operator));
        Assert.False(svc.IsHaltedBy("PETR4", HaltOrigin.Venue));

        // Operator resume — fully cleared now.
        fullyCleared = svc.Resume("PETR4", HaltOrigin.Operator);
        Assert.True(fullyCleared);
        Assert.False(svc.IsHalted("PETR4"));
    }

    [Fact]
    public void ResumeReturnsFalseWhenOriginNotHalting()
    {
        var svc = new SymbolHaltService();
        Assert.False(svc.Resume("PETR4", HaltOrigin.Venue));
        svc.Halt("PETR4", HaltOrigin.Operator);
        Assert.False(svc.Resume("PETR4", HaltOrigin.Venue));
        Assert.True(svc.IsHalted("PETR4"));
    }

    [Fact]
    public void HaltIsIdempotentPerOrigin()
    {
        var svc = new SymbolHaltService();
        svc.Halt("PETR4", HaltOrigin.Venue);
        svc.Halt("PETR4", HaltOrigin.Venue);
        Assert.True(svc.Resume("PETR4", HaltOrigin.Venue)); // first resume clears
        Assert.False(svc.IsHalted("PETR4"));
    }

    [Fact]
    public void RawSnapshotWithOriginRoundTripsViaRestoreWithOrigin()
    {
        var src = new SymbolHaltService();
        src.Halt("PETR4", HaltOrigin.Operator);
        src.Halt("PETR4", HaltOrigin.Venue);
        src.Halt("VALE3", HaltOrigin.Venue);
        src.Halt("ITUB4", HaltOrigin.Operator);

        var snapshot = src.RawSnapshotWithOrigin();
        Assert.Equal(3, snapshot.Length);

        var dst = new SymbolHaltService();
        dst.RestoreWithOrigin(snapshot);

        Assert.True(dst.IsHaltedBy("PETR4", HaltOrigin.Operator));
        Assert.True(dst.IsHaltedBy("PETR4", HaltOrigin.Venue));
        Assert.False(dst.IsHaltedBy("VALE3", HaltOrigin.Operator));
        Assert.True(dst.IsHaltedBy("VALE3", HaltOrigin.Venue));
        Assert.True(dst.IsHaltedBy("ITUB4", HaltOrigin.Operator));
        Assert.False(dst.IsHaltedBy("ITUB4", HaltOrigin.Venue));
    }

    [Fact]
    public void LegacyRestoreCoercesToOperator()
    {
        var svc = new SymbolHaltService();
        svc.Halt("OLD", HaltOrigin.Venue);
        svc.Restore(new[] { "PETR4", "VALE3" });

        Assert.False(svc.IsHalted("OLD"));
        Assert.True(svc.IsHaltedBy("PETR4", HaltOrigin.Operator));
        Assert.False(svc.IsHaltedBy("PETR4", HaltOrigin.Venue));
        Assert.True(svc.IsHaltedBy("VALE3", HaltOrigin.Operator));
    }

    // ── VenueHaltSubscriber translation ────────────────────────────

    [Fact]
    public async Task VenueSubscriber_TranslatesPauseAndOpenAndIgnoresPhaseCodes()
    {
        var fake = new FakeMdSubscriber();
        var halts = new SymbolHaltService();
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var sub = new VenueHaltSubscriber(fake, halts, dispatcher);
        await sub.StartAsync(CancellationToken.None);

        fake.Raise("PETR4", SecurityTradingStatusCodes.Pause);
        Assert.True(halts.IsHaltedBy("PETR4", HaltOrigin.Venue));
        Assert.Single(store.HaltEvents);
        Assert.Equal(HaltOrigin.Venue, store.HaltEvents[0].Origin);
        Assert.True(store.HaltEvents[0].Halted);

        // CLOSE / RESERVED / FINAL_CLOSING_CALL are phases → ignored.
        fake.Raise("PETR4", 4);
        fake.Raise("PETR4", 21);
        fake.Raise("PETR4", 101);
        Assert.True(halts.IsHaltedBy("PETR4", HaltOrigin.Venue));
        Assert.Single(store.HaltEvents);

        // OPEN clears the venue halt.
        fake.Raise("PETR4", SecurityTradingStatusCodes.Open);
        Assert.False(halts.IsHaltedBy("PETR4", HaltOrigin.Venue));
        Assert.Equal(2, store.HaltEvents.Count);
        Assert.False(store.HaltEvents[1].Halted);
    }

    [Fact]
    public async Task VenueSubscriber_SuppressesNoOpTransitions()
    {
        var fake = new FakeMdSubscriber();
        var halts = new SymbolHaltService();
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var sub = new VenueHaltSubscriber(fake, halts, dispatcher);
        await sub.StartAsync(CancellationToken.None);

        fake.Raise("PETR4", SecurityTradingStatusCodes.Pause);
        fake.Raise("PETR4", SecurityTradingStatusCodes.Forbidden); // still a halt
        fake.Raise("PETR4", SecurityTradingStatusCodes.Pause);     // still a halt
        Assert.Single(store.HaltEvents);

        fake.Raise("PETR4", SecurityTradingStatusCodes.Open);
        fake.Raise("PETR4", SecurityTradingStatusCodes.Open); // already resumed
        Assert.Equal(2, store.HaltEvents.Count);
    }

    [Fact]
    public async Task VenueResumeLeavesOperatorHaltIntact()
    {
        var fake = new FakeMdSubscriber();
        var halts = new SymbolHaltService();
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var sub = new VenueHaltSubscriber(fake, halts, dispatcher);
        await sub.StartAsync(CancellationToken.None);

        halts.Halt("PETR4", HaltOrigin.Operator);
        fake.Raise("PETR4", SecurityTradingStatusCodes.Pause); // venue halts too
        fake.Raise("PETR4", SecurityTradingStatusCodes.Open);  // venue clears

        Assert.True(halts.IsHalted("PETR4"));
        Assert.True(halts.IsHaltedBy("PETR4", HaltOrigin.Operator));
        Assert.False(halts.IsHaltedBy("PETR4", HaltOrigin.Venue));
    }

    [Fact]
    public async Task VenueSubscriber_StopUnsubscribes()
    {
        var fake = new FakeMdSubscriber();
        var halts = new SymbolHaltService();
        var store = new RecordingEventStore();
        var dispatcher = new EventDispatcher(store);
        var sub = new VenueHaltSubscriber(fake, halts, dispatcher);
        await sub.StartAsync(CancellationToken.None);
        await sub.StopAsync(CancellationToken.None);

        fake.Raise("PETR4", SecurityTradingStatusCodes.Pause);
        Assert.Empty(store.HaltEvents);
        Assert.False(halts.IsHalted("PETR4"));
    }

    // ── test doubles ───────────────────────────────────────────────

    private sealed class FakeMdSubscriber : IMarketDataSubscriber
    {
#pragma warning disable CS0067
        public event Action<MarketTrade>? Trade;
        public event Action<MarketInfoSnapshot>? InfoSnapshot;
        public event Action<MarketDataConnectionState>? ConnectionStateChanged;
        public event Action<MarketSubscribeError>? SubscribeError;
        public event Action<MarketTheoreticalOpening>? TheoreticalOpening;
        public event Action<MarketAuctionImbalance>? AuctionImbalance;
        public event Action<MarketAuctionPrint>? AuctionPrint;
        public event Action<MarketBookSnapshot>? BookSnapshot;
        public event Action<MarketOrderAdded>? OrderAdded;
        public event Action<MarketOrderUpdated>? OrderUpdated;
        public event Action<MarketOrderDeleted>? OrderDeleted;
        public event Action<MarketBookCleared>? BookCleared;
#pragma warning restore CS0067
        public event Action<MarketTradingStatusChange>? TradingStatusChanged;

        public MarketDataConnectionState State => MarketDataConnectionState.Connected;
        public long DroppedEventCount => 0;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask SubscribeAsync(string symbol, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Raise(string symbol, long status) =>
            TradingStatusChanged?.Invoke(new MarketTradingStatusChange(
                symbol, SecurityId: 0, PreviousStatus: null,
                NewStatus: status, ReceivedUtc: DateTimeOffset.UtcNow));
    }

    private sealed class RecordingEventStore : IEventStore
    {
        private long _seq;
        public List<SymbolHaltToggledEvent> HaltEvents { get; } = new();
        public long CurrentSeq => _seq;
        public long Append(WalEvent evt)
        {
            if (evt is SymbolHaltToggledEvent sh) HaltEvents.Add(sh);
            return ++_seq;
        }
        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload) => Append(evt);
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
