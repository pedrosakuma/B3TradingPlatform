
using B3.Trading.Application.Observability;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Application;

/// <summary>
/// Source-agnostic ER → domain dispatcher. Wire-side gateways feed
/// raw fields into <see cref="Apply"/>; this class resolves the owner via
/// <see cref="OrderOwnershipMap"/>, mutates the <see cref="Order"/> in
/// <see cref="WorkingOrderBook"/>, applies fills to
/// <see cref="PositionKeeper"/>, and publishes an
/// <see cref="ExecutionEvent"/> for downstream fan-out.
///
/// <para>
/// <b>Idempotency:</b> safe to call with the same ER twice and with ERs
/// arriving out-of-order. Fills are advanced via cumulative-quantity
/// (<see cref="Order.ApplyCumulativeFill"/>) — only the forward delta
/// books to <see cref="PositionKeeper"/>, so a replayed ER (FIXP
/// retransmit after reconnect, or WAL replay at cold start) cannot
/// double-count a position. Terminal-state ERs are guarded against
/// regression. The method never throws for a replay-realistic input,
/// because the WAL writes ERs unconditionally before mutation: a throw
/// here would poison recovery.
/// </para>
/// </summary>
public sealed class ExecutionReportProcessor
{
    private readonly OrderOwnershipMap _ownership;
    private readonly WorkingOrderBook _orders;
    private readonly PositionKeeper _positions;
    private readonly IExecutionEventSink _sink;
    private readonly Risk.IMarginProvider _margin;
    private readonly ILogger<ExecutionReportProcessor> _logger;
    private readonly IAlgoSignalQueue? _algoSignals;
    private readonly CashLedger? _cash;
    private readonly IFeeCalculator? _feeCalculator;
    private readonly FeeKeeper? _feeKeeper;
    private readonly PnlKeeper? _pnlKeeper;
    private readonly SubAccountPositionKeeper? _subAccountPositions;
    private readonly SubAccountPnlKeeper? _subAccountPnl;
    private readonly Persistence.EventDispatcher? _dispatcher;
    private readonly PendingReplacementRegistry? _replacements;
    private readonly Risk.IReplaceMarginCoordinator? _replaceMargin;
    private readonly IBotErRouter? _botErRouter;
    private readonly Scheduling.GtdExpirationScheduler? _gtdScheduler;
    private readonly Scheduling.IocFokWatchdog? _iocWatchdog;
    private readonly FillProjection? _fillProjection;

    public ExecutionReportProcessor(
        OrderOwnershipMap ownership,
        WorkingOrderBook orders,
        PositionKeeper positions,
        IExecutionEventSink sink,
        Risk.IMarginProvider margin,
        ILogger<ExecutionReportProcessor> logger,
        IAlgoSignalQueue? algoSignals = null,
        CashLedger? cash = null,
        PendingReplacementRegistry? replacements = null,
        Risk.IReplaceMarginCoordinator? replaceMargin = null,
        IBotErRouter? botErRouter = null,
        Scheduling.GtdExpirationScheduler? gtdScheduler = null,
        IFeeCalculator? feeCalculator = null,
        FeeKeeper? feeKeeper = null,
        Persistence.EventDispatcher? dispatcher = null,
        PnlKeeper? pnlKeeper = null,
        SubAccountPositionKeeper? subAccountPositions = null,
        SubAccountPnlKeeper? subAccountPnl = null,
        FillProjection? fillProjection = null,
        Scheduling.IocFokWatchdog? iocWatchdog = null)
    {
        _ownership = ownership;
        _orders = orders;
        _positions = positions;
        _sink = sink;
        _margin = margin;
        _logger = logger;
        _algoSignals = algoSignals;
        _cash = cash;
        _replacements = replacements;
        _replaceMargin = replaceMargin;
        _botErRouter = botErRouter;
        _gtdScheduler = gtdScheduler;
        _feeCalculator = feeCalculator;
        _feeKeeper = feeKeeper;
        _dispatcher = dispatcher;
        _pnlKeeper = pnlKeeper;
        _subAccountPositions = subAccountPositions;
        _subAccountPnl = subAccountPnl;
        _fillProjection = fillProjection;
        _iocWatchdog = iocWatchdog;
    }

