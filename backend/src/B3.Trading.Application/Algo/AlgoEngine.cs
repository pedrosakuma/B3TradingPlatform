using System.Collections.Concurrent;
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
    private readonly ILogger<AlgoEngine> _logger;

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
        ILogger<AlgoEngine> logger)
    {
        _queue = queue;
        _algos = algos;
        _orders = orders;
        _submitter = submitter;
        _clOrdIds = clOrdIds;
        _gateway = gateway;
        _algoSink = algoSink;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AlgoEngine consumer task starting.");
        Reconcile();
        try
        {
            await foreach (var signal in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
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
            // refill via OnChildErAsync.
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
                await RecordTerminalAsync(algo, rt, AlgoStatus.Suspended, AlgoTerminalReason.RiskRejected).ConfigureAwait(false);
                return;
        }
    }

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
            _ => OrderType.Limit,
        };

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
        await Task.CompletedTask;
    }

    private static (long Quantity, decimal? Price) ComputeNextSlice(Algo algo)
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
                    // TWAP slice sizing is the slice-6 problem; fall back
                    // to even split here for now so a partial implementation
                    // doesn't compile-break the iceberg path.
                    var slices = Math.Max(1, tp.SliceCount);
                    var qty = Math.Max(1, algo.TotalQuantity / slices);
                    return (Math.Min(qty, algo.RemainingQuantity), tp.ChildPrice);
                }
            default:
                return (algo.RemainingQuantity, null);
        }
    }

    private static bool IsChildTerminal(Order o) =>
        o.Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected;

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
