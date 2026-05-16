using System.Collections.Concurrent;
using B3.Trading.Application.MarketData;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Application;

/// <summary>
/// Single-consumer hosted service that reacts to <see cref="AlgoSignal"/>s
/// queued by the API and the ER processor. RFC algo-orders-v0 §4.3:
/// "one IHostedService, bounded Channel, single consumer in v0, per-parent
/// serialisation via SemaphoreSlim". V0 keeps the per-parent semaphore
/// implicit — there is exactly one consumer task, so reactor invocations
/// are already serialised. The runtime state map is therefore touched
/// only by this thread; no locks are needed beyond the implicit ones in
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>.
///
/// <para>
/// The reactor implements the Iceberg state machine end-to-end:
/// <list type="bullet">
///   <item><c>AlgoCreated</c> → submit first child slice (no-op if a live
///         child already exists; idempotent under replay/reconciliation).</item>
///   <item><c>ChildExecutionObserved</c> → record fill delta against the
///         parent; on terminal Filled refill the next slice or mark
///         <c>Completed</c>; on Cancelled propagate to the parent based on
///         whether the cancel was operator-driven; on Rejected suspend.</item>
///   <item><c>AlgoCancelRequested</c> → cancel the live child via the
///         gateway; the actual <c>Cancelled</c> transition lands when the
///         child cancel-ack arrives back through the ER pipeline.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Retries:</b> gateway/WAL transient failures retry up to 3 times with
/// 100/300/900 ms back-off via <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
/// Doing the delay inline blocks the consumer for up to ~1.3s on a flaky
/// venue — acceptable in v0 because algo flow is orders-of-magnitude lower
/// than ER flow and pause latency only affects other algos, never order
/// reception. Promotion to a dedicated retry queue is future work if
/// production traffic ever justifies it.
/// </para>
///
/// <para>
/// <b>Recovery:</b> on <see cref="ExecuteAsync"/> entry the engine walks
/// every non-terminal algo, sums the cumulative-quantity of the children
/// in <see cref="WorkingOrderBook"/>, primes per-parent runtime state
/// (live child + slice counter + cum baseline), then re-enqueues an
/// <see cref="AlgoCreatedSignal"/>. The reactor pass that follows either
/// observes a still-live child (no-op) or submits the next slice — same
/// code path as steady-state, so recovery and live behaviour cannot drift.
/// </para>
/// </summary>
public sealed class AlgoEngine : BackgroundService
{
    // Retry policy for transient submit failures (gateway/WAL backpressure).
    // Sequence intentionally short: the operator notices a stuck algo via
    // the suspended-with-RetriesExhausted state much faster than via
    // dashboards, and a long retry tail just delays the alert.
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(900),
    };

    private readonly AlgoSignalQueue _queue;
    private readonly AlgoBook _algos;
    private readonly WorkingOrderBook _orders;
    private readonly OrderSubmissionService _submitter;
    private readonly ClOrdIdPrefixRegistry _clOrdIds;
    private readonly IExchangeGateway _gateway;
    private readonly IAlgoEventSink _algoSink;
    private readonly EventDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly ILogger<AlgoEngine> _logger;
    private readonly OrderOwnershipMap _ownership;
    private readonly VolumeCurveEstimator? _vwapCurve;

    // Per-parent runtime state. Owned by the consumer task; the
    // ConcurrentDictionary is only used because TryAdd/TryGetValue are
    // convenient — no concurrent writers exist in v0.
    private readonly ConcurrentDictionary<(string FirmId, ulong AlgoId), AlgoParentRuntime> _runtime =
        new();

    public AlgoEngine(
        AlgoSignalQueue queue,
        AlgoBook algos,
        WorkingOrderBook orders,
        OrderSubmissionService submitter,
        ClOrdIdPrefixRegistry clOrdIds,
        IExchangeGateway gateway,
        IAlgoEventSink algoSink,
        EventDispatcher dispatcher,
        TimeProvider clock,
        ILogger<AlgoEngine> logger,
        OrderOwnershipMap ownership,
        VolumeCurveEstimator? vwapCurve = null)
    {
        _queue = queue;
        _algos = algos;
        _orders = orders;
        _submitter = submitter;
        _clOrdIds = clOrdIds;
        _gateway = gateway;
        _algoSink = algoSink;
        _dispatcher = dispatcher;
        _clock = clock;
        _logger = logger;
        _ownership = ownership;
        _vwapCurve = vwapCurve;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AlgoEngine consumer task starting.");
        Reconcile();
        try
        {
            await foreach (var signal in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                MetricsRegistry.AlgoSignalsConsumed.Add(1,
                    new KeyValuePair<string, object?>("kind", SignalKind(signal)));
                try
                {
                    await ReactAsync(signal, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // The reactor must never let an exception kill the
                    // consumer task — one stuck algo would freeze every
                    // other algo on the host. Log + continue; the affected
                    // parent stays in whatever state the failed transition
                    // left it (operator-visible via GET /algo).
                    _logger.LogError(ex, "AlgoEngine reactor failed for signal {Kind} algo {AlgoId}/{Firm}.",
                        SignalKind(signal), AlgoIdOf(signal), signal.FirmId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            _logger.LogInformation("AlgoEngine consumer task stopped.");
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Complete();
        return base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Boot-time pass over every non-terminal parent. Builds the runtime
    /// state from the order book (live child + cumulative-fill baseline +
    /// next slice seq) and re-enqueues an <see cref="AlgoCreatedSignal"/>
    /// so the reactor evaluates "do I need to submit more?" through the
    /// same code path as steady-state. Safe to call multiple times because
    /// <see cref="Algo.RehydrateProgress"/> never moves <c>FilledQuantity</c>
    /// backwards and the reactor itself is idempotent on a still-live
    /// child.
    /// </summary>
    private void Reconcile()
    {
        var algos = _algos.EnumerateAll(includeTerminal: false);
        if (algos.Count == 0) return;

        foreach (var algo in algos)
        {
            var rt = _runtime.GetOrAdd((algo.FirmId, algo.AlgoId), static _ => new AlgoParentRuntime());
            var children = _orders.EnumerateChildrenOf(algo.FirmId, algo.AlgoId);

            long totalCum = 0;
            int maxSeq = -1;
            ulong? liveChild = null;
            foreach (var child in children)
            {
                totalCum += child.CumulativeQuantity;
                if (child.AlgoSliceSeq is { } seq && seq > maxSeq) maxSeq = seq;
                rt.ChildBookedCum[child.ClOrdId] = child.CumulativeQuantity;
                if (!IsChildTerminal(child)) liveChild = child.ClOrdId;
            }

            algo.RehydrateProgress(totalCum);
            rt.NextSliceSeq = maxSeq + 1;
            rt.LiveChildClOrdId = liveChild;

            // Re-arm the reactor regardless: even if a live child exists,
            // the reactor may need to react if the child has since become
            // terminal between snapshot capture and recovery.
            if (!_queue.TryEnqueue(new AlgoCreatedSignal { FirmId = algo.FirmId, AlgoId = algo.AlgoId }))
            {
                MetricsRegistry.AlgoSignalsDropped.Add(1,
                    new KeyValuePair<string, object?>("kind", "created"));
                _logger.LogWarning(
                    "AlgoEngine reconciliation dropped Created signal for {Firm}/{AlgoId} (queue full).",
                    algo.FirmId, algo.AlgoId);
            }
        }

        _logger.LogInformation("AlgoEngine reconciliation enqueued {Count} non-terminal parents.", algos.Count);
    }

    private async Task ReactAsync(AlgoSignal signal, CancellationToken ct)
    {
        var (firmId, algoId) = (signal.FirmId, AlgoIdOf(signal));
        if (algoId == 0)
        {
            _logger.LogWarning("AlgoEngine received signal with zero AlgoId; ignoring.");
            return;
        }
        if (!_algos.TryGet(firmId, algoId, out var algo) || algo is null)
        {
            // The parent is gone — should not happen except in tests that
            // tear the book down between submit and consume. Drop quietly.
            _logger.LogWarning("AlgoEngine signal for unknown algo {Firm}/{AlgoId}; dropping.", firmId, algoId);
            return;
        }

        var rt = _runtime.GetOrAdd((firmId, algoId), static _ => new AlgoParentRuntime());

        switch (signal)
        {
            case AlgoCreatedSignal:
                await OnCreatedAsync(algo, rt, ct).ConfigureAwait(false);
                break;
            case ChildExecutionObservedSignal er:
                await OnChildErAsync(algo, rt, er, ct).ConfigureAwait(false);
                break;
            case AlgoCancelRequestedSignal:
                await OnCancelRequestedAsync(algo, rt, ct).ConfigureAwait(false);
                break;
            default:
                _logger.LogWarning("AlgoEngine ignoring unknown signal type {Type}.", signal.GetType().Name);
                break;
        }
    }

    private async Task OnCreatedAsync(Algo algo, AlgoParentRuntime rt, CancellationToken ct)
    {
        if (algo.IsTerminal) return;
        if (algo.Status == AlgoStatus.Cancelling) return;

        if (rt.LiveChildClOrdId is { } existing)
        {
            // A child is already outstanding (steady-state or post-recovery).
            // Idempotent no-op — when the child reaches terminal we'll
            // refill (Iceberg) or wait for the next scheduled tick (TWAP)
            // via OnChildErAsync.
            _logger.LogDebug(
                "AlgoEngine OnCreated: algo {Firm}/{AlgoId} already has live child {Child}; no resubmit.",
                algo.FirmId, algo.AlgoId, existing);
            return;
        }

        if (algo.RemainingQuantity <= 0)
        {
            // Recovery edge-case: snapshot says fully filled but no terminal
            // event yet (crashed between final fill and terminal write).
            // Promote to Completed now so /algo reflects truth.
            await RecordTerminalAsync(algo, rt, AlgoStatus.Completed, AlgoTerminalReason.None).ConfigureAwait(false);
            return;
        }

        // TWAP: scheduling decisions for "is the next slice due?" and
        // "has the window expired?" live here. The scheduler thread fires
        // an AlgoCreatedSignal at every relevant transition; the engine
        // re-evaluates from scratch using the deterministic plan so the
        // two threads cannot drift.
        if (algo.Type == AlgoType.Twap && algo.Parameters is TwapParameters tp)
        {
            var now = _clock.GetUtcNow();
            if (now >= tp.EndUtc)
            {
                // Window passed (mid-execution OR during downtime). RFC
                // §4.6: parent transitions to Expired with the residue
                // preserved on FilledQuantity. If RemainingQuantity is
                // zero the earlier branch above already routed to
                // Completed.
                await RecordTerminalAsync(algo, rt, AlgoStatus.Expired, AlgoTerminalReason.TwapWindowExpired).ConfigureAwait(false);
                return;
            }

            if (rt.NextSliceSeq >= tp.SliceCount)
            {
                // Plan exhausted but residue remains and window still
                // open — no slices left to submit; wait for window
                // expiry to mark Expired.
                return;
            }

            var dueAt = TwapPlan.PlannedAtUtc(tp.StartUtc, tp.EndUtc, tp.SliceCount, rt.NextSliceSeq);
            if (now < dueAt)
            {
                // Not due yet — scheduler will re-fire when plannedAtUtc
                // arrives. No-op (idempotent under tick storms).
                return;
            }
        }
        else if (algo.Type == AlgoType.Vwap && algo.Parameters is VwapParameters vp)
        {
            // VWAP: same shape as TWAP but the slice-quantity decision
            // is driven by the live volume curve via ComputeNextSlice.
            // Empty slots (gap <= 0) are skipped by advancing
            // NextSliceSeq without submitting — keeps the slot index in
            // step with the deterministic plannedAtUtc grid so recovery
            // is unambiguous.
            var now = _clock.GetUtcNow();
            if (now >= vp.EndUtc)
            {
                await RecordTerminalAsync(algo, rt, AlgoStatus.Expired, AlgoTerminalReason.VwapWindowExpired).ConfigureAwait(false);
                return;
            }

            // Skip any already-due empty slots up to the first one with
            // qty > 0 (or the first one not yet due, whichever comes
            // first). The scheduler ticks every 100ms so the catch-up
            // loop runs at most until the volume curve produces a non-
            // zero gap or we hit the future.
            while (true)
            {
                var dueAt = VwapPlan.PlannedAtUtc(vp.StartUtc, vp.TickInterval, rt.NextSliceSeq);
                if (now < dueAt) return;
                if (dueAt >= vp.EndUtc)
                {
                    // No more in-window slots; let the window-expiry
                    // path drive the terminal transition.
                    return;
                }
                var (qty, _, _, _) = ComputeVwapSlice(algo, vp, dueAt);
                if (qty > 0) break;
                rt.NextSliceSeq++;
            }
        }

        await SubmitNextSliceAsync(algo, rt, ct).ConfigureAwait(false);
    }

    private async Task OnChildErAsync(Algo algo, AlgoParentRuntime rt, ChildExecutionObservedSignal er, CancellationToken ct)
    {
        if (!_orders.TryGet(er.ChildClOrdId, out var child) || child is null)
        {
            _logger.LogWarning("AlgoEngine child ER for unknown child {ClOrdId}; dropping.", er.ChildClOrdId);
            return;
        }

        // Book the cum-quantity delta. Child orders deliver fills via
        // ApplyCumulativeFill so child.CumulativeQuantity is monotonic;
        // we just diff against the last value we credited to the parent.
        var prevBooked = rt.ChildBookedCum.GetValueOrDefault(child.ClOrdId, 0L);
        var delta = child.CumulativeQuantity - prevBooked;
        if (delta > 0)
        {
            algo.RecordFill(delta);
            rt.ChildBookedCum[child.ClOrdId] = child.CumulativeQuantity;
        }

        // Non-terminal child: nothing more to do (the next ER will land).
        if (!IsChildTerminal(child)) return;

        // Child is terminal — this clOrdId is no longer live regardless of
        // outcome. Clear the slot before transitioning so re-entrancy via
        // RecordTerminalAsync sees a clean state.
        if (rt.LiveChildClOrdId == child.ClOrdId)
            rt.LiveChildClOrdId = null;

        switch (child.Status)
        {
            case OrderStatus.Filled:
                if (algo.IsTerminal) return; // redundant ER after we already terminal-ed
                if (algo.Status == AlgoStatus.Cancelling)
                {
                    // Race: operator cancelled while the venue was filling
                    // the residue. Treat the parent as Cancelled with the
                    // residue booked — partial-fill outcome is preserved
                    // by FilledQuantity already.
                    await RecordTerminalAsync(algo, rt, AlgoStatus.Cancelled, AlgoTerminalReason.UserCancelled).ConfigureAwait(false);
                    return;
                }
                if (algo.RemainingQuantity <= 0)
                {
                    await RecordTerminalAsync(algo, rt, AlgoStatus.Completed, AlgoTerminalReason.None).ConfigureAwait(false);
                    return;
                }
                rt.RetryAttempts = 0;
                if (algo.Type == AlgoType.Twap && algo.Parameters is TwapParameters tpFilled)
                {
                    // TWAP child finished but residue remains. The
                    // scheduler — not the engine — drives the next slice:
                    // the next AlgoCreatedSignal will arrive at
                    // plannedAtUtc(NextSliceSeq) (or sooner if catch-up
                    // is needed). RFC §4.6 forbids the engine from
                    // bursting slices on its own. Window-expired-during-
                    // child-fill is also handled here: if the window
                    // already passed there is no point waiting for the
                    // scheduler tick.
                    if (_clock.GetUtcNow() >= tpFilled.EndUtc)
                    {
                        await RecordTerminalAsync(algo, rt, AlgoStatus.Expired, AlgoTerminalReason.TwapWindowExpired).ConfigureAwait(false);
                    }
                    return;
                }
                if (algo.Type == AlgoType.Vwap && algo.Parameters is VwapParameters vpFilled)
                {
                    // VWAP child finished but residue remains. Mirror
                    // TWAP: scheduler will re-fire at the next slot.
                    // Window-expired-during-child-fill same handling.
                    if (_clock.GetUtcNow() >= vpFilled.EndUtc)
                    {
                        await RecordTerminalAsync(algo, rt, AlgoStatus.Expired, AlgoTerminalReason.VwapWindowExpired).ConfigureAwait(false);
                    }
                    return;
                }
                await SubmitNextSliceAsync(algo, rt, ct).ConfigureAwait(false);
                return;

            case OrderStatus.Cancelled:
                if (algo.IsTerminal) return;
                if (algo.Status == AlgoStatus.Cancelling)
                {
                    // Operator-driven cancel completed; mark Cancelled.
                    // FilledQuantity already reflects any partial that
                    // landed before the cancel-ack.
                    await RecordTerminalAsync(algo, rt, AlgoStatus.Cancelled, AlgoTerminalReason.UserCancelled).ConfigureAwait(false);
                }
                else if (IsTwapWindowExpired(algo))
                {
                    // RFC §4.6 "window passed during downtime AND child was
                    // live": engine reconciles the child via the ordinary
                    // ER path, then evaluates the parent — if not Completed,
                    // mark Expired regardless of why the child terminated.
                    // Window-expiry is the more specific signal here than
                    // VenueCancelled.
                    await RecordTerminalAsync(algo, rt, AlgoStatus.Expired, AlgoTerminalReason.TwapWindowExpired).ConfigureAwait(false);
                }
                else if (IsVwapWindowExpired(algo))
                {
                    await RecordTerminalAsync(algo, rt, AlgoStatus.Expired, AlgoTerminalReason.VwapWindowExpired).ConfigureAwait(false);
                }
                else
                {
                    // Venue cancelled the child without operator request
                    // (FAK timeout, cross prevention, etc). Suspend with
                    // VenueCancelled so the operator can decide whether
                    // to resubmit; auto-refilling in this case can spin
                    // a tight loop against an unhappy venue.
                    await RecordTerminalAsync(algo, rt, AlgoStatus.Suspended, AlgoTerminalReason.VenueCancelled).ConfigureAwait(false);
                }
                return;

            case OrderStatus.Rejected:
                if (algo.IsTerminal) return;
                if (IsTwapWindowExpired(algo))
                {
                    await RecordTerminalAsync(algo, rt, AlgoStatus.Expired, AlgoTerminalReason.TwapWindowExpired).ConfigureAwait(false);
                    return;
                }
                if (IsVwapWindowExpired(algo))
                {
                    await RecordTerminalAsync(algo, rt, AlgoStatus.Expired, AlgoTerminalReason.VwapWindowExpired).ConfigureAwait(false);
                    return;
                }
                await RecordTerminalAsync(algo, rt, AlgoStatus.Suspended, AlgoTerminalReason.RiskRejected).ConfigureAwait(false);
                return;
        }
    }

    private bool IsTwapWindowExpired(Algo algo) =>
        algo.Type == AlgoType.Twap
        && algo.Parameters is TwapParameters tp
        && _clock.GetUtcNow() >= tp.EndUtc;

    private bool IsVwapWindowExpired(Algo algo) =>
        algo.Type == AlgoType.Vwap
        && algo.Parameters is VwapParameters vp
        && _clock.GetUtcNow() >= vp.EndUtc;

    private async Task OnCancelRequestedAsync(Algo algo, AlgoParentRuntime rt, CancellationToken ct)
    {
        // Operator already drove the parent into Cancelling via the API;
        // reactor's job is to take down the live child (if any). When
        // there is no live child the parent can move straight to Cancelled
        // — nothing to wait for from the venue.
        if (algo.Status != AlgoStatus.Cancelling)
        {
            // Replay-time edge-case: a cancel was enqueued but later events
            // (gateway-failed, etc) already moved the parent past
            // Cancelling. Nothing to do.
            return;
        }

        if (rt.LiveChildClOrdId is not { } childClOrdId)
        {
            await RecordTerminalAsync(algo, rt, AlgoStatus.Cancelled, AlgoTerminalReason.UserCancelled).ConfigureAwait(false);
            return;
        }

        if (!_orders.TryGet(childClOrdId, out var child) || child is null)
        {
            // Live child went missing — assume already cancelled out-of-band.
            rt.LiveChildClOrdId = null;
            await RecordTerminalAsync(algo, rt, AlgoStatus.Cancelled, AlgoTerminalReason.UserCancelled).ConfigureAwait(false);
            return;
        }

        var newClOrdId = _clOrdIds.Generate(child.Owner);
        try
        {
            // Pre-register the cancel-side → original mapping so the
            // cancel-ack ER can resolve back to the child order even if
            // upstream omits OrigClOrdID on the wire.
            _ownership.RegisterCancelLink(newClOrdId, child.ClOrdId);
            await _gateway.CancelAsync(child, newClOrdId, ct).ConfigureAwait(false);
            // Don't mark terminal here — wait for the cancel-ack ER to land
            // via OnChildErAsync. That's what makes the engine consistent
            // with replay (the WAL records the ER, never the cancel intent).
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AlgoEngine cancel of child {Child} for algo {Firm}/{AlgoId} failed; operator may retry DELETE.",
                childClOrdId, algo.FirmId, algo.AlgoId);
            // Stay in Cancelling so DELETE is re-driveable; do not auto-suspend.
        }
    }

    private async Task SubmitNextSliceAsync(Algo algo, AlgoParentRuntime rt, CancellationToken ct)
    {
        var (sliceQty, slicePrice) = ComputeNextSlice(algo);
        if (sliceQty <= 0)
        {
            if (algo.Type == AlgoType.Vwap)
            {
                // VWAP: empty slot — the parent is ahead of the curve.
                // Advance NextSliceSeq so the next scheduler tick
                // evaluates the next slot. Do NOT mark terminal.
                rt.NextSliceSeq++;
                return;
            }
            // Should be unreachable (RemainingQuantity == 0 is checked by
            // callers) — defensive log + complete.
            await RecordTerminalAsync(algo, rt, AlgoStatus.Completed, AlgoTerminalReason.None).ConfigureAwait(false);
            return;
        }

        var sliceSeq = rt.NextSliceSeq;
        var orderType = algo.Parameters switch
        {
            IcebergParameters => OrderType.Limit,
            TwapParameters tp => tp.ChildOrderType,
            VwapParameters vp => vp.ChildOrderType,
            _ => OrderType.Limit,
        };

        // Capture VWAP audit envelope inputs BEFORE submit so we can WAL
        // them once the venue accepts the child. Recompute is cheap and
        // it keeps the envelope honest under retry loops.
        long vwapTargetCum = 0, vwapExecutedCum = 0;
        DateTimeOffset vwapPlannedAt = default;
        if (algo.Parameters is VwapParameters vpForAudit)
        {
            vwapPlannedAt = VwapPlan.PlannedAtUtc(vpForAudit.StartUtc, vpForAudit.TickInterval, sliceSeq);
            var (_, _, target, gap) = ComputeVwapSlice(algo, vpForAudit, vwapPlannedAt);
            vwapTargetCum = target;
            vwapExecutedCum = algo.FilledQuantity;
            MetricsRegistry.AlgoVwapTargetVsActualDiff.Record(gap);
        }

        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            var req = new OrderSubmissionRequest(
                Owner: algo.Owner,
                FirmId: algo.FirmId,
                Symbol: algo.Symbol,
                SecurityId: algo.SecurityId,
                Side: algo.Side,
                Type: orderType,
                Quantity: sliceQty,
                Price: slicePrice,
                Source: OrderSubmissionSource.Algo,
                ParentAlgoId: algo.AlgoId,
                AlgoSliceSeq: sliceSeq);

            OrderSubmissionResult result;
            try
            {
                result = await _submitter.SubmitAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "AlgoEngine submit threw for algo {Firm}/{AlgoId} slice {Seq}; suspending.",
                    algo.FirmId, algo.AlgoId, sliceSeq);
                await RecordTerminalAsync(algo, rt, AlgoStatus.Suspended, AlgoTerminalReason.GatewayUnavailable).ConfigureAwait(false);
                return;
            }

            switch (result.Kind)
            {
                case OrderSubmissionResultKind.Accepted:
                    algo.MarkWorking();
                    rt.LiveChildClOrdId = result.ClOrdId;
                    rt.ChildBookedCum[result.ClOrdId] = 0;
                    rt.NextSliceSeq = sliceSeq + 1;
                    rt.RetryAttempts = 0;
                    MetricsRegistry.AlgoChildrenSubmitted.Add(1,
                        new KeyValuePair<string, object?>("type", algo.Type.ToString().ToLowerInvariant()));
                    if (algo.Type == AlgoType.Vwap)
                    {
                        MetricsRegistry.AlgoVwapSlicesEmitted.Add(1);
                        // Best-effort audit envelope. WAL backpressure
                        // here is non-fatal — recovery doesn't need it.
                        try
                        {
                            _dispatcher.Dispatch(
                                new AlgoVwapSlicedEvent
                                {
                                    AlgoId = algo.AlgoId,
                                    FirmId = algo.FirmId,
                                    SliceSeq = sliceSeq,
                                    TargetCumQty = vwapTargetCum,
                                    ExecutedCum = vwapExecutedCum,
                                    SliceQty = sliceQty,
                                    PlannedAtUtc = vwapPlannedAt,
                                },
                                static () => { });
                        }
                        catch (WalBackpressureException)
                        {
                            MetricsRegistry.WalBackpressure.Add(1,
                                new KeyValuePair<string, object?>("call_site", "algo.vwap.sliced"));
                        }
                    }
                    _algoSink.PublishAlgoSnapshot(algo.Owner, algo.FirmId, algo.AlgoId);
                    return;

                case OrderSubmissionResultKind.Rejected:
                    // Risk rejected the slice — treat the parent as
                    // suspended so the operator can decide. The synthetic
                    // rejection ER will arrive separately and cycle through
                    // OnChildErAsync, which is a no-op once the parent is
                    // already terminal.
                    await RecordTerminalAsync(algo, rt, AlgoStatus.Suspended, AlgoTerminalReason.RiskRejected).ConfigureAwait(false);
                    return;

                case OrderSubmissionResultKind.Drained:
                    await RecordTerminalAsync(algo, rt, AlgoStatus.Suspended, AlgoTerminalReason.Drained).ConfigureAwait(false);
                    return;

                case OrderSubmissionResultKind.BadRequest:
                    // Programming error: the engine produced an invalid
                    // submit request. Suspend with the closest reason and
                    // log loudly so it's noticed.
                    _logger.LogError(
                        "AlgoEngine submit returned BadRequest ({Reason}) for algo {Firm}/{AlgoId}; suspending.",
                        result.Reason, algo.FirmId, algo.AlgoId);
                    await RecordTerminalAsync(algo, rt, AlgoStatus.Suspended, AlgoTerminalReason.RiskRejected).ConfigureAwait(false);
                    return;

                case OrderSubmissionResultKind.GatewayFailed:
                case OrderSubmissionResultKind.WalBackpressure:
                    if (attempt >= RetryDelays.Length)
                    {
                        var reason = result.Kind == OrderSubmissionResultKind.GatewayFailed
                            ? AlgoTerminalReason.RetriesExhausted
                            : AlgoTerminalReason.RetriesExhausted;
                        await RecordTerminalAsync(algo, rt, AlgoStatus.Suspended, reason).ConfigureAwait(false);
                        return;
                    }
                    _logger.LogWarning(
                        "AlgoEngine submit transient ({Kind}, {Reason}) for algo {Firm}/{AlgoId} slice {Seq}; retry {Attempt}.",
                        result.Kind, result.Reason, algo.FirmId, algo.AlgoId, sliceSeq, attempt + 1);
                    try
                    {
                        await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    continue;
            }
        }
    }

    private async Task RecordTerminalAsync(Algo algo, AlgoParentRuntime rt, AlgoStatus status, AlgoTerminalReason reason)
    {
        if (algo.IsTerminal) return;

        rt.LiveChildClOrdId = null;
        var atUtc = DateTimeOffset.UtcNow;
        try
        {
            _dispatcher.Dispatch(
                new AlgoTerminalStateRecordedEvent
                {
                    AlgoId = algo.AlgoId,
                    FirmId = algo.FirmId,
                    Status = status.ToString(),
                    Reason = reason.ToString(),
                    AtUtc = atUtc,
                },
                () => algo.RecordTerminal(status, reason, atUtc));
        }
        catch (WalBackpressureException)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "algo.terminal"));
            // Don't mutate the in-memory aggregate without journaling — that
            // would desynchronise WAL replay. Operator will see the parent
            // stuck and can retry via DELETE; a chronic WAL outage already
            // triggers the broader backpressure alert.
            _logger.LogError("WAL backpressure recording terminal {Status}/{Reason} for algo {Firm}/{AlgoId}.",
                status, reason, algo.FirmId, algo.AlgoId);
            return;
        }

        _algoSink.PublishAlgoSnapshot(algo.Owner, algo.FirmId, algo.AlgoId);
        if (algo.Type == AlgoType.Vwap && status == AlgoStatus.Cancelled)
        {
            MetricsRegistry.AlgoVwapCancelled.Add(1);
        }
        await Task.CompletedTask;
    }

    private (long Quantity, decimal? Price) ComputeNextSlice(Algo algo)
    {
        switch (algo.Parameters)
        {
            case IcebergParameters ip:
                {
                    var qty = Math.Min(ip.DisplayQuantity, algo.RemainingQuantity);
                    return (qty, ip.LimitPrice);
                }
            case TwapParameters tp:
                {
                    // Deterministic per-slice quantity (RFC §4.8): floor on
                    // slices 0..n-2 and the remainder on the last slice so
                    // the parent total reconciles exactly. Recovery
                    // re-derives the same numbers from the parameters
                    // alone — no separate persisted plan.
                    var rt = _runtime[(algo.FirmId, algo.AlgoId)];
                    var seq = rt.NextSliceSeq;
                    var planned = TwapPlan.SliceQty(algo.TotalQuantity, tp.SliceCount, seq);
                    // Cap at remaining: fills from earlier slices may
                    // partially-cancel a slice and shift residue forward,
                    // so the planned quantity can exceed what's actually
                    // outstanding.
                    var qty = Math.Min(planned, algo.RemainingQuantity);
                    return (qty, tp.ChildPrice);
                }
            case VwapParameters vp:
                {
                    var rt = _runtime[(algo.FirmId, algo.AlgoId)];
                    var dueAt = VwapPlan.PlannedAtUtc(vp.StartUtc, vp.TickInterval, rt.NextSliceSeq);
                    var (qty, price, _, _) = ComputeVwapSlice(algo, vp, dueAt);
                    return (qty, price);
                }
            default:
                return (algo.RemainingQuantity, null);
        }
    }

    /// <summary>
    /// VWAP slice computation: ask the curve for the CDF at the slot's
    /// <c>plannedAtUtc</c>, derive the target cumulative qty, apply the
    /// per-slice caps. Returns <c>(qty, price, targetCum, gap)</c> so the
    /// emission path can record the audit envelope without recomputing.
    /// </summary>
    private (long Qty, decimal? Price, long TargetCum, long Gap) ComputeVwapSlice(
        Algo algo, VwapParameters vp, DateTimeOffset evaluateAtUtc)
    {
        var cdf = _vwapCurve?.CdfAt(algo.Symbol, vp.StartUtc, vp.EndUtc, evaluateAtUtc)
            ?? UniformCdf(vp.StartUtc, vp.EndUtc, evaluateAtUtc);
        var targetCum = VwapPlan.TargetCumQty(algo.TotalQuantity, cdf);
        long recentMarketVolume = 0;
        if (vp.ParticipationCap is { } && _vwapCurve is not null)
        {
            // "Recent" volume window = one tick interval prior to now.
            // Smaller windows over-react to micro-bursts; larger windows
            // mute the cap. One tick interval matches the slice cadence.
            var lookback = evaluateAtUtc - vp.TickInterval;
            if (lookback < vp.StartUtc) lookback = vp.StartUtc;
            recentMarketVolume = _vwapCurve.VolumeBetween(algo.Symbol, lookback, evaluateAtUtc);
        }
        var executedCum = algo.FilledQuantity;
        var gap = targetCum - executedCum;
        var qty = VwapPlan.SliceQty(
            targetCum,
            executedCum,
            algo.RemainingQuantity,
            algo.TotalQuantity,
            vp.SliceMaxPct,
            vp.ParticipationCap,
            recentMarketVolume);
        var price = VwapPlan.ClampPrice(vp.ChildPrice, vp.PriceLimit, algo.Side);
        return (qty, price, targetCum, gap);
    }

    private static double UniformCdf(DateTimeOffset start, DateTimeOffset end, DateTimeOffset at)
    {
        if (end <= start) return 0;
        if (at <= start) return 0;
        if (at >= end) return 1;
        return (at - start).TotalSeconds / (end - start).TotalSeconds;
    }

    private static bool IsChildTerminal(Order o) =>
        o.Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Replaced;

    private static string SignalKind(AlgoSignal s) => s switch
    {
        AlgoCreatedSignal => "created",
        AlgoCancelRequestedSignal => "cancel_requested",
        ChildExecutionObservedSignal => "child_er",
        _ => "unknown",
    };

    private static ulong AlgoIdOf(AlgoSignal s) => s switch
    {
        AlgoCreatedSignal c => c.AlgoId,
        AlgoCancelRequestedSignal c => c.AlgoId,
        ChildExecutionObservedSignal c => c.AlgoId,
        _ => 0,
    };

    /// <summary>
    /// Mutable per-parent runtime state. Lives only in memory (not
    /// snapshotted) — recovery rebuilds it from the order book on engine
    /// start. Not thread-safe; only the single consumer task touches it.
    /// </summary>
    private sealed class AlgoParentRuntime
    {
        public ulong? LiveChildClOrdId;
        public int NextSliceSeq;
        public int RetryAttempts;
        public Dictionary<ulong, long> ChildBookedCum { get; } = new();
    }
}
