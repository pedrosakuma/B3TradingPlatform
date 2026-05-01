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

    public StateSnapshotter(
        WorkingOrderBook orders,
        PositionKeeper positions,
        KillSwitchService killSwitch,
        ClOrdIdPrefixRegistry clOrdIds,
        OrderOwnershipMap ownership)
    {
        _orders = orders;
        _positions = positions;
        _killSwitch = killSwitch;
        _clOrdIds = clOrdIds;
        _ownership = ownership;
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
    };

    public void Restore(PlatformSnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(snap);
        _orders.Restore(snap.WorkingOrders);
        _positions.Restore(snap.Positions);
        _killSwitch.Restore(snap.KilledEndClients, snap.KilledFirms);
        _clOrdIds.Restore(snap.ClOrdIds);
        _ownership.Restore(snap.Ownership);
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

    public EventReplayer(
        WorkingOrderBook orders,
        OrderOwnershipMap ownership,
        KillSwitchService killSwitch,
        ExecutionReportProcessor processor)
    {
        _orders = orders;
        _ownership = ownership;
        _killSwitch = killSwitch;
        _processor = processor;
    }

    public void Apply(WalEvent evt)
    {
        switch (evt)
        {
            case OrderSubmittedEvent o:
                var owner = new EndClientId(o.EndClientId);
                var side = Enum.Parse<OrderSide>(o.Side, ignoreCase: true);
                var type = Enum.Parse<OrderType>(o.Type, ignoreCase: true);
                _orders.TryAdd(new Order(o.ClOrdId, owner, o.Symbol, o.SecurityId, side, type, o.Quantity, o.Price));
                _ownership.Register(o.ClOrdId, owner);
                break;
            case ExecutionReportReceivedEvent er:
                if (Enum.TryParse<ExecKind>(er.ExecKind, ignoreCase: true, out var kind))
                {
                    _processor.Apply(er.ClOrdId, kind, er.LeavesQuantity, er.CumulativeQuantity,
                        er.LastQuantity, er.LastPrice, er.RejectReason);
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
        }
    }
}
