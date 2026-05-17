using B3.Trading.Domain;

using B3.Trading.Application;

namespace B3.Trading.Infrastructure;

/// <summary>
/// <see cref="IExchangeGateway"/> implementation that translates domain
/// commands into <see cref="IEntryPointClient"/> calls. The reverse
/// direction (ER → domain) is handled by
/// <see cref="EntryPointExecutionReportRouter"/>.
///
/// Thin adapter — no ClOrdID allocation, no risk decisions; the caller
/// passes a fully-formed <see cref="Order"/> with a registry-issued
/// ClOrdID + securityId so policy stays in one place.
/// </summary>
public sealed class EntryPointClientGateway : IExchangeGateway
{
    private readonly IEntryPointClient _client;
    private readonly string _firmId;

    public EntryPointClientGateway(IEntryPointClient client, string firmId)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _firmId = firmId ?? throw new ArgumentNullException(nameof(firmId));
    }

    public Task SubmitAsync(Order order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        var req = new NewOrderSingle(
            order.ClOrdId,
            order.SecurityId,
            order.Symbol,
            order.Side == OrderSide.Buy ? EpSide.Buy : EpSide.Sell,
            order.Type == OrderType.Limit ? EpOrderType.Limit : EpOrderType.Market,
            order.Quantity,
            order.Price,
            _firmId,
            // Q3.4 (#284). Plumb DisplayQty as MaxFloor through the
            // mock seam so tests can pin wire mapping. The real SDK
            // path in B3EntryPointClientGateway maps the same field
            // to UpModels.NewOrderRequest.MaxFloor.
            MaxFloor: order.DisplayQty);

        return _client.SubmitNewOrderAsync(req, cancellationToken);
    }

    public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        return _client.SubmitCancelAsync(
            new OrderCancelRequest(
                newClOrdId,
                order.ClOrdId,
                order.SecurityId,
                order.Side == OrderSide.Buy ? EpSide.Buy : EpSide.Sell,
                _firmId),
            cancellationToken);
    }

    public Task CancelReplaceAsync(
        Order original, ulong newClOrdId, long newQuantity, decimal? newPrice,
        TimeInForce? requestedTimeInForce, decimal? requestedStopPrice, DateTimeOffset? requestedGoodTillDate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        // Legacy adapter: OrderCancelReplaceRequest predates Q1.1 and
        // does not carry TIF / StopPrice / GoodTillDate over the wire.
        // The Q1.1 modify pipeline targets the B3EntryPointClientGateway
        // (real adapter); this path is retained for the older
        // IEntryPointClient seam used by tests and the demo driver.
        return _client.SubmitCancelReplaceAsync(
            new OrderCancelReplaceRequest(
                original.ClOrdId, newClOrdId, original.SecurityId,
                original.Side == OrderSide.Buy ? EpSide.Buy : EpSide.Sell,
                newQuantity, newPrice, _firmId,
                // Q3.4 (#284). Replace inherits the original's visible
                // portion (clamped to newQuantity when the new order qty
                // would otherwise be < DisplayQty), mirroring the real
                // SDK path in B3EntryPointClientGateway.CancelReplaceAsync.
                MaxFloor: original.DisplayQty is { } odq
                    ? Math.Min(odq, newQuantity)
                    : (long?)null),
            cancellationToken);
    }
}
