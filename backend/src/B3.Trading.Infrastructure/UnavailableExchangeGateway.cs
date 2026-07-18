using B3.Trading.Domain;

using B3.Trading.Application;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Fail-closed gateway: every call throws so <c>OrdersEndpoints</c>
/// catches it, synthesizes a <c>gateway_unavailable</c> rejection through
/// the same WAL+sink path real ER rejections use, and returns 502.
/// <para>
/// Wire it via <c>Trading:Exchange:Mode = Unavailable</c> when the host
/// must run honestly without a broker — typical for the Docker bootstrap,
/// disaster-recovery drills, and any deployment where the wire side
/// genuinely is not configured. Strictly safer than <see cref="StubExchangeGateway"/>
/// (which silently accepts) for anything resembling production.
/// </para>
/// </summary>
public sealed class UnavailableExchangeGateway : IExchangeGateway, IExchangeGatewayPreSendOnly
{
    /// <summary>Single canonical message; <c>OrdersEndpoints</c> maps it to <c>gateway_unavailable</c> + 502.</summary>
    public const string Reason = "exchange gateway unavailable (mode=Unavailable)";

    public Task SubmitAsync(Order order, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Reason);

    public Task<ExchangeGatewayReceipt> SubmitWithReceiptAsync(
        Order order,
        ExchangeGatewayFramePreparedCallback onFramePrepared,
        CancellationToken cancellationToken) =>
        ProvenUnsent();

    public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Reason);

    public Task<ExchangeGatewayReceipt> CancelWithReceiptAsync(
        Order order,
        ulong newClOrdId,
        ExchangeGatewayFramePreparedCallback onFramePrepared,
        CancellationToken cancellationToken) =>
        ProvenUnsent();

    public Task CancelReplaceAsync(
        Order order, ulong newClOrdId, long newQuantity, decimal? newPrice,
        TimeInForce? requestedTimeInForce, decimal? requestedStopPrice, DateTimeOffset? requestedGoodTillDate,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Reason);

    public Task<ExchangeGatewayReceipt> CancelReplaceWithReceiptAsync(
        Order order, ulong newClOrdId, long newQuantity, decimal? newPrice,
        TimeInForce? requestedTimeInForce, decimal? requestedStopPrice, DateTimeOffset? requestedGoodTillDate,
        ExchangeGatewayFramePreparedCallback onFramePrepared,
        CancellationToken cancellationToken) =>
        ProvenUnsent();

    private static Task<ExchangeGatewayReceipt> ProvenUnsent() =>
        Task.FromException<ExchangeGatewayReceipt>(
            new ExchangeGatewayAttemptException(
                Reason,
                ExchangeGatewayFailureDisposition.OutboundProvenUnsent,
                ExchangeGatewayAttemptStage.NotStarted,
                frame: null));
}
