using B3.Trading.Application;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Scheduling;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Tests.Scheduling;

/// <summary>
/// #351 — Behavioural coverage for the IOC/FOK silent-drop watchdog.
/// </summary>
public class IocFokWatchdogTests
{
    private static readonly EndClientId Alice = new("alice");

    private sealed class CapturingSink : IExecutionEventSink
    {
        public List<ExecutionEvent> Events { get; } = new();
        public void Publish(ExecutionEvent ev) { lock (Events) Events.Add(ev); }
    }

    private sealed class CountingMargin : IMarginProvider
    {
        public int ReleaseCalls;
        public int ExecutionCalls;
        public Task<RiskDecision> TryReserveAsync(ulong clOrdId, RiskContext ctx, CancellationToken ct) =>
            Task.FromResult(RiskDecision.Approve);
        public void OnExecution(ulong clOrdId, ExecKind kind, long lastQty)
        {
            Interlocked.Increment(ref ExecutionCalls);
        }
        public void ReleaseReservation(ulong clOrdId) => Interlocked.Increment(ref ReleaseCalls);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class Harness
    {
        public VirtualTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-05-19T00:00:00Z"));
        public WorkingOrderBook Book { get; } = new();
        public EventDispatcher Dispatcher { get; } = new(new NullEventStore());
        public CapturingSink Sink { get; } = new();
        public CountingMargin Margin { get; } = new();
        public IocFokWatchdog Sut { get; }
        public IocFokWatchdogOptions Options { get; } = new() { Enabled = true, TimeoutMs = 500 };

        public Harness()
        {
            Sut = new IocFokWatchdog(
                Book, Dispatcher, Sink, Margin,
                new StaticOptionsMonitor<IocFokWatchdogOptions>(Options),
                Clock, NullLogger<IocFokWatchdog>.Instance);
        }

        public Order SeedAndRegister(ulong clOrdId, TimeInForce tif)
        {
            var o = new Order(
                clOrdId, Alice, "PETR4", 4321UL, OrderSide.Sell, OrderType.Limit,
                100, 30m, firmId: "firm-a", timeInForce: tif);
            Book.TryAdd(o);
            Sut.Register(o);
            return o;
        }
    }

    [Fact]
    public void Ioc_no_response_within_timeout_synthesises_terminal_cancel()
    {
        var h = new Harness();
        var o = h.SeedAndRegister(101UL, TimeInForce.IOC);

        Assert.Equal(1, h.Sut.TrackedCount);
        Assert.Equal(OrderStatus.PendingNew, o.Status);

        h.Clock.Advance(TimeSpan.FromMilliseconds(600));

        Assert.Equal(OrderStatus.Cancelled, o.Status);
        Assert.Equal(0, h.Sut.TrackedCount);
        Assert.Single(h.Sink.Events);
        var ev = h.Sink.Events[0];
        Assert.Equal(ExecKind.Canceled, ev.Kind);
        Assert.Equal(IocFokWatchdog.SyntheticCancelReason, ev.RejectReason);
        Assert.Equal("firm-a", ev.FirmId);
        Assert.Equal(1, h.Margin.ExecutionCalls);
    }

    [Fact]
    public void Terminal_er_before_timeout_cancels_watchdog_and_skips_synthetic()
    {
        var h = new Harness();
        var o = h.SeedAndRegister(102UL, TimeInForce.IOC);

        // Terminal ER lands quickly — the order book mutation is simulated
        // here (the live path mutates via ExecutionReportProcessor).
        o.MarkCancelled();
        h.Sut.OnOrderTerminal(o.ClOrdId);

        h.Clock.Advance(TimeSpan.FromMilliseconds(600));

        Assert.Equal(0, h.Sut.TrackedCount);
        Assert.Empty(h.Sink.Events);
        Assert.Equal(0, h.Margin.ExecutionCalls);
    }

    [Fact]
    public void Non_ioc_orders_are_not_tracked()
    {
        var h = new Harness();
        h.SeedAndRegister(103UL, TimeInForce.Day);
        h.SeedAndRegister(104UL, TimeInForce.GTC);

        Assert.Equal(0, h.Sut.TrackedCount);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(h.Sink.Events);
    }

    [Fact]
    public void Fok_is_tracked_just_like_ioc()
    {
        var h = new Harness();
        var o = h.SeedAndRegister(105UL, TimeInForce.FOK);

        Assert.Equal(1, h.Sut.TrackedCount);
        h.Clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.Equal(OrderStatus.Cancelled, o.Status);
        Assert.Single(h.Sink.Events);
    }

    [Fact]
    public void Late_terminal_after_synthetic_does_not_double_fire()
    {
        var h = new Harness();
        var o = h.SeedAndRegister(106UL, TimeInForce.IOC);

        // Watchdog fires first.
        h.Clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.Single(h.Sink.Events);

        // Real terminal ER arrives later — the OnOrderTerminal hook
        // must be safe to call (no-op).
        h.Sut.OnOrderTerminal(o.ClOrdId);
        Assert.Single(h.Sink.Events);
    }

    [Fact]
    public void Disabled_options_skips_registration()
    {
        var h = new Harness();
        h.Options.Enabled = false;

        var o = h.SeedAndRegister(107UL, TimeInForce.IOC);
        Assert.Equal(0, h.Sut.TrackedCount);

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(OrderStatus.PendingNew, o.Status);
        Assert.Empty(h.Sink.Events);
    }

    [Fact]
    public void Fire_against_already_terminal_order_skips_synthetic()
    {
        var h = new Harness();
        var o = h.SeedAndRegister(108UL, TimeInForce.IOC);

        // Status flips terminal but the watchdog wasn't notified
        // (simulates a race: ER applied to order in the gap before
        // the OnOrderTerminal hook runs). Fire must observe the
        // terminal status and skip.
        o.MarkCancelled();
        h.Clock.Advance(TimeSpan.FromMilliseconds(600));

        Assert.Empty(h.Sink.Events);
        Assert.Equal(0, h.Sut.TrackedCount);
    }

    // ---------- Shared virtual time provider ------------

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
                if (next is null) { lock (_lock) _now = deadline; return; }
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
