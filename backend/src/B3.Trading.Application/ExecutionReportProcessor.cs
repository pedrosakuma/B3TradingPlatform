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
/// </summary>
public sealed class ExecutionReportProcessor
{
    private readonly OrderOwnershipMap _ownership;
    private readonly WorkingOrderBook _orders;
    private readonly PositionKeeper _positions;
    private readonly IExecutionEventSink _sink;
    private readonly ILogger<ExecutionReportProcessor> _logger;

    public ExecutionReportProcessor(
        OrderOwnershipMap ownership,
        WorkingOrderBook orders,
        PositionKeeper positions,
        IExecutionEventSink sink,
        ILogger<ExecutionReportProcessor> logger)
    {
        _ownership = ownership;
        _orders = orders;
        _positions = positions;
        _sink = sink;
        _logger = logger;
    }

    public void Apply(string clOrdId, ExecKind kind, long leaves, long cumQty, long lastQty, decimal lastPx, string? rejectReason)
    {
        if (!_ownership.TryResolve(clOrdId, out var owner) || owner is null)
        {
            // Unknown ClOrdID is not necessarily a bug — could be an ER
            // for an order owned by an end-client that has since dropped
            // out of memory (ephemeral state, see issue #1 §3). Log and
            // drop; Phase 3 will handle this via ER replay on reconnect.
            _logger.LogWarning("ER for unknown ClOrdID {ClOrdId}; dropping.", clOrdId);
            return;
        }

        if (!_orders.TryGet(clOrdId, out var order) || order is null)
        {
            _logger.LogWarning("ER for known owner {Owner} but missing order {ClOrdId}; dropping.", owner, clOrdId);
            return;
        }

        switch (kind)
        {
            case ExecKind.New:
                order.MarkWorking();
                break;
            case ExecKind.PartialFill:
            case ExecKind.Fill:
                if (lastQty > 0)
                {
                    order.ApplyFill(lastQty);
                    _positions.ApplyFill(owner, order.Symbol, order.Side, lastQty, lastPx);
                }
                break;
            case ExecKind.Canceled:
                order.MarkCancelled();
                break;
            case ExecKind.Rejected:
                order.MarkRejected();
                break;
            case ExecKind.Replaced:
                // Re-issuance: the gateway is responsible for calling
                // OrderOwnershipMap.RegisterReplacement first; here we
                // just leave the original order alone — the new ClOrdID
                // already has its own Order.
                break;
        }

        _sink.Publish(new ExecutionEvent(
            owner,
            clOrdId,
            order.Symbol,
            order.Side,
            order.Status,
            kind,
            order.LeavesQuantity,
            order.CumulativeQuantity,
            lastQty,
            lastPx,
            rejectReason,
            DateTimeOffset.UtcNow));
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
}
