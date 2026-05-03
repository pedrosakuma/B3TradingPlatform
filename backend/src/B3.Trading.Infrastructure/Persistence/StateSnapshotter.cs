using B3.Trading.Application;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;

namespace B3.Trading.Infrastructure.Persistence;

/// <summary>
/// Captures and restores the platform's stateful Application-layer
/// components in a single round-trip. Snapshot capture must run under
/// the <see cref="EventDispatcher"/> lock to be consistent with the
/// recorded WAL seq; restore is single-threaded and runs at startup
/// before the host begins accepting requests.
/// </summary>
public sealed class StateSnapshotter
{
    private readonly WorkingOrderBook _orders;
    private readonly PositionKeeper _positions;
    private readonly KillSwitchService _killSwitch;
    private readonly ClOrdIdPrefixRegistry _clOrdIds;
    private readonly OrderOwnershipMap _ownership;
    private readonly AlgoBook _algos;
    private readonly AlgoIdRegistry _algoIds;

    public StateSnapshotter(
        WorkingOrderBook orders,
        PositionKeeper positions,
        KillSwitchService killSwitch,
        ClOrdIdPrefixRegistry clOrdIds,
        OrderOwnershipMap ownership,
        AlgoBook algos,
        AlgoIdRegistry algoIds)
    {
        _orders = orders;
        _positions = positions;
        _killSwitch = killSwitch;
        _clOrdIds = clOrdIds;
        _ownership = ownership;
        _algos = algos;
        _algoIds = algoIds;
    }

    public PlatformSnapshot Capture(long seq) => new()
    {
        Seq = seq,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        WorkingOrders = _orders.Snapshot().ToList(),
        Positions = _positions.Snapshot().ToList(),
        KilledEndClients = _killSwitch.ListKilledEndClients().ToList(),
        KilledFirms = _killSwitch.ListKilledFirms().ToList(),
        ClOrdIds = _clOrdIds.Snapshot(),
        Ownership = _ownership.Snapshot().ToList(),
        Algos = _algos.Snapshot().ToList(),
        AlgoIds = _algoIds.Snapshot(),
    };

    public void Restore(PlatformSnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(snap);
        _orders.Restore(snap.WorkingOrders);
        _positions.Restore(snap.Positions);
        _killSwitch.Restore(snap.KilledEndClients, snap.KilledFirms);
        _clOrdIds.Restore(snap.ClOrdIds);
        _ownership.Restore(snap.Ownership);
        _algos.Restore(snap.Algos);
        _algoIds.Restore(snap.AlgoIds);
    }
}

/// <summary>
/// Replays a single WAL event onto in-memory state. Used by recovery to
/// bring the world up-to-date past the latest snapshot. No fan-out via
/// <c>IExecutionEventSink</c> happens during replay — there are no
/// subscribers yet at startup, and re-emitting historical ERs would just
/// be noise.
/// </summary>
public sealed class EventReplayer
{
    private readonly WorkingOrderBook _orders;
    private readonly OrderOwnershipMap _ownership;
    private readonly KillSwitchService _killSwitch;
    private readonly ExecutionReportProcessor _processor;
    private readonly AlgoBook _algos;

    public EventReplayer(
        WorkingOrderBook orders,
        OrderOwnershipMap ownership,
        KillSwitchService killSwitch,
        ExecutionReportProcessor processor,
        AlgoBook algos)
    {
        _orders = orders;
        _ownership = ownership;
        _killSwitch = killSwitch;
        _processor = processor;
        _algos = algos;
    }

    public void Apply(WalEvent evt)
    {
        switch (evt)
        {
            case OrderSubmittedEvent o:
                var owner = new EndClientId(o.EndClientId);
                var side = Enum.Parse<OrderSide>(o.Side, ignoreCase: true);
                var type = Enum.Parse<OrderType>(o.Type, ignoreCase: true);
                _orders.TryAdd(new Order(o.ClOrdId, owner, o.Symbol, o.SecurityId, side, type,
                    o.Quantity, o.Price, o.FirmId, o.ParentAlgoId, o.AlgoSliceSeq));
                _ownership.Register(o.ClOrdId, owner);
                // Parent state-machine progression on first child accept is
                // engine-side (slice 5/6); replay only re-creates the order
                // — the parent's Working/Filled state is reconstructed from
                // the child ER stream through the processor below.
                break;
            case ExecutionReportReceivedEvent er:
                if (Enum.TryParse<ExecKind>(er.ExecKind, ignoreCase: true, out var kind))
                {
                    _processor.Apply(er.ClOrdId, kind, er.LeavesQuantity, er.CumulativeQuantity,
                        er.LastQuantity, er.LastPrice, er.RejectReason, er.OrigClOrdId);
                }
                break;
            case KillSwitchToggledEvent k:
                if (k.Scope.Equals("end-client", StringComparison.OrdinalIgnoreCase))
                {
                    if (k.Killed) _killSwitch.KillEndClient(new EndClientId(k.Target));
                    else _killSwitch.ReviveEndClient(new EndClientId(k.Target));
                }
                else if (k.Scope.Equals("firm", StringComparison.OrdinalIgnoreCase))
                {
                    if (k.Killed) _killSwitch.KillFirm(k.Target);
                    else _killSwitch.ReviveFirm(k.Target);
                }
                break;
            case AlgoCreatedEvent ac:
                ApplyAlgoCreated(ac);
                break;
            case AlgoCancelRequestedEvent acr:
                if (_algos.TryGet(acr.FirmId, acr.AlgoId, out var cancelling) && cancelling is not null)
                    cancelling.RequestCancel();
                break;
            case AlgoTerminalStateRecordedEvent at:
                if (_algos.TryGet(at.FirmId, at.AlgoId, out var algo) && algo is not null)
                {
                    var status = Enum.Parse<AlgoStatus>(at.Status, ignoreCase: true);
                    var reason = Enum.Parse<AlgoTerminalReason>(at.Reason, ignoreCase: true);
                    algo.RecordTerminal(status, reason, at.AtUtc);
                }
                break;
        }
    }

    private void ApplyAlgoCreated(AlgoCreatedEvent ac)
    {
        var owner = new EndClientId(ac.EndClientId);
        var side = Enum.Parse<OrderSide>(ac.Side, ignoreCase: true);
        var type = Enum.Parse<AlgoType>(ac.Type, ignoreCase: true);
        AlgoParameters parameters = type switch
        {
            AlgoType.Iceberg => new IcebergParameters(
                ac.IcebergDisplayQuantity ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing IcebergDisplayQuantity."),
                ac.IcebergLimitPrice),
            AlgoType.Twap => new TwapParameters(
                ac.TwapStartUtc ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing TwapStartUtc."),
                ac.TwapEndUtc ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing TwapEndUtc."),
                ac.TwapSliceCount ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing TwapSliceCount."),
                Enum.Parse<OrderType>(ac.TwapChildOrderType ?? throw new InvalidOperationException($"AlgoCreatedEvent {ac.AlgoId} missing TwapChildOrderType."), ignoreCase: true),
                ac.TwapChildPrice),
            _ => throw new InvalidOperationException($"Unknown algo type: {ac.Type}"),
        };
        _algos.TryAdd(new Algo(ac.AlgoId, owner, ac.FirmId, ac.Symbol, ac.SecurityId,
            side, type, ac.TotalQuantity, parameters, ac.CreatedAtUtc));
    }
}
