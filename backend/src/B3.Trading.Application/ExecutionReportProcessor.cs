
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
    private readonly Persistence.EventDispatcher? _dispatcher;
    private readonly PendingReplacementRegistry? _replacements;
    private readonly Risk.IReplaceMarginCoordinator? _replaceMargin;
    private readonly IBotErRouter? _botErRouter;
    private readonly Scheduling.GtdExpirationScheduler? _gtdScheduler;

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
        Persistence.EventDispatcher? dispatcher = null)
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
    public void Apply(ulong clOrdId, ExecKind kind, long leaves, long cumQty, long lastQty, decimal lastPx, string? rejectReason, ulong origClOrdId = 0, Persistence.ExecutionFanOut? fanOut = null, bool isReplay = false, DateTimeOffset? eventTimestampUtc = null)
    {
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
                    _positions.ApplyFill(owner, order.Symbol, order.Side, delta, lastPx);
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
            DateTimeOffset.UtcNow,
            isNativeStp);
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
            IsNativeStp: false);
        if (fanOut is not null)
        {
            fanOut.Add(rejectedEv, Persistence.ExecutionFanOutTargets.BotRouter);
        }
        else if (_botErRouter is not null)
        {
            _botErRouter.Route(rejectedEv);
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
            false);
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
        Order newOrder;
        try
        {
            newOrder = Order.HydrateReplacement(
                origOrder, newClOrdId, intent.NewQuantity, intent.NewPrice, erLeaves, erCum,
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
        var confirmedRemaining = (intent.Side == OrderSide.Buy
                                  && intent.Type.IsMarginBearing()
                                  && intent.NewPrice is { } px
                                  && erLeaves > 0)
            ? px * erLeaves
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
            false);
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
}
