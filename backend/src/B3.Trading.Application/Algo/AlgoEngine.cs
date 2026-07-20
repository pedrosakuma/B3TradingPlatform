using System.Collections.Concurrent;
using B3.Trading.Application.MarketData;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
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
///         common outbound coordinator; the actual <c>Cancelled</c> transition lands when the
///         child cancel-ack arrives back through the ER pipeline.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Retries:</b> the engine never retries an outbound mutation merely
/// because time elapsed or the process epoch changed. Proven-unsent retry
/// is an explicit coordinator decision against the same durable logical
/// action and is bounded by the outbound attempt cap.
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
    private readonly AlgoSignalQueue _queue;
    private readonly AlgoBook _algos;
    private readonly WorkingOrderBook _orders;
    private readonly OrderSubmissionService _submitter;
    private readonly OrderCancelService _canceller;
    private readonly OrderModifyService _modifier;
    private readonly OutboundMutationLedger _outboundLedger;
    private readonly IAlgoEventSink _algoSink;
    private readonly EventDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly ILogger<AlgoEngine> _logger;
    private readonly OrderOwnershipMap _ownership;
    private readonly VolumeCurveEstimator? _vwapCurve;
    private readonly MarketDataVolumePump? _volumePump;
    private readonly PegBookTopCache? _pegBookTop;
    private readonly MarketDataPegBookPump? _pegBookPump;
    /// <summary>
    /// Pass-1 review (#295) P1#1. Per-POV scheduling progress
    /// (cumulative market volume + last-evaluate timestamp). Persisted
    /// via <see cref="Persistence.AlgoPovSlicedEvent"/> on slice emit
    /// + via the platform snapshot, restored on engine boot in
    /// <see cref="Reconcile"/>. Null-tolerant for legacy test
    /// compositions that don't exercise POV.
    /// </summary>
    private readonly PovProgressBook? _povProgress;
    /// <summary>
    /// Pass-1 review (#296) P1-C. Per-Pegged in-flight repeg-cycle
    /// marker. Persisted via
    /// <see cref="Persistence.AlgoPeggedRepegStartedEvent"/> /
    /// <see cref="Persistence.AlgoPeggedRepegResolvedEvent"/> + the
    /// platform snapshot. Reconcile reads the book on engine boot
    /// to rebuild <c>AlgoParentRuntime.RepegPending</c> +
    /// <c>LastRepegCancelledChildId</c> so a post-restart cancel-ack
    /// ER routes through SubmitNextSliceAsync rather than the
    /// venue-cancel suspension path. Null-tolerant.
    /// </summary>
    private readonly PeggedRepegBook? _peggedRepeg;

    /// <summary>
    /// Q3.5 (#285). In-flight cancel-replace intents for child orders
    /// the engine has modified. Same registry the manual modify
    /// pipeline (<see cref="OrderModifyService"/>) populates — the
    /// ER processor consumes from it on the Replaced ack to hydrate
    /// the new child Order in the book. Optional only to keep the
    /// test composition (which builds the engine without the full
    /// modify pipeline) buildable; production composition always
    /// supplies it.
    /// </summary>
    private readonly PendingReplacementRegistry? _replacements;

    private readonly IReplaceMarginCoordinator? _replaceMargin;
    private readonly Lifecycle.IDrainController? _reconciliationDrain;
    private readonly Outbound.IOutboundRecoveryGate _outboundRecovery;
    private readonly OutboundProcessEpoch? _outboundEpoch;

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
        OrderCancelService canceller,
        OrderModifyService modifier,
        OutboundMutationLedger outboundLedger,
        IAlgoEventSink algoSink,
        EventDispatcher dispatcher,
        TimeProvider clock,
        ILogger<AlgoEngine> logger,
        OrderOwnershipMap ownership,
        VolumeCurveEstimator? vwapCurve = null,
        MarketDataVolumePump? volumePump = null,
        PovProgressBook? povProgress = null,
        PegBookTopCache? pegBookTop = null,
        MarketDataPegBookPump? pegBookPump = null,
        PeggedRepegBook? peggedRepeg = null,
        PendingReplacementRegistry? replacements = null,
        IReplaceMarginCoordinator? replaceMargin = null,
        SymbolDirectory? symbols = null,
        Lifecycle.IDrainController? reconciliationDrain = null,
        Outbound.IOutboundRecoveryGate? outboundRecovery = null,
        OutboundProcessEpoch? outboundEpoch = null)
    {
        _queue = queue;
        _algos = algos;
        _orders = orders;
        _submitter = submitter;
        _canceller = canceller;
        _modifier = modifier;
        _outboundLedger = outboundLedger;
        _algoSink = algoSink;
        _dispatcher = dispatcher;
        _clock = clock;
        _logger = logger;
        _ownership = ownership;
        _vwapCurve = vwapCurve;
        _volumePump = volumePump;
        _povProgress = povProgress;
        _pegBookTop = pegBookTop;
        _pegBookPump = pegBookPump;
        _peggedRepeg = peggedRepeg;
        _replacements = replacements;
        _replaceMargin = replaceMargin;
        _symbols = symbols;
        _reconciliationDrain = reconciliationDrain;
        _outboundRecovery = outboundRecovery
            ?? Outbound.ImmediateOutboundRecoveryGate.Instance;
        _outboundEpoch = outboundEpoch;
    }

    /// <summary>
    /// #518. Instrument lot-size table (optional, null-tolerant for test
    /// compositions). Used by <see cref="ResolveLotSize"/> /
    /// <see cref="RoundDownToLot"/> so every algo child slice is a whole
    /// multiple of the instrument lot — otherwise the pre-trade
    /// <c>MinLotSizeCheck</c> rejects the child and terminally suspends the
    /// parent on round-lot venues (every B3 equity has lot 100).
    /// </summary>
    private readonly SymbolDirectory? _symbols;

    /// <summary>
    /// #518. Resolves the instrument lot size for <paramref name="symbol"/>,
    /// or 1 when unknown / unconstrained (fail-open, mirroring
    /// <c>MinLotSizeCheck</c>'s posture for unconfigured symbols).
    /// </summary>
    private long ResolveLotSize(string symbol)
    {
        if (_symbols is not null
            && _symbols.TryGetSpec(symbol, out var spec)
            && spec.LotSize is { } lot
            && lot > 1)
        {
            return lot;
        }
        return 1;
    }

    /// <summary>
    /// #518. Rounds <paramref name="qty"/> down to the largest whole
    /// multiple of the instrument lot size. A no-op when the lot is 1 /
    /// unknown or <paramref name="qty"/> is already aligned. A positive
    /// sub-lot quantity floors to zero, which the slice dispatcher treats
    /// as "defer this tick" rather than submitting an odd lot the venue
    /// would reject.
    /// </summary>
    private long RoundDownToLot(string symbol, long qty)
    {
        if (qty <= 0) return qty;
        var lot = ResolveLotSize(symbol);
        return lot > 1 ? qty - (qty % lot) : qty;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _outboundRecovery.WaitUntilAllRequiredBusinessIngressOpenAsync(stoppingToken)
            .ConfigureAwait(false);
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
    /// Test-only deterministic adoption probe (#329, #434). Returns
    /// the parent's currently-adopted child ClOrdID — i.e. the value
    /// the modify path will read from <c>rt.LiveChildClOrdId</c> when
    /// the next operator-modify signal is dispatched. The book
    /// carrying the new child does NOT guarantee adoption has
    /// happened: the ER processor first hydrates the child (visible
    /// in the book) then enqueues a <see cref="ChildExecutionObservedSignal"/>;
    /// the engine consumer task processes that signal asynchronously
    /// and only then updates <c>LiveChildClOrdId</c>. Tests that
    /// drive successive modify cycles MUST poll this before issuing
    /// the next modify, otherwise the engine still sees the prior
    /// child and dispatches a replace with the wrong OriginalClOrdId.
    /// #434 ordering guarantee: by the time this probe observes the
    /// new ClOrdID, <see cref="AlgoParentRuntime.RetireChildSlot"/>,
    /// the <c>AlgoModifyRetiredChildEvictedTotal</c> counter bump and
    /// the pegged-repeg resolution dispatch are ALL already committed
    /// — adoption is published via a lock-fenced setter so prior
    /// bookkeeping is happens-before visible to readers that observe
    /// the flip. Returns null when the parent runtime hasn't been
    /// created yet or has no live child (e.g. mid-terminal transition).
    /// </summary>
    internal ulong? TryGetLiveChildClOrdId(string firmId, ulong algoId)
    {
        return _runtime.TryGetValue((firmId, algoId), out var rt)
            ? rt.LiveChildClOrdId
            : null;
    }

    /// <summary>
    /// Boot-time pass over every non-terminal parent. Builds the runtime
    /// state from the outbound ledger plus order book (live child +
    /// cumulative-fill baseline + next slice seq) and re-enqueues an <see cref="AlgoCreatedSignal"/>
    /// so the reactor evaluates "do I need to submit more?" through the
    /// same code path as steady-state. Safe to call multiple times because
    /// <see cref="Algo.RehydrateProgress"/> never moves <c>FilledQuantity</c>
    /// backwards and the reactor itself is idempotent on a still-live
    /// child. A recovered child without durable algo-origin ledger evidence
    /// is suspended as reconciliation-required instead of being re-sliced.
    /// </summary>
    private void Reconcile()
    {
        var algos = _algos.EnumerateAll(includeTerminal: false);
        if (algos.Count == 0)
        {
            // Even with no live algos, prune any orphan POV progress
            // entries restored from a snapshot written by an older
            // engine version that didn't remove on terminal.
            PruneOrphanPovProgress(algos);
            PrunePeggedRepegBookOrphans(algos);
            return;
        }

        foreach (var algo in algos)
        {
            var rt = _runtime.GetOrAdd((algo.FirmId, algo.AlgoId), static _ => new AlgoParentRuntime());
            var children = _orders.EnumerateChildrenOf(algo.FirmId, algo.AlgoId);
            var algoMutations = _outboundLedger.GetAlgoMutations(algo.FirmId, algo.AlgoId);
            var missingLedgerEvidence = children.Any(child =>
                !_outboundLedger.TryGetByClOrdId(child.ClOrdId, out var mutation)
                || mutation?.AlgoOriginIdentity?.ParentAlgoId != algo.AlgoId);
            var unresolvedProvenNoWriteChild = algoMutations.Any(m =>
                m.AlgoOriginIdentity?.ActionKind == AlgoOutboundActionKind.NewChild
                && m.State == OutboundMutationState.OperatorResolved
                && string.Equals(
                    m.Resolution?.EvidenceKind,
                    "OutboundProvenNoWrite",
                    StringComparison.Ordinal));
            if (missingLedgerEvidence || unresolvedProvenNoWriteChild)
            {
                MarkRecoveryReconciliationRequired(algo, rt);
                continue;
            }

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
            var maxLedgerSliceSeq = algoMutations
                .Where(m => m.AlgoOriginIdentity?.ActionKind == AlgoOutboundActionKind.NewChild)
                .Select(m => m.AlgoOriginIdentity!.Sequence)
                .DefaultIfEmpty(-1)
                .Max();
            rt.NextSliceSeq = Math.Max(maxSeq, maxLedgerSliceSeq) + 1;
            rt.LiveChildClOrdId = liveChild;

            // Pass-1 review (#295) P1#1. Restore POV scheduling baseline
            // so a post-restart tick targets the pre-restart cumulative
            // market volume * rate (NOT post-restart-only volume * rate,
            // which would under-slice until the in-memory estimator
            // caught up to FilledQuantity). Falls back to (0, StartUtc)
            // on a fresh POV — same as a from-cold start.
            if (algo.Type == AlgoType.Pov && algo.Parameters is PovParameters ppRec)
            {
                var progress = _povProgress?.TryGet(algo.FirmId, algo.AlgoId);
                if (progress is { } p && p.LastEvaluateAtUtc != default)
                {
                    rt.PovMarketVolumeSeen = p.MarketVolumeSeen;
                    rt.PovLastEvaluateAtUtc = p.LastEvaluateAtUtc;
                }
                else
                {
                    rt.PovMarketVolumeSeen = 0;
                    rt.PovLastEvaluateAtUtc = ppRec.StartUtc;
                }
            }

            // Pass-1 review (#296) P1-C, refined by Pass-3 P1-C.
            // Restore an in-flight Pegged repeg cycle: a cancel was
            // emitted pre-restart but the cancel-ack ER may or may
            // not have been replayed.
            //
            // * Always set the sticky LastRepegCancelledChildId so any
            //   stray ER for that child is classified as expected by
            //   OnChildErAsync (no Suspended/VenueCancelled false
            //   positive).
            // * Set RepegPending=true ONLY when the cancelled child is
            //   still live in the order book — i.e. the cancel-ack has
            //   not been replayed yet. In that case OnCreatedAsync
            //   reactor evaluation will see the live child and
            //   EvaluatePeggedRepegAsync will short-circuit (throttle)
            //   until the ER actually arrives; that ER then routes
            //   through SubmitNextSliceAsync via the RepegPending
            //   branch.
            // * If the cancel-ack already replayed (old child terminal,
            //   no live child), Reconcile leaves rt.LiveChildClOrdId
            //   null + RepegPending=false so the re-enqueued
            //   AlgoCreatedSignal drives a fresh slice via the empty-
            //   slot path. The book entry is dropped to keep state
            //   bounded; we ALSO dispatch a synthetic
            //   AlgoPeggedRepegResolvedEvent(Aborted=true) so a future
            //   replay of the WAL sees the cycle as terminated and
            //   doesn't re-create the orphan book entry. Best-effort
            //   on WAL backpressure — in-memory removal is the
            //   correctness invariant; replay convergence is the nice
            //   bonus.
            if (algo.Type == AlgoType.Pegged && _peggedRepeg is not null)
            {
                var pending = _peggedRepeg.TryGet(algo.FirmId, algo.AlgoId);
                if (pending is { } pgd)
                {
                    rt.LastRepegCancelledChildId = pgd.CancelledChildClOrdId;
                    // Pass-5 review (#296) P1. Defensive: make sure
                    // the cancelled child id is in the dedup history
                    // ring. RestoreHistory + EventReplayer normally
                    // populate it, but a snapshot taken by a pre-
                    // pass-5 binary carries a pending entry without
                    // any history rows — seed from the pending entry
                    // so the post-restart late-ER dedup still works.
                    _peggedRepeg.MarkCancelledChild(algo.FirmId, algo.AlgoId, pgd.CancelledChildClOrdId);
                    var stillLive = _orders.TryGet(pgd.CancelledChildClOrdId, out var oldChild)
                                    && oldChild is not null
                                    && !IsChildTerminal(oldChild);
                    if (stillLive)
                    {
                        rt.RepegPending = true;
                    }
                    else
                    {
                        SelfHealOrphanRepeg(algo.FirmId, algo.AlgoId, pgd.CancelledChildClOrdId);
                    }
                }
            }

            // Re-arm the reactor regardless: even if a live child exists,
            // the reactor may need to react if the child has since become
            // terminal between snapshot capture and recovery.
            AlgoSignal signal = algo.Status == AlgoStatus.Cancelling
                ? new AlgoCancelRequestedSignal { FirmId = algo.FirmId, AlgoId = algo.AlgoId }
                : new AlgoCreatedSignal { FirmId = algo.FirmId, AlgoId = algo.AlgoId };
            if (!_queue.TryEnqueue(signal))
            {
                MetricsRegistry.AlgoSignalsDropped.Add(1,
                    new KeyValuePair<string, object?>("kind", "created"));
                _logger.LogWarning(
                    "AlgoEngine reconciliation dropped Created signal for {Firm}/{AlgoId} (queue full).",
                    algo.FirmId, algo.AlgoId);
            }
        }

        _logger.LogInformation("AlgoEngine reconciliation enqueued {Count} non-terminal parents.", algos.Count);
        PruneOrphanPovProgress(algos);
        PrunePeggedRepegBookOrphans(algos);
    }

    private void MarkRecoveryReconciliationRequired(Algo algo, AlgoParentRuntime rt)
    {
        rt.LiveChildClOrdId = null;
        var atUtc = _clock.GetUtcNow();
        try
        {
            _dispatcher.DispatchCommitted(
                new AlgoTerminalStateRecordedEvent
                {
                    AlgoId = algo.AlgoId,
                    FirmId = algo.FirmId,
                    Status = AlgoStatus.Suspended.ToString(),
                    Reason = AlgoTerminalReason.ReconciliationRequired.ToString(),
                    AtUtc = atUtc,
                    TimestampUtc = atUtc,
                },
                () => algo.RecordTerminal(
                    AlgoStatus.Suspended,
                    AlgoTerminalReason.ReconciliationRequired,
                    atUtc),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is WalBackpressureException or WalFaultedException)
        {
            _reconciliationDrain?.BeginDrain("algo_recovery_missing_outbound_ledger");
            _logger.LogCritical(
                ex,
                "Recovered algo {Firm}/{AlgoId} has child state without durable outbound origin evidence.",
                algo.FirmId,
                algo.AlgoId);
        }
    }

    /// <summary>
    /// Pass-2 review (#296) P2-C — mirror of
    /// <see cref="PruneOrphanPovProgress"/> for Pegged. Iterate every
    /// entry in <see cref="PeggedRepegBook"/> and drop those whose
    /// parent algo is absent from the non-terminal live set. Covers
    /// (a) entries persisted by a previous engine version that didn't
    /// <c>Remove</c> on terminal, (b) entries whose parent was
    /// concurrently terminated/expired between snapshot capture and
    /// restart, and (c) entries left behind by the cancel-fail
    /// roll-back path (P1-A) if the Resolved event hit WAL
    /// backpressure. Idempotent and cheap (one pass over a small
    /// map).
    /// </summary>
    private void PrunePeggedRepegBookOrphans(IReadOnlyCollection<Algo> liveAlgos)
    {
        if (_peggedRepeg is null) return;
        var live = new HashSet<(string FirmId, ulong AlgoId)>(liveAlgos.Count);
        foreach (var a in liveAlgos)
        {
            live.Add((a.FirmId, a.AlgoId));
        }
        foreach (var (firmId, algoId, _) in _peggedRepeg.Snapshot().ToList())
        {
            if (!live.Contains((firmId, algoId)))
            {
                _peggedRepeg.Remove(firmId, algoId);
            }
        }
    }

    /// <summary>
    /// Pass-3 review (#296) P1-C. Defensive self-heal invoked from
    /// <see cref="Reconcile"/> when a PeggedRepegBook entry survives
    /// across a restart but the associated cancelled child id is no
    /// longer present as a live order. Covers two histories:
    /// <list type="bullet">
    ///   <item>Cancel-ack ER was already replayed → child terminal/gone
    ///   → cycle effectively complete.</item>
    ///   <item>Old-binary WALs written before Pass-3 P1-B (approach B
    ///   reorder) that persisted a Started event for a cancel that
    ///   never actually reached the venue and whose child has since
    ///   been terminated by another path (e.g. fill, operator cancel
    ///   via the cancelling branch).</item>
    /// </list>
    /// Drops the in-memory book entry (the correctness invariant —
    /// stops Reconcile setting RepegPending=true on subsequent loops)
    /// and dispatches a synthetic
    /// <see cref="AlgoPeggedRepegResolvedEvent"/> with
    /// <c>Aborted=true</c> so a future WAL replay sees the cycle as
    /// terminated and does not reconstruct the orphan. Best-effort:
    /// on WAL backpressure the in-memory removal still wins because
    /// the apply callback runs only on successful append — we mirror
    /// the Remove via the catch block to guarantee state convergence
    /// for the current process.
    /// </summary>
    private void SelfHealOrphanRepeg(string firmId, ulong algoId, ulong cancelledChildClOrdId)
    {
        if (_peggedRepeg is null) return;
        try
        {
            var book = _peggedRepeg;
            _dispatcher.Dispatch(
                new AlgoPeggedRepegResolvedEvent
                {
                    AlgoId = algoId,
                    FirmId = firmId,
                    CancelledChildClOrdId = cancelledChildClOrdId,
                    AtUtc = _clock.GetUtcNow(),
                    Aborted = true,
                },
                () => book.Remove(firmId, algoId));
        }
        catch (WalBackpressureException)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "algo.pegged.repeg-self-heal"));
            _peggedRepeg.Remove(firmId, algoId);
        }
    }

    /// <summary>
    /// Pass-2 review (#295) P2 — defensive. Iterate every entry in
    /// <see cref="PovProgressBook"/> and drop those whose parent algo
    /// is absent from the non-terminal live set. Covers (a) entries
    /// persisted by a previous engine version that didn't
    /// <c>Remove</c> on terminal, and (b) entries whose parent was
    /// concurrently terminated/expired between snapshot capture and
    /// restart. Idempotent and cheap (one pass over a small map).
    /// </summary>
    private void PruneOrphanPovProgress(IReadOnlyCollection<Algo> liveAlgos)
    {
        if (_povProgress is null) return;
        var live = new HashSet<(string FirmId, ulong AlgoId)>(liveAlgos.Count);
        foreach (var a in liveAlgos)
        {
            live.Add((a.FirmId, a.AlgoId));
        }
        foreach (var (firmId, algoId, _) in _povProgress.Snapshot().ToList())
        {
            if (!live.Contains((firmId, algoId)))
            {
                _povProgress.Remove(firmId, algoId);
            }
        }
    }

    private async Task ReactAsync(AlgoSignal signal, CancellationToken ct)
    {
        var (firmId, algoId) = (signal.FirmId, AlgoIdOf(signal));
        if (!_outboundRecovery.IsBusinessIngressOpen(firmId))
        {
            _ = RequeueAfterRecoveryAsync(signal, ct);
            return;
        }
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
            case AlgoCancelRequestedSignal cancel:
                await OnCancelRequestedAsync(algo, rt, cancel.ExplicitRetry, ct).ConfigureAwait(false);
                break;
            case AlgoModifyRequestedSignal mod:
                await OnModifyRequestedAsync(algo, rt, mod, ct).ConfigureAwait(false);
                break;
            default:
                _logger.LogWarning("AlgoEngine ignoring unknown signal type {Type}.", signal.GetType().Name);
                break;
        }
    }

    private async Task RequeueAfterRecoveryAsync(AlgoSignal signal, CancellationToken ct)
    {
        try
        {
            await _outboundRecovery.WaitUntilBusinessIngressOpenAsync(signal.FirmId, ct)
                .ConfigureAwait(false);
            if (!_queue.TryEnqueue(signal))
            {
                MetricsRegistry.AlgoSignalsDropped.Add(1,
                    new KeyValuePair<string, object?>("kind", SignalKind(signal)));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "AlgoEngine could not defer signal {SignalKind} for firm {FirmId} until outbound recovery opened.",
                SignalKind(signal),
                signal.FirmId);
        }
    }

    private async Task OnCreatedAsync(Algo algo, AlgoParentRuntime rt, CancellationToken ct)
    {
        if (!_outboundRecovery.IsBusinessIngressOpen(algo.FirmId)) return;
        if (algo.IsTerminal) return;
        if (algo.Status == AlgoStatus.Cancelling)
        {
            await OnCancelRequestedAsync(algo, rt, explicitRetry: false, ct).ConfigureAwait(false);
            return;
        }
        if (_outboundLedger.HasBlockingAlgoMutation(algo.FirmId, algo.AlgoId))
        {
            if (algo.Type == AlgoType.Pegged
                && rt.LiveChildClOrdId is null
                && FindLatestBlockingRepegMutation(algo) is
                {
                    OriginalClOrdId: { } orphanedOriginalClOrdId,
                })
            {
                await FailClosedPeggedRepegAsync(
                    algo,
                    rt,
                    orphanedOriginalClOrdId,
                    "blocking repeg has no live original child").ConfigureAwait(false);
                return;
            }

            var retryableRepeg = algo.Type == AlgoType.Pegged
                && rt.LiveChildClOrdId is { } retryChildClOrdId
                && FindSoleRetryableProvenUnsentMutation(
                    algo,
                    retryChildClOrdId,
                    AlgoOutboundActionKind.Repeg) is not null;
            var terminalRepegCleanup = algo.Type == AlgoType.Pegged
                && rt.LiveChildClOrdId is { } terminalChildClOrdId
                && FindLatestBlockingRepegMutation(
                    algo,
                    terminalChildClOrdId) is not null
                && (!_orders.TryGet(terminalChildClOrdId, out var terminalChild)
                    || terminalChild is null
                    || IsChildTerminal(terminalChild));
            if (!retryableRepeg && !terminalRepegCleanup) return;
        }

        // Pass-1 review (#294) P1. VWAP needs the SDK subscribed to its
        // symbol so the VolumeCurveEstimator receives trade prints; without
        // a non-empty curve and ParticipationCap set, VwapPlan.SliceQty
        // returns 0 until window expiry → silent no-op algo. The pump
        // dedupes per symbol so repeated calls (reactor re-evaluation,
        // multiple parents on the same symbol, WAL replay-driven
        // reconciliation) collapse to one SDK Subscribe per process.
        if ((algo.Type == AlgoType.Vwap || algo.Type == AlgoType.Pov) && _volumePump is not null)
        {
            await _volumePump.EnsureSubscribedAsync(algo.Symbol, ct).ConfigureAwait(false);
        }
        // Q3.3 (#283). Pegged needs the SDK subscribed to its symbol so
        // PegBookTopCache receives prints; without that the engine has
        // no live reference and every repeg eval is a no-op (silent
        // do-nothing algo).
        if (algo.Type == AlgoType.Pegged && _pegBookPump is not null)
        {
            await _pegBookPump.EnsureSubscribedAsync(algo.Symbol, ct).ConfigureAwait(false);
        }

        if (rt.LiveChildClOrdId is { } existing)
        {
            // A child is already outstanding (steady-state or post-recovery).
            // Pegged is the only algo where a live child can trigger
            // engine-driven action: evaluate "has the live ref drifted
            // far enough to repeg?" and cancel the child if so. Iceberg
            // refills on terminal (OnChildErAsync), TWAP/VWAP/POV wait
            // for the scheduler tick — for those it's an idempotent no-op
            // here.
            if (algo.Type == AlgoType.Pegged && algo.Parameters is PeggedParameters pgEval)
            {
                await EvaluatePeggedRepegAsync(algo, rt, pgEval, existing, ct).ConfigureAwait(false);
                return;
            }
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
                if (RoundDownToLot(algo.Symbol, qty) > 0) break;
                rt.NextSliceSeq++;
            }
        }
        else if (algo.Type == AlgoType.Pov && algo.Parameters is PovParameters pp)
        {
            // POV: mirrors VWAP — the engine is driven by periodic
            // scheduler ticks, and the per-slot decision is purely
            // reactive to observed cumulative market volume. Empty
            // slots advance NextSliceSeq so recovery is unambiguous.
            var now = _clock.GetUtcNow();
            if (now >= pp.EndUtc)
            {
                await RecordTerminalAsync(algo, rt, AlgoStatus.Expired, AlgoTerminalReason.PovWindowExpired).ConfigureAwait(false);
                return;
            }
            while (true)
            {
                var dueAt = PovPlan.PlannedAtUtc(pp.StartUtc, pp.TickInterval, rt.NextSliceSeq);
                if (now < dueAt) return;
                if (dueAt >= pp.EndUtc) return;
                var (qty, _, _) = ComputePovSlice(algo, pp, rt, dueAt);
                if (RoundDownToLot(algo.Symbol, qty) > 0) break;
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
        if (algo.IsTerminal)
        {
            // Terminal parent quantities are immutable. In particular, a
            // late replacement child from an earlier ambiguous repeg must
            // never be adopted or credited after the parent completed.
            _logger.LogDebug(
                "AlgoEngine ignoring child ER for {Child} because algo {Firm}/{AlgoId} is terminal {Status}.",
                child.ClOrdId,
                algo.FirmId,
                algo.AlgoId,
                algo.Status);
            return;
        }

        // Pass-1 review (#299) P1-A. First observation of a replacement
        // child — adopt it here (NOT eagerly at modify-dispatch time) so
        // any Fill ER for the OLD child that arrived between dispatch and
        // the Replaced ack got booked against the OLD child's slot above
        // without re-targeting the parent. The Replaced ER's
        // ApplyReplaceAccepted has just hydrated `child` under the new
        // ClOrdID (status Working / PartiallyFilled / Filled depending on
        // erCum vs newQty) and dispatched this signal; we re-target now
        // and seed the new child's booked-cum baseline from the venue's
        // echoed cum so the next Fill ER for the new child computes a
        // correct delta. The OLD child has already been MarkReplaced'd
        // by the processor (Replaced ⇒ terminal); its ChildBookedCum
        // entry is NOT pruned synchronously — a late stray ER for OLD
        // could still arrive and we must keep the prior booked-cum so
        // its delta computation yields 0 (rather than re-booking the
        // OLD cum from a missing-key default of 0). Pass-2 review
        // (#299) P2 caps that bookkeeping by enqueueing OLD into a
        // bounded FIFO; once the FIFO overflows (cap=8) the eldest
        // retired slot is evicted from ChildBookedCum.
        if (_replacements is not null
            && rt.LiveChildClOrdId is { } oldLive
            && oldLive != child.ClOrdId
            && child.ParentAlgoId == algo.AlgoId
            && !rt.ChildBookedCum.ContainsKey(child.ClOrdId)
            && _ownership.TryResolveOrig(child.ClOrdId, out var origOfNew)
            && origOfNew == oldLive)
        {
            // #434: All adoption-side bookkeeping (ChildBookedCum
            // seed, RetireChildSlot + eviction counter, pegged repeg
            // resolution) is performed BEFORE the LiveChildClOrdId
            // flip below. The flip then publishes via a lock-fenced
            // setter so any cross-thread reader (notably tests
            // gating on TryGetLiveChildClOrdId) that observes the
            // new ClOrdID is also guaranteed to observe the prior
            // bookkeeping — closing the #347 / #345 / #329 race
            // class where a "next modify cycle" or "assert counter"
            // gate fired before the side state was committed.
            rt.ChildBookedCum[child.ClOrdId] = child.CumulativeQuantity;

            // #300 retrofit. Discriminate operator-modify adoption vs
            // engine-driven Pegged repeg adoption via the
            // PeggedRepegBook pending entry: a pending row whose
            // CancelledChildClOrdId matches `oldLive` means
            // EvaluatePeggedRepegAsync issued this cancel-replace as
            // part of a repeg cycle, so the adoption signal IS the
            // cycle-resolved trigger (replacing the pre-#300
            // Cancelled-on-OLD ack as the trigger). Approach (b) from
            // the design note: cheap synchronous lookup, no schema
            // change on OrderReplacementIntent or the WAL.
            //
            // The engine consumer is single-threaded so the Set
            // inside EvaluatePeggedRepegAsync (executed earlier on
            // the same task) is guaranteed visible here before any
            // ChildExecutionObservedSignal for the replacement child
            // can be processed.
            var peggedRepegAdoption =
                algo.Type == AlgoType.Pegged
                && rt.RepegPending
                && _peggedRepeg?.TryGet(algo.FirmId, algo.AlgoId) is { } pendingCycle
                && pendingCycle.CancelledChildClOrdId == oldLive;

            var evicted = rt.RetireChildSlot(oldLive, out var firstRetiredEviction);
            if (evicted > 0)
            {
                // Pass-3 review (#299) P2. Mirror PR #296's
                // CancelledChildRing observability — silent FIFO
                // eviction is a class of "you can't rely on this
                // ChildBookedCum row being there if a late stray ER
                // arrives for the OLD id". Always bump the counter so
                // dashboards can spot sustained eviction churn; emit a
                // single per-parent warn the first time to surface the
                // condition without log spam thereafter.
                var algoTypeEvictTag = algo.Type.ToString().ToLowerInvariant();
                MetricsRegistry.AlgoModifyRetiredChildEvictedTotal.Add(evicted,
                    new KeyValuePair<string, object?>("algoType", algoTypeEvictTag));
                if (firstRetiredEviction)
                {
                    _logger.LogWarning(
                        "AlgoEngine retired-child FIFO overflow on algo {Firm}/{AlgoId} (cap={Cap}): oldest ChildBookedCum row evicted. Late stray ERs for evicted child ids will fall through to a missing-key default of 0 booked cum.",
                        algo.FirmId, algo.AlgoId, 8);
                }
            }
            // Fall through so any erCum > prior booked OLD cum that the
            // venue carried over (e.g. a fill landed at venue strictly
            // between our last OLD-child ER and the Replaced ack) is NOT
            // double-booked: the seed above made delta == 0 in the
            // accounting block below. Any later Fill ER for the new
            // ClOrdID advances cum monotonically against this seeded
            // baseline.

            // #300 retrofit. Pegged-repeg cycle resolution now happens
            // HERE (on the Replaced-ER-driven adoption signal) instead
            // of on the Cancelled-on-OLD ack the pre-#300 code waited
            // for. Clear RepegPending so the next scheduler tick can
            // evaluate a fresh drift, reset the dropped-adoption
            // watchdog (the signal we were waiting for has now landed),
            // and dispatch the audit-pair Resolved event so a future
            // WAL replay drops the pending entry.
            if (peggedRepegAdoption)
            {
                rt.RepegPending = false;
                rt.PeggedReplacedHoldTicks = 0;
                try
                {
                    var firmIdSnap = algo.FirmId;
                    var algoIdSnap = algo.AlgoId;
                    var oldIdSnap = oldLive;
                    var book = _peggedRepeg;
                    _dispatcher.Dispatch(
                        new AlgoPeggedRepegResolvedEvent
                        {
                            AlgoId = algo.AlgoId,
                            FirmId = algo.FirmId,
                            CancelledChildClOrdId = oldLive,
                            AtUtc = _clock.GetUtcNow(),
                        },
                        () => book?.Remove(firmIdSnap, algoIdSnap));
                }
                catch (WalBackpressureException)
                {
                    // Best-effort under WAL backpressure: leave the
                    // book entry in place; Reconcile's orphan prune
                    // (PrunePeggedRepegBookOrphans) and the parent-
                    // terminal RemoveAll both converge state later.
                    MetricsRegistry.WalBackpressure.Add(1,
                        new KeyValuePair<string, object?>("call_site", "algo.pegged.repeg-resolved"));
                }
            }

            // #434: Publish the adoption LAST. The lock-fenced setter
            // (AlgoParentRuntime.LiveChildClOrdId) ensures all prior
            // writes in this block — ChildBookedCum seed,
            // RetiredChildSlots enqueue, counter Add, repeg book
            // Remove dispatch — are happens-before visible to any
            // reader that observes the new ClOrdID.
            rt.LiveChildClOrdId = child.ClOrdId;
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
        if (!IsChildTerminal(child))
        {
            if (algo.Status == AlgoStatus.Cancelling
                && rt.LiveChildClOrdId == child.ClOrdId)
            {
                await OnCancelRequestedAsync(algo, rt, explicitRetry: false, ct).ConfigureAwait(false);
            }
            return;
        }

        // Child is terminal — this clOrdId is no longer live regardless of
        // outcome. Clear the slot before transitioning so re-entrancy via
        // RecordTerminalAsync sees a clean state.
        if (rt.LiveChildClOrdId == child.ClOrdId)
            rt.LiveChildClOrdId = null;

        switch (child.Status)
        {
            case OrderStatus.Filled:
                // Pass-4 review (#296) P1. Repeg-cancel ↔ Fill race. The
                // engine issued (or has just issued) a cancel for this
                // child as part of a repeg cycle, but a terminal Fill ER
                // arrived before / instead of the cancel-ack — the venue
                // filled the residue while our cancel was still in
                // flight. Quantity is already booked above; route through
                // ResolveRepegOnFillAsync so the cycle is wound down
                // without spawning a replacement here (the existing child
                // consumed the qty, and the next AlgoCreatedSignal tick
                // owns submitting a fresh slice for any remaining qty).
                // A later duplicate ER (e.g. a stale Cancelled wire that
                // gets clamped to Filled by Order.MarkCancelled) re-
                // enters this branch and is a clean no-op because
                // rt.RepegPending is already false and the qty delta is
                // zero.
                //
                // Pass-5 review (#296) P1. Match against
                // PeggedRepegBook.IsCancelledChild (a bounded FIFO ring
                // of every child id we've engine-cancelled for this
                // parent) instead of the single-slot
                // LastRepegCancelledChildId. The single slot only
                // pinpoints the LATEST cycle and is overwritten on
                // every subsequent repeg, so a late Fill ER for an
                // OLDER cancelled child (e.g. cycle A cancelled, cycle
                // B started, then the venue belatedly reports cycle
                // A's terminal) used to slip past this dedup and fall
                // through to either the normal Fill-then-resubmit
                // path (orphan replacement) or the VenueCancelled
                // branch (spurious Suspended).
                if (algo.Type == AlgoType.Pegged
                    && (_peggedRepeg?.IsCancelledChild(algo.FirmId, algo.AlgoId, child.ClOrdId) ?? false))
                {
                    await ResolveRepegOnFillAsync(algo, rt, child).ConfigureAwait(false);
                    return;
                }
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
                if (algo.Type == AlgoType.Pov && algo.Parameters is PovParameters ppFilled)
                {
                    // POV: mirror VWAP — opportunistic, scheduler-driven.
                    if (_clock.GetUtcNow() >= ppFilled.EndUtc)
                    {
                        await RecordTerminalAsync(algo, rt, AlgoStatus.Expired, AlgoTerminalReason.PovWindowExpired).ConfigureAwait(false);
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
                else if (rt.RepegPending && algo.Type == AlgoType.Pegged
                         && rt.LastRepegCancelledChildId == child.ClOrdId)
                {
                    // ExecutionReportProcessor consumes the replacement
                    // intent before this signal is enqueued. With the
                    // original now terminal and the live slot already
                    // cleared, no Replaced adoption or terminal-child
                    // watchdog can finish this cycle. Fail closed because
                    // a split cancel-replace venue may still create the
                    // replacement after acknowledging the original cancel.
                    await FailClosedPeggedRepegAsync(
                        algo,
                        rt,
                        child.ClOrdId,
                        "sent replace ended with Cancelled on original").ConfigureAwait(false);
                    return;
                }
                else if (algo.Type == AlgoType.Pegged
                         && (_peggedRepeg?.IsCancelledChild(
                             algo.FirmId,
                             algo.AlgoId,
                             child.ClOrdId) ?? false)
                         && IsUnresolvedPotentiallySentRepegMutation(
                             algo,
                             child.ClOrdId))
                {
                    // The ambiguous-send path clears RepegPending and does
                    // not persist a Started record, but retains the history
                    // marker because the venue may have accepted the
                    // replace. A later Cancelled-on-original consumes that
                    // retained intent in ExecutionReportProcessor. Suspend
                    // explicitly instead of letting the history dedup hide
                    // the terminal and strand an empty live slot.
                    await FailClosedPeggedRepegAsync(
                        algo,
                        rt,
                        child.ClOrdId,
                        "unresolved replace ended with Cancelled on original").ConfigureAwait(false);
                    return;
                }
                else if (algo.Type == AlgoType.Pegged
                         && (_peggedRepeg?.IsCancelledChild(algo.FirmId, algo.AlgoId, child.ClOrdId) ?? false)
                         && !IsConfirmedProvenUnsentMutation(
                             algo,
                             child.ClOrdId,
                             AlgoOutboundActionKind.Repeg))
                {
                    // Pass-1 review (#296) P1-A. Duplicate / late
                    // Cancelled ER for a child we already cancelled
                    // for a repeg. Treat as a no-op so the parent
                    // does NOT fall through to the VenueCancelled
                    // branch and get suspended.
                    //
                    // Pass-5 review (#296) P1. Match against the
                    // PeggedRepegBook history ring (bounded FIFO of
                    // every recently engine-cancelled child id)
                    // instead of the single-slot
                    // LastRepegCancelledChildId. The single slot only
                    // identifies the LATEST cycle; once a subsequent
                    // repeg overwrites it a late Cancelled ER for an
                    // older cycle's child used to escape this guard
                    // and reach the VenueCancelled-suspension path.
                    //
                    // #300 retrofit. The ring now serves a dual
                    // purpose: (1) defensive dedup of late Cancelled
                    // ERs that may still arrive on the live cancel-
                    // replace path (rare/spurious — venue shouldn't
                    // emit Cancelled when the modify succeeded), AND
                    // (2) replay safety for older WAL segments that
                    // recorded a bare cancel (pre-#300 semantics)
                    // whose Cancelled ER is being replayed against a
                    // post-#300 engine. Both arms fall here.
                    return;
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
                else if (IsPovWindowExpired(algo))
                {
                    await RecordTerminalAsync(algo, rt, AlgoStatus.Expired, AlgoTerminalReason.PovWindowExpired).ConfigureAwait(false);
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
                if (algo.Type == AlgoType.Pegged
                    && (rt.RepegPending
                        || (_peggedRepeg?.IsCancelledChild(
                            algo.FirmId,
                            algo.AlgoId,
                            child.ClOrdId) ?? false))
                    && IsUnresolvedPotentiallySentRepegMutation(
                        algo,
                        child.ClOrdId))
                {
                    await FailClosedPeggedRepegAsync(
                        algo,
                        rt,
                        child.ClOrdId,
                        "unresolved replace ended with Rejected on original").ConfigureAwait(false);
                    return;
                }
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
                if (IsPovWindowExpired(algo))
                {
                    await RecordTerminalAsync(algo, rt, AlgoStatus.Expired, AlgoTerminalReason.PovWindowExpired).ConfigureAwait(false);
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

    private bool IsPovWindowExpired(Algo algo) =>
        algo.Type == AlgoType.Pov
        && algo.Parameters is PovParameters pp
        && _clock.GetUtcNow() >= pp.EndUtc;

    /// <summary>
    /// Q3.5 (#285). Operator-driven cancel-replace of an algo child.
    /// Resolves the target child (explicit id from the signal, else
    /// the parent's <c>LiveChildClOrdId</c>), validates terminal /
    /// modify-below-filled invariants on the consumer thread, then
    /// delegates the wire-call + WAL plumbing to
    /// <see cref="TryReplaceChildAsync"/>. Race semantics: if the
    /// target child has reached terminal between API accept and
    /// reactor pick-up the modify is rejected gracefully (metric +
    /// log) and the operator can retry — never applied to the
    /// replacement that the engine may have already submitted in a
    /// subsequent Pegged repeg cycle.
    /// </summary>
    private async Task OnModifyRequestedAsync(
        Algo algo, AlgoParentRuntime rt, AlgoModifyRequestedSignal sig, CancellationToken ct)
    {
        if (!_outboundRecovery.IsBusinessIngressOpen(algo.FirmId)) return;
        var algoTypeTag = algo.Type.ToString().ToLowerInvariant();
        if (algo.IsTerminal)
        {
            MetricsRegistry.AlgoModifyRejectedTotal.Add(1,
                new KeyValuePair<string, object?>("algoType", algoTypeTag),
                new KeyValuePair<string, object?>("reason", "algo_terminal"));
            _logger.LogDebug(
                "AlgoEngine modify rejected: algo {Firm}/{AlgoId} is terminal ({Status}).",
                algo.FirmId, algo.AlgoId, algo.Status);
            return;
        }
        if (algo.Status == AlgoStatus.Cancelling)
        {
            MetricsRegistry.AlgoModifyRejectedTotal.Add(1,
                new KeyValuePair<string, object?>("algoType", algoTypeTag),
                new KeyValuePair<string, object?>("reason", "algo_cancelling"));
            return;
        }

        var targetChildClOrdId = sig.TargetChildClOrdId ?? rt.LiveChildClOrdId;
        if (targetChildClOrdId is not { } childClOrdId || childClOrdId == 0)
        {
            MetricsRegistry.AlgoModifyRejectedTotal.Add(1,
                new KeyValuePair<string, object?>("algoType", algoTypeTag),
                new KeyValuePair<string, object?>("reason", "no_live_child"));
            return;
        }

        if (!_orders.TryGet(childClOrdId, out var child) || child is null)
        {
            MetricsRegistry.AlgoModifyRejectedTotal.Add(1,
                new KeyValuePair<string, object?>("algoType", algoTypeTag),
                new KeyValuePair<string, object?>("reason", "child_not_found"));
            return;
        }
        if (child.ParentAlgoId != algo.AlgoId)
        {
            MetricsRegistry.AlgoModifyRejectedTotal.Add(1,
                new KeyValuePair<string, object?>("algoType", algoTypeTag),
                new KeyValuePair<string, object?>("reason", "child_not_owned"));
            return;
        }
        if (IsChildTerminal(child))
        {
            MetricsRegistry.AlgoModifyRejectedTotal.Add(1,
                new KeyValuePair<string, object?>("algoType", algoTypeTag),
                new KeyValuePair<string, object?>("reason", "child_terminal"));
            return;
        }

        var newQty = sig.NewQuantity ?? child.Quantity;
        var newPrice = sig.NewPrice ?? child.Price;
        if (newQty <= child.CumulativeQuantity)
        {
            // Modify-to-invalid: residue would go non-positive. Surface
            // as a metric — the API layer can't reject this cleanly
            // because the cum is observed on the consumer thread.
            MetricsRegistry.AlgoModifyRejectedTotal.Add(1,
                new KeyValuePair<string, object?>("algoType", algoTypeTag),
                new KeyValuePair<string, object?>("reason", "qty_below_filled"));
            return;
        }

        var explicitRetry = FindExplicitProvenUnsentMutation(
            algo,
            child.ClOrdId,
            AlgoOutboundActionKind.ReplaceChild);
        await TryReplaceChildAsync(
            algo, child, newQty, newPrice, sig.Reason,
            AlgoOutboundActionKind.ReplaceChild, ct,
            explicitRetry?.AlgoOriginIdentity).ConfigureAwait(false);
    }

    internal async Task<bool> TryReplaceChildAsync(
        Algo algo,
        Order child,
        long newQuantity,
        decimal? newPrice,
        string reason,
        AlgoOutboundActionKind actionKind,
        CancellationToken ct,
        AlgoOutboundOriginIdentity? explicitRetryOrigin = null)
    {
        if (!_outboundRecovery.IsBusinessIngressOpen(algo.FirmId)) return false;
        var algoTypeTag = algo.Type.ToString().ToLowerInvariant();
        OutboundMutationSnapshot? retryMutation = null;
        if (explicitRetryOrigin is not null)
        {
            _outboundLedger.TryGetByAlgoOrigin(
                algo.FirmId,
                explicitRetryOrigin,
                out retryMutation);
        }
        var retryAllowed = retryMutation is
        {
            State: OutboundMutationState.ProvenUnsent,
            RequiresReconciliation: false,
        }
            && retryMutation.OriginalClOrdId == child.ClOrdId
            && retryMutation.AlgoOriginIdentity?.ActionKind == actionKind
            && retryMutation.Approval?.CanonicalCommandNonSensitive.Quantity
                == newQuantity
            && retryMutation.Approval.CanonicalCommandNonSensitive.Price
                == newPrice
            && !_outboundLedger.HasBlockingAlgoMutationExcept(
                algo.FirmId,
                algo.AlgoId,
                retryMutation.MutationId);
        if (!retryAllowed
            && _outboundLedger.HasBlockingAlgoMutation(algo.FirmId, algo.AlgoId))
        {
            MetricsRegistry.AlgoModifyRejectedTotal.Add(1,
                new KeyValuePair<string, object?>("algoType", algoTypeTag),
                new KeyValuePair<string, object?>("reason", "outbound_unresolved"));
            return false;
        }

        var origin = retryAllowed
            ? explicitRetryOrigin!
            : new AlgoOutboundOriginIdentity(
                algo.AlgoId,
                actionKind,
                NextAlgoActionSequence(algo.FirmId, algo.AlgoId, actionKind));
        var result = await _modifier.ModifyAsync(
            new OrderModifyRequest(
                child.Owner,
                child.ClOrdId,
                newQuantity,
                newPrice,
                FirmId: algo.FirmId,
                Origin: OutboundMutationOrigin.Algo,
                AlgoOriginIdentity: origin),
            ct).ConfigureAwait(false);
        if (result.Kind != OrderModifyResultKind.Accepted)
        {
            var rejectionReason = result.Kind switch
            {
                OrderModifyResultKind.GatewayAmbiguous => "gateway_ambiguous",
                OrderModifyResultKind.RiskRejected
                    when string.Equals(result.Code, "margin_rejected", StringComparison.Ordinal)
                        => "margin_rejected",
                OrderModifyResultKind.RiskRejected => "risk_rejected",
                _ => result.Kind.ToString().ToLowerInvariant(),
            };
            MetricsRegistry.AlgoModifyRejectedTotal.Add(1,
                new KeyValuePair<string, object?>("algoType", algoTypeTag),
                new KeyValuePair<string, object?>("reason", rejectionReason));
            return false;
        }

        try
        {
            _dispatcher.Dispatch(
                new AlgoChildModifiedEvent
                {
                    AlgoId = algo.AlgoId,
                    FirmId = algo.FirmId,
                    OldChildClOrdId = child.ClOrdId,
                    NewChildClOrdId = result.NewClOrdId,
                    NewQuantity = newQuantity,
                    NewPrice = newPrice,
                    Reason = reason,
                    AtUtc = _clock.GetUtcNow(),
                },
                static () => { });
        }
        catch (WalBackpressureException)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "algo.child.modified"));
        }

        MetricsRegistry.AlgoChildModifiesTotal.Add(1,
            new KeyValuePair<string, object?>("algoType", algoTypeTag),
            new KeyValuePair<string, object?>("reason", reason));
        return true;
    }

    private int NextAlgoActionSequence(
        string firmId,
        ulong algoId,
        AlgoOutboundActionKind actionKind) =>
        _outboundLedger.GetAlgoMutations(firmId, algoId)
            .Where(m => m.AlgoOriginIdentity?.ActionKind == actionKind)
            .Select(m => m.AlgoOriginIdentity!.Sequence)
            .DefaultIfEmpty(-1)
            .Max() + 1;

    private OutboundMutationSnapshot? FindExplicitProvenUnsentMutation(
        Algo algo,
        ulong originalClOrdId,
        AlgoOutboundActionKind actionKind) =>
        FindLatestAlgoMutation(algo, originalClOrdId, actionKind) is
        {
            State: OutboundMutationState.ProvenUnsent,
            RequiresReconciliation: false,
        } mutation
            ? mutation
            : null;

    private OutboundMutationSnapshot? FindLatestAlgoMutation(
        Algo algo,
        ulong originalClOrdId,
        AlgoOutboundActionKind actionKind) =>
        _outboundLedger.GetAlgoMutations(algo.FirmId, algo.AlgoId)
            .Where(m => m.AlgoOriginIdentity?.ActionKind == actionKind
                && m.OriginalClOrdId == originalClOrdId)
            .OrderByDescending(m => m.RecordedAtUtc)
            .ThenByDescending(m => m.MutationId.Value)
            .FirstOrDefault();

    private bool IsConfirmedProvenUnsentMutation(
        Algo algo,
        ulong originalClOrdId,
        AlgoOutboundActionKind actionKind) =>
        FindLatestAlgoMutation(algo, originalClOrdId, actionKind) is
        {
            State: OutboundMutationState.ProvenUnsent,
            RequiresReconciliation: false,
        };

    private bool IsUnresolvedPotentiallySentRepegMutation(
        Algo algo,
        ulong originalClOrdId)
    {
        var mutation = FindLatestBlockingRepegMutation(algo, originalClOrdId);
        return mutation is not null
            && mutation.State != OutboundMutationState.ProvenUnsent;
    }

    private OutboundMutationSnapshot? FindLatestBlockingRepegMutation(
        Algo algo,
        ulong? originalClOrdId = null) =>
        _outboundLedger.GetAlgoMutations(algo.FirmId, algo.AlgoId)
            .Where(m =>
                m.AlgoOriginIdentity?.ActionKind == AlgoOutboundActionKind.Repeg
                && (originalClOrdId is null
                    || m.OriginalClOrdId == originalClOrdId.Value)
                && IsBlockingAlgoRepegMutation(m))
            .OrderByDescending(m => m.RecordedAtUtc)
            .ThenByDescending(m => m.MutationId.Value)
            .FirstOrDefault();

    private static bool IsBlockingAlgoRepegMutation(
        OutboundMutationSnapshot mutation) =>
        mutation.RequiresReconciliation
        || mutation.State is OutboundMutationState.ApprovedToSend
            or OutboundMutationState.AttemptIntentPrepared
            or OutboundMutationState.FramePrepared
            or OutboundMutationState.TransportWriteCompleted
            or OutboundMutationState.ProvenUnsent
            or OutboundMutationState.Ambiguous
            or OutboundMutationState.LegacyUnknown
            or OutboundMutationState.LegacyUnknownReplace;

    private OutboundMutationSnapshot? FindSoleRetryableProvenUnsentMutation(
        Algo algo,
        ulong originalClOrdId,
        AlgoOutboundActionKind actionKind)
    {
        var mutation = FindExplicitProvenUnsentMutation(
            algo,
            originalClOrdId,
            actionKind);
        return mutation is not null
            && mutation.Attempts.Count < OutboundMutationLedger.MaxOutboundAttempts
            && (_outboundEpoch is null
                || (_outboundEpoch.IsInitialized
                    && mutation.Attempts.LastOrDefault()?.ProcessEpochId
                        == _outboundEpoch.Id))
            && !_outboundLedger.HasBlockingAlgoMutationExcept(
                algo.FirmId,
                algo.AlgoId,
                mutation.MutationId)
            ? mutation
            : null;
    }

    private async Task OnCancelRequestedAsync(
        Algo algo,
        AlgoParentRuntime rt,
        bool explicitRetry,
        CancellationToken ct)
    {
        if (!_outboundRecovery.IsBusinessIngressOpen(algo.FirmId)) return;
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

        OutboundMutationSnapshot? retryMutation = null;
        if (explicitRetry && rt.LiveChildClOrdId is { } retryChildClOrdId)
        {
            retryMutation = FindExplicitProvenUnsentMutation(
                algo,
                retryChildClOrdId,
                AlgoOutboundActionKind.CancelChild);
        }
        var retryAllowed = retryMutation is not null
            && !_outboundLedger.HasBlockingAlgoMutationExcept(
                algo.FirmId,
                algo.AlgoId,
                retryMutation.MutationId);
        if (!retryAllowed
            && _outboundLedger.HasBlockingAlgoMutation(algo.FirmId, algo.AlgoId))
        {
            _logger.LogWarning(
                "Algo parent cancel for {Firm}/{AlgoId} is waiting for unresolved child outbound evidence.",
                algo.FirmId,
                algo.AlgoId);
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

        var origin = retryAllowed
            ? retryMutation!.AlgoOriginIdentity!
            : new AlgoOutboundOriginIdentity(
                algo.AlgoId,
                AlgoOutboundActionKind.CancelChild,
                NextAlgoActionSequence(
                    algo.FirmId,
                    algo.AlgoId,
                    AlgoOutboundActionKind.CancelChild));
        var result = await _canceller.CancelAsync(
            child.Owner,
            child.ClOrdId,
            ct,
            firmId: algo.FirmId,
            origin: OutboundMutationOrigin.Algo,
            algoOriginIdentity: origin).ConfigureAwait(false);
        if (result.Kind != OrderCancelResultKind.Accepted)
        {
            _logger.LogWarning(
                "Algo parent cancel for {Firm}/{AlgoId} child {Child} remains pending: {Kind} {Reason}.",
                algo.FirmId,
                algo.AlgoId,
                childClOrdId,
                result.Kind,
                result.Reason);
        }
    }

    private async Task SubmitNextSliceAsync(Algo algo, AlgoParentRuntime rt, CancellationToken ct)
    {
        if (!_outboundRecovery.IsBusinessIngressOpen(algo.FirmId)) return;
        var (rawQty, slicePrice) = ComputeNextSlice(algo);
        // #518. Final lot-size invariant: no algo child leaves the engine
        // with an odd lot the venue's MinLotSizeCheck would reject (which
        // would terminally suspend the parent). VWAP/POV gaps and any
        // partial-fill residue are floored to a whole lot here; TWAP
        // (lot-unit plan), Iceberg/Pegged (lot-valid total/residue) are
        // already aligned so this is a no-op for them on the happy path.
        var sliceQty = RoundDownToLot(algo.Symbol, rawQty);
        if (sliceQty <= 0)
        {
            // A positive quantity that floored to zero is a sub-lot residue
            // (or sub-lot curve gap). It is NOT "nothing owed": submitting
            // it would be risk-rejected and completing would strand the
            // residue. Defer — a later tick (curve growth / fills) presents
            // a full lot, or the window-expiry path retires the parent.
            if (rawQty > 0)
            {
                if (algo.Type == AlgoType.Vwap || algo.Type == AlgoType.Pov)
                    rt.NextSliceSeq++;
                _logger.LogDebug(
                    "Algo {AlgoId}/{Firm} ({Type}) owed sub-lot residue {Residue} (lot {Lot}); deferring slice.",
                    algo.AlgoId, algo.FirmId, algo.Type, rawQty, ResolveLotSize(algo.Symbol));
                return;
            }
            if (algo.Type == AlgoType.Vwap || algo.Type == AlgoType.Pov)
            {
                // VWAP/POV: empty slot — the parent is ahead of the
                // curve (VWAP) or there is insufficient market volume
                // yet (POV). Advance NextSliceSeq so the next scheduler
                // tick evaluates the next slot. Do NOT mark terminal.
                rt.NextSliceSeq++;
                return;
            }
            if (algo.Type == AlgoType.Pegged)
            {
                // Pegged: no live reference price yet (cache empty —
                // SDK hasn't delivered any prints for the symbol). The
                // scheduler will fire another AlgoCreatedSignal at the
                // next tick; we just no-op until the cache warms.
                return;
            }
            if (algo.RemainingQuantity > 0)
            {
                // #518. rawQty == 0 but the parent still owes quantity. This
                // is reachable for TWAP when the lot table becomes
                // authoritative AFTER admission (SDK SecurityDefinition
                // overlay): an interior slice floors to zero in lot units
                // while the remainder-bearing last slice still carries the
                // outstanding quantity. Advancing the slot index lets that
                // final slice (or, failing that, window expiry) work the
                // residue. Completing here would silently strand it.
                if (algo.Type == AlgoType.Twap)
                    rt.NextSliceSeq++;
                _logger.LogDebug(
                    "Algo {AlgoId}/{Firm} ({Type}) produced an empty slice with {Remaining} remaining (lot {Lot}); deferring.",
                    algo.AlgoId, algo.FirmId, algo.Type, algo.RemainingQuantity, ResolveLotSize(algo.Symbol));
                return;
            }
            // Genuinely nothing owed (RemainingQuantity == 0) — terminalize.
            await RecordTerminalAsync(algo, rt, AlgoStatus.Completed, AlgoTerminalReason.None).ConfigureAwait(false);
            return;
        }

        var sliceSeq = rt.NextSliceSeq;
        var orderType = algo.Parameters switch
        {
            IcebergParameters => OrderType.Limit,
            TwapParameters tp => tp.ChildOrderType,
            VwapParameters vp => vp.ChildOrderType,
            PovParameters pp => pp.ChildOrderType,
            PeggedParameters pgp => pgp.ChildOrderType,
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
        long povCumMarketVolume = 0, povExecutedCum = 0;
        DateTimeOffset povPlannedAt = default;
        DateTimeOffset povLastEvaluateAtUtc = default;
        if (algo.Parameters is PovParameters ppForAudit)
        {
            povPlannedAt = PovPlan.PlannedAtUtc(ppForAudit.StartUtc, ppForAudit.TickInterval, sliceSeq);
            var (_, _, cumMv) = ComputePovSlice(algo, ppForAudit, rt, povPlannedAt);
            povCumMarketVolume = cumMv;
            povExecutedCum = algo.FilledQuantity;
            // Pass-1 review (#295) P1#1. Persisted alongside the slice
            // so a restart restores BOTH the baseline and the wall-clock
            // anchor: post-restart catch-up integrates VolumeBetween
            // from this instant, not from StartUtc (which would double-
            // count buckets already accumulated into MarketVolumeSeen).
            povLastEvaluateAtUtc = rt.PovLastEvaluateAtUtc;
            if (cumMv > 0)
            {
                MetricsRegistry.AlgoPovActualParticipationRate.Record((double)povExecutedCum / cumMv);
            }
        }

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
                AlgoSliceSeq: sliceSeq,
                AlgoTypeTag: algo.Type.ToString().ToLowerInvariant())
            {
                UseDurableOutboundCoordinator = true,
                AlgoOriginIdentity = new AlgoOutboundOriginIdentity(
                    algo.AlgoId,
                    AlgoOutboundActionKind.NewChild,
                    sliceSeq),
            };

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
                    else if (algo.Type == AlgoType.Pov)
                    {
                        MetricsRegistry.AlgoPovSlicesEmitted.Add(1);
                        try
                        {
                            // Pass-1 review (#295) P1#1. Persist the
                            // running POV baseline so a restart resumes
                            // off the pre-crash cumulative-market-volume
                            // total (NOT zero, which would under-slice).
                            // PovProgressBook.Set runs inside the
                            // dispatcher action so live + replay paths
                            // converge on identical book state.
                            var firmId = algo.FirmId;
                            var algoId = algo.AlgoId;
                            var marketVolumeSeenSnapshot = povCumMarketVolume;
                            var lastEvaluateAtUtcSnapshot = povLastEvaluateAtUtc;
                            var povBook = _povProgress;
                            _dispatcher.Dispatch(
                                new AlgoPovSlicedEvent
                                {
                                    AlgoId = algo.AlgoId,
                                    FirmId = algo.FirmId,
                                    SliceSeq = sliceSeq,
                                    CumMarketVolume = povCumMarketVolume,
                                    ExecutedCum = povExecutedCum,
                                    SliceQty = sliceQty,
                                    PlannedAtUtc = povPlannedAt,
                                    MarketVolumeSeen = povCumMarketVolume,
                                    LastEvaluateAtUtc = povLastEvaluateAtUtc,
                                },
                                () => povBook?.Set(firmId, algoId, marketVolumeSeenSnapshot, lastEvaluateAtUtcSnapshot));
                        }
                        catch (WalBackpressureException)
                        {
                            MetricsRegistry.WalBackpressure.Add(1,
                                new KeyValuePair<string, object?>("call_site", "algo.pov.sliced"));
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

                case OrderSubmissionResultKind.ReconciliationRequired:
                    await RecordTerminalAsync(
                        algo,
                        rt,
                        AlgoStatus.Suspended,
                        AlgoTerminalReason.ReconciliationRequired).ConfigureAwait(false);
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
                    _logger.LogWarning(
                        "AlgoEngine child submit for {Firm}/{AlgoId} slice {Seq} returned {Kind}; no automatic retry is permitted.",
                        algo.FirmId, algo.AlgoId, sliceSeq, result.Kind);
                    await RecordTerminalAsync(
                        algo,
                        rt,
                        AlgoStatus.Suspended,
                        AlgoTerminalReason.ReconciliationRequired).ConfigureAwait(false);
                    return;
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
        else if (algo.Type == AlgoType.Pov && status == AlgoStatus.Cancelled)
        {
            MetricsRegistry.AlgoPovCancelled.Add(1);
        }
        else if (algo.Type == AlgoType.Pegged && status == AlgoStatus.Cancelled)
        {
            MetricsRegistry.AlgoPeggedCancelled.Add(1);
        }
        // Pass-2 review (#295) P2. Drop the per-POV progress entry on
        // every terminal transition so PovProgressBook stays bounded
        // and the next snapshot doesn't carry stale state for a dead
        // parent. Safe to call for non-POV parents (no-op when no
        // entry exists).
        if (algo.Type == AlgoType.Pov)
        {
            _povProgress?.Remove(algo.FirmId, algo.AlgoId);
        }
        // Pass-1 review (#296) P1-C. Drop any in-flight repeg marker
        // for a Pegged parent on terminal so the book stays bounded
        // and the next snapshot doesn't carry stale state.
        //
        // Pass-5 review (#296) P1. Also drop the cancelled-child
        // history ring (RemoveAll) — once the parent is terminal no
        // further late ERs can affect routing, so the dedup memory
        // is dead weight.
        if (algo.Type == AlgoType.Pegged)
        {
            _peggedRepeg?.RemoveAll(algo.FirmId, algo.AlgoId);
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
                    var lot = ResolveLotSize(algo.Symbol);
                    var planned = TwapPlan.SliceQty(algo.TotalQuantity, tp.SliceCount, seq, lot);
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
            case PovParameters pp:
                {
                    var rt = _runtime[(algo.FirmId, algo.AlgoId)];
                    var dueAt = PovPlan.PlannedAtUtc(pp.StartUtc, pp.TickInterval, rt.NextSliceSeq);
                    var (qty, price, _) = ComputePovSlice(algo, pp, rt, dueAt);
                    return (qty, price);
                }
            case PeggedParameters pgp:
                {
                    // Pegged: single working slice covers the full
                    // residue. Target = clamped(ref + offsetTicks*tick).
                    // When the cache has no live reference yet, qty=0
                    // signals "defer" — SubmitNextSliceAsync recognises
                    // the Pegged-empty case and no-ops without marking
                    // terminal (see the special case above).
                    var target = ResolvePeggedTarget(algo, pgp);
                    if (target is null) return (0, null);
                    return (algo.RemainingQuantity, target);
                }
            default:
                return (algo.RemainingQuantity, null);
        }
    }

    /// <summary>
    /// Q3.3 (#283). Resolves the (possibly clamped) target price for a
    /// Pegged parent off the live <see cref="PegBookTopCache"/>.
    /// Returns <c>null</c> when the cache has not been seeded yet for
    /// the symbol — the caller must defer (no-op) and let the next
    /// scheduler tick re-evaluate.
    /// </summary>
    private decimal? ResolvePeggedTarget(Algo algo, PeggedParameters pgp)
    {
        var book = _pegBookTop?.TryGet(algo.Symbol);
        if (book is null) return null;
        var refPrice = book.Value.RefPrice(pgp.Ref, algo.Side);
        if (refPrice is null) return null;
        var rawTarget = PeggedPlan.ComputeTarget(refPrice, pgp.OffsetTicks, pgp.TickSize);
        if (rawTarget is null) return null;
        return PeggedPlan.ClampToLimit(rawTarget.Value, pgp.PriceLimit, algo.Side);
    }

    /// <summary>
    /// Pass-4 review (#296) P1. Wind down a pegged repeg cycle when a
    /// terminal Fill ER arrives for the child the engine had just
    /// cancelled (or was cancelling) for repeg. The venue filled the
    /// residue while our cancel was in flight; the qty was already
    /// booked to the parent by the caller (delta accounting in
    /// <see cref="OnChildErAsync"/>), so the cycle is complete from the
    /// parent's POV and we MUST NOT submit a replacement here:
    /// <list type="bullet">
    ///   <item>The just-filled child consumed the qty the replacement
    ///   would have targeted — placing a new slice now would over-buy
    ///   (orphan duplicate working order).</item>
    ///   <item>If parent qty remains, the next
    ///   <see cref="AlgoCreatedSignal"/> scheduler tick lands in
    ///   <see cref="OnCreatedAsync"/> with <c>LiveChildClOrdId=null</c>
    ///   and submits the next slice through the canonical empty-slot
    ///   path. That keeps the "submit new working slice" entry-point
    ///   single — the repeg race never has its own.</item>
    /// </list>
    ///
    /// <para>
    /// Three timing windows are handled, all the same in-memory shape
    /// (<c>RepegPending=true</c>, sticky
    /// <c>LastRepegCancelledChildId=child.ClOrdId</c>) but with
    /// different WAL state because the Started marker is persisted only
    /// AFTER <c>CancelAsync</c> returns (pass-3 review #296 P1 approach
    /// B):
    /// <list type="number">
    ///   <item><b>Window 1</b> — Fill processed before <c>CancelAsync</c>
    ///   is even invoked. <c>PeggedRepegBook</c> is empty; clearing the
    ///   in-memory flags is enough. The post-cancel guard in
    ///   <see cref="EvaluatePeggedRepegAsync"/> then sees
    ///   <c>RepegPending=false</c> and skips the Started dispatch so
    ///   no orphan WAL marker is written.</item>
    ///   <item><b>Window 2</b> — Fill processed after <c>CancelAsync</c>
    ///   returned but before Started was persisted. Same shape as
    ///   Window 1.</item>
    ///   <item><b>Window 3</b> — Fill processed after Started was
    ///   persisted (book has an entry). Dispatch the matching
    ///   <see cref="AlgoPeggedRepegResolvedEvent"/> with
    ///   <c>Aborted=false</c> + <c>Reason=FilledBeforeCancelAck</c>
    ///   so the audit pair is balanced and replay clears the book.
    ///   <c>Aborted</c> stays <c>false</c> because the cancel did
    ///   reach the venue; the rollback bit is reserved for cancel
    ///   wire-call failure (pass-2 #296 P1-A).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <c>LastRepegCancelledChildId</c> stays sticky — a follow-up
    /// late Cancelled ER for the same child id (e.g. a venue
    /// CancelReject converted to a stale Cancelled wire) flows back
    /// into <see cref="OnChildErAsync"/>, hits the
    /// <c>!RepegPending &amp;&amp; LastRepegCancelledChildId==child.ClOrdId</c>
    /// dedup branch in the Cancelled case, and is a no-op rather than
    /// flipping the parent to <c>Suspended/VenueCancelled</c>. A
    /// duplicate Fill ER similarly re-enters this method but with
    /// <c>RepegPending=false</c> and zero qty delta — no second
    /// Resolved is emitted because the book entry is gone.
    /// </para>
    /// </summary>
    private async Task ResolveRepegOnFillAsync(Algo algo, AlgoParentRuntime rt, Order child)
    {
        var wasPending = rt.RepegPending;
        rt.RepegPending = false;
        // Keep LastRepegCancelledChildId sticky for late-ER dedup; the
        // marker is cleared only on parent terminal.

        // #300 retrofit. Pre-#300 the in-flight cycle was a bare
        // cancel — nothing to clean up beyond the in-memory marker.
        // Post-#300 the cycle is a cancel-replace whose intent +
        // (possibly) held margin live in PendingReplacementRegistry.
        // The intent may exist even when RepegPending is false: an
        // ambiguous dispatch deliberately clears the in-memory throttle
        // but retains the registry row until authoritative evidence.
        // A Fill on the OLD child has already settled the cycle from
        // the parent's POV, so the replace is now meaningless: if the
        // venue eventually emits a Replaced ER the adoption block
        // will pick up a residue-zero new child and orphan it.
        // Consume the intent here so the registry + margin reserve
        // are released; the late Replaced ER will then bypass
        // PendingReplacementRegistry's intercept and the synthetic
        // child is silently dropped.
        if (_replacements is not null)
        {
            if (_replacements.TryConsumeByOriginal(child.ClOrdId, out var intent, out var ambiguousHeld))
            {
                MetricsRegistry.AlgoPeggedRepegFailed.Add(1);
                // Code review feedback on #300 (PR #334). PrepareReplaceAsync
                // always records a reservation under the new ClOrdID (even
                // for zero-delta replaces) — the only registry-driven
                // cleanup APIs are CommitReplace/AbortReplace. Once we've
                // consumed the intent the venue's later Replaced/Rejected/
                // Canceled ER will no longer hit those cleanup paths, so
                // the reservation must be aborted unconditionally here —
                // the earlier ambiguousHeld-only gate leaked normal in-
                // flight reservations indefinitely.
                if (_replaceMargin is not null && intent is not null)
                {
                    try { _replaceMargin.AbortReplace(intent.NewClOrdId); }
                    catch (Exception abortEx)
                    {
                        _logger.LogWarning(abortEx,
                            "AlgoEngine pegged repeg fill-race: AbortReplace failed for new ClOrdID {NewClOrdId} (ambiguousHeld={Ambiguous}).",
                            intent.NewClOrdId, ambiguousHeld);
                    }
                }
            }
        }

        if (wasPending && _peggedRepeg?.TryGet(algo.FirmId, algo.AlgoId) is not null)
        {
            // Window 3: Started already in WAL + book. Emit the
            // Resolved companion so the audit pair is balanced and
            // replay drops the pending entry. Best-effort under WAL
            // backpressure — Reconcile's orphan-prune covers a missed
            // Resolved on the next restart.
            try
            {
                var firmIdSnap = algo.FirmId;
                var algoIdSnap = algo.AlgoId;
                var book = _peggedRepeg;
                _dispatcher.Dispatch(
                    new AlgoPeggedRepegResolvedEvent
                    {
                        AlgoId = algo.AlgoId,
                        FirmId = algo.FirmId,
                        CancelledChildClOrdId = child.ClOrdId,
                        AtUtc = _clock.GetUtcNow(),
                        Aborted = false,
                        Reason = "FilledBeforeCancelAck",
                    },
                    () => book?.Remove(firmIdSnap, algoIdSnap));
            }
            catch (WalBackpressureException)
            {
                MetricsRegistry.WalBackpressure.Add(1,
                    new KeyValuePair<string, object?>("call_site", "algo.pegged.repeg-resolved"));
            }
        }

        if (algo.RemainingQuantity <= 0)
        {
            await RecordTerminalAsync(algo, rt, AlgoStatus.Completed, AlgoTerminalReason.None).ConfigureAwait(false);
        }
        // Else: residue remains. Do NOT submit a replacement here —
        // the next AlgoCreatedSignal tick re-enters OnCreatedAsync via
        // the empty-slot path and prices a fresh slice at the current
        // ref. This keeps "new working slice" submission single-sourced.
    }

    private async Task FailClosedPeggedRepegAsync(
        Algo algo,
        AlgoParentRuntime rt,
        ulong originalClOrdId,
        string outcome)
    {
        rt.RepegPending = false;
        rt.LastRepegCancelledChildId = null;
        rt.PeggedReplacedHoldTicks = 0;

        if (_replacements is not null
            && _replacements.TryConsumeByOriginal(
                originalClOrdId,
                out var staleIntent,
                out var ambiguousHeld)
            && staleIntent is not null
            && _replaceMargin is not null)
        {
            try
            {
                _replaceMargin.AbortReplace(staleIntent.NewClOrdId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "AlgoEngine pegged repeg terminal-original cleanup: AbortReplace failed for new ClOrdID {NewClOrdId} (ambiguousHeld={Ambiguous}).",
                    staleIntent.NewClOrdId,
                    ambiguousHeld);
            }
        }

        if (_peggedRepeg?.TryGet(algo.FirmId, algo.AlgoId) is not null)
        {
            SelfHealOrphanRepeg(algo.FirmId, algo.AlgoId, originalClOrdId);
        }
        _peggedRepeg?.UnmarkCancelledChild(
            algo.FirmId,
            algo.AlgoId,
            originalClOrdId);
        MetricsRegistry.AlgoPeggedRepegFailed.Add(1);
        _logger.LogWarning(
            "AlgoEngine pegged repeg for {Firm}/{AlgoId} child {Child} cannot adopt a replacement: {Outcome}; suspending for reconciliation.",
            algo.FirmId,
            algo.AlgoId,
            originalClOrdId,
            outcome);
        await RecordTerminalAsync(
            algo,
            rt,
            AlgoStatus.Suspended,
            AlgoTerminalReason.ReconciliationRequired).ConfigureAwait(false);
    }

    /// <summary>
    /// Q3.3 (#283). Repeg evaluation for a Pegged parent that already
    /// has a live working slice. The decision tree, in order:
    ///
    /// <list type="number">
    /// <item><b>Throttle</b>: if &lt; <c>RepegInterval</c> has elapsed
    /// since the last eval, no-op. Bounds the cancel-replace churn
    /// against an overactive scheduler tick.</item>
    /// <item><b>Resolve target</b>: if the cache has no live ref yet,
    /// no-op (don't touch the live child without a fresh reference —
    /// pulling it would expose the parent to "no order" risk for free).</item>
    /// <item><b>Compare</b>: if <c>|child.Price - target| &lt; 1 tick</c>,
    /// no-op and bump <c>PeggedLastEvalUtc</c> so the throttle is
    /// honoured.</item>
    /// <item><b>PriceLimit clamp</b>: target is already clamped by
    /// <see cref="ResolvePeggedTarget"/>; if the clamped target equals
    /// the child price the comparison above already returned no-op.
    /// This means a target that would otherwise cross the limit gets
    /// quietly absorbed and the live child stays put — matches the
    /// issue spec "Never cross spread beyond priceLimit".</item>
    /// <item><b>Cancel + flag</b>: cancel the live child via the
    /// gateway, set <c>rt.RepegPending</c> so
    /// <see cref="OnChildErAsync"/> re-submits at a fresh target when
    /// the cancel-ack lands (rather than treating the cancel as a
    /// venue-cancel suspension).</item>
    /// </list>
    /// </summary>
    private async Task EvaluatePeggedRepegAsync(
        Algo algo, AlgoParentRuntime rt, PeggedParameters pgp,
        ulong liveChildClOrdId, CancellationToken ct)
    {
        // Pass-1 review (#296) P1-B. If a prior repeg cycle is still
        // in flight (cancel emitted, ack not yet consumed) skip the
        // evaluation entirely. Without this guard a delayed cancel-
        // ack (slow venue, queue backpressure) past RepegInterval
        // would let the next scheduler tick issue ANOTHER cancel
        // for the same already-cancel-pending child — a cancel storm
        // that produces multiple replacement children racing into
        // the book. Do NOT advance PeggedLastEvalUtc here: the
        // throttle anchor is unrelated to in-flight cycles, and
        // bumping it would mask the next legitimate drift eval once
        // the cycle completes.
        // #300 (PR #334) code-review fix. The terminal-OLD watchdog
        // (#329) MUST run before the RepegPending throttle. Pre-#300
        // the throttle was the only state that meant "cycle in flight"
        // and would naturally clear on the Cancelled-ack. Post-#300 it
        // only clears on Replaced-adoption — and if AlgoSignalQueue
        // drops that signal the throttle stays true forever, the
        // watchdog at lines ~2126-2134 never runs, and the parent is
        // wedged on the terminal OLD child indefinitely. Order matters:
        // watchdog first (it both ticks the counter and can clean up
        // when ticks exhaust), throttle second (governs new cycles only).
        if (!_orders.TryGet(liveChildClOrdId, out var child) || child is null
            || IsChildTerminal(child))
        {
            if (child is null
                && IsConfirmedProvenUnsentMutation(
                    algo,
                    liveChildClOrdId,
                    AlgoOutboundActionKind.Repeg))
            {
                // The retry can no longer be applied because its original
                // child disappeared from the durable order book. Fail
                // closed rather than clearing the slot and leaving the
                // ProvenUnsent action permanently blocking scheduling.
                _peggedRepeg?.UnmarkCancelledChild(
                    algo.FirmId,
                    algo.AlgoId,
                    liveChildClOrdId);
                rt.RepegPending = false;
                rt.LastRepegCancelledChildId = null;
                await RecordTerminalAsync(
                    algo,
                    rt,
                    AlgoStatus.Suspended,
                    AlgoTerminalReason.ReconciliationRequired).ConfigureAwait(false);
                return;
            }

            if (child?.Status == OrderStatus.Filled
                && (rt.RepegPending
                    || (_peggedRepeg?.IsCancelledChild(
                        algo.FirmId,
                        algo.AlgoId,
                        child.ClOrdId) ?? false)))
            {
                var previouslyBooked = rt.ChildBookedCum.GetValueOrDefault(
                    child.ClOrdId,
                    0L);
                var fillDelta = child.CumulativeQuantity - previouslyBooked;
                if (fillDelta > 0)
                {
                    algo.RecordFill(fillDelta);
                    rt.ChildBookedCum[child.ClOrdId] = child.CumulativeQuantity;
                }
                if (rt.LiveChildClOrdId == child.ClOrdId)
                    rt.LiveChildClOrdId = null;
                await ResolveRepegOnFillAsync(algo, rt, child).ConfigureAwait(false);
                return;
            }

            if (child is not null
                && child.Status is OrderStatus.Cancelled or OrderStatus.Rejected
                && IsConfirmedProvenUnsentMutation(
                    algo,
                    child.ClOrdId,
                    AlgoOutboundActionKind.Repeg))
            {
                // The replace never reached the venue, so this terminal
                // status belongs to the original child and must not be
                // deduplicated as a successful repeg side effect. Process
                // it here as well as through the queued ER signal so a
                // dropped signal cannot leave a blocking mutation with an
                // empty live slot.
                _peggedRepeg?.UnmarkCancelledChild(
                    algo.FirmId,
                    algo.AlgoId,
                    child.ClOrdId);
                rt.RepegPending = false;
                rt.LastRepegCancelledChildId = null;
                await OnChildErAsync(
                    algo,
                    rt,
                    new ChildExecutionObservedSignal
                    {
                        FirmId = algo.FirmId,
                        AlgoId = algo.AlgoId,
                        ChildClOrdId = child.ClOrdId,
                    },
                    ct).ConfigureAwait(false);
                return;
            }

            if (child is not null
                && child.Status is OrderStatus.Cancelled or OrderStatus.Rejected
                && IsUnresolvedPotentiallySentRepegMutation(
                    algo,
                    child.ClOrdId))
            {
                await FailClosedPeggedRepegAsync(
                    algo,
                    rt,
                    child.ClOrdId,
                    $"unresolved replace observed terminal {child.Status} on original")
                    .ConfigureAwait(false);
                return;
            }

            // #329: When the child is terminal with status Replaced, an
            // adoption signal is normally in flight from the ER processor
            // (ApplyReplaceAccepted enqueues ChildExecutionObservedSignal
            // for the NEW child AFTER MarkReplaced flips the OLD to
            // Replaced). The adoption block in OnChildErAsync requires
            // `rt.LiveChildClOrdId is { } oldLive` — if we null it here
            // first, adoption is skipped and the parent ends up orphaned
            // with no live child until the next scheduler tick spawns a
            // fresh slice (which leaks a clOrdID and skips the retired-
            // child FIFO accounting that powers AlgoModifyRetiredChildEvictedTotal).
            // Leave the slot pointing at OLD so the imminent adoption
            // signal can transition it atomically.
            //
            // Safety valve: the adoption signal can be dropped if the
            // bounded AlgoSignalQueue is full when ApplyReplaceAccepted
            // tries to enqueue (TryEnqueue returns false and bumps
            // AlgoSignalsDropped). Without a fallback the parent would
            // be wedged on the terminal OLD child until process restart.
            // After PeggedReplacedHoldMaxTicks consecutive evaluations
            // where we still see OLD as Replaced, give up on the
            // adoption signal, clear the slot, and let the next tick
            // resubmit from the empty path. The threshold is generous
            // (≈1 second @ 100ms scheduler tick) so the normal in-order
            // case always lands before fallback kicks in.
            if (child is not null && child.Status == OrderStatus.Replaced)
            {
                rt.PeggedReplacedHoldTicks++;
                if (rt.PeggedReplacedHoldTicks < AlgoParentRuntime.PeggedReplacedHoldMaxTicks)
                    return;
                _logger.LogWarning(
                    "AlgoEngine Pegged repeg: live child {Child} on {Firm}/{AlgoId} observed terminal=Replaced for {Ticks} ticks; adoption signal appears dropped — clearing slot and resubmitting on next tick.",
                    liveChildClOrdId, algo.FirmId, algo.AlgoId, rt.PeggedReplacedHoldTicks);

                // #300 (PR #334) code-review fix. The dropped-adoption
                // fallback must also wind down the cancel-replace cycle
                // it started: release the pending replace intent + its
                // margin reservation, clear RepegPending, and emit the
                // Resolved-with-Aborted WAL companion if a Started was
                // already persisted. Without this the parent leaks a
                // PendingReplacementRegistry row + a reserve-margin
                // entry indefinitely AND the audit pair stays
                // unbalanced. Best-effort under WAL backpressure —
                // Reconcile's orphan-prune covers the audit-pair gap on
                // the next restart.
                if (rt.RepegPending)
                {
                    rt.RepegPending = false;
                    if (_replacements is not null
                        && _replacements.TryConsumeByOriginal(liveChildClOrdId, out var staleIntent, out var staleAmbiguous)
                        && staleIntent is not null
                        && _replaceMargin is not null)
                    {
                        try { _replaceMargin.AbortReplace(staleIntent.NewClOrdId); }
                        catch (Exception abortEx)
                        {
                            _logger.LogWarning(abortEx,
                                "AlgoEngine Pegged repeg watchdog: AbortReplace failed for new ClOrdID {NewClOrdId} (ambiguousHeld={Ambiguous}).",
                                staleIntent.NewClOrdId, staleAmbiguous);
                        }
                    }
                    if (_peggedRepeg?.TryGet(algo.FirmId, algo.AlgoId) is not null)
                    {
                        try
                        {
                            var firmIdSnap = algo.FirmId;
                            var algoIdSnap = algo.AlgoId;
                            var cancelledIdSnap = liveChildClOrdId;
                            var atUtcSnap = _clock.GetUtcNow();
                            var book = _peggedRepeg;
                            _dispatcher.Dispatch(
                                new AlgoPeggedRepegResolvedEvent
                                {
                                    AlgoId = algoIdSnap,
                                    FirmId = firmIdSnap,
                                    CancelledChildClOrdId = cancelledIdSnap,
                                    AtUtc = atUtcSnap,
                                    Aborted = true,
                                },
                                () => book?.Remove(firmIdSnap, algoIdSnap));
                        }
                        catch (WalBackpressureException)
                        {
                            MetricsRegistry.WalBackpressure.Add(1,
                                new KeyValuePair<string, object?>("call_site", "algo.pegged.repeg-resolved.watchdog"));
                        }
                    }
                    MetricsRegistry.AlgoPeggedRepegFailed.Add(1);
                }
            }
            rt.PeggedReplacedHoldTicks = 0;
            rt.LiveChildClOrdId = null;
            return;
        }

        // Non-terminal child observed — clear the dropped-adoption
        // watchdog so a future Replaced does not inherit a stale count.
        rt.PeggedReplacedHoldTicks = 0;

        // Throttle: don't start a NEW cycle while one is in flight.
        // Moved here from before the terminal block (PR #334 code review)
        // so the dropped-adoption watchdog above always runs.
        if (rt.RepegPending) return;

        var now = _clock.GetUtcNow();
        if (rt.PeggedLastEvalUtc != default
            && (now - rt.PeggedLastEvalUtc) < pgp.RepegInterval)
        {
            return;
        }

        var explicitRetry = FindSoleRetryableProvenUnsentMutation(
            algo,
            child.ClOrdId,
            AlgoOutboundActionKind.Repeg);
        var retryCommand =
            explicitRetry?.Approval?.CanonicalCommandNonSensitive;
        var target = ResolvePeggedTarget(algo, pgp);
        if (retryCommand is null && target is null)
        {
            // No live ref yet — don't disturb the working slice; next
            // tick may have a price.
            return;
        }

        var currentPrice = child.Price ?? 0m;
        if (retryCommand is null
            && (currentPrice <= 0m
                || !PeggedPlan.IsRepegNeeded(
                    currentPrice,
                    target!.Value,
                    pgp.TickSize)))
        {
            // No drift — record eval-at so the throttle holds.
            rt.PeggedLastEvalUtc = now;
            return;
        }

        // #300 retrofit. Pre-#300 set RepegPending + sticky cancel-id
        // BEFORE the wire-call so a racing cancel-ack ER on a
        // different consumer-loop iteration was classified as
        // expected. With cancel-replace (TryReplaceChildAsync below)
        // there is no Cancelled-on-OLD ack to race — the OLD child's
        // terminal transition is the Replaced ER, handled by the
        // adoption block at OnChildErAsync (~line 714). We still set
        // these markers before dispatch so:
        //   * RepegPending throttles a second EvaluatePeggedRepegAsync
        //     re-entry on the same algo while the replace is in
        //     flight (single-consumer reactor: an in-flight modify
        //     can span the consumer awaiting the gateway).
        //   * LastRepegCancelledChildId + MarkCancelledChild populate
        //     the dedup ring so a defensive Cancelled-on-OLD ER
        //     (rare/spurious — venue shouldn't emit) and any
        //     pre-#300 replay-time Cancelled ER both no-op through
        //     the IsCancelledChild branch in OnChildErAsync.
        // PeggedRepegBook.Set + the WAL Started marker still defer
        // until AFTER the wire-call succeeds (mirrors the pre-#300
        // approach-B ordering, now anchored on
        // TryReplaceChildAsync's return value): an ambiguous-send
        // failure must NOT leave a Started without a Resolved.
        rt.RepegPending = true;
        rt.LastRepegCancelledChildId = liveChildClOrdId;
        _peggedRepeg?.MarkCancelledChild(algo.FirmId, algo.AlgoId, liveChildClOrdId);
        rt.PeggedLastEvalUtc = now;
        var oldChildPrice = currentPrice;
        var refForAudit = _pegBookTop?.TryGet(algo.Symbol)?.RefPrice(pgp.Ref, algo.Side) ?? 0m;

        // #300 retrofit. Replace bare CancelAsync with the Q3.5
        // cancel-replace plumbing introduced in #299 so venue book
        // priority is preserved across the repeg. Reason="AlgoInternal"
        // is already in ModifyAlgoRequest.AllowedReasons and bounds
        // the metric label cardinality. TryReplaceChildAsync handles:
        //   * pre-trade risk + margin Prepare gates;
        //   * new-ClOrdID allocation + PendingReplacementRegistry
        //     intent registration via OrderReplaceRequestedEvent
        //     dispatch (durable);
        //   * AlgoChildModifiedEvent audit envelope;
        //   * AlgoChildModifiesTotal metric (algoType=pegged,
        //     reason=AlgoInternal);
        //   * ambiguous-send retention of the intent (and held
        //     margin) so a late Replaced ER still resolves.
        // On false-return we don't know whether the venue accepted —
        // we MUST NOT persist Started (else dangling without
        // Resolved). The next scheduler tick re-evaluates drift; if
        // it was an ambiguous send the #329 watchdog +
        // AlgoScheduler.SweepAmbiguousReplaceIntents bound recovery.
        var dispatchQuantity = retryCommand?.Quantity ?? child.Quantity;
        var dispatchPrice = retryCommand?.Price ?? target!.Value;
        bool replaced;
        try
        {
            replaced = await TryReplaceChildAsync(
                algo, child, dispatchQuantity, dispatchPrice,
                reason: "AlgoInternal", AlgoOutboundActionKind.Repeg, ct,
                explicitRetry?.AlgoOriginIdentity).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // TryReplaceChildAsync swallows gateway/WAL exceptions
            // internally; any escape here is a programmer error
            // surface (e.g. a synchronous throw from the risk
            // pipeline). Clear in-memory state so the next tick can
            // retry from a clean slate, mirroring the pre-#300
            // cancel-failure rollback.
            rt.RepegPending = false;
            rt.LastRepegCancelledChildId = null;
            if (FindLatestAlgoMutation(
                    algo,
                    liveChildClOrdId,
                    AlgoOutboundActionKind.Repeg) is null
                || IsConfirmedProvenUnsentMutation(
                    algo,
                    liveChildClOrdId,
                    AlgoOutboundActionKind.Repeg))
            {
                _peggedRepeg?.UnmarkCancelledChild(
                    algo.FirmId,
                    algo.AlgoId,
                    liveChildClOrdId);
            }
            MetricsRegistry.AlgoPeggedRepegFailed.Add(1);
            _logger.LogWarning(ex,
                "AlgoEngine pegged repeg cancel-replace failed for algo {Firm}/{AlgoId} child {Child}; cleared marker, will retry next tick.",
                algo.FirmId, algo.AlgoId, liveChildClOrdId);
            return;
        }

        if (!replaced)
        {
            // Replace was rejected (risk/margin) or its send was
            // ambiguous. In the rejection case the intent was rolled
            // back inside TryReplaceChildAsync — clear in-memory
            // markers so the next tick retries. In the ambiguous-
            // send case TryReplaceChildAsync deliberately RETAINS
            // the intent + margin reservation; we still clear our
            // in-memory markers because we are NOT going to dispatch
            // a Started (no audit pair to balance) — if the venue
            // had accepted, the Replaced ER will adopt through the
            // normal operator-modify path (no Resolved emitted; OK
            // because no Started either). The held intent leaks are
            // bounded by AlgoScheduler.SweepAmbiguousReplaceIntents.
            // The throttle is intentionally NOT advanced: the next
            // tick should be allowed to try again if drift persists.
            rt.RepegPending = false;
            rt.LastRepegCancelledChildId = null;
            var failedMutation = FindLatestAlgoMutation(
                algo,
                liveChildClOrdId,
                AlgoOutboundActionKind.Repeg);
            if (failedMutation is null
                || (failedMutation.State == OutboundMutationState.ProvenUnsent
                    && !failedMutation.RequiresReconciliation))
            {
                // MarkCancelledChild is optimistic because it must exist
                // before a potentially-sent replace can race a terminal
                // ER. ProvenUnsent (and pre-ledger rejection) proves that
                // no repeg-induced cancel can arrive, so retaining the
                // marker would hide a genuine venue cancellation of the
                // original child and wedge the parent with no live slot.
                _peggedRepeg?.UnmarkCancelledChild(
                    algo.FirmId,
                    algo.AlgoId,
                    liveChildClOrdId);
            }
            if (failedMutation is
                {
                    State: OutboundMutationState.ProvenUnsent,
                    RequiresReconciliation: false,
                }
                && failedMutation.Attempts.Count
                    >= OutboundMutationLedger.MaxOutboundAttempts)
            {
                await FailClosedPeggedRepegAsync(
                    algo,
                    rt,
                    liveChildClOrdId,
                    "ProvenUnsent repeg exhausted its outbound attempt cap")
                    .ConfigureAwait(false);
                return;
            }
            MetricsRegistry.AlgoPeggedRepegFailed.Add(1);
            return;
        }

        // #300 retrofit. Same Window 1/2 guard as pre-#300: if a
        // racing terminal Fill ER for `liveChildClOrdId` was
        // processed by OnChildErAsync between TryReplaceChildAsync
        // returning and here (only reachable if the gateway awaited
        // a Replaced ER synchronously inside CancelReplaceAsync,
        // unusual but defensive), ResolveRepegOnFillAsync has
        // already cleared rt.RepegPending and we MUST NOT persist
        // Started here — adoption won't fire (parent terminal) and
        // the audit pair would dangle.
        if (!rt.RepegPending)
        {
            return;
        }

        try
        {
            var firmIdSnap = algo.FirmId;
            var algoIdSnap = algo.AlgoId;
            var cancelledIdSnap = liveChildClOrdId;
            var targetSnap = dispatchPrice;
            var atUtcSnap = now;
            var book = _peggedRepeg;
            _dispatcher.Dispatch(
                new AlgoPeggedRepegStartedEvent
                {
                    AlgoId = algo.AlgoId,
                    FirmId = algo.FirmId,
                    CancelledChildClOrdId = liveChildClOrdId,
                    // #300 retrofit. NewClOrdId is no longer
                    // surfaced from TryReplaceChildAsync's caller
                    // because the replacement is observability-only
                    // here (the durable record is the
                    // OrderReplaceRequestedEvent the helper already
                    // dispatched). Carry 0 as a sentinel — the
                    // replayer keys only on CancelledChildClOrdId
                    // and the field is audit-only.
                    NewClOrdId = 0UL,
                    TargetPrice = dispatchPrice,
                    AtUtc = now,
                },
                () => book?.Set(firmIdSnap, algoIdSnap, cancelledIdSnap, targetSnap, atUtcSnap));
        }
        catch (WalBackpressureException)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "algo.pegged.repeg-started"));
            // WAL backpressure after a successful replace: the
            // dispatch apply (book.Set) never ran. Mirror it
            // manually so the in-process adoption block can still
            // discriminate Pegged-repeg vs operator-modify (the
            // book lookup is the discriminator under approach (b)).
            // Recovery for this specific cycle is best-effort;
            // Reconcile's orphan-prune covers any drift on restart.
            _peggedRepeg?.Set(algo.FirmId, algo.AlgoId, liveChildClOrdId, dispatchPrice, now);
        }

        // Best-effort audit envelope; WAL backpressure is non-fatal
        // because the next OrderSubmittedEvent already records the
        // new child price.
        try
        {
            var sliceSeq = rt.NextSliceSeq;
            _dispatcher.Dispatch(
                new AlgoPeggedRepeggedEvent
                {
                    AlgoId = algo.AlgoId,
                    FirmId = algo.FirmId,
                    SliceSeq = sliceSeq,
                    RefKind = pgp.Ref.ToString(),
                    RefPrice = refForAudit,
                    OldChildPrice = oldChildPrice,
                    NewTargetPrice = dispatchPrice,
                    AtUtc = now,
                },
                static () => { });
        }
        catch (WalBackpressureException)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "algo.pegged.repegged"));
        }

        MetricsRegistry.AlgoPeggedRepegsTotal.Add(1);
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

    /// <summary>
    /// POV slice computation — restart-resilient (pass-1 review #295 P1#1).
    ///
    /// <para>
    /// The slice target is derived from the parent's OWN running
    /// cumulative-market-volume baseline (<see cref="AlgoParentRuntime.PovMarketVolumeSeen"/>)
    /// rather than from <c>VolumeCurveEstimator.VolumeBetween(StartUtc, now)</c>.
    /// The estimator only carries in-memory buckets so it cannot
    /// reconstruct pre-restart trade volume; under the old formulation
    /// a restarted live POV would under-slice until post-restart volume
    /// "caught up" to the already-executed cumulative. Here we instead
    /// accumulate <c>VolumeBetween(lastEvaluateAtUtc, now)</c> on every
    /// tick and persist the running total via
    /// <see cref="Persistence.AlgoPovSlicedEvent"/>; replay restores
    /// the baseline before the engine resumes.
    /// </para>
    ///
    /// <para>
    /// <b>Side-effecting:</b> updates <paramref name="rt"/>'s
    /// <c>PovMarketVolumeSeen</c> and <c>PovLastEvaluateAtUtc</c>. The
    /// caller must invoke this exactly once per scheduler tick (emit or
    /// skip) so the persisted snapshot on the next emit reflects every
    /// observed bucket. Returns <c>(qty, price, marketVolumeSeen)</c>
    /// — <c>marketVolumeSeen</c> is the just-updated baseline that the
    /// caller should record on the emitted WAL event.
    /// </para>
    /// </summary>
    private (long Qty, decimal? Price, long MarketVolumeSeen) ComputePovSlice(
        Algo algo, PovParameters pp, AlgoParentRuntime rt, DateTimeOffset evaluateAtUtc)
    {
        // First evaluation for a freshly-created POV (no Reconcile path
        // touched this runtime): seed the baseline at StartUtc so the
        // initial integration covers [StartUtc, evaluateAtUtc) — same
        // window as the pre-fix formulation, preserving the cold-start
        // semantics that the issue's #294 acceptance baseline measured.
        if (rt.PovLastEvaluateAtUtc == default)
        {
            rt.PovLastEvaluateAtUtc = pp.StartUtc;
        }
        if (evaluateAtUtc > rt.PovLastEvaluateAtUtc && _vwapCurve is not null)
        {
            var incremental = _vwapCurve.VolumeBetween(algo.Symbol, rt.PovLastEvaluateAtUtc, evaluateAtUtc);
            if (incremental > 0)
                rt.PovMarketVolumeSeen += incremental;
            rt.PovLastEvaluateAtUtc = evaluateAtUtc;
        }
        // Pass-2 review (#295) P1. Persist the just-advanced baseline
        // into PovProgressBook on EVERY tick (emit OR skip). On the
        // emit path the dispatcher action at SubmitNextSliceAsync will
        // re-Set the same values (idempotent, last-write-wins). On the
        // skip path the snapshotter is the only persistence — without
        // this update a restart between snapshots loses the observed
        // market volume and the algo under-slices until post-restart
        // volume catches up. The trader's loss is bounded by snapshot
        // cadence (no per-tick WAL event is emitted on skip).
        _povProgress?.Set(algo.FirmId, algo.AlgoId, rt.PovMarketVolumeSeen, rt.PovLastEvaluateAtUtc);
        var qty = PovPlan.SliceQty(
            rt.PovMarketVolumeSeen,
            algo.FilledQuantity,
            algo.RemainingQuantity,
            pp.ParticipationRate,
            pp.MinSliceQty);
        var price = PovPlan.ClampPrice(pp.ChildPrice, pp.PriceLimit, algo.Side);
        return (qty, price, rt.PovMarketVolumeSeen);
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
        AlgoModifyRequestedSignal => "modify_requested",
        _ => "unknown",
    };

    private static ulong AlgoIdOf(AlgoSignal s) => s switch
    {
        AlgoCreatedSignal c => c.AlgoId,
        AlgoCancelRequestedSignal c => c.AlgoId,
        ChildExecutionObservedSignal c => c.AlgoId,
        AlgoModifyRequestedSignal c => c.AlgoId,
        _ => 0,
    };

    /// <summary>
    /// Mutable per-parent runtime state. Lives only in memory (not
    /// snapshotted) — recovery rebuilds it from the order book on engine
    /// start. The engine consumer task is the sole writer for every
    /// field; <see cref="LiveChildClOrdId"/> additionally publishes via
    /// a lock-fenced setter/getter so cross-thread readers (notably
    /// tests gating on <see cref="AlgoEngine.TryGetLiveChildClOrdId"/>)
    /// observe the adoption flip strictly after every prior write in
    /// the adoption block (#434).
    /// </summary>
    internal sealed class AlgoParentRuntime
    {
        // #434: backing field for the lock-fenced LiveChildClOrdId
        // property below. Single-writer (engine consumer task) +
        // occasional cross-thread reader (tests). The lock acts as
        // a release-store on write and acquire-load on read so any
        // bookkeeping mutated by the writer BEFORE assigning this
        // field becomes happens-before visible to a reader that
        // observes the new value.
        private ulong? _liveChildClOrdId;
        private readonly object _liveChildLock = new();

        public ulong? LiveChildClOrdId
        {
            get { lock (_liveChildLock) return _liveChildClOrdId; }
            set { lock (_liveChildLock) _liveChildClOrdId = value; }
        }
        public int NextSliceSeq;
        public int RetryAttempts;
        public Dictionary<ulong, long> ChildBookedCum { get; } = new();

        // Pass-2 review (#299) P2. Bounded FIFO of retired (replaced)
        // child ClOrdIds for this parent. On adoption of a replacement
        // child via OnChildErAsync, the OLD child id is enqueued here;
        // when the queue overflows <see cref="RetiredChildSlotsCap"/>,
        // the eldest id is dequeued AND its row removed from
        // <see cref="ChildBookedCum"/>. We keep the booked-cum row for
        // recently-retired slots so a late stray ER for that OLD id
        // (e.g. a duplicate Cancelled ack the venue emits post-replace)
        // computes delta = childCum - prevBooked == 0 instead of being
        // re-booked from a missing-key default of 0. Cap mirrors the
        // <c>CancelledChildRing</c> sizing pattern from PR #296.
        private const int RetiredChildSlotsCap = 8;
        public Queue<ulong> RetiredChildSlots { get; } = new(RetiredChildSlotsCap);

        // Pass-3 review (#299) P2. One-shot warn latch mirroring the
        // <see cref="CancelledChildRing"/> pattern from PR #296 pass-7
        // / pass-8: emit a single warn on the FIRST eviction so an
        // operator notices that ChildBookedCum rows are now being
        // forgotten faster than late stray ERs may arrive, without
        // spamming logs once we're past the cap. Set atomically by
        // <see cref="RetireChildSlot"/> in the same step that drops the
        // row, so a concurrent reader cannot observe the eviction
        // without also observing <c>RetiredEvictionLogged=true</c>.
        // AlgoParentRuntime is deliberately not snapshotted (recovery
        // rebuilds it from the order book) so this latch resets on
        // restart — acceptable: a post-restart warn just re-arms the
        // operator's attention with no duplicate-spam risk because
        // restarts are rare and the warn is per-parent.
        public bool RetiredEvictionLogged;

        /// <summary>
        /// Enqueue <paramref name="oldChildClOrdId"/> into the retired
        /// FIFO; if the cap is exceeded, dequeue the eldest, drop its
        /// <see cref="ChildBookedCum"/> row, and set
        /// <paramref name="firstEviction"/> to <c>true</c> iff this is
        /// the FIRST eviction observed on this parent (latch flip).
        /// Returns the count of rows evicted by this call (0 when
        /// below cap, 1 in the steady-state overflow case).
        /// </summary>
        public int RetireChildSlot(ulong oldChildClOrdId, out bool firstEviction)
        {
            firstEviction = false;
            RetiredChildSlots.Enqueue(oldChildClOrdId);
            var evictedCount = 0;
            while (RetiredChildSlots.Count > RetiredChildSlotsCap)
            {
                var evicted = RetiredChildSlots.Dequeue();
                ChildBookedCum.Remove(evicted);
                evictedCount++;
                if (!RetiredEvictionLogged)
                {
                    RetiredEvictionLogged = true;
                    firstEviction = true;
                }
            }
            return evictedCount;
        }

        // Legacy signature retained for the AlgoParentRuntimeTests
        // direct-construction surface — delegates to the two-arg
        // variant and discards the eviction observability hooks.
        public void RetireChildSlot(ulong oldChildClOrdId) =>
            RetireChildSlot(oldChildClOrdId, out _);

        // Pass-1 review (#295) P1#1. POV scheduling state. Initialised
        // to (0, default) so the first tick treats StartUtc as the
        // baseline and integrates volume from there; the engine seeds
        // these from PovProgressBook on Reconcile so a restart resumes
        // from the most recently persisted slice instead of re-scanning
        // an empty estimator.
        public long PovMarketVolumeSeen;
        public DateTimeOffset PovLastEvaluateAtUtc;

        // Q3.3 (#283) — Pegged scheduling state. PeggedLastEvalUtc
        // anchors the RepegInterval throttle (next eval allowed at
        // last + interval). RepegPending signals the cancel-ack ER
        // that the engine itself initiated the cancel for a repeg —
        // OnChildErAsync uses it to route through SubmitNextSlice
        // (place new) rather than the VenueCancelled-suspension path.
        public DateTimeOffset PeggedLastEvalUtc;
        public bool RepegPending;

        // Pass-1 review (#296) P1-A. Sticky marker: the ClOrdId of
        // the most recent engine-initiated cancel issued for a
        // pegged repeg cycle. Set BEFORE the cancel wire-call (in
        // EvaluatePeggedRepegAsync) and never cleared during normal
        // lifecycle — a new repeg cycle overwrites it. Cleared on
        // parent terminal. Used by OnChildErAsync to classify any
        // Cancelled ER whose child-id matches this marker as
        // "expected cancel" (so duplicate or post-restart-delayed
        // ERs do not flip the parent to Suspended/VenueCancelled).
        public ulong? LastRepegCancelledChildId;

        // #329. Watchdog for the adoption-signal-dropped recovery in
        // EvaluatePeggedRepegAsync. Counts consecutive scheduler-tick
        // observations where the live child is terminal with status
        // Replaced (i.e. an adoption signal is expected but hasn't been
        // processed yet). Cleared on any non-Replaced observation OR
        // when the watchdog fires. The threshold is generous so the
        // normal in-order case (signal queued, consumer picks it up
        // within one tick) never triggers it; only a genuinely dropped
        // signal due to a full bounded queue lets the count reach the
        // ceiling and force a fallback resubmit.
        public int PeggedReplacedHoldTicks;
        public const int PeggedReplacedHoldMaxTicks = 10;
    }
}
