using B3.Trading.Domain;

namespace B3.Trading.Infrastructure;

/// <summary>
/// <see cref="IExchangeGateway"/> implementation that translates domain
/// commands into <see cref="IEntryPointClient"/> calls. The reverse
/// direction (ER → domain) is handled in
/// <c>B3.Trading.Application.ExecutionReportProcessor</c>.
///
/// This class deliberately knows nothing about ClOrdID allocation — the
/// caller passes a fully-formed <see cref="Order"/> with a registry-issued
/// ClOrdID — so policy lives in one place
/// (<c>ClOrdIdPrefixRegistry</c>) and the gateway stays a thin adapter.
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
            order.Symbol,
            order.Side == OrderSide.Buy ? EpSide.Buy : EpSide.Sell,
            order.Type == OrderType.Limit ? EpOrderType.Limit : EpOrderType.Market,
            order.Quantity,
            order.Price,
            _firmId);

        return _client.SubmitNewOrderAsync(req, cancellationToken);
    }

    public Task CancelAsync(string clOrdId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clOrdId);
        return _client.SubmitCancelAsync(new OrderCancelRequest(clOrdId, _firmId), cancellationToken);
    }

    public Task CancelReplaceAsync(string originalClOrdId, string newClOrdId, long newQuantity, decimal? newPrice, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalClOrdId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newClOrdId);

        return _client.SubmitCancelReplaceAsync(
            new OrderCancelReplaceRequest(originalClOrdId, newClOrdId, newQuantity, newPrice, _firmId),
            cancellationToken);
    }
}