    /// <summary>
    /// <paramref name="origClOrdId"/> is non-zero on cancel-ack and modify-ack.
    /// When set, the processor mutates the order identified by that ID
    /// (the original order) — the cancel/replace request itself uses a
    /// fresh ClOrdID that has no in-memory order behind it.
    ///
    /// <para>
    /// RFC §5.2 (F2). When invoked from
    /// <see cref="EventDispatcher.Dispatch(WalEvent, System.Action{Persistence.ExecutionFanOut})"/>,
    /// <paramref name="fanOut"/> is non-null and every outbound
    /// <see cref="ExecutionEvent"/> is recorded onto it instead of being
    /// synchronously published. The dispatcher then enqueues the
    /// captured events onto every registered fan-out sink WHILE STILL
    /// HOLDING THE LOCK so subscribers see ERs in WAL-append order even
    /// though the actual publish work happens off-lock on each sink's
    /// drain thread. When <paramref name="fanOut"/> is null (test
    /// helpers that drive the processor directly without going through
    /// a dispatcher) the legacy synchronous-publish behavior is
    /// preserved so existing tests don't need rewiring.
    /// </para>
    /// </summary>
    public void Apply(ulong clOrdId, ExecKind kind, long leaves, long cumQty, long lastQty, decimal lastPx, string? rejectReason, ulong origClOrdId = 0, Persistence.ExecutionFanOut? fanOut = null, bool isReplay = false, DateTimeOffset? eventTimestampUtc = null, string? envelopeFirmId = null, MarketData.BookTouchSnapshot? bookTouch = null)
    {
        // PR #317 P1. Cross-firm guard hoisted ABOVE the replace
        // intercept block so a misrouted ER cannot consume the pending
        // replacement intent (Rejected / Replaced / cancel-as-replace
        // Canceled all run TryConsume before any order lookup, and the
        // post-intercept check below at the main path would never fire
        // for the replace lifecycle). Two authoritative sources of the
        // expected FirmId, in priority order:
        //
        //   1. A pending replacement intent keyed by this ER's ClOrdID
        //      (only for replace-shaped kinds). The intent was stamped
        //      with the originating firm at modify-endpoint time.
        //   2. The order in the book, resolved via the same orig/link
        //      logic the main path uses below. NewOrderSingle stamps
        //      Order.FirmId at construction time.
        //
        // Legacy WAL events (envelopeFirmId == null, pre-#317) bypass
        // the check — historical segments hydrate identically. A
        // populated envelope FirmId that disagrees with either source
        // results in: log + counter + return without consuming intent
        // or mutating state. On the replay path a mismatch escalates
        // to log.error since it implies WAL corruption or a developer
        // mistake (live wire ERs always go through a per-firm gateway).
        if (envelopeFirmId is not null)
        {
            string? expectedFirmId = null;
            if (_replacements is not null
                && kind is ExecKind.Rejected or ExecKind.Replaced or ExecKind.Canceled
                && _replacements.TryGet(clOrdId, out var pendingIntent)
                && pendingIntent is not null)
            {
                expectedFirmId = pendingIntent.FirmId;
            }
            else
            {
                // Mirror the main-path resolution (origClOrdId fallback
                // via the cancel-link map for cancel/replace acks that
                // dropped OrigClOrdID). When the order is not in the
                // book, expectedFirmId stays null and the main path
                // below handles the unknown-ClOrdID drop with its own
                // log + metric.
                var earlyResolvedOrig = origClOrdId;
                if ((kind is ExecKind.Canceled or ExecKind.Replaced) && earlyResolvedOrig == 0
                    && _ownership.TryResolveOrig(clOrdId, out var earlyLinked))
                {
                    earlyResolvedOrig = earlyLinked;
                }
                var earlyLookupId = (kind is ExecKind.Canceled or ExecKind.Replaced) && earlyResolvedOrig != 0
                    ? earlyResolvedOrig
                    : clOrdId;
                if (_orders.TryGet(earlyLookupId, out var earlyOrder) && earlyOrder is not null)
                    expectedFirmId = earlyOrder.FirmId;
            }

            if (expectedFirmId is not null
                && !string.Equals(envelopeFirmId, expectedFirmId, StringComparison.Ordinal))
            {
                MetricsRegistry.ExecutionReportFirmMismatch.Add(1,
                    new KeyValuePair<string, object?>("exec_type", kind.ToString()));
                if (isReplay)
                {
                    _logger.LogError(
                        "ER firm mismatch on REPLAY for {ClOrdId} (orig={Orig}): envelope firm={EnvelopeFirm}, expected firm={ExpectedFirm}; suspected WAL corruption — refusing to mutate.",
                        clOrdId, origClOrdId, envelopeFirmId, expectedFirmId);
                }
                else
                {
                    _logger.LogWarning(
                        "ER firm mismatch for {ClOrdId} (orig={Orig}): envelope firm={EnvelopeFirm}, expected firm={ExpectedFirm}; rejecting without state mutation.",
                        clOrdId, origClOrdId, envelopeFirmId, expectedFirmId);
                }
                return;
            }
        }

        // Slice 2 of #122: replace lifecycle early intercepts. Both
        // branches are gated on the registry having an intent recorded
        // for this ClOrdID — outside of that, the original switch
        // semantics apply unchanged.
        if (_replacements is not null)
        {
            if (kind == ExecKind.Rejected
                && _replacements.TryConsume(clOrdId, out var rejectedIntent)
                && rejectedIntent is not null)
            {
                ApplyReplaceRejected(clOrdId, rejectedIntent, rejectReason, fanOut);
                return;
            }
            if (kind == ExecKind.Replaced
                && _replacements.TryConsume(clOrdId, out var replaceIntent)
                && replaceIntent is not null)
            {
                ApplyReplaceAccepted(clOrdId, leaves, cumQty, lastPx, origClOrdId, replaceIntent, fanOut);
                return;
            }
            // Issue #241: B3MatchingPlatform implements OrderCancelReplaceRequest
            // via a "priority-lost" path (any change other than same-price /
            // qty-down) by emitting Cancel(orig) + Trade/New(new) under the
            // replace's NEW ClOrdID — never an ExecType=Replaced. Without this
            // intercept the Cancel terminalises the original AND the new
            // ClOrdID is never created in the book, so the subsequent Trade
            // ER drops with "missing order" and the fill is silently lost
            // (position/cash diverge from the venue). Funnel through
            // ApplyReplaceAccepted so the original goes Replaced (not
            // Cancelled), the new Order is hydrated under newClOrdId, and
            // the margin coordinator sees Commit (not a leaked reservation).
            if (kind == ExecKind.Canceled
                && _replacements.TryConsume(clOrdId, out var cancelAsReplaceIntent)
                && cancelAsReplaceIntent is not null)
            {
                ApplyReplaceAccepted(
                    newClOrdId: clOrdId,
                    erLeaves: cancelAsReplaceIntent.NewQuantity,
                    erCum: 0,
                    erLastPx: 0m,
                    erOrigClOrdId: origClOrdId,
                    intent: cancelAsReplaceIntent,
                    fanOut: fanOut);
                return;
            }
        }

        // For cancel/replace acks, the meaningful identity is the original
        // ClOrdID; the cancel-side ClOrdID was never registered as an order.
        // Some upstream gateways (and certain SDK versions) drop OrigClOrdID
        // on cancel acks — fall back to the cancel-link map populated when
        // we sent the request so the ER still resolves to the right order.
        var resolvedOrig = origClOrdId;
        if ((kind is ExecKind.Canceled or ExecKind.Replaced) && resolvedOrig == 0
            && _ownership.TryResolveOrig(clOrdId, out var linked))
        {
            resolvedOrig = linked;
        }

        var lookupId = (kind is ExecKind.Canceled or ExecKind.Replaced) && resolvedOrig != 0
            ? resolvedOrig
            : clOrdId;

        if (!_ownership.TryResolve(lookupId, out var owner) || owner is null)
        {
            // Unknown ClOrdID is not necessarily a bug — could be an ER
            // for an order owned by an end-client that has since dropped
            // out of memory (ephemeral state, see issue #1 §3). Log and
            // drop; Phase 3 will handle this via ER replay on reconnect.
            _logger.LogWarning("ER for unknown ClOrdID {ClOrdId} (orig={Orig}); dropping.", clOrdId, origClOrdId);
            return;
        }

        if (!_orders.TryGet(lookupId, out var order) || order is null)
        {
            // Issue #241: silent loss of a fill for a known owner is a P0
            // correctness bug (position/cash divergence with the venue),
            // not an idiomatic "ephemeral state" miss. Surface as error +
            // metric so ops can alert; the most common cause is the
            // priority-lost cancel-as-replace branch above failing to
            // intercept (e.g. registry not wired or already consumed).
            MetricsRegistry.ExecutionReportsDroppedKnownOwnerMissingOrder.Add(1, KindTag(kind));
            _logger.LogError("ER for known owner {Owner} but missing order {ClOrdId} (orig={Orig}); dropping.", owner, clOrdId, origClOrdId);
            return;
        }

        // PR #317 P1. Cross-firm guard runs at the top of Apply now
        // (see hoisted block above the replace intercepts). The post-
        // intercept path here intentionally has no duplicate check —
        // the hoisted guard already resolved the same expected FirmId
        // (intent or order) using the same orig/link logic as below.

        switch (kind)
        {
            case ExecKind.New:
                if (order.Status != OrderStatus.PendingNew)
                {
                    MetricsRegistry.ExecutionReportsReplayDeduped.Add(1, KindTag(kind));
                    _logger.LogDebug("Dropping replayed New ER for {ClOrdId}; order already in {Status}.", lookupId, order.Status);
                    return;
                }
                order.MarkWorking();
                break;
            case ExecKind.PartialFill:
            case ExecKind.Fill:
                {
                    var wasTerminal = order.Status is OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Replaced;
                    var delta = order.ApplyCumulativeFill(cumQty);
                    if (delta == 0)
                    {
                        // Stale or duplicate fill (cumQty didn't advance). Expected
                        // after FIXP retransmit; harmless because we book nothing.
                        MetricsRegistry.ExecutionReportsReplayDeduped.Add(1, KindTag(kind));
                        _logger.LogDebug(
                            "Dropping stale fill for {ClOrdId}: ER cumQty={ErCum} <= order cumQty={OrderCum}.",
                            lookupId, cumQty, order.CumulativeQuantity);
                        return;
                    }
                    if (wasTerminal)
                    {
                        // Late fill against a terminal order — exchange's truth
                        // wins for position keeping; order keeps its terminal
                        // status (preserved by ApplyCumulativeFill).
                        MetricsRegistry.ExecutionReportsLateFillAfterTerminal.Add(1, KindTag(kind));
                        _logger.LogWarning(
                            "Late fill after terminal status for {ClOrdId}: status={Status}, delta={Delta}, lastPx={LastPx}.",
                            lookupId, order.Status, delta, lastPx);
                    }
                    if (delta != lastQty)
                    {
                        // Cumulative advanced by an amount that disagrees with the
                        // ER's own LastQuantity — most often because an
                        // intermediate fill ER was lost or arrived out of order.
                        // Position is booked at the observed delta @ lastPx.
                        MetricsRegistry.ExecutionReportsFillDeltaMismatch.Add(1, KindTag(kind));
                        _logger.LogWarning(
                            "Fill delta mismatch for {ClOrdId}: ER lastQty={LastQty}, computed delta={Delta}.",
                            lookupId, lastQty, delta);
                    }
                    // Q2.4 (#271). Capture the pre-fill avg-cost basis
                    // BEFORE _positions.ApplyFill mutates it — realized
                    // delta is computed off the pre-fill state.
                    //
                    // PR #316 P2. The basis we read here must be the
                    // BUCKET-of-the-fill's basis (master when
                    // SubAccountId is null, else the sub-bucket), NOT
                    // the aggregate _pnlKeeper basis — otherwise a
                    // sub-account fill that offsets a position held in
                    // the master bucket realises against the master's
                    // avg cost and the spec's "P&L segregated" contract
                    // is broken. Falls back to the aggregate keeper
                    // only when _subAccountPnl is not wired (test
                    // contexts that haven't been migrated), which
                    // collapses to the original aggregate behaviour
                    // for the no-sub-account case.
                    long preFillQty = 0;
                    decimal preFillAvg = 0m;
                    if (_subAccountPnl is not null)
                    {
                        var bucket = _subAccountPnl.GetBucketAvgCost(order.FirmId, owner.Value, order.SubAccountId, order.Symbol);
                        if (bucket is not null) { preFillQty = bucket.NetQuantity; preFillAvg = bucket.AvgPrice; }
                    }
                    else if (_pnlKeeper is not null)
                    {
                        var avg = _pnlKeeper.GetAvgCost(order.FirmId, owner.Value, order.Symbol);
                        if (avg is not null) { preFillQty = avg.NetQuantity; preFillAvg = avg.AvgPrice; }
                    }
                    _positions.ApplyFill(order.FirmId, owner, order.Symbol, order.Side, delta, lastPx);
                    // Q4.1 (#301). Sub-account-tagged fills are also
                    // booked into the parallel sub-account keeper so a
                    // ?subAccount=X filter on GET /positions can read
                    // a segregated row. The master keeper above sees
                    // every fill (sub-account-null + sub-account-tagged)
                    // so the aggregate view is naturally preserved.
                    // FirmId is forwarded so the same login under two
                    // firms with the same sub-account id stays
                    // segregated (PR review #301 P1).
                    if (order.SubAccountId is { } sa)
                        _subAccountPositions?.ApplyFill(order.FirmId, owner, sa, order.Symbol, order.Side, delta, lastPx);
                    // Book the cash leg of the fill on the same delta as
                    // the position. Buys debit, Sells credit; T+0 settle.
                    // Null when the host hasn't wired CashLedger yet
                    // (test contexts only — production DI always injects).
                    _cash?.ApplyFill(owner, order.Side, delta, lastPx);
                    // Q2.3 (#270). Fees are computed off the fill delta
                    // (NOT the cumulative quantity) using the live
                    // FeeOptions snapshot. The breakdown is deterministic
                    // from (symbol, side, delta, lastPx) + options, so
                    // we apply it on BOTH live and replay paths:
                    //
                    //   Live:    Append FeeAccruedEvent (seq N+1) +
                    //            FeeKeeper.Apply under the dispatcher
                    //            lock — single ER@N, Fee@N+1 ordering.
                    //   Replay:  FeeKeeper.Apply only (no WAL append).
                    //            FeeAccruedEvent in the WAL is also
                    //            replayed via EventReplayer's switch
                    //            case → FeeKeeper dedupes on ExecutionId,
                    //            so no double-count. If the WAL is
                    //            missing the FeeAccruedEvent (process
                    //            crashed in the window between the ER
                    //            append and the Fee append), the synth
                    //            here recovers the keeper state — fees
                    //            remain accurate even when the audit
                    //            event is lost. Known limitation: this
                    //            assumes FeeOptions has not changed
                    //            since the original event; a future
                    //            FeeRateChangedEvent would close that
                    //            gap.
                    //
                    // The live backpressure-fallback path (router catches
                    // WalBackpressureException on the ER and re-invokes
                    // Apply WITHOUT a fanOut) is NOT replay — fees are
                    // accrued via the dispatcher branch. If that nested
                    // Dispatch itself throws backpressure, swallow it
                    // (metric + log + direct keeper.Apply): same "log
                    // dropped, state intact" tradeoff the router makes
                    // for the ER itself, and bubbling here would skip
                    // _margin.OnExecution + fan-out below.
                    if (_feeCalculator is not null && _feeKeeper is not null)
                    {
                        var nowUtc = eventTimestampUtc ?? DateTimeOffset.UtcNow;
                        var executionId = lookupId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + ":" + order.CumulativeQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (isReplay || _dispatcher is null)
                        {
                            // Replay (or test path with no dispatcher):
                            // defer the synth as a pending entry. If a
                            // durable FeeAccruedEvent follows in the
                            // WAL, Apply(FeeAccruedEvent) will supersede
                            // it (and emit reconciled=true). If not —
                            // i.e. the true ER-then-crash window —
                            // PersistenceRecovery.FinalizeReplay
                            // materialises it with the current
                            // FeeOptions snapshot (known limitation
                            // documented above).
                            _feeKeeper.RegisterPendingReplaySynth(
                                executionId, owner.Value, order.Symbol, order.Side,
                                delta, lastPx, nowUtc);
                        }
                        else
                        {
                            var breakdown = _feeCalculator.Compute(order.Symbol, order.Side, delta, lastPx);
                            var feeEvt = new Persistence.FeeAccruedEvent
                            {
                                ClOrdId = lookupId,
                                ExecutionId = executionId,
                                EndClientId = owner.Value,
                                Symbol = order.Symbol,
                                Side = order.Side.ToString(),
                                FillQuantity = delta,
                                FillPrice = lastPx,
                                Notional = delta * lastPx,
                                Brokerage = breakdown.Brokerage,
                                Emolumentos = breakdown.Emolumentos,
                                Liquidacao = breakdown.Liquidacao,
                                Total = breakdown.Total,
                                TimestampUtc = nowUtc,
                            };
                            var keeper = _feeKeeper;
                            try
                            {
                                _dispatcher.Dispatch(feeEvt, () => keeper.Apply(feeEvt));
                            }
                            catch (Persistence.WalBackpressureException)
                            {
                                // ER-level state already mutated; we
                                // can't roll back the WAL append of the
                                // ER. Apply the fee directly to the
                                // keeper so in-memory fees stay
                                // accurate; the audit event is dropped
                                // (surfaced as a backpressure metric).
                                MetricsRegistry.WalBackpressure.Add(1,
                                    new KeyValuePair<string, object?>("call_site", "fees.dispatch"));
                                _logger.LogWarning(
                                    "Dropping FeeAccruedEvent for {ClOrdId} on WAL backpressure; applying fee directly to keeper.",
                                    lookupId);
                                keeper.Apply(feeEvt);
                            }
                        }
                    }
                    // Q2.4 (#271). Realized P&L. Compute the delta from
                    // the pre-fill (qty, avg) snapshot captured above
                    // and advance the avg-cost basis tracker. Same
                    // live/replay split as fees:
                    //
                    //   Live:    advance avg-cost basis, then Append
                    //            RealizedPnlEvent + PnlKeeper.Apply
                    //            under the dispatcher lock — single
                    //            ER@N, Pnl@N+k ordering preserved.
                    //   Replay:  defer a pending synth via the pre-fill
                    //            snapshot. A durable RealizedPnlEvent
                    //            following in the WAL supersedes it via
                    //            Apply(RealizedPnlEvent); FinalizeReplay
                    //            materialises any survivor (true
                    //            ER-then-crash window).
                    //
                    // Same WalBackpressureException swallow policy as
                    // the fees branch: in-memory state stays accurate,
                    // audit event is dropped (surfaced as a metric).
                    if (_pnlKeeper is not null)
                    {
                        var nowUtcPnl = eventTimestampUtc ?? DateTimeOffset.UtcNow;
                        var executionIdPnl = lookupId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + ":" + order.CumulativeQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (isReplay || _dispatcher is null)
                        {
                            // Mirror the live-path guard: only register
                            // a synth when the fill would produce a
                            // non-zero realized delta. Opening fills
                            // and same-side adds emit no event in live
                            // mode, so they have nothing to reconcile
                            // — registering them would only inflate
                            // the FinalizeReplay materialisation count.
                            //
                            // PR #316 P1.2. (preFillQty, preFillAvg) is
                            // the BUCKET-of-the-fill's pre-fill state
                            // (master vs sub), so the would-realize
                            // gate and synth payload match what live
                            // would emit. The synth carries the
                            // originating SubAccountId so FinalizeReplay
                            // can fold the materialised delta into the
                            // per-bucket realised total in
                            // SubAccountPnlKeeper as well — without
                            // this, a sub-bucket realised delta whose
                            // RealizedPnlEvent did not survive the
                            // ER-then-crash window would leak into the
                            // aggregate keeper only.
                            var wouldRealize = PnlKeeper.ComputeRealizedDelta(preFillQty, preFillAvg, order.Side, delta, lastPx);
                            if (wouldRealize != 0m)
                            {
                                _pnlKeeper.RegisterPendingReplaySynth(
                                    order.FirmId, executionIdPnl, owner.Value, order.Symbol, order.Side,
                                    delta, lastPx, nowUtcPnl, preFillQty, preFillAvg,
                                    order.SubAccountId?.Value);
                            }
                            // Still advance the basis tracker on replay
                            // — the durable event Apply path uses
                            // RunningTotal directly, so basis is purely
                            // in-memory state used for live computation.
                            // PR #316 P2. Aggregate basis stays in
                            // lockstep with PositionKeeper; bucket
                            // basis is advanced separately so a future
                            // live close after replay uses the right
                            // segregated basis.
                            _pnlKeeper.ApplyFillToAvgCost(order.FirmId, owner.Value, order.Symbol, order.Side, delta, lastPx);
                            _subAccountPnl?.ApplyBucketFill(order.FirmId, owner.Value, order.SubAccountId, order.Symbol, order.Side, delta, lastPx);
                        }
                        else
                        {
                            // Pass-2 review (#278) P1#1. The compose-
                            // realized → advance basis → Append +
                            // Apply sequence runs under the dispatcher
                            // lock for both incoming paths:
                            //
                            //   * normal live: the ER router calls
                            //     EventDispatcher.Dispatch which holds
                            //     the lock for the whole apply
                            //     callback (the lock is reentrant, so
                            //     the nested Dispatch below for the
                            //     RealizedPnlEvent re-acquires it on
                            //     the same thread);
                            //   * WAL backpressure: the ER router's
                            //     fallback wraps the processor's
                            //     Apply in EventDispatcher.RunExclusive
                            //     so the same serialisation discipline
                            //     applies (no WAL append, just the
                            //     in-memory mutations + nested
                            //     fee/PnL Dispatch calls).
                            //
                            // No per-key lock is taken — the previous
                            // design's per-key lock created an AB-BA
                            // inversion against the dispatcher lock on
                            // the fallback path. Dispatcher
                            // serialisation is sufficient because all
                            // live ER processing flows through it.
                            var dayKey = DateOnly.FromDateTime(nowUtcPnl.UtcDateTime);
                            // PR #316 P2. The aggregate keeper still
                            // advances its basis (consumed by legacy
                            // statement paths, the live snapshot
                            // mirror, and the no-sub-account fallback
                            // pre-fill read above), but its returned
                            // delta is NOT the event source. The
                            // authoritative realized delta is computed
                            // against the bucket-of-the-fill's own
                            // basis via the sub-account keeper — so a
                            // sub-bucket close against an aggregate
                            // position dominated by the master bucket
                            // realises against the SUB's basis, not
                            // the master's, satisfying the
                            // "fill in sub-account A increments only
                            // A's P&L" contract. When _subAccountPnl
                            // is not wired (legacy test contexts) we
                            // fall back to the aggregate delta so
                            // pre-#316 ER processor tests keep
                            // passing unchanged.
                            var aggregateRealized = _pnlKeeper.ApplyFillToAvgCost(order.FirmId, owner.Value, order.Symbol, order.Side, delta, lastPx);
                            var realized = _subAccountPnl is not null
                                ? _subAccountPnl.ApplyBucketFill(order.FirmId, owner.Value, order.SubAccountId, order.Symbol, order.Side, delta, lastPx)
                                : aggregateRealized;
                            if (realized != 0m)
                            {
                                var prevTotal = _pnlKeeper.GetDayRealized(order.FirmId, owner.Value, order.Symbol, dayKey);
                                var running = prevTotal + realized;
                                var pnlEvt = new Persistence.RealizedPnlEvent
                                {
                                    ClOrdId = lookupId,
                                    ExecutionId = executionIdPnl,
                                    EndClientId = owner.Value,
                                    Symbol = order.Symbol,
                                    DayKey = dayKey,
                                    DeltaRealized = realized,
                                    RunningTotal = running,
                                    TimestampUtc = nowUtcPnl,
                                    // PR #316 P1. Firm namespace for owner-keyed
                                    // PnL fan-out. Always populated from the order
                                    // (no longer conditional on SubAccountId) so a
                                    // snapshot+tail recovery routes the realized
                                    // delta into the correct firm bucket — required
                                    // so the same JWT sub registered in two firms
                                    // doesn't see cross-firm realized leaking on
                                    // GET /pnl/today or pnl.me.
                                    SubAccountId = order.SubAccountId?.Value,
                                    FirmId = order.FirmId,
                                };
                                var keeperPnl = _pnlKeeper;
                                var subPnl = _subAccountPnl;
                                var subTag = order.SubAccountId;
                                var firmTag = order.FirmId;
                                try
                                {
                                    _dispatcher.Dispatch(pnlEvt, () =>
                                    {
                                        keeperPnl.Apply(pnlEvt);
                                        if (subTag is { } saInner)
                                            subPnl?.Add(firmTag, owner.Value, saInner, order.Symbol, dayKey, realized);
                                    });
                                    MetricsRegistry.PnlRealizedAppended.Add(1,
                                        new KeyValuePair<string, object?>("firmId", firmTag));
                                }
                                catch (Persistence.WalBackpressureException)
                                {
                                    MetricsRegistry.WalBackpressure.Add(1,
                                        new KeyValuePair<string, object?>("call_site", "pnl.dispatch"),
                                        new KeyValuePair<string, object?>("firmId", firmTag));
                                    _logger.LogWarning(
                                        "Dropping RealizedPnlEvent for {ClOrdId} on WAL backpressure; applying realized pnl directly to keeper.",
                                        lookupId);
                                    keeperPnl.Apply(pnlEvt);
                                    if (subTag is { } saInner2)
                                        subPnl?.Add(firmTag, owner.Value, saInner2, order.Symbol, dayKey, realized);
                                }
                            }
                        }
                    }
                    // Release reserved margin against the actual booked
                    // delta — not the wire lastQty — so a lost
                    // intermediate ER can't leave the ledger under-released.
                    _margin.OnExecution(lookupId, kind, delta);
                    break;
                }
            case ExecKind.Canceled:
                if (order.Status is OrderStatus.Cancelled or OrderStatus.Filled or OrderStatus.Rejected or OrderStatus.Replaced)
                {
                    MetricsRegistry.ExecutionReportsReplayDeduped.Add(1, KindTag(kind));
                    _logger.LogDebug("Dropping Cancelled ER for {ClOrdId}; order already {Status}.", lookupId, order.Status);
                    return;
                }
                order.MarkCancelled();
                _margin.OnExecution(lookupId, kind, 0);
                // Pass-4 review (#299) P1. If a pass-1 ambiguous-send
                // left a still-held replace intent keyed by THIS orig
                // (because the gateway dispatch threw post-Prepare),
                // and the venue ultimately Canceled the orig (i.e.
                // dropped both the orig and the never-acked
                // replacement), we must release the held upsize-delta
                // reservation NOW — otherwise it sits until the TTL
                // sweep fires. Clearing the intent here also prevents
                // a stray late ER (under the never-created new
                // ClOrdID) from being misinterpreted as a replace
                // confirmation. Safe to call regardless of whether
                // an intent existed; no-op when none did.
                if (_replacements is not null
                    && _replacements.TryConsumeByOriginal(lookupId, out var canceledOrigIntent, out _)
                    && canceledOrigIntent is not null)
                {
                    _replaceMargin?.AbortReplace(canceledOrigIntent.NewClOrdId);
                    _logger.LogInformation(
                        "event=order.replace.dropped_on_orig_cancel newClOrdId={NewClOrdId} origClOrdId={OrigClOrdId} owner={Owner} symbol={Symbol}; releasing held upsize-delta reservation.",
                        canceledOrigIntent.NewClOrdId, canceledOrigIntent.OriginalClOrdId, canceledOrigIntent.Owner.Value, canceledOrigIntent.Symbol);
                }
                break;
            case ExecKind.Rejected:
                if (order.Status is OrderStatus.Rejected or OrderStatus.Filled or OrderStatus.PartiallyFilled or OrderStatus.Cancelled or OrderStatus.Replaced)
                {
                    MetricsRegistry.ExecutionReportsReplayDeduped.Add(1, KindTag(kind));
                    _logger.LogDebug("Dropping Rejected ER for {ClOrdId}; order already {Status}.", lookupId, order.Status);
                    return;
                }
                order.MarkRejected();
                _margin.OnExecution(lookupId, kind, 0);
                break;
            case ExecKind.Replaced:
                // Slice 2 of #122 handles replace acks via the early
                // intercept above (consumes a PendingReplacementRegistry
                // intent). Falling through here means no intent was
                // tracked — either we're in a test context with no
                // registry wired, or we received an unsolicited Replaced
                // ER. Original behavior: leave the original alone.
                break;
        }

        // Slice 1 of #132. A real terminal ER means the venue actually
        // still knew this order — any prior advisory stale flag was a
        // false positive. Lift it as a side-effect; replay reconstructs
        // the same end state because the OrderStaledEvent is applied
        // first and this terminal ER arrives later in the same WAL
        // stream. Partial fills do NOT clear (the venue may know the
        // original child but the trader's worry that the rest is
        // ghosted is still valid).
        if (order.IsStale && order.Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Replaced)
        {
            order.ClearStale();
        }

        // Q1.3 (#255). GTD scheduler bookkeeping: drop tracked orders
        // whose lifecycle just ended, regardless of whether they
        // ended via venue cancel, fill, reject, or replace. Cheap
        // no-op for orders the scheduler is not tracking.
        if (order.Status is OrderStatus.Filled or OrderStatus.Cancelled
            or OrderStatus.Rejected or OrderStatus.Replaced)
        {
            _gtdScheduler?.OnOrderTerminal(lookupId);
            // #351 — Cancel the IOC/FOK watchdog timer (if any). The
            // expected happy-path: a fill / cancel / reject ER lands
            // within the watchdog timeout, this hook disposes the
            // pending timer, and the synthetic Cancel never fires.
            // Cheap no-op for non IOC/FOK orders.
            _iocWatchdog?.OnOrderTerminal(lookupId);
        }

        // Server-side STP detection (#117): if the matching engine
        // emitted a cancel with a SelfTradePrevention restatement
        // reason, mark the outbound event so the UI/logs can surface
        // it differently from generic cancels. Native STP is the
        // ONLY layer that can catch a self-cross within the gateway
        // dispatch race window; this surfacing is what makes the
        // event actionable for diagnostics.
        var isNativeStp = kind == ExecKind.Canceled
            && NativeStpDetector.IsNativeStpReason(rejectReason);
        if (isNativeStp)
        {
            _logger.LogInformation(
                "event=stp.native.cancel clOrdId={ClOrdId} owner={Owner} symbol={Symbol} side={Side} reason={Reason}",
                lookupId, owner.Value, order.Symbol, order.Side, rejectReason);
        }

        var ev = new ExecutionEvent(
            owner,
            lookupId,
            order.Symbol,
            order.Side,
            order.Status,
            kind,
            order.LeavesQuantity,
            order.CumulativeQuantity,
            lastQty,
            lastPx,
            rejectReason,
            // Pass-2 P2 (#324). Honour the durable WAL timestamp on
            // replay so legacy fills (no BookTouch, capturedAt falls
            // back to record.TimestampUtc on the REST surface) keep
            // their original execution time across restart. Live
            // dispatch passes null → UtcNow as before.
            eventTimestampUtc ?? DateTimeOffset.UtcNow,
            isNativeStp,
            order.FirmId,
            // Q4.7 (#307). Only fills carry a book-touch snapshot —
            // cancels / rejects / news leave the field null on the wire.
            BookTouch: kind is ExecKind.Fill or ExecKind.PartialFill ? bookTouch : null);

        // Q4.7 (#307). Fold the fill into the in-memory projection so
        // GET /fills/{id}/touch can read it back. Runs on both live
        // dispatch and WAL replay (the latter passes the BookTouch
        // hydrated from the WAL ER event) so cold restart preserves
        // every fill's touch evidence without a separate snapshot
        // section.
        if (kind is ExecKind.Fill or ExecKind.PartialFill && lastQty > 0)
        {
            _fillProjection?.Record(
                clOrdId: lookupId,
                cumulativeQuantityAfterFill: order.CumulativeQuantity,
                owner: owner,
                firmId: order.FirmId,
                symbol: order.Symbol,
                side: order.Side,
                lastQuantity: lastQty,
                lastPrice: lastPx,
                timestampUtc: ev.TimestampUtc,
                bookTouch: bookTouch);
        }
        // RFC §5.2 (F2). Capture-then-fan-out path: the dispatcher walks
        // the writer and TryWrites into every per-sink channel while still
        // under the dispatcher lock so subscribers observe events in WAL
        // seq order; actual Publish/Route work happens on each sink's
        // drain thread (off-lock). When the writer is null we fall back
        // to the legacy synchronous publish path used by tests that drive
        // the processor without a dispatcher.
        if (fanOut is not null)
        {
            fanOut.Add(ev);
        }
        else
        {
            _sink.Publish(ev);
            _botErRouter?.Route(ev);
        }

        // Algo engine hook: signal AFTER fan-out so the engine reactor
        // sees the same world the WS subscribers see, and so the dispatch
        // path (which may be holding internal locks) is fully unwound
        // before the engine's per-parent semaphore is acquired
        // (RFC algo-orders-v0 §4.3).
        if (_algoSignals is not null
            && order.ParentAlgoId is { } parentAlgoId
            && !string.IsNullOrEmpty(order.FirmId))
        {
            var enqueued = _algoSignals.TryEnqueue(new ChildExecutionObservedSignal
            {
                FirmId = order.FirmId,
                AlgoId = parentAlgoId,
                ChildClOrdId = lookupId,
            });
            if (!enqueued)
            {
                MetricsRegistry.AlgoSignalsDropped.Add(1,
                    new KeyValuePair<string, object?>("kind", "child_er"));
                _logger.LogWarning(
                    "Algo signal queue full; dropped child_er signal for parent {AlgoId} child {ClOrdId}.",
                    parentAlgoId, lookupId);
            }
        }
    }

