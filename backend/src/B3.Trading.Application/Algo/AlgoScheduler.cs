using System.Diagnostics;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Outbound;
using B3.Trading.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application;

/// <summary>
/// Time-driven driver for TWAP parents (RFC algo-orders-v0 §4.6 + §4.11).
/// Runs on its own hosted-service thread separate from the
/// <see cref="AlgoEngine"/> consumer so slice-firing latency is bounded
/// by the periodic tick interval and is never coupled to consumer-side
/// work (iceberg refills, terminal-state recording).
///
/// <para>
/// <b>Scheduling model.</b> A simple periodic 100ms tick. Each tick scans
/// non-terminal TWAP parents and asks one question: "is the next due
/// slice's <c>plannedAtUtc &lt;= now</c> AND there is no live child?" If
/// yes, enqueue an <see cref="AlgoCreatedSignal"/> so the engine
/// re-evaluates the same code path it uses at submit time. The engine —
/// not the scheduler — is the sole writer of slice signals to keep the
/// reactor's idempotency contract intact.
/// </para>
///
/// <para>
/// <b>No catch-up burst.</b> RFC §4.6 explicitly forbids dumping a backlog
/// of skipped slices in one tick: at most one slice per parent per tick.
/// A recovered host that was down for several minutes therefore catches
/// up at 100ms granularity, which is "no faster than TWAP itself would
/// have produced + one." This scheduler enforces it implicitly: it
/// enqueues exactly one <see cref="AlgoCreatedSignal"/> per parent per
/// tick, and the engine's per-parent serialisation guarantees the next
/// slice does not fire until the previous child reports back terminal.
/// </para>
///
/// <para>
/// <b>Window expiry.</b> When <c>now &gt;= endUtc</c> the scheduler
/// enqueues an <see cref="AlgoCreatedSignal"/> too — the engine handler
/// recognises the expired-window case and transitions the parent to
/// <c>Expired/TwapWindowExpired</c>. Doing the transition through the
/// engine (rather than the scheduler directly) keeps WAL writes
/// single-threaded and keeps the post-restart code path identical to
/// steady state.
/// </para>
///
/// <para>
/// <b>Promotion plan.</b> RFC §4.11 deferred-B leaves periodic-tick →
/// min-heap → time-wheel as future work, gated on benchmarks from the
/// metrics this slice ships
/// (<c>algo.scheduler.tick_duration</c>, <c>algo.twap.slice_fire_jitter</c>).
/// </para>
/// </summary>
public sealed class AlgoScheduler : BackgroundService
{
    /// <summary>
    /// Tick interval. 100ms is the RFC §4.11 v0 pick — large enough that
    /// the periodic-tick scan stays trivially cheap on modest fleets,
    /// small enough that slice-fire jitter is well below human-noticeable
    /// latency. Tuneable via the constructor for tests that need a
    /// faster clock.
    /// </summary>
    public static readonly TimeSpan DefaultTickInterval = TimeSpan.FromMilliseconds(100);

    private readonly AlgoBook _algos;
    private readonly WorkingOrderBook _orders;
    private readonly IAlgoSignalQueue _signals;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _tickInterval;
    private readonly ILogger<AlgoScheduler> _logger;
    // Pass-4 review (#299) P1. Ambiguous-send replace reservation
    // sweep dependencies. Both optional — test compositions that
    // exercise the scheduler without the modify pipeline must
    // continue to compile. Production composition always supplies
    // both via DI alongside the registry itself.
    private readonly PendingReplacementRegistry? _replacements;
    private readonly IReplaceMarginCoordinator? _replaceMargin;
    private readonly IOptionsMonitor<RiskOptions>? _riskOptions;
    private readonly IOutboundRecoveryGate _recovery;

