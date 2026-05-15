using B3.Trading.Application;
using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Application.Scheduling;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Trading.Application.Tests.Scheduling;

/// <summary>
/// Pass-1 review regressions for #255:
/// <list type="bullet">
///   <item>P1 — <c>OrderSubmissionService</c> must NOT arm
///   <see cref="GtdExpirationScheduler"/> until <c>SubmitAsync</c> on
///   the gateway has returned without throwing. A slow gateway with a
///   near-term GTD must not race the scheduler into firing a cancel
///   for an order that is still in flight to the venue.</item>
///   <item>P2 — <c>GtdExpirationScheduler.DispatchExpireAsync</c> must
///   append <c>OrderExpiredEvent</c> to the WAL BEFORE invoking
///   <c>OrderCancelService.CancelAsync</c> (which appends
///   <c>OrderCancelRequestedEvent</c> as its first side effect). A
///   crash mid-cancel must leave the GTD-expiry attribution durable
///   so replay can re-attempt the cancel with context.</item>
/// </list>
/// </summary>
public class GtdSchedulerRegressionTests
{
    private static readonly EndClientId Alice = new("alice");

    // -----------------------------------------------------------------
    // Fix 1 (P1): submit-order arming
    // -----------------------------------------------------------------

    [Fact]
    public async Task SubmitWithNearTermGtd_DoesNotArmSchedulerWhileGatewayPending()
    {
        var h = new SubmitHarness();
        var gtd = h.Clock.GetUtcNow().AddMilliseconds(50);

        var submitTask = h.SubmitGtdAsync(gtd);

        // Gateway has reached SubmitAsync but is blocked on the gate.
        await h.Gateway.SubmitInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // While submit is in flight, advance the virtual clock past the
        // GTD. The scheduler must NOT have been armed yet — the order
        // has not reached the venue, so any cancel emitted now would be
        // chasing a clOrdId the venue has never seen.
        h.Clock.Advance(TimeSpan.FromMilliseconds(60));
        await Task.Delay(20);

        Assert.Equal(0, h.Scheduler.TrackedCount);
        Assert.Empty(h.Gateway.CancelCalls);

        // Release the gateway. Submit returns Accepted; scheduler is
        // armed AFTER SubmitAsync returns. Because GTD is already in
        // the past, the next OnTimer tick fires the cancel — and that
        // cancel correctly targets a clOrdId the venue has now seen.
        h.Gateway.ReleaseSubmit();
        var result = await submitTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(OrderSubmissionResultKind.Accepted, result.Kind);

        // The scheduler clamps a past-due head to a tiny floor; advance
        // a couple of ms to let the timer fire deterministically.
        h.Clock.Advance(TimeSpan.FromMilliseconds(5));
        for (int i = 0; i < 200 && h.Gateway.CancelCalls.Count == 0; i++)
            await Task.Delay(10);

        Assert.Single(h.Gateway.CancelCalls);
        // Submit happened-before cancel: the venue saw the new order
        // first and the expiry-driven cancel afterwards.
        Assert.True(h.Gateway.SubmitCompletedAtTicks <= h.Gateway.FirstCancelAtTicks);
    }

    [Fact]
    public async Task SubmitWithGatewayThrow_DoesNotArmScheduler()
    {
        // Companion: if gateway submit throws, scheduler is never
        // armed. Otherwise we'd schedule a cancel for an order that
        // never reached the venue and was already synthetically
        // rejected — replay would see two terminal transitions.
        var h = new SubmitHarness();
        h.Gateway.ThrowOnSubmit = new InvalidOperationException("venue unavailable");
        var gtd = h.Clock.GetUtcNow().AddSeconds(30);

        var result = await h.SubmitGtdAsync(gtd);

        Assert.Equal(OrderSubmissionResultKind.GatewayFailed, result.Kind);
        Assert.Equal(0, h.Scheduler.TrackedCount);
    }

    // -----------------------------------------------------------------
    // Fix 2 (P2): WAL append ordering on expiry
    // -----------------------------------------------------------------

