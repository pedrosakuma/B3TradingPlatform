using B3.Trading.Domain;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Boundary toward <c>B3.EntryPoint.Client</c> (the wire-puro FIXP/SBE
/// library). Real implementation will adapt EntryPoint
/// NewOrder/CancelReplace/Cancel + ExecutionReport to/from the domain;
/// the interface lives in Infrastructure so Application stays wire-agnostic.
/// </summary>
public interface IExchangeGateway
{
    /// <summary>
    /// Submit a freshly-built order to the exchange. The caller is expected
    /// to have already allocated <see cref="Order.ClOrdId"/> via the
    /// <c>ClOrdIdPrefixRegistry</c>.
    /// </summary>
    Task SubmitAsync(Order order, CancellationToken cancellationToken);

    /// <summary>
    /// Cancel a working order. Takes the full <see cref="Order"/> because
    /// the upstream <c>CancelOrderRequest</c> requires <c>SecurityId</c> +
    /// <c>Side</c> in addition to the original ClOrdID.
    /// </summary>
    Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken);

    /// <summary>
    /// Cancel-replace a working order. <paramref name="newClOrdId"/> must
    /// already be allocated; the original ClOrdID is the one being replaced.
    /// </summary>
    Task CancelReplaceAsync(Order original, ulong newClOrdId, long newQuantity, decimal? newPrice, CancellationToken cancellationToken);
}
