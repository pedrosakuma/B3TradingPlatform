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
            MaxFloor: order.DisplayQty,
            // #457. Plumb MinQty through the mock seam (FIX MinQty);
            // real SDK path maps to UpModels.NewOrderRequest.MinQty.
            MinQty: order.MinQty);

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
        // #437. The mock seam now carries TIF / StopPrice / GoodTillDate
        // so test wire-mapping assertions stay faithful to the real
        // adapter (B3EntryPointClientGateway.CancelReplaceAsync lines
        // 232-288). Domain.Order.MergeReplacementOptionals enforces the
        // invariants (StopPrice required iff Stop*; GTD required iff
        // TIF==GoodTillDate; auto-clear when TIF moves away from GTD).
        // Any violation throws ArgumentException which the caller
        // (OrderModifyService) treats as a gateway-side failure and
        // rolls back the same way it does any other replace dispatch
        // failure.
        var (effTif, effStop, effGtd) = Order.MergeReplacementOptionals(
            original.Type, original.TimeInForce, original.StopPrice, original.GoodTillDate,
            requestedTimeInForce, requestedStopPrice, requestedGoodTillDate);

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
                    : (long?)null,
                // #457. Replace inherits the original's MinQty (clamped to
                // newQuantity when the new order qty would otherwise be <
                // MinQty), mirroring the real SDK path.
                MinQty: original.MinQty is { } omq
                    ? Math.Min(omq, newQuantity)
                    : (long?)null,
                TimeInForce: effTif,
                StopPrice: effStop,
                GoodTillDate: effGtd),
            cancellationToken);
    }
}