    private static KeyValuePair<string, object?> KindTag(ExecKind kind) =>
        new("kind", kind.ToString());

    private void ApplyReplaceRejected(ulong newClOrdId, OrderReplacementIntent intent, string? rejectReason, Persistence.ExecutionFanOut? fanOut)
    {
        // Replace-reject: original order is untouched (continues
        // Working / PartiallyFilled), pending margin delta is released.
        _replaceMargin?.AbortReplace(newClOrdId);
        _logger.LogInformation(
            "event=order.replace.rejected newClOrdId={NewClOrdId} origClOrdId={OrigClOrdId} owner={Owner} symbol={Symbol} reason={Reason}",
            newClOrdId, intent.OriginalClOrdId, intent.Owner.Value, intent.Symbol, rejectReason ?? "(none)");

        // Sub-issue #172 (F): the bot owns the cancel/replace ClOrdID
        // it issued and must see a terminal Reject for it. Synthesise
        // a minimal ExecutionEvent — there is no Order in the book for
        // the replace-side ClOrdID by design (replace requests don't
        // create an order until accepted) so this event is meaningful
        // only to the bot router; tagging with BotRouter prevents the
        // WS hub channel from receiving an event for a ClOrdID its
        // orders.me view has no record of (RFC §5.2 / §6.3).
        var rejectedEv = new ExecutionEvent(
            intent.Owner,
            newClOrdId,
            intent.Symbol,
            intent.Side,
            OrderStatus.Rejected,
            ExecKind.Rejected,
            LeavesQuantity: 0,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: rejectReason,
            TimestampUtc: DateTimeOffset.UtcNow,
            IsNativeStp: false,
            FirmId: intent.FirmId);
        if (fanOut is not null)
        {
            fanOut.Add(rejectedEv, Persistence.ExecutionFanOutTargets.BotRouter);
        }
        else if (_botErRouter is not null)
        {
            _botErRouter.Route(rejectedEv);
        }

        // #381. The replace-side BotRouter event above intentionally
        // does NOT reach the WS hub (orders.me has no record of the
        // replace-side ClOrdID). But the operator who clicked Modify
        // on the *original* ClOrdID has every right to know their
        // PUT /orders/{clOrdId} got rejected — without this second
        // event the UI silently re-enables the Modify button as if
        // nothing happened. Emit a discriminated ExecKind.ReplaceRejected
        // scoped to intent.OriginalClOrdId, carrying the original's
        // preserved Leaves/Cumulative so the UI doesn't have to special-
        // case "this is a reject, ignore the quantities". Route to
        // WsHub | DropCopy — never BotRouter, to avoid double-delivery
        // to bots that already received the synthetic Rejected above.
        long origLeaves = 0;
        long origCum = 0;
        var origStatus = OrderStatus.Working;
        if (_orders.TryGet(intent.OriginalClOrdId, out var origOrder) && origOrder is not null)
        {
            origLeaves = origOrder.LeavesQuantity;
            origCum = origOrder.CumulativeQuantity;
            origStatus = origOrder.Status;
        }

        var origVisibleEv = new ExecutionEvent(
            intent.Owner,
            intent.OriginalClOrdId,
            intent.Symbol,
            intent.Side,
            origStatus,
            ExecKind.ReplaceRejected,
            LeavesQuantity: origLeaves,
            CumulativeQuantity: origCum,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: rejectReason,
            TimestampUtc: DateTimeOffset.UtcNow,
            IsNativeStp: false,
            FirmId: intent.FirmId);
        if (fanOut is not null)
        {
            fanOut.Add(origVisibleEv,
                Persistence.ExecutionFanOutTargets.WsHub | Persistence.ExecutionFanOutTargets.DropCopy);
        }
        else
        {
            // Test / legacy path with no fan-out registry: surface via
            // the direct sink. BotRouter is deliberately not invoked —
            // bots get the replace-side Rejected event above.
            _sink.Publish(origVisibleEv);
        }
    }

