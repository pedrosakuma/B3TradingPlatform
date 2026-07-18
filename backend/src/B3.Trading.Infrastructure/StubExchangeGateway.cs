using B3.Trading.Domain;

using B3.Trading.Application;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Bootstrap-only stub gateway. No wire I/O; logs the call and returns.
/// Kept for tests that want a no-op gateway, and so the Host can fall back
/// to it via config when no firm sessions are configured.
/// </summary>
public sealed class StubExchangeGateway : IExchangeGateway
{
    public Task SubmitAsync(Order order, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CancelReplaceAsync(
        Order original, ulong newClOrdId, long newQuantity, decimal? newPrice,
        TimeInForce? requestedTimeInForce, decimal? requestedStopPrice, DateTimeOffset? requestedGoodTillDate,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<ExchangeGatewayReceipt> SubmitWithReceiptAsync(
        Order order,
        ExchangeGatewayFramePreparedCallback onFramePrepared,
        CancellationToken cancellationToken) =>
        Unsupported();

    public Task<ExchangeGatewayReceipt> CancelWithReceiptAsync(
        Order order,
        ulong newClOrdId,
        ExchangeGatewayFramePreparedCallback onFramePrepared,
        CancellationToken cancellationToken) =>
        Unsupported();

    public Task<ExchangeGatewayReceipt> CancelReplaceWithReceiptAsync(
        Order original, ulong newClOrdId, long newQuantity, decimal? newPrice,
        TimeInForce? requestedTimeInForce, decimal? requestedStopPrice, DateTimeOffset? requestedGoodTillDate,
        ExchangeGatewayFramePreparedCallback onFramePrepared,
        CancellationToken cancellationToken) =>
        Unsupported();

    private static Task<ExchangeGatewayReceipt> Unsupported() =>
        Task.FromException<ExchangeGatewayReceipt>(
            ExchangeGatewayAttemptException.ReceiptNotSupported());
}
