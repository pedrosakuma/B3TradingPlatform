using B3.Trading.Application;

namespace B3.Trading.Api.WebSockets;

/// <summary>
/// Real <see cref="IExecutionEventSink"/> backed by the WebSocket
/// <see cref="SubscriptionManager"/>. Routes a single
/// <see cref="ExecutionEvent"/> to all impacted channels for the owner.
/// </summary>
public sealed class WebSocketExecutionEventSink : IExecutionEventSink
{
    private readonly SubscriptionManager _subs;
    private readonly WorkingOrderBook _orders;
    private readonly PositionKeeper _positions;

    public WebSocketExecutionEventSink(SubscriptionManager subs, WorkingOrderBook orders, PositionKeeper positions)
    {
        _subs = subs;
        _orders = orders;
        _positions = positions;
    }

    public void Publish(ExecutionEvent ev)
    {
        if (_subs.CountFor(ev.Owner) == 0)
            return;

        // executions.me — every ER becomes an execution event.
        _subs.Publish(ev.Owner, Channels.ExecutionsMe, ev.ToDto());

        // orders.me — current order state after mutation.
        if (_orders.TryGet(ev.ClOrdId, out var order) && order is not null)
            _subs.Publish(ev.Owner, Channels.OrdersMe, order.ToDto());

        // positions.me — only fills affect positions.
        if (ev.Kind is ExecKind.Fill or ExecKind.PartialFill && ev.LastQuantity > 0)
        {
            var position = _positions.GetOrCreate(ev.Owner, ev.Symbol);
            _subs.Publish(ev.Owner, Channels.PositionsMe, position.ToDto());
        }
    }
}