    public AlgoScheduler(
        AlgoBook algos,
        WorkingOrderBook orders,
        IAlgoSignalQueue signals,
        TimeProvider clock,
        ILogger<AlgoScheduler> logger,
        PendingReplacementRegistry? replacements = null,
        IReplaceMarginCoordinator? replaceMargin = null,
        IOptionsMonitor<RiskOptions>? riskOptions = null,
        IOutboundRecoveryGate? recovery = null)
        : this(algos, orders, signals, clock, DefaultTickInterval, logger,
               replacements, replaceMargin, riskOptions, recovery)
    {
    }

    /// <summary>
    /// Test-friendly constructor; production resolution uses the
    /// <see cref="DefaultTickInterval"/> overload.
    /// </summary>
    public AlgoScheduler(
        AlgoBook algos,
        WorkingOrderBook orders,
        IAlgoSignalQueue signals,
        TimeProvider clock,
        TimeSpan tickInterval,
        ILogger<AlgoScheduler> logger,
        PendingReplacementRegistry? replacements = null,
        IReplaceMarginCoordinator? replaceMargin = null,
        IOptionsMonitor<RiskOptions>? riskOptions = null,
        IOutboundRecoveryGate? recovery = null)
    {
        if (tickInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(tickInterval));
        _algos = algos;
        _orders = orders;
        _signals = signals;
        _clock = clock;
        _tickInterval = tickInterval;
        _logger = logger;
        _replacements = replacements;
        _replaceMargin = replaceMargin;
        _riskOptions = riskOptions;
        _recovery = recovery ?? ImmediateOutboundRecoveryGate.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _recovery.WaitUntilAllRequiredBusinessIngressOpenAsync(stoppingToken)
            .ConfigureAwait(false);
        _logger.LogInformation("AlgoScheduler starting (tick={TickMs}ms).", _tickInterval.TotalMilliseconds);
        // PeriodicTimer is the right primitive: it does not drift on slow
        // ticks (next fire is still aligned to wall-clock interval) and
        // releases the thread between ticks.
        using var timer = new PeriodicTimer(_tickInterval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var sw = ValueStopwatch.StartNew();
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    // Tick must never throw out — that would kill the
                    // scheduler thread and silently freeze every TWAP
                    // parent on the host. Log + carry on.
                    _logger.LogError(ex, "AlgoScheduler tick failed; continuing.");
                }
                MetricsRegistry.AlgoSchedulerTickDuration.Record(sw.GetElapsedMilliseconds());
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            _logger.LogInformation("AlgoScheduler stopped.");
        }
    }

    /// <summary>
    /// Single tick body. Public for tests so they can drive the scheduler
    /// deterministically without spinning the timer.
    /// </summary>
    public void Tick()
    {
        var now = _clock.GetUtcNow();
        // Alert on ambiguous-send replace intents whose held margin
        // reservation has aged past TTL; never release on elapsed time.
        // Cheap no-op when no entries are in the ambiguous state
        // (the common case): the sweep iterates the registry but
        // only acts on entries explicitly flagged via
        // MarkAmbiguousMarginHeld.
        SweepAmbiguousReplaceIntents(now);
        var algos = _algos.EnumerateAll(includeTerminal: false);
        foreach (var algo in algos)
        {
            if (algo.IsTerminal) continue;
            if (algo.Status == AlgoStatus.Cancelling) continue;

            if (algo.Type == AlgoType.Twap && algo.Parameters is TwapParameters tp)
            {
                TickTwap(algo, tp, now);
                continue;
            }

            if (algo.Type == AlgoType.Vwap && algo.Parameters is VwapParameters vp)
            {
                TickVwap(algo, vp, now);
                continue;
            }

            if (algo.Type == AlgoType.Pov && algo.Parameters is PovParameters pp)
            {
                TickPov(algo, pp, now);
                continue;
            }

            if (algo.Type == AlgoType.Pegged && algo.Parameters is PeggedParameters pgp)
            {
                TickPegged(algo, pgp, now);
                continue;
            }
        }
    }

    /// <summary>
    /// Q3.3 (#283). Pegged tick: enqueue an <see cref="AlgoCreatedSignal"/>
    /// unconditionally so the engine re-evaluates the live reference
    /// price. No window/expiry gate (Pegged runs until Filled or DELETE);
    /// no live-child gate (the engine has to evaluate exactly when there
    /// IS a live child — that's the repeg path); no per-slice gate
    /// (single working slice). All throttling lives in the engine via
    /// <c>PeggedLastEvalUtc</c> + <c>RepegInterval</c> for symmetry with
    /// VWAP/POV which also catch up in the engine.
    /// </summary>
    private void TickPegged(Algo algo, PeggedParameters pgp, DateTimeOffset now)
    {
        _ = pgp; _ = now;
        Enqueue(algo);
    }

    private void TickTwap(Algo algo, TwapParameters tp, DateTimeOffset now)
    {
        // Window expiry is independent of slice progress: even if more
        // slices are nominally scheduled, once endUtc has passed the
        // engine must promote to Expired so the parent stops counting
        // as "live work" everywhere.
        if (now >= tp.EndUtc)
        {
            Enqueue(algo);
            return;
        }

        // Determine "is there a live child?" and "what's the next
        // slice we owe?" by scanning the order book — the scheduler
        // intentionally does not share state with the engine to keep
        // the threads decoupled (RFC §4.11 commitment 1).
        int maxSeq = -1;
        bool hasLiveChild = false;
        foreach (var child in _orders.EnumerateChildrenOf(algo.FirmId, algo.AlgoId))
        {
            if (child.AlgoSliceSeq is { } seq && seq > maxSeq) maxSeq = seq;
            if (!IsChildTerminal(child)) hasLiveChild = true;
        }
        if (hasLiveChild) return;

        var nextSeq = maxSeq + 1;
        if (nextSeq >= tp.SliceCount) return; // plan exhausted; wait for endUtc

        var dueAt = TwapPlan.PlannedAtUtc(tp.StartUtc, tp.EndUtc, tp.SliceCount, nextSeq);
        if (now < dueAt) return;

        // Slice fire: capture jitter as (now - plannedAtUtc) before
        // the channel write so dropped signals still surface in the
        // metric.
        MetricsRegistry.AlgoTwapSliceFireJitter.Record((now - dueAt).TotalMilliseconds);
        Enqueue(algo);
    }

    private void TickVwap(Algo algo, VwapParameters vp, DateTimeOffset now)
    {
        // VWAP mirrors TWAP scheduling: window-expired parents need an
        // engine pass to mark Expired; otherwise enqueue at most one
        // Created signal per parent per tick so the engine evaluates
        // the slice (it owns the catch-up loop for empty slots — see
        // OnCreatedAsync).
        if (now >= vp.EndUtc)
        {
            Enqueue(algo);
            return;
        }

        int maxSeq = -1;
        bool hasLiveChild = false;
        foreach (var child in _orders.EnumerateChildrenOf(algo.FirmId, algo.AlgoId))
        {
            if (child.AlgoSliceSeq is { } seq && seq > maxSeq) maxSeq = seq;
            if (!IsChildTerminal(child)) hasLiveChild = true;
        }
        if (hasLiveChild) return;

        var nextSeq = maxSeq + 1;
        var dueAt = VwapPlan.PlannedAtUtc(vp.StartUtc, vp.TickInterval, nextSeq);
        if (now < dueAt) return;
        if (dueAt >= vp.EndUtc) return; // window passed at the slot boundary

        Enqueue(algo);
    }

    private void TickPov(Algo algo, PovParameters pp, DateTimeOffset now)
    {
        // POV mirrors VWAP scheduling: enqueue at most one Created
        // signal per parent per tick so the engine evaluates the slice
        // (it owns the empty-slot catch-up loop in OnCreatedAsync). The
        // engine takes the "is there market volume to share?" decision
        // — the scheduler only gates on time + live-child presence.
        if (now >= pp.EndUtc)
        {
            Enqueue(algo);
            return;
        }

        int maxSeq = -1;
        bool hasLiveChild = false;
        foreach (var child in _orders.EnumerateChildrenOf(algo.FirmId, algo.AlgoId))
        {
            if (child.AlgoSliceSeq is { } seq && seq > maxSeq) maxSeq = seq;
            if (!IsChildTerminal(child)) hasLiveChild = true;
        }
        if (hasLiveChild) return;

        var nextSeq = maxSeq + 1;
        var dueAt = PovPlan.PlannedAtUtc(pp.StartUtc, pp.TickInterval, nextSeq);
        if (now < dueAt) return;
        if (dueAt >= pp.EndUtc) return;

        Enqueue(algo);
    }

    private void Enqueue(Algo algo)
    {
        if (!_signals.TryEnqueue(new AlgoCreatedSignal { FirmId = algo.FirmId, AlgoId = algo.AlgoId }))
        {
            MetricsRegistry.AlgoSignalsDropped.Add(1,
                new KeyValuePair<string, object?>("kind", "scheduler"));
            _logger.LogWarning(
                "AlgoScheduler dropped signal for {Firm}/{AlgoId} (queue full).",
                algo.FirmId, algo.AlgoId);
        }
    }

    /// <summary>
    /// Alert on any margin reservation tied
    /// to a replace intent that's been pending past
    /// <see cref="MarginOptions.AmbiguousReplaceTtl"/> after the
    /// AlgoEngine intentionally retained both intent + reserve on
    /// an ambiguous gateway dispatch failure. Each overdue entry
    /// bumps <c>algo.modify_ambiguous_intent_expired_total</c>
    /// tagged with the parent's algoType (looked up by ParentAlgoId
    /// + FirmId; falls back to "unknown" when the parent is no
    /// longer in the book). Public for the unit test that drives
    /// the sweep deterministically without spinning the timer.
    /// </summary>
    public void SweepAmbiguousReplaceIntents(DateTimeOffset now)
    {
        if (_replacements is null || _replaceMargin is null || _riskOptions is null)
            return;
        var ttl = _riskOptions.CurrentValue.Margin.AmbiguousReplaceTtl;
        if (ttl <= TimeSpan.Zero) return;

        var expired = _replacements.SweepExpiredAmbiguous(now, ttl);
        if (expired.Count == 0) return;

        foreach (var intent in expired)
        {
            // Best-effort algoType tag: ParentAlgoId is null for
            // operator-driven plain-order modifies (those don't
            // currently flow through this AlgoEngine ambiguous path,
            // but tolerate the absence anyway).
            var algoType = "unknown";
            if (intent.ParentAlgoId is { } parentId
                && !string.IsNullOrEmpty(intent.FirmId)
                && _algos.TryGet(intent.FirmId, parentId, out var parent)
                && parent is not null)
            {
                algoType = parent.Type.ToString().ToLowerInvariant();
            }
            MetricsRegistry.AlgoModifyAmbiguousIntentExpiredTotal.Add(1,
                new KeyValuePair<string, object?>("algoType", algoType));

            _logger.LogWarning(
                "Ambiguous-send replace for new ClOrdID {NewClOrdId} (orig {OrigClOrdId}, owner {Owner}, algoType {AlgoType}) exceeded TTL {TtlSeconds}s; reservation remains held and operator reconciliation is required.",
                intent.NewClOrdId, intent.OriginalClOrdId, intent.Owner.Value, algoType, ttl.TotalSeconds);
        }
    }

    private static bool IsChildTerminal(Order o) =>
        o.Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Replaced;

    /// <summary>
    /// Cheap stopwatch-equivalent that avoids allocating a Stopwatch per
    /// tick. <see cref="Stopwatch"/>'s allocation overhead is negligible
    /// in absolute terms but the scheduler ticks 10x/sec so it adds up.
    /// </summary>
    private readonly struct ValueStopwatch
    {
        private static readonly double TimestampToMs = 1000d / Stopwatch.Frequency;

        private readonly long _start;

        private ValueStopwatch(long start) => _start = start;

        public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());

        public double GetElapsedMilliseconds() => (Stopwatch.GetTimestamp() - _start) * TimestampToMs;
    }
}
