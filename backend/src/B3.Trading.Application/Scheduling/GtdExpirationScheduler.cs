using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Application.Scheduling;

/// <summary>
/// Q1.3 (#255). Min-heap scheduler that fires a per-order cancel
/// when an <see cref="Order.GoodTillDate"/> elapses, for orders with
/// <see cref="TimeInForce.GTD"/> in non-terminal status.
///
/// <para>
/// <b>Heap.</b> A <see cref="PriorityQueue{TElement,TPriority}"/> keyed
/// by <c>ClOrdId</c> with <c>GoodTillDate</c> as the priority. A
/// companion <see cref="_index"/> dictionary stores the live expiry
/// for each tracked order so cancel/replace/terminal events do
/// "lazy" removal (the heap entry stays in place; the index either
/// no longer carries it or carries a different priority, and the
/// entry is dropped from the head on the next peek). This avoids
/// the O(n) cost of removing arbitrary entries from a heap and
/// matches the well-known dijkstra-style pattern.
/// </para>
///
/// <para>
/// <b>Timer.</b> A single <see cref="ITimer"/> fired off
/// <see cref="TimeProvider.CreateTimer"/> drives the head expiry; on
/// every heap mutation we recompute the head and re-arm the timer for
/// the new head's <c>expiry - now</c> (with a 1ms safety floor so
/// past-due heads still complete one OS scheduling round-trip).
/// Tests pin the clock with <c>FakeTimeProvider</c>.
/// </para>
///
/// <para>
/// <b>Dispatcher seam.</b> When the timer fires, the scheduler drains
/// every entry whose expiry is <c>&lt;= now</c> and dispatches each one
/// onto the .NET thread-pool via <see cref="Task.Run(Func{Task})"/>.
/// The actual cancel goes through the regular
/// <see cref="OrderCancelService.CancelAsync"/> pipeline — same WAL
/// append, same gateway dispatch, same fan-out to the WS sink as a
/// user-initiated cancel. After the cancel is accepted, the scheduler
/// also appends an <see cref="OrderExpiredEvent"/> to the WAL and
/// publishes a synthetic <see cref="ExecutionEvent"/> with
/// <see cref="ExecKind.Expired"/> so WS subscribers can distinguish a
/// policy-driven expiry from a venue-initiated cancel.
/// </para>
///
/// <para>
/// <b>Cold-start replay.</b> On <see cref="StartAsync"/> the scheduler
/// scans <see cref="WorkingOrderBook"/> for every non-terminal,
/// non-stale GTD order and re-inserts it into the heap. Orders whose
/// expiry already elapsed during the host's downtime fire immediately
/// with <c>AtUtc = order.GoodTillDate</c> (the original expiry) so the
/// audit trail reflects the true policy boundary rather than the
/// post-restart wall clock.
/// </para>
///
/// <para>
/// <b>Replay/snapshot interaction.</b> <see cref="OrderExpiredEvent"/>
/// is informational only: the downstream <c>Canceled</c> ER (also on
/// the WAL, produced by the cancel pipeline) is what mutates the
/// order's status. Re-running the scheduler at boot can therefore
/// re-emit a duplicate <c>OrderExpiredEvent</c> for an order whose
/// cancel ER is on the WAL — but the duplicate is harmless because
/// (a) the order is already terminal so the
/// <see cref="OrderCancelService"/> short-circuits with
/// <c>NotFound</c> at the book lookup, and (b) the projection is a
/// pure audit ping. Bootstrapping ordering is preserved by
/// <c>TradingHostStartup.RunRecoveryAndSeedingAsync</c> which awaits
/// recovery before <c>app.Run()</c> starts hosted services.
/// </para>
/// </summary>
public sealed class GtdExpirationScheduler : IHostedService, IDisposable
{
    /// <summary>
    /// Minimum delay handed to <see cref="ITimer.Change(TimeSpan, TimeSpan)"/>
    /// for a head whose expiry is already past. One millisecond is the
    /// finest grain the .NET timer round-trips with reasonable
    /// determinism on commodity OS schedulers, and it forces the
    /// drain to happen on the timer's worker thread rather than
    /// inline under the caller's lock-acquisition path.
    /// </summary>
    public static readonly TimeSpan MinTimerFloor = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Maximum delay handed to <see cref="ITimer.Change(TimeSpan, TimeSpan)"/>.
    /// The CLR's underlying <c>TimerQueue</c> rejects any due time above
    /// <c>uint.MaxValue - 1</c> milliseconds (~49.7 days). GTD orders can
    /// legally carry expiries weeks or months out (per <c>RiskOptions</c>
    /// the configured horizon defaults to 30 days but can be larger).
    /// We therefore cap the timer at a far smaller "poll" interval and
    /// rely on <see cref="OnTimer"/> to re-arm with the (now smaller)
    /// remaining-until-head delay each time it fires. This keeps
    /// behaviour identical for short expiries while making the scheduler
    /// safe for arbitrarily long ones.
    /// </summary>
    public static readonly TimeSpan MaxTimerPoll = TimeSpan.FromHours(1);

