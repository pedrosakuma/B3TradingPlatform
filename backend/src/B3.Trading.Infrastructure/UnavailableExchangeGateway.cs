using B3.Trading.Domain;

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
public sealed class UnavailableExchangeGateway : IExchangeGateway
{
    /// <summary>Single canonical message; <c>OrdersEndpoints</c> maps it to <c>gateway_unavailable</c> + 502.</summary>
    public const string Reason = "exchange gateway unavailable (mode=Unavailable)";

    public Task SubmitAsync(Order order, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Reason);

    public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Reason);

    public Task CancelReplaceAsync(Order original, ulong newClOrdId, long newQuantity, decimal? newPrice, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Reason);
}