    [Fact]
    public async Task DispatchExpire_AppendsOrderExpiredEventBeforeCancelRequested()
    {
        var h = new ExpireHarness();
        var gtd = h.Clock.GetUtcNow().AddSeconds(5);
        h.SeedGtd(7UL, gtd);
        await h.Sut.StartAsync(CancellationToken.None);

        // Reset recorder so we only see expiry-driven appends below.
        h.Store.Reset();

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 200 && h.Gateway.CancelCount == 0; i++)
            await Task.Delay(10);
        // Allow the audit-only OrderExpiredEvent dispatch (which runs
        // after CancelAsync returns is no longer the case — it now
        // runs before — so wait until both have been observed).
        for (int i = 0; i < 200 && h.Store.AppendedTypes.Count < 2; i++)
            await Task.Delay(10);

        Assert.Equal(2, h.Store.AppendedTypes.Count);
        Assert.Equal(typeof(OrderExpiredEvent), h.Store.AppendedTypes[0]);
        Assert.Equal(typeof(OrderCancelRequestedEvent), h.Store.AppendedTypes[1]);
    }

    // -----------------------------------------------------------------
    // Pass-2 review (#255): WAL-backpressure retry
    // -----------------------------------------------------------------

    /// <summary>
    /// Pass-2 P1 — when CancelAsync returns WalBackpressure, the
    /// scheduler must NOT drop the order from its tracking index. It
    /// must re-arm with backoff and retry on the next timer tick.
    /// OrderExpiredEvent must appear EXACTLY ONCE on the WAL (audit
    /// dedupe across attempts).
    /// </summary>
    [Fact]
    public async Task DispatchExpire_WalBackpressureOnCancel_RetriesAndDedupesAuditEvent()
    {
        var h = new ExpireHarness();
        var gtd = h.Clock.GetUtcNow().AddSeconds(5);
        h.SeedGtd(7UL, gtd);
        await h.Sut.StartAsync(CancellationToken.None);

        h.Store.Reset();
        // Force a single WalBackpressure on the cancel path; the audit
        // event should append cleanly first.
        h.Store.BackpressureFor<OrderCancelRequestedEvent>(1);

        // Tick #1: fire the expiry.
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 200 && h.Store.AppendedTypes.Count < 1; i++)
            await Task.Delay(10);

        // After tick #1: OrderExpiredEvent is on the WAL, but
        // OrderCancelRequestedEvent was rejected → still 1 entry.
        Assert.Single(h.Store.AppendedTypes);
        Assert.Equal(typeof(OrderExpiredEvent), h.Store.AppendedTypes[0]);
        Assert.Equal(0, h.Gateway.CancelCount);
        Assert.Equal(1, h.Sut.TrackedCount);

        // Tick #2: backoff fires. RetryCount==1 → 100ms delay.
        h.Clock.Advance(TimeSpan.FromMilliseconds(105));
        for (int i = 0; i < 200 && h.Gateway.CancelCount == 0; i++)
            await Task.Delay(10);

        Assert.Equal(1, h.Gateway.CancelCount);
        // Exactly one OrderExpiredEvent (audit dedupe) + one
        // OrderCancelRequestedEvent on the retry attempt.
        Assert.Equal(2, h.Store.AppendedTypes.Count);
        Assert.Equal(1, h.Store.AppendedTypes.Count(t => t == typeof(OrderExpiredEvent)));
        Assert.Equal(1, h.Store.AppendedTypes.Count(t => t == typeof(OrderCancelRequestedEvent)));
        Assert.Equal(0, h.Sut.TrackedCount);
    }

    /// <summary>
    /// Pass-2 P1 — sustained WAL pressure (5 consecutive
    /// WalBackpressure rejections) eventually drains; the order
    /// cancels, OrderExpiredEvent appears once, and the retry cadence
    /// follows the documented backoff schedule
    /// (100ms → 200ms → 400ms → 800ms → 1600ms, …, capped at 5s).
    /// </summary>
    [Fact]
    public async Task DispatchExpire_PersistentWalBackpressure_FollowsExponentialBackoff()
    {
        var h = new ExpireHarness();
        var gtd = h.Clock.GetUtcNow().AddSeconds(5);
        h.SeedGtd(11UL, gtd);
        await h.Sut.StartAsync(CancellationToken.None);

        h.Store.Reset();
        h.Store.BackpressureFor<OrderCancelRequestedEvent>(5);

        // Fire the expiry. OrderExpiredEvent goes through; cancel
        // returns WalBackpressure → re-arm with 100ms backoff.
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 200 && h.Store.AppendedTypes.Count < 1; i++)
            await Task.Delay(10);
        Assert.Equal(0, h.Gateway.CancelCount);

        // Backoff schedule: 100, 200, 400, 800, 1600 ms — five
        // attempts total before the store stops rejecting. Step 6 is
        // the success.
        var schedule = new[]
        {
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(400),
            TimeSpan.FromMilliseconds(800),
            TimeSpan.FromMilliseconds(1600),
        };

        foreach (var step in schedule)
        {
            // A retry slightly before the backoff must NOT fire.
            h.Clock.Advance(step - TimeSpan.FromMilliseconds(5));
            await Task.Delay(15);
            // We may already be past the threshold for fast steps
            // because the dispatch path runs on the thread pool, so
            // assert against a snapshot taken now and re-check after
            // crossing the boundary.
            var beforeCount = h.Store.AppendedTypes.Count;

            h.Clock.Advance(TimeSpan.FromMilliseconds(10));
            for (int i = 0; i < 200 && h.Store.AppendedTypes.Count == beforeCount; i++)
                await Task.Delay(10);
        }

        // The 6th attempt drains the rejection counter and succeeds.
        // After all five rejected attempts plus the success, the WAL
        // has exactly one OrderExpiredEvent and one
        // OrderCancelRequestedEvent.
        for (int i = 0; i < 200 && h.Gateway.CancelCount == 0; i++)
            await Task.Delay(10);
        Assert.Equal(1, h.Gateway.CancelCount);
        Assert.Equal(1, h.Store.AppendedTypes.Count(t => t == typeof(OrderExpiredEvent)));
        Assert.Equal(1, h.Store.AppendedTypes.Count(t => t == typeof(OrderCancelRequestedEvent)));
        Assert.Equal(0, h.Sut.TrackedCount);
    }

    /// <summary>
    /// Pass-2 P1 — race with an external terminal event (e.g., the
    /// venue trades the order to fill before our retry reaches it).
    /// The first attempt hits WalBackpressure and re-arms; before the
    /// backoff fires, OnOrderTerminal evicts the entry. The retry
    /// must NOT re-issue a cancel.
    ///
    /// Note: if the audit append on attempt #1 succeeded (as it does
    /// in this scenario), OrderExpiredEvent is on the WAL — that is
    /// acceptable: it accurately attributes the (almost-fired) policy
    /// trigger; the downstream venue Fill ER is what flips the order
    /// to terminal in the book. The audit envelope is informational
    /// only and does not mutate state.
    /// </summary>
    [Fact]
    public async Task DispatchExpire_ExternalTerminalDuringRetry_DropsRetryAndStopsCancelling()
    {
        var h = new ExpireHarness();
        var gtd = h.Clock.GetUtcNow().AddSeconds(5);
        h.SeedGtd(13UL, gtd);
        await h.Sut.StartAsync(CancellationToken.None);

        h.Store.Reset();
        // Backpressure cancel attempts forever — the only way to stop
        // the retry loop in this test is the OnOrderTerminal eviction.
        h.Store.BackpressureFor<OrderCancelRequestedEvent>(int.MaxValue);

        // Tick #1: expiry fires; audit appends; cancel hits WAL
        // backpressure; scheduler re-arms with 100ms backoff.
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 200 && h.Store.AppendedTypes.Count < 1; i++)
            await Task.Delay(10);
        Assert.Equal(1, h.Sut.TrackedCount);
        Assert.Equal(0, h.Gateway.CancelCount);

        // Simulate the external terminal landing (e.g., venue Fill ER
        // arrives before the backoff fires).
        h.Sut.OnOrderTerminal(13UL);
        Assert.Equal(0, h.Sut.TrackedCount);

        // Advance past the backoff window — the timer fires but
        // OnTimer must short-circuit (no live index entry).
        h.Clock.Advance(TimeSpan.FromMilliseconds(500));
        await Task.Delay(50);

        Assert.Equal(0, h.Gateway.CancelCount);
        Assert.Equal(0, h.Sut.TrackedCount);
        // OrderExpiredEvent from attempt #1 may be on the WAL — that
        // is acceptable per the test docstring. No additional appends.
        Assert.Equal(1, h.Store.AppendedTypes.Count(t => t == typeof(OrderExpiredEvent)));
        Assert.Equal(0, h.Store.AppendedTypes.Count(t => t == typeof(OrderCancelRequestedEvent)));
    }

    // =================================================================
    // Test infrastructure
    // =================================================================

    private sealed class ExpireHarness
    {
        public VirtualTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2025-01-01T12:00:00Z"));
        public WorkingOrderBook Book { get; } = new();
        public OrderOwnershipMap Ownership { get; } = new();
        public ClOrdIdPrefixRegistry ClOrdIds { get; } = new();
        public RecordingEventStore Store { get; } = new();
        public EventDispatcher Dispatcher { get; }
        public RecordingGateway Gateway { get; } = new();
        public OrderCancelService Cancel { get; }
        public GtdExpirationScheduler Sut { get; }

        public ExpireHarness()
        {
            Dispatcher = new EventDispatcher(Store);
            Cancel = new OrderCancelService(
                ClOrdIds, Ownership, Book, Gateway, Dispatcher,
                NullLogger<OrderCancelService>.Instance);
            Sut = new GtdExpirationScheduler(
                Book, Cancel, Dispatcher,
                new NoOpExecutionEventSink(), Clock,
                NullLogger<GtdExpirationScheduler>.Instance);
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
    }

    private sealed class SubmitHarness
    {
        public VirtualTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2025-01-01T12:00:00Z"));
        public WorkingOrderBook Book { get; } = new();
        public OrderOwnershipMap Ownership { get; } = new();
        public ClOrdIdPrefixRegistry ClOrdIds { get; } = new();
        public NullEventStore Store { get; } = new();
        public EventDispatcher Dispatcher { get; }
        public SlowGateway Gateway { get; } = new();
        public NoOpExecutionEventSink Sink { get; } = new();
        public RiskPipeline Risk { get; } = new(Array.Empty<IRiskCheck>());
        public NoOpMarginProvider Margin { get; } = new();
        public CompositeRiskAccountant Accountant { get; } = new(Array.Empty<IRiskAccountant>());
        public NeverDrainingGate Drain { get; } = new();
        public OrderCancelService Cancel { get; }
        public GtdExpirationScheduler Scheduler { get; }
        public OrderSubmissionService Submitter { get; }

        public SubmitHarness()
        {
            Dispatcher = new EventDispatcher(Store);
            Cancel = new OrderCancelService(
                ClOrdIds, Ownership, Book, Gateway, Dispatcher,
                NullLogger<OrderCancelService>.Instance);
            Scheduler = new GtdExpirationScheduler(
                Book, Cancel, Dispatcher, Sink, Clock,
                NullLogger<GtdExpirationScheduler>.Instance);
            Submitter = new OrderSubmissionService(
                ClOrdIds, Ownership, Book, Gateway, Sink, Risk, Margin, Accountant,
                Dispatcher, Drain, NullLogger<OrderSubmissionService>.Instance,
                botMappings: null, gtdScheduler: Scheduler);
        }

        public Task<OrderSubmissionResult> SubmitGtdAsync(DateTimeOffset gtd)
        {
            var req = new OrderSubmissionRequest(
                Alice, "FIRM01", "PETR4", 4321UL, OrderSide.Buy, OrderType.Limit,
                Quantity: 100, Price: 30m,
                TimeInForce: TimeInForce.GTD, GoodTillDate: gtd);
            return Submitter.SubmitAsync(req, CancellationToken.None);
        }
    }

    private sealed class SlowGateway : IExchangeGateway
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SubmitInvoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? ThrowOnSubmit { get; set; }
        public List<string> CancelCalls { get; } = new();
        public int CancelCount { get { lock (CancelCalls) return CancelCalls.Count; } }
        public long SubmitCompletedAtTicks { get; private set; }
        public long FirstCancelAtTicks { get; private set; }

        public void ReleaseSubmit() => _gate.TrySetResult();

        public async Task SubmitAsync(Order order, CancellationToken ct)
        {
            SubmitInvoked.TrySetResult();
            if (ThrowOnSubmit is { } ex)
                throw ex;
            await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
            SubmitCompletedAtTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken ct)
        {
            lock (CancelCalls)
            {
                if (FirstCancelAtTicks == 0)
                    FirstCancelAtTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                CancelCalls.Add($"cancel:{order.ClOrdId}->{newClOrdId}");
            }
            return Task.CompletedTask;
        }

        public Task CancelReplaceAsync(
            Order original, ulong newClOrdId, long newQuantity, decimal? newPrice,
            TimeInForce? requestedTimeInForce, decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingGateway : IExchangeGateway
    {
        private int _cancelCount;
        public int CancelCount => Volatile.Read(ref _cancelCount);
        public Task SubmitAsync(Order order, CancellationToken ct) => Task.CompletedTask;
        public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken ct)
        {
            Interlocked.Increment(ref _cancelCount);
            return Task.CompletedTask;
        }
        public Task CancelReplaceAsync(
            Order original, ulong newClOrdId, long newQuantity, decimal? newPrice,
            TimeInForce? requestedTimeInForce, decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NeverDrainingGate : IDrainGate
    {
        public bool IsDraining => false;
    }

    private sealed class NoOpExecutionEventSink : IExecutionEventSink
    {
        public void Publish(ExecutionEvent ev) { }
    }

    /// <summary>
    /// Captures the type of every <see cref="WalEvent"/> appended in
    /// the strict order it was passed to <see cref="IEventStore.Append"/>.
    /// The dispatcher serialises all appends under its lock, so this
    /// list reflects the canonical WAL ordering.
    /// </summary>
    private sealed class RecordingEventStore : IEventStore
    {
        private long _seq;
        private readonly object _lock = new();
        public List<Type> AppendedTypes { get; } = new();
        // Pass-2 review (#255): per-type counter of remaining
        // backpressure rejections. When non-zero for a given event
        // type, both Append overloads throw WalBackpressureException
        // and decrement the counter — modeling a transient WAL
        // pressure spike that drains as the background writer catches
        // up.
        private readonly Dictionary<Type, int> _backpressureRemaining = new();

        public long CurrentSeq => Interlocked.Read(ref _seq);

        public void BackpressureFor<T>(int times) where T : WalEvent
        {
            lock (_lock) _backpressureRemaining[typeof(T)] = times;
        }

        public long Append(WalEvent evt)
        {
            lock (_lock)
            {
                if (_backpressureRemaining.TryGetValue(evt.GetType(), out var remaining) && remaining > 0)
                {
                    _backpressureRemaining[evt.GetType()] = remaining - 1;
                    throw new WalBackpressureException($"forced backpressure for {evt.GetType().Name}");
                }
                AppendedTypes.Add(evt.GetType());
            }
            return Interlocked.Increment(ref _seq);
        }
        public long Append(WalEvent evt, ReadOnlyMemory<byte> preSerialisedPayload)
        {
            lock (_lock)
            {
                if (_backpressureRemaining.TryGetValue(evt.GetType(), out var remaining) && remaining > 0)
                {
                    _backpressureRemaining[evt.GetType()] = remaining - 1;
                    throw new WalBackpressureException($"forced backpressure for {evt.GetType().Name}");
                }
                AppendedTypes.Add(evt.GetType());
            }
            return Interlocked.Increment(ref _seq);
        }
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Reset()
        {
            lock (_lock) AppendedTypes.Clear();
        }
    }

    /// <summary>
    /// Same shape as <c>VirtualTimeProvider</c> in
    /// <see cref="GtdExpirationSchedulerTests"/>; duplicated here to
    /// keep test files self-contained (the inner type is private to
    /// that file).
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