    /// <summary>
    /// Free-text reason carried on <see cref="OrderExpiredEvent.Reason"/>
    /// + the synthetic <c>ExecutionEvent.RejectReason</c> when the
    /// trigger is a GTD expiry. Stable wire format (string, not
    /// enum) so future trigger types — e.g. auction-expired GFA —
    /// can be added additively without breaking old WAL replays.
    /// </summary>
    public const string ReasonGtd = "Gtd";

    private readonly object _lock = new();
    private readonly PriorityQueue<ulong, DateTimeOffset> _heap = new();
    private readonly Dictionary<ulong, DateTimeOffset> _index = new();
    private readonly WorkingOrderBook _book;
    private readonly OrderCancelService _cancel;
    private readonly EventDispatcher _dispatcher;
    private readonly IExecutionEventSink _sink;
    private readonly TimeProvider _clock;
    private readonly ILogger<GtdExpirationScheduler>? _logger;

    private ITimer? _timer;
    private DateTimeOffset _scheduledFor = DateTimeOffset.MaxValue;
    private bool _replayed;

    public GtdExpirationScheduler(
        WorkingOrderBook book,
        OrderCancelService cancel,
        EventDispatcher dispatcher,
        IExecutionEventSink sink,
        TimeProvider? clock = null,
        ILogger<GtdExpirationScheduler>? logger = null)
    {
        _book = book ?? throw new ArgumentNullException(nameof(book));
        _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _sink = sink ?? new NoOpExecutionEventSink();
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// Number of live (non-tombstoned) entries currently tracked.
    /// Test/diagnostic surface only.
    /// </summary>
    public int TrackedCount
    {
        get { lock (_lock) return _index.Count; }
    }

    /// <summary>
    /// Hook called from the live submit path
    /// (<c>OrderSubmissionService</c>) and from the replacement-order
    /// hydration path (<c>ExecutionReportProcessor</c>) once the
    /// new <see cref="Order"/> is in <see cref="WorkingOrderBook"/>.
    /// No-ops for non-GTD orders so callers don't have to re-check the
    /// invariant. Idempotent: re-tracking the same <c>ClOrdId</c>
    /// updates the priority and re-arms the timer (used by future
    /// modify flows that mutate <c>GoodTillDate</c> in place).
    /// </summary>
    public void OnOrderTracked(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.TimeInForce != TimeInForce.GTD) return;
        if (order.GoodTillDate is not { } gtd) return;
        if (IsTerminal(order.Status)) return;
        Schedule(order.ClOrdId, gtd);
    }

    /// <summary>
    /// Hook called from <c>ExecutionReportProcessor</c> on every
    /// terminal ER (<see cref="ExecKind.Canceled"/>,
    /// <see cref="ExecKind.Fill"/>, <see cref="ExecKind.Rejected"/>,
    /// <see cref="ExecKind.Replaced"/>) so a tracked GTD order whose
    /// venue lifecycle ended early — fill, user cancel, native STP,
    /// reject — drops out of the heap before its expiry would have
    /// triggered another (harmless but noisy) cancel attempt.
    /// </summary>
    public void OnOrderTerminal(ulong clOrdId)
    {
        Remove(clOrdId);
    }

    private void Schedule(ulong clOrdId, DateTimeOffset expiry)
    {
        lock (_lock)
        {
            _index[clOrdId] = expiry;
            _heap.Enqueue(clOrdId, expiry);
            Reschedule_NoLock();
        }
    }

    private void Remove(ulong clOrdId)
    {
        lock (_lock)
        {
            if (_index.Remove(clOrdId))
                Reschedule_NoLock();
        }
    }

    private void Reschedule_NoLock()
    {
        // Drop tombstoned heads (entries whose live priority no longer
        // matches the heap-recorded one — either they were removed or
        // re-scheduled with a different expiry).
        while (_heap.TryPeek(out var topId, out var topExpiry))
        {
            if (_index.TryGetValue(topId, out var liveExpiry) && liveExpiry == topExpiry)
                break;
            _heap.Dequeue();
        }

        if (!_heap.TryPeek(out _, out var headExpiry))
        {
            _scheduledFor = DateTimeOffset.MaxValue;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        if (_scheduledFor == headExpiry && _timer is not null) return;

        var now = _clock.GetUtcNow();
        var due = headExpiry - now;
        if (due < MinTimerFloor) due = MinTimerFloor;
        if (due > MaxTimerPoll) due = MaxTimerPoll;

        _scheduledFor = headExpiry;
        if (_timer is null)
            _timer = _clock.CreateTimer(static s => ((GtdExpirationScheduler)s!).OnTimer(), this, due, Timeout.InfiniteTimeSpan);
        else
            _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        List<(ulong ClOrdId, DateTimeOffset Expiry)>? batch = null;
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            while (_heap.TryPeek(out var topId, out var topExpiry))
            {
                if (!_index.TryGetValue(topId, out var liveExpiry) || liveExpiry != topExpiry)
                {
                    _heap.Dequeue();
                    continue;
                }
                if (topExpiry > now) break;

                _heap.Dequeue();
                _index.Remove(topId);
                (batch ??= new()).Add((topId, topExpiry));
            }
            // Force re-arm: zero out _scheduledFor so the next
            // Reschedule_NoLock recomputes against the new head.
            _scheduledFor = DateTimeOffset.MaxValue;
            Reschedule_NoLock();
        }

        if (batch is null) return;
        foreach (var entry in batch)
        {
            // Dispatch each cancel onto the thread-pool. The cancel
            // pipeline serialises its WAL write through the dispatcher
            // lock, so concurrent expiries cannot interleave WAL
            // appends — same posture as user-initiated cancels racing
            // each other through the REST endpoint.
            _ = Task.Run(() => DispatchExpireAsync(entry.ClOrdId, entry.Expiry));
        }
    }

