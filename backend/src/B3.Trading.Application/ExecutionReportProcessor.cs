
using B3.Trading.Application.Observability;
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

    public ExecutionReportProcessor(
        OrderOwnershipMap ownership,
        WorkingOrderBook orders,
        PositionKeeper positions,
        IExecutionEventSink sink,
        Risk.IMarginProvider margin,
        ILogger<ExecutionReportProcessor> logger,
        IAlgoSignalQueue? algoSignals = null,
        CashLedger? cash = null)
    {
        _ownership = ownership;
        _orders = orders;
        _positions = positions;
        _sink = sink;
        _margin = margin;
        _logger = logger;
        _algoSignals = algoSignals;
        _cash = cash;
    }

    /// <summary>
    /// <paramref name="origClOrdId"/> is non-zero on cancel-ack and modify-ack.
    /// When set, the processor mutates the order identified by that ID
    /// (the original order) — the cancel/replace request itself uses a
    /// fresh ClOrdID that has no in-memory order behind it.
    /// </summary>
    public void Apply(ulong clOrdId, ExecKind kind, long leaves, long cumQty, long lastQty, decimal lastPx, string? rejectReason, ulong origClOrdId = 0)
    {
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
            _logger.LogWarning("ER for known owner {Owner} but missing order {ClOrdId} (orig={Orig}); dropping.", owner, clOrdId, origClOrdId);
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
                // Re-issuance: the gateway is responsible for calling
                // OrderOwnershipMap.RegisterReplacement first; here we
                // just leave the original order alone — the new ClOrdID
                // already has its own Order.
                break;
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

        _sink.Publish(new ExecutionEvent(
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
            isNativeStp));

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
}
