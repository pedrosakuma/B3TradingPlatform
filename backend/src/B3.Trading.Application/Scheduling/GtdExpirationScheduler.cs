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
    /// Pass-2 review (#255). Initial WAL-backpressure retry delay; the
    /// scheduler re-arms the heap entry at <c>now + RetryBaseDelay</c>
    /// after the first transient failure (audit append or
    /// <see cref="OrderCancelService.CancelAsync"/> returning
    /// <see cref="OrderCancelResultKind.WalBackpressure"/>) and then
    /// doubles each subsequent attempt up to <see cref="RetryMaxDelay"/>.
    /// </summary>
    public static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Pass-2 review (#255). Cap on the WAL-backpressure retry delay.
    /// Once reached, the scheduler keeps retrying at this cadence
    /// indefinitely — WAL pressure is treated as transient (drained as
    /// the background writer catches up); abandoning would re-introduce
    /// the orphan-at-venue bug the retry is meant to fix.
    /// </summary>
    public static readonly TimeSpan RetryMaxDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Free-text reason carried on <see cref="OrderExpiredEvent.Reason"/>
    /// + the synthetic <c>ExecutionEvent.RejectReason</c> when the
    /// trigger is a GTD expiry. Stable wire format (string, not
    /// enum) so future trigger types — e.g. auction-expired GFA —
    /// can be added additively without breaking old WAL replays.
    /// </summary>
    public const string ReasonGtd = "Gtd";

    /// <summary>
    /// Per-order tracking state held in <see cref="_index"/>. Mutable
    /// reference type so retry bookkeeping (audit-append dedupe, retry
    /// count for backoff calc) survives the heap dequeue+re-enqueue
    /// cycle that a WAL-backpressure retry performs.
    /// </summary>
    private sealed class Entry
    {
        /// <summary>Currently scheduled fire time (original GTD on first
        /// arm; <c>now + backoff</c> after each WAL-backpressure
        /// failure). The heap key always equals this value at the moment
        /// of enqueue, so a divergence between
        /// <c>_index[id].Expiry</c> and a peeked heap priority is the
        /// canonical "tombstone" signal.</summary>
        public DateTimeOffset Expiry;
        /// <summary>The order's actual <see cref="Order.GoodTillDate"/>
        /// captured at first arm. Used as the
        /// <see cref="OrderExpiredEvent.AtUtc"/> on every (re)try so the
        /// audit trail reflects the policy boundary, not the wall-clock
        /// time of the eventual successful append.</summary>
        public DateTimeOffset OriginalGtd;
        /// <summary>True once <see cref="OrderExpiredEvent"/> has been
        /// successfully appended for this expiry. Prevents duplicate
        /// audit envelopes across WAL-backpressure retry attempts.</summary>
        public bool ExpiredAuditAppended;
        /// <summary>Number of WAL-backpressure re-arms so far; drives
        /// the exponential backoff in <see cref="ComputeBackoff"/>.</summary>
        public int RetryCount;
    }

    private readonly object _lock = new();
    private readonly PriorityQueue<ulong, DateTimeOffset> _heap = new();
    private readonly Dictionary<ulong, Entry> _index = new();
    /// <summary>
    /// Pass-3 review (#255). Set of ClOrdIds whose
    /// <see cref="OrderExpiredEvent"/> audit envelope has already been
    /// observed on the WAL — populated either by the live dispatch
    /// path (after a successful audit append) or by the
    /// <see cref="MarkExpiredAuditAppended"/> hook the
    /// <c>EventReplayer</c> calls during cold-start replay. When
    /// <see cref="Schedule"/> seeds a new <see cref="Entry"/> for an
    /// id present in this set, it pre-sets
    /// <see cref="Entry.ExpiredAuditAppended"/> so the post-restart
    /// timer fire does NOT emit a second
    /// <see cref="OrderExpiredEvent"/> for the same expiry.
    /// Bookkeeping is bounded: <see cref="Resolve"/> removes the id
    /// once the cancel completes (or the entry is evicted) so the
    /// set stays the size of the in-flight expired-but-not-yet-cancel-
    /// completed window.
    /// </summary>
    private readonly HashSet<ulong> _auditedExpiredIds = new();
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
            if (_index.TryGetValue(clOrdId, out var existing))
            {
                // Re-track (e.g., modify in place, replay): treat as a
                // fresh scheduling — reset audit/retry state so the new
                // GTD gets its own OrderExpiredEvent and a clean backoff
                // ladder if it too hits WAL pressure.
                existing.Expiry = expiry;
                existing.OriginalGtd = expiry;
                existing.ExpiredAuditAppended = false;
                existing.RetryCount = 0;
            }
            else
            {
                // Pass-3 review (#255). If a prior incarnation of the
                // process appended OrderExpiredEvent for this ClOrdId
                // before crashing (audit on disk, cancel not yet
                // requested), the EventReplayer hook
                // MarkExpiredAuditAppended will have populated
                // _auditedExpiredIds during cold-start replay. Seed
                // ExpiredAuditAppended=true so the post-restart timer
                // fire reuses the existing audit envelope instead of
                // emitting a duplicate.
                _index[clOrdId] = new Entry
                {
                    Expiry = expiry,
                    OriginalGtd = expiry,
                    ExpiredAuditAppended = _auditedExpiredIds.Contains(clOrdId),
                };
            }
            _heap.Enqueue(clOrdId, expiry);
            Reschedule_NoLock();
        }
    }

    /// <summary>
    /// Pass-3 review (#255). Hook called by the WAL <c>EventReplayer</c>
    /// for every <see cref="OrderExpiredEvent"/> it encounters during
    /// cold-start replay. Records that the audit envelope is durably
    /// on disk for the given <paramref name="clOrdId"/> so a
    /// subsequent <see cref="Schedule"/> call (issued from
    /// <see cref="StartAsync"/> for an order whose cancel ER did NOT
    /// land before the crash) seeds the resulting <see cref="Entry"/>
    /// with <see cref="Entry.ExpiredAuditAppended"/> already set.
    /// Without this hook the post-restart timer fire would re-append
    /// <see cref="OrderExpiredEvent"/> for the same expiry, producing
    /// a duplicate audit envelope on the WAL.
    /// <para>
    /// Idempotent. Must be called BEFORE
    /// <see cref="StartAsync"/> seeds the heap from the book snapshot
    /// — guaranteed by <c>TradingHostStartup.RunRecoveryAndSeedingAsync</c>
    /// which awaits WAL recovery before <c>app.Run()</c> kicks off
    /// hosted-service <c>StartAsync</c>.
    /// </para>
    /// </summary>
    public void MarkExpiredAuditAppended(ulong clOrdId)
    {
        lock (_lock) _auditedExpiredIds.Add(clOrdId);
    }

    /// <summary>
    /// Pass-4 review (#255). Snapshot-time export of the in-flight
    /// audit-set: every ClOrdId whose <see cref="OrderExpiredEvent"/>
    /// has already been written to the WAL but whose downstream cancel
    /// has not yet resolved (terminal cancel result evicts the id via
    /// <see cref="Resolve"/>). Returned as a sorted array for
    /// deterministic snapshot bytes.
    /// <para>
    /// <b>Lock ordering.</b> Caller (the snapshot service) holds
    /// <c>EventDispatcher.WithSnapshotLock</c>; this method then takes
    /// the scheduler's own <see cref="_lock"/>. Mutators of
    /// <see cref="_auditedExpiredIds"/> live in two places:
    /// <list type="bullet">
    ///   <item><see cref="DispatchExpireAsync"/> performs the
    ///   <c>Add</c> inside the <see cref="EventDispatcher.Dispatch(WalEvent, Action)"/>
    ///   apply callback, i.e. under the dispatcher lock first then the
    ///   scheduler lock — same order as snapshot capture (no deadlock).</item>
    ///   <item><see cref="Resolve"/> takes only the scheduler lock and
    ///   never re-enters the dispatcher.</item>
    /// </list>
    /// </para>
    /// </summary>
    public ulong[] SnapshotAuditedExpiredIds()
    {
        lock (_lock)
        {
            if (_auditedExpiredIds.Count == 0) return Array.Empty<ulong>();
            var copy = new ulong[_auditedExpiredIds.Count];
            _auditedExpiredIds.CopyTo(copy);
            Array.Sort(copy);
            return copy;
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

    /// <summary>
    /// Bounded exponential backoff: <c>RetryBaseDelay * 2^retryCount</c>
    /// capped at <see cref="RetryMaxDelay"/>. Caller passes the
    /// post-increment retry count (1 for the first re-arm, 2 for the
    /// second, …); shift is clamped at 10 to keep the multiplier well
    /// inside <see cref="long"/> range before the cap kicks in.
    /// </summary>
    private DateTimeOffset ComputeBackoff(int retryCount)
    {
        var shift = Math.Min(Math.Max(retryCount - 1, 0), 10);
        var ms = RetryBaseDelay.TotalMilliseconds * (1L << shift);
        var delay = ms >= RetryMaxDelay.TotalMilliseconds
            ? RetryMaxDelay
            : TimeSpan.FromMilliseconds(ms);
        return _clock.GetUtcNow() + delay;
    }

    private void Reschedule_NoLock()
    {
        // Drop tombstoned heads (entries whose live priority no longer
        // matches the heap-recorded one — either they were removed or
        // re-scheduled with a different expiry).
        while (_heap.TryPeek(out var topId, out var topExpiry))
        {
            if (_index.TryGetValue(topId, out var liveEntry) && liveEntry.Expiry == topExpiry)
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
        List<ulong>? batch = null;
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            while (_heap.TryPeek(out var topId, out var topExpiry))
            {
                if (!_index.TryGetValue(topId, out var entry) || entry.Expiry != topExpiry)
                {
                    _heap.Dequeue();
                    continue;
                }
                if (topExpiry > now) break;

                // Pass-2 review (#255): dequeue the heap entry but
                // LEAVE the order in _index. DispatchExpireAsync takes
                // ownership: on a terminal CancelAsync result it calls
                // Resolve to evict, on a WAL-backpressure result it
                // calls ReArmRetry to re-enqueue with backoff. Removing
                // here would orphan the order at the venue if cancel
                // returned WalBackpressure (the original P1 bug).
                _heap.Dequeue();
                (batch ??= new()).Add(topId);
            }
            // Force re-arm: zero out _scheduledFor so the next
            // Reschedule_NoLock recomputes against the new head.
            _scheduledFor = DateTimeOffset.MaxValue;
            Reschedule_NoLock();
        }

        if (batch is null) return;
        foreach (var clOrdId in batch)
        {
            // Dispatch each cancel onto the thread-pool. The cancel
            // pipeline serialises its WAL write through the dispatcher
            // lock, so concurrent expiries cannot interleave WAL
            // appends — same posture as user-initiated cancels racing
            // each other through the REST endpoint.
            _ = Task.Run(() => DispatchExpireAsync(clOrdId));
        }
    }

    /// <summary>
    /// Drops <paramref name="clOrdId"/> from the live tracking index
    /// after a terminal dispatch outcome (CancelAsync returned
    /// Accepted / NotFound / Stale / GatewayFailed, or the order was
    /// already terminal in the book). No heap mutation needed: the
    /// dispatching code already dequeued the heap entry; any future
    /// Schedule() for the same id starts fresh.
    /// </summary>
    private void Resolve(ulong clOrdId)
    {
        lock (_lock)
        {
            _index.Remove(clOrdId);
            // Pass-3 review (#255). Drop the audited-id bookkeeping
            // alongside the index entry: once the cancel pipeline
            // resolved (Accepted / NotFound / Stale / GatewayFailed),
            // the order is no longer at risk of a post-crash duplicate
            // audit because Schedule will not re-arm a terminal/
            // missing-from-book order. Keeps the set bounded by the
            // in-flight expired-but-cancel-not-completed window.
            _auditedExpiredIds.Remove(clOrdId);
        }
    }

    /// <summary>
    /// Pass-2 review (#255). Re-arms a transient WAL-backpressure
    /// failure with bounded exponential backoff. If the order was
    /// concurrently removed (OnOrderTerminal raced ahead — venue fill
    /// or external cancel landed before our retry), the re-arm is
    /// suppressed: the entry is gone from the index and there is
    /// nothing to retry.
    /// </summary>
    private void ReArmRetry(ulong clOrdId)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(clOrdId, out var entry))
                return;
            entry.RetryCount++;
            entry.Expiry = ComputeBackoff(entry.RetryCount);
            _heap.Enqueue(clOrdId, entry.Expiry);
            // Force re-arm: the new backoff expiry might be later than
            // the previously-scheduled head; Reschedule_NoLock will
            // recompute regardless because we dequeued in OnTimer.
            _scheduledFor = DateTimeOffset.MaxValue;
            Reschedule_NoLock();
        }
    }

    private async Task DispatchExpireAsync(ulong clOrdId)
    {
        try
        {
            // Snapshot the per-order tracking state under the lock so
            // we observe a consistent (audit-flag, original-gtd) pair
            // even if a concurrent OnOrderTerminal evicts the entry
            // mid-dispatch.
            DateTimeOffset originalGtd;
            bool auditAlreadyAppended;
            lock (_lock)
            {
                if (!_index.TryGetValue(clOrdId, out var entry))
                    return;
                originalGtd = entry.OriginalGtd;
                auditAlreadyAppended = entry.ExpiredAuditAppended;
            }

            if (!_book.TryGet(clOrdId, out var order) || order is null)
            {
                Resolve(clOrdId);
                return;
            }
            if (IsTerminal(order.Status))
            {
                Resolve(clOrdId);
                return;
            }

            // 1) Append the GTD-expiry audit envelope BEFORE issuing
            // the cancel so a crash mid-cancel still leaves the
            // expiry-attribution durable. Pass-2 review (#255): if WAL
            // append itself hits backpressure, re-arm with backoff and
            // retry on the next timer tick — DO NOT proceed to cancel
            // (CancelAsync would also write to the same pressured
            // channel and we'd have no audit record on disk).
            if (!auditAlreadyAppended)
            {
                try
                {
                    _dispatcher.Dispatch(
                        new OrderExpiredEvent
                        {
                            ClOrdId = clOrdId,
                            Reason = ReasonGtd,
                            AtUtc = originalGtd,
                        },
                        // Pass-4 review (#255): record the audit-appended
                        // bookkeeping INSIDE the dispatcher's apply
                        // callback so it is atomic with the WAL append
                        // under the dispatcher lock. A snapshot taken
                        // between the audit append and the cancel append
                        // (snapshot capture also holds the dispatcher
                        // lock via WithSnapshotLock) now observes both
                        // the post-append seq AND the populated
                        // _auditedExpiredIds — the snapshot then carries
                        // the id forward in PlatformSnapshot.AuditedExpiredIds
                        // and the post-restart Schedule() seeds
                        // ExpiredAuditAppended=true so the next timer
                        // fire does not emit a duplicate audit envelope.
                        () =>
                        {
                            lock (_lock)
                            {
                                if (_index.TryGetValue(clOrdId, out var cur))
                                    cur.ExpiredAuditAppended = true;
                                _auditedExpiredIds.Add(clOrdId);
                            }
                        });
                }
                catch (WalBackpressureException ex)
                {
                    MetricsRegistry.WalBackpressure.Add(1,
                        new KeyValuePair<string, object?>("call_site", "gtd.expired"));
                    _logger?.LogWarning(ex,
                        "WAL backpressure appending OrderExpiredEvent for {ClOrdId}; retrying with backoff.",
                        clOrdId);
                    MetricsRegistry.GtdOrdersExpired.Add(1,
                        new KeyValuePair<string, object?>("cancel_result", "WalBackpressureRetry"));
                    ReArmRetry(clOrdId);
                    return;
                }
            }

            // 2) Issue the cancel through the regular pipeline.
            var result = await _cancel.CancelAsync(
                    order.Owner,
                    clOrdId,
                    CancellationToken.None,
                    origin: Outbound.OutboundMutationOrigin.Scheduler)
                .ConfigureAwait(false);

            if (result.Kind == OrderCancelResultKind.WalBackpressure)
            {
                _logger?.LogWarning(
                    "WAL backpressure on CancelAsync for GTD-expired {ClOrdId}; retrying with backoff.",
                    clOrdId);
                MetricsRegistry.GtdOrdersExpired.Add(1,
                    new KeyValuePair<string, object?>("cancel_result", result.Kind.ToString()));
                ReArmRetry(clOrdId);
                return;
            }

            // Terminal outcome (Accepted / NotFound / Stale /
            // GatewayFailed): drop the entry. NotFound covers the race
            // where OnOrderTerminal already cleaned up but Resolve is
            // still safe (Dictionary.Remove is no-op on missing key).
            Resolve(clOrdId);

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
                    TimestampUtc: originalGtd,
                    FirmId: order.FirmId));
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