    private void ApplyReplaceAccepted(
        ulong newClOrdId,
        long erLeaves,
        long erCum,
        decimal erLastPx,
        ulong erOrigClOrdId,
        OrderReplacementIntent intent,
        Persistence.ExecutionFanOut? fanOut)
    {
        var origId = erOrigClOrdId != 0 ? erOrigClOrdId : intent.OriginalClOrdId;

        if (!_orders.TryGet(origId, out var origOrder) || origOrder is null)
        {
            _logger.LogWarning(
                "Replaced ER for new ClOrdID {NewClOrdId} but original {OrigClOrdId} not found in book; aborting margin transfer.",
                newClOrdId, origId);
            _replaceMargin?.AbortReplace(newClOrdId);
            return;
        }

        // 1) Terminalize the original. MarkReplaced is idempotent and
        //    refuses to regress true terminal states (Filled / Rejected
        //    / Cancelled), so a Replaced ack racing a final fill keeps
        //    the original at its real terminal status.
        var originalAlreadyTerminal = origOrder.Status is OrderStatus.Filled
            or OrderStatus.Rejected
            or OrderStatus.Cancelled
            or OrderStatus.Replaced;
        origOrder.MarkReplaced();
        // Slice 1 of #132: terminalising clears any advisory stale.
        if (origOrder.IsStale)
            origOrder.ClearStale();
        // Q1.3 (#255): drop tracked GTD entry for the original now
        // that it is terminal (the replacement may or may not carry
        // GTD; OnOrderTracked re-adds it after TryAdd below).
        _gtdScheduler?.OnOrderTerminal(origId);

        var origEv = new ExecutionEvent(
            intent.Owner,
            origId,
            origOrder.Symbol,
            origOrder.Side,
            origOrder.Status,
            ExecKind.Replaced,
            origOrder.LeavesQuantity,
            origOrder.CumulativeQuantity,
            0,
            0m,
            null,
            DateTimeOffset.UtcNow,
            false,
            intent.FirmId);
        if (fanOut is not null)
        {
            fanOut.Add(origEv);
        }
        else
        {
            _sink.Publish(origEv);
            // Sub-issue #172 (F): the bot must observe the original's
            // terminalisation as part of the replace ack stream.
            _botErRouter?.Route(origEv);
        }

        // 2) Hydrate the replacement with intent metadata + venue's
        //    cum/leaves baseline. Existing fills booked under the
        //    original are NOT re-booked — PositionKeeper already saw
        //    them. The cum/leaves on the new Order exists so subsequent
        //    fill ERs (now arriving under newClOrdID) advance from the
        //    correct baseline.
        //
        // Pass-2 review (#299) P1. Translators default missing CumQty
        // to 0 (B3EntryPointClientGateway OrderModified arm, simulator
        // /admin/simulator/er Replaced arm). If the venue/sim sends a
        // Replaced ER with stale or zero CumQty AFTER the original
        // accumulated fills, hydrating the replacement with that low
        // baseline causes the very next Fill ER for the new ClOrdID to
        // be diffed against an under-seeded prevBooked in the algo
        // engine and re-book the original's prior fills against the
        // parent. Clamp the seed cum upward to the original's cum and
        // adjust leaves down so the (cum + leaves == newQty) invariant
        // holds across the seam.
        var seedCum = erCum;
        var seedLeaves = erLeaves;
        if (seedCum < origOrder.CumulativeQuantity)
        {
            _logger.LogWarning(
                "Replaced ER for new ClOrdID {NewClOrdId} carried stale CumQty={ErCum} below original CumQty={OrigCum}; clamping to original to avoid re-booking prior fills.",
                newClOrdId, erCum, origOrder.CumulativeQuantity);
            seedCum = origOrder.CumulativeQuantity;
            seedLeaves = Math.Max(0L, intent.NewQuantity - seedCum);
        }
        Order newOrder;
        try
        {
            newOrder = Order.HydrateReplacement(
                origOrder, newClOrdId, intent.NewQuantity, intent.NewPrice, seedLeaves, seedCum,
                intent.RequestedTimeInForce, intent.RequestedStopPrice, intent.RequestedGoodTillDate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to hydrate replacement for new ClOrdID {NewClOrdId}; aborting margin.", newClOrdId);
            _replaceMargin?.AbortReplace(newClOrdId);
            return;
        }

        if (!_orders.TryAdd(newOrder))
        {
            _logger.LogWarning(
                "Replacement order {NewClOrdId} already in book; aborting (duplicate Replaced ER?).", newClOrdId);
            _replaceMargin?.AbortReplace(newClOrdId);
            return;
        }

        // Q1.3 (#255). The replacement may carry a different TIF/GTD
        // than the original. Notify the scheduler so a TIF change to
        // GTD starts tracking, a TIF change away from GTD stops
        // tracking the old entry (the original was already terminal-
        // cleared via OnOrderTerminal at the top of Apply when we
        // marked it Replaced), and a same-TIF change re-arms the
        // timer with the new expiry.
        _gtdScheduler?.OnOrderTracked(newOrder);

        // 3) Margin commit: rebalance to venue-confirmed remaining.
        //    For Buy + margin-bearing type (Limit / StopLimit /
        //    MarketWithLeftover) + cash, that's intent.NewPrice * leaves;
        //    everything else is zero (the coordinator no-ops on 0).
        //    Pass-2 review (#299) P1: use the clamped seedLeaves so the
        //    margin reservation matches the order book's leaves after
        //    the cum-stale clamp above (avoids over-reserving margin
        //    for residue that has already filled at the venue).
        var confirmedRemaining = (intent.Side == OrderSide.Buy
                                  && intent.Type.IsMarginBearing()
                                  && intent.NewPrice is { } px
                                  && seedLeaves > 0)
            ? px * seedLeaves
            : 0m;
        _replaceMargin?.CommitReplace(intent.OriginalClOrdId, newClOrdId, confirmedRemaining);

        // 4) Publish event for the new order — same shape as a New ack
        //    so WS subscribers see the order appear in the blotter.
        var newEv = new ExecutionEvent(
            intent.Owner,
            newClOrdId,
            newOrder.Symbol,
            newOrder.Side,
            newOrder.Status,
            ExecKind.Replaced,
            newOrder.LeavesQuantity,
            newOrder.CumulativeQuantity,
            0,
            erLastPx,
            null,
            DateTimeOffset.UtcNow,
            false,
            intent.FirmId);
        if (fanOut is not null)
        {
            fanOut.Add(newEv);
        }
        else
        {
            _sink.Publish(newEv);
            _botErRouter?.Route(newEv);
        }

        // 5) Algo-engine signal: replacement is, for the engine's
        //    purposes, an execution observation on the parent. Mirrors
        //    the bottom-of-method logic for normal ERs.
        if (_algoSignals is not null
            && intent.ParentAlgoId is { } parentAlgoId
            && !string.IsNullOrEmpty(intent.FirmId))
        {
            var enqueued = _algoSignals.TryEnqueue(new ChildExecutionObservedSignal
            {
                FirmId = intent.FirmId,
                AlgoId = parentAlgoId,
                ChildClOrdId = newClOrdId,
            });
            if (!enqueued)
            {
                MetricsRegistry.AlgoSignalsDropped.Add(1,
                    new KeyValuePair<string, object?>("kind", "child_er"));
            }
        }

        _ = originalAlreadyTerminal; // currently observational; future metric hook.
    }
}

/// <summary>
/// Wire-agnostic execution kind. Mirrors the EntryPoint enum on the
/// Infrastructure side; declared in Application so the domain stays
/// independent of the wire library's types.
/// </summary>
public enum ExecKind
{
    New,
    PartialFill,
    Fill,
    Canceled,
    Replaced,
    Rejected,
    /// <summary>
    /// Slice 5 of #132. Synthetic notification emitted by
    /// <see cref="OrderStalenessService"/> when an order is flagged as
    /// suspected-stale-by-venue (admin path or auto-detect on FIXP
    /// peer desync). Not produced by the venue — it carries
    /// <c>LastQuantity=0</c>, no fill price, and only changes the
    /// advisory <c>IsStale</c> overlay on the order. Downstream
    /// consumers (UI executions log, future risk/positions
    /// projections) treat it as a state-change ping, not as an
    /// economic event.
    /// </summary>
    Suspended,
    /// <summary>
    /// Q1.3 (#255). Synthetic projection of an
    /// <see cref="Persistence.OrderExpiredEvent"/>: the GTD scheduler
    /// determined an order's <c>GoodTillDate</c> has elapsed and
    /// dispatched a cancel against it through the regular
    /// <c>OrderCancelService</c>. The order's actual terminal status
    /// transition still flows from the eventual <see cref="Canceled"/>
    /// ER produced by the cancel pipeline; this synthetic event lets
    /// WS subscribers see <c>kind=Expired</c> alongside the regular
    /// <c>kind=Canceled</c> so the UI can distinguish a venue cancel
    /// from a policy expiry. Carries <c>LastQuantity=0</c> and no
    /// fill price (no economic event).
    /// </summary>
    Expired,
    /// <summary>
    /// Slice 5 of #132. Synthetic counterpart of <see cref="Suspended"/>:
    /// emitted when the stale overlay is lifted (admin clear path). The
    /// auto-clear branch in <see cref="ExecutionReportProcessor"/> does
    /// NOT publish a separate Restored event — it lifts the stale flag
    /// as a side-effect of the genuine ER (Filled/Canceled/Rejected/
    /// Replaced) which already broadcasts; emitting both would
    /// double-update consumers for the same observation.
    /// </summary>
    Restored,
    /// <summary>
    /// #381. Synthetic projection of a venue-side replace-reject (FIXP
    /// <c>OrderCancelReplaceRequest</c> rejected, surfaced as ER kind
    /// <see cref="Rejected"/> against the replace-side ClOrdID and
    /// intercepted by the <c>PendingReplacementRegistry</c> consumer).
    /// The original order is untouched (status stays Working /
    /// PartiallyFilled) but the operator who issued the
    /// <c>PUT /orders/{clOrdId}</c> must know the modify failed.
    /// <para>
    /// Routing: the <see cref="Rejected"/> event for the *replace-side*
    /// ClOrdID continues to flow to <c>BotRouter</c> only (per #172 F:
    /// the WS hub has no <c>orders.me</c> record for a ClOrdID that
    /// never opened an order). The <c>ReplaceRejected</c> event scoped
    /// to <c>intent.OriginalClOrdId</c> targets <c>WsHub | DropCopy</c>
    /// — never <c>BotRouter</c>, to avoid double-delivery to bots that
    /// already received the <see cref="Rejected"/> event via the
    /// existing path.
    /// </para>
    /// Carries the original's preserved <c>LeavesQuantity</c> /
    /// <c>CumulativeQuantity</c> and the venue's reject reason; no
    /// fill price (no economic event).
    /// </summary>
    ReplaceRejected,
}
