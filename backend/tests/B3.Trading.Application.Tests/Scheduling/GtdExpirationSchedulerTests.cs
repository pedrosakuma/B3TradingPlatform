using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Scheduling;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Scheduling;

/// <summary>
/// Q1.3 (#255). Behavioural coverage for the GTD expiration scheduler:
/// timer fires, lazy heap removal on cancel/replace, cold-start replay
/// from book snapshot, and the synthetic Expired ExecutionEvent.
///
/// <para>
/// Tests pin the clock with a virtual <see cref="VirtualTimeProvider"/>
/// that schedules timer callbacks against a software queue; advancing
/// time fires every callback whose due time has elapsed. This keeps
/// the suite deterministic — no Thread.Sleep, no flakiness on
/// wall-clock granularity.
/// </para>
/// </summary>
public class GtdExpirationSchedulerTests
{
    private static readonly EndClientId Alice = new("alice");

    private sealed class RecordingGateway : IExchangeGateway
    {
        public List<string> Calls { get; } = new();

        public Task SubmitAsync(Order order, CancellationToken ct) => Task.CompletedTask;

        public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"cancel:{order.ClOrdId}->{newClOrdId}");
            return Task.CompletedTask;
        }

        public Task CancelReplaceAsync(
            Order original, ulong newClOrdId, long newQuantity, decimal? newPrice,
            TimeInForce? requestedTimeInForce, decimal? requestedStopPrice, DateTimeOffset? requestedGoodTillDate,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CapturingSink : IExecutionEventSink
    {
        public List<ExecutionEvent> Events { get; } = new();
        public void Publish(ExecutionEvent ev)
        {
            lock (Events) Events.Add(ev);
        }
    }

    private sealed class Harness
    {
        public VirtualTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2025-01-01T12:00:00Z"));
        public WorkingOrderBook Book { get; } = new();
        public OrderOwnershipMap Ownership { get; } = new();
        public ClOrdIdPrefixRegistry ClOrdIds { get; } = new();
        public EventDispatcher Dispatcher { get; } = new(new NullEventStore());
        public RecordingGateway Gateway { get; } = new();
        public CapturingSink Sink { get; } = new();
        public OrderCancelService Cancel { get; }
        public GtdExpirationScheduler Sut { get; }

        public Harness()
        {
            Cancel = new OrderCancelService(
                ClOrdIds, Ownership, Book, Gateway, Dispatcher,
                NullLogger<OrderCancelService>.Instance);
            Sut = new GtdExpirationScheduler(
                Book, Cancel, Dispatcher, Sink, Clock, NullLogger<GtdExpirationScheduler>.Instance);
        }

        public Order SeedGtd(ulong clOrdId, DateTimeOffset gtd)
        {
            var o = new Order(
                clOrdId, Alice, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
                100, 30m, timeInForce: TimeInForce.GTD, goodTillDate: gtd);
            Assert.True(Book.TryAdd(o));
            Ownership.Register(clOrdId, Alice);
            return o;
        }

        public async Task DrainDispatchedAsync(int expectedCancels = 1)
        {
            // The scheduler's OnTimer dispatches via Task.Run; spin
            // briefly to let the cancel attempt land on the recording
            // gateway. 2s upper bound keeps the suite snappy.
            for (int i = 0; i < 200 && (Gateway.Calls.Count < expectedCancels || Sink.Events.Count < expectedCancels); i++)
                await Task.Delay(10);
        }
    }

    [Fact]
    public async Task GtdOrder_ExpiresAfterDelay_TriggersCancelAndExpiredEvent()
    {
        var h = new Harness();
        var gtd = h.Clock.GetUtcNow().AddSeconds(5);
        h.SeedGtd(1UL, gtd);
        await h.Sut.StartAsync(CancellationToken.None);
        Assert.Equal(1, h.Sut.TrackedCount);

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        await h.DrainDispatchedAsync();

        Assert.Single(h.Gateway.Calls);
        Assert.StartsWith("cancel:1->", h.Gateway.Calls[0]);
        Assert.Contains(h.Sink.Events, e => e.ClOrdId == 1UL && e.Kind == ExecKind.Expired);
        Assert.Equal(0, h.Sut.TrackedCount);
    }

    [Fact]
    public async Task TerminalBeforeExpiry_RemovesFromHeap_NoCancel()
    {
        var h = new Harness();
        var gtd = h.Clock.GetUtcNow().AddSeconds(10);
        var o = h.SeedGtd(2UL, gtd);
        h.Sut.OnOrderTracked(o);
        Assert.Equal(1, h.Sut.TrackedCount);

        h.Sut.OnOrderTerminal(2UL);
        Assert.Equal(0, h.Sut.TrackedCount);

        h.Clock.Advance(TimeSpan.FromSeconds(20));
        await Task.Delay(50);

        Assert.Empty(h.Gateway.Calls);
        Assert.DoesNotContain(h.Sink.Events, e => e.Kind == ExecKind.Expired);
    }

    [Fact]
    public async Task RetrackingWithEarlierExpiry_ReschedulesTimer()
    {
        var h = new Harness();
        var first = h.Clock.GetUtcNow().AddMinutes(30);
        var o = h.SeedGtd(3UL, first);
        h.Sut.OnOrderTracked(o);

        // Replace path hydrates a new Order with a sooner GoodTillDate.
        // The original entry tombstones; the new live entry wins.
        var replacement = new Order(
            3UL, Alice, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
            100, 30m, timeInForce: TimeInForce.GTD,
            goodTillDate: h.Clock.GetUtcNow().AddSeconds(2));
        h.Sut.OnOrderTracked(replacement);

        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await h.DrainDispatchedAsync();

        Assert.Single(h.Gateway.Calls);
        Assert.StartsWith("cancel:3->", h.Gateway.Calls[0]);
    }

    [Fact]
    public async Task ColdStart_PastDueGtd_FiresImmediatelyWithOriginalAtUtc()
    {
        var h = new Harness();
        var pastExpiry = h.Clock.GetUtcNow().AddSeconds(-30);
        h.SeedGtd(4UL, pastExpiry);

        await h.Sut.StartAsync(CancellationToken.None);
        // Past-due head sits at MinTimerFloor due time; advance just
        // enough to fire it.
        h.Clock.Advance(TimeSpan.FromMilliseconds(2));
        await h.DrainDispatchedAsync();

        Assert.Single(h.Gateway.Calls);
        var expired = Assert.Single(h.Sink.Events, e => e.Kind == ExecKind.Expired);
        Assert.Equal(pastExpiry, expired.TimestampUtc);
    }

    [Fact]
    public async Task NonGtdOrder_IsIgnored()
    {
        var h = new Harness();
        var day = new Order(5UL, Alice, "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit, 100, 30m);
        Assert.True(h.Book.TryAdd(day));
        h.Sut.OnOrderTracked(day);

        Assert.Equal(0, h.Sut.TrackedCount);
        h.Clock.Advance(TimeSpan.FromHours(1));
        await Task.Delay(20);
        Assert.Empty(h.Gateway.Calls);
    }

    [Fact]
    public async Task ManyOrders_FireBeforeAdvanceCompletes()
    {
        var h = new Harness();
        var rng = new Random(42);
        for (ulong id = 1; id <= 100; id++)
        {
            var gtd = h.Clock.GetUtcNow().AddSeconds(rng.Next(1, 60));
            var o = h.SeedGtd(id, gtd);
            h.Sut.OnOrderTracked(o);
        }
        Assert.Equal(100, h.Sut.TrackedCount);

        h.Clock.Advance(TimeSpan.FromSeconds(60));
        for (int i = 0; i < 600 && h.Gateway.Calls.Count < 100; i++)
            await Task.Delay(10);

        Assert.Equal(100, h.Gateway.Calls.Count);
        Assert.Equal(0, h.Sut.TrackedCount);
    }

    [Fact]
    public async Task LongHorizonGtd_DoesNotCrashTimer()
    {
        // Regression: TimeProvider.CreateTimer rejects dueTime above
        // ~49.7 days. The scheduler must clamp to MaxTimerPoll and
        // re-arm later — ArgumentOutOfRangeException would crash the
        // submit pipeline (was a 500 in the API).
        var h = new Harness();
        var gtd = h.Clock.GetUtcNow().AddDays(60);
        var o = h.SeedGtd(6UL, gtd);
        h.Sut.OnOrderTracked(o);
        await h.Sut.StartAsync(CancellationToken.None);

        Assert.Equal(1, h.Sut.TrackedCount);
        Assert.Empty(h.Gateway.Calls);
    }

    /// <summary>
    /// Minimal virtual-time TimeProvider with a software timer queue
    /// sufficient for the scheduler under test (which only ever holds
    /// one ITimer at a time). <c>CreateTimer</c> registers the
    /// callback in <see cref="_timers"/>; <c>Advance</c> walks forward
    /// in time and fires every timer whose absolute due time is
    /// reached (callback re-arming via <c>Change</c> is honoured for
    /// the loop's next iteration).
    /// </summary>
    private sealed class VirtualTimeProvider : TimeProvider
    {
        private readonly object _lock = new();
        private DateTimeOffset _now;
        private readonly List<VTimer> _timers = new();

        public VirtualTimeProvider(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() { lock (_lock) return _now; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new VTimer(this, callback, state);
            t.Change(dueTime, period);
            return t;
        }

        public void Advance(TimeSpan delta)
        {
            var deadline = Now + delta;
            while (true)
            {
                VTimer? next = null;
                lock (_lock)
                {
                    foreach (var t in _timers)
                    {
                        if (t.DueAt is { } d && d <= deadline)
                        {
                            if (next is null || d < next.DueAt) next = t;
                        }
                    }
                }
                if (next is null)
                {
                    lock (_lock) _now = deadline;
                    return;
                }
                lock (_lock) _now = next.DueAt!.Value;
                next.Fire();
            }
        }

        internal void Register(VTimer t) { lock (_lock) _timers.Add(t); }
        internal void Unregister(VTimer t) { lock (_lock) _timers.Remove(t); }
        internal DateTimeOffset Now { get { lock (_lock) return _now; } }
    }

    private sealed class VTimer : ITimer
    {
        private readonly VirtualTimeProvider _owner;
        private readonly TimerCallback _cb;
        private readonly object? _state;
        public DateTimeOffset? DueAt { get; private set; }

        public VTimer(VirtualTimeProvider owner, TimerCallback cb, object? state)
        {
            _owner = owner; _cb = cb; _state = state;
            _owner.Register(this);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner.Now + dueTime;
            return true;
        }

        public void Fire()
        {
            DueAt = null;
            _cb(_state);
        }

        public void Dispose() => _owner.Unregister(this);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
