using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Boundary toward <c>B3.EntryPoint.Client</c> (the wire-puro FIXP/SBE
/// library). The interface lives in Application — implementations live in
/// Infrastructure (ports-and-adapters), so the algo engine and the
/// HTTP submit pipeline can share the same abstraction without
/// dragging in wire-library types.
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
    ///
    /// <para>
    /// Q1.1 (#253). The trailing optionals carry the modify pipeline's
    /// new-value overrides for TIF / StopPrice / GoodTillDate. Null on
    /// any one of them means "inherit from <paramref name="original"/>";
    /// non-null means "use the requested value on the outbound
    /// ReplaceOrderRequest". OrderType is intentionally NOT modifiable
    /// at this layer (FIX 35=G semantics on B3).
    /// </para>
    /// </summary>
    Task CancelReplaceAsync(
        Order original,
        ulong newClOrdId,
        long newQuantity,
        decimal? newPrice,
        TimeInForce? requestedTimeInForce,
        decimal? requestedStopPrice,
        DateTimeOffset? requestedGoodTillDate,
        CancellationToken cancellationToken);
}

/// <summary>
/// Signals that the gateway proved no wire attempt occurred. Callers may
/// durably terminalise the outbound intent instead of retaining it as an
/// ambiguous send.
/// </summary>
public sealed class ExchangeGatewayPreSendException : Exception
{
    public ExchangeGatewayPreSendException(string message) : base(message) { }
    public ExchangeGatewayPreSendException(string message, Exception innerException)
        : base(message, innerException) { }
}