    private async Task DispatchExpireAsync(ulong clOrdId, DateTimeOffset atUtc)
    {
        try
        {
            if (!_book.TryGet(clOrdId, out var order) || order is null)
                return;
            if (IsTerminal(order.Status))
                return;

            var result = await _cancel.CancelAsync(order.Owner, clOrdId, CancellationToken.None)
                .ConfigureAwait(false);

            // Whatever the cancel outcome (Accepted / GatewayFailed /
            // Stale / NotFound / WalBackpressure), append the audit
            // envelope so operators can tell the heap actually fired.
            // The downstream venue Canceled ER is what flips order
            // status; this event is informational only.
            try
            {
                _dispatcher.Dispatch(
                    new OrderExpiredEvent
                    {
                        ClOrdId = clOrdId,
                        Reason = ReasonGtd,
                        AtUtc = atUtc,
                    },
                    static () => { /* no in-memory mutation; audit-only */ });
            }
            catch (WalBackpressureException ex)
            {
                MetricsRegistry.WalBackpressure.Add(1,
                    new KeyValuePair<string, object?>("call_site", "gtd.expired"));
                _logger?.LogWarning(ex,
                    "WAL backpressure appending OrderExpiredEvent for {ClOrdId}; cancel already requested.",
                    clOrdId);
            }

            // WS projection: emit a synthetic Expired ExecutionEvent so
            // subscribers see kind=Expired alongside the regular
            // kind=Canceled. Off-lock and best-effort — the WAL audit
            // record is the durable state.
            try
            {
                _sink.Publish(new ExecutionEvent(
                    Owner: order.Owner,
                    ClOrdId: clOrdId,
                    Symbol: order.Symbol,
                    Side: order.Side,
                    Status: order.Status,
                    Kind: ExecKind.Expired,
                    LeavesQuantity: order.LeavesQuantity,
                    CumulativeQuantity: order.CumulativeQuantity,
                    LastQuantity: 0,
                    LastPrice: 0m,
                    RejectReason: ReasonGtd,
                    TimestampUtc: atUtc));
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex,
                    "Failed to publish synthetic Expired ExecutionEvent for {ClOrdId}; ignoring.",
                    clOrdId);
            }

            MetricsRegistry.GtdOrdersExpired.Add(1,
                new KeyValuePair<string, object?>("cancel_result", result.Kind.ToString()));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Unhandled error dispatching GTD expiry for {ClOrdId}.", clOrdId);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_replayed) return Task.CompletedTask;
        _replayed = true;

        // Cold-start replay: every non-terminal GTD order in the book
        // (post WAL replay + snapshot restore) gets seeded into the
        // heap. Past-due entries fire immediately with AtUtc set to
        // the original expiry, so the audit trail reflects the policy
        // boundary not the post-restart wall clock.
        // EnumerateForFirm is per-firm; the book has no "all firms"
        // enumerator, so iterate the snapshot which covers everything.
        var seeded = 0;
        foreach (var snap in _book.Snapshot())
        {
            if (snap.TimeInForce != nameof(TimeInForce.GTD)) continue;
            if (snap.GoodTillDate is not { } gtd) continue;
            if (!Enum.TryParse<OrderStatus>(snap.Status, out var status)) continue;
            if (IsTerminal(status)) continue;
            Schedule(snap.ClOrdId, gtd);
            seeded++;
        }
        _logger?.LogInformation(
            "GtdExpirationScheduler started: seeded {Count} GTD order(s) from book snapshot.",
            seeded);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ITimer? t;
        lock (_lock) { t = _timer; _timer = null; }
        t?.Dispose();
    }

    private static bool IsTerminal(OrderStatus s) =>
        s is OrderStatus.Filled or OrderStatus.Cancelled
          or OrderStatus.Rejected or OrderStatus.Replaced;
}
