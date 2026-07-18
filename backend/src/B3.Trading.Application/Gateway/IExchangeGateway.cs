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
    /// Evidence-aware submit boundary for the durable outbound coordinator.
    /// The caller must commit its attempt intent before entry and durably commit
    /// the supplied frame identity in <paramref name="onFramePrepared"/>.
    /// Existing callers intentionally remain on <see cref="SubmitAsync"/> until
    /// #642/#643 wire the coordinator.
    /// </summary>
    Task<ExchangeGatewayReceipt> SubmitWithReceiptAsync(
        Order order,
        ExchangeGatewayFramePreparedCallback onFramePrepared,
        CancellationToken cancellationToken) =>
        Task.FromException<ExchangeGatewayReceipt>(
            ExchangeGatewayAttemptException.ReceiptNotSupported());

    /// <summary>
    /// Cancel a working order. Takes the full <see cref="Order"/> because
    /// the upstream <c>CancelOrderRequest</c> requires <c>SecurityId</c> +
    /// <c>Side</c> in addition to the original ClOrdID.
    /// </summary>
    Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken);

    /// <summary>Evidence-aware cancel boundary. See <see cref="SubmitWithReceiptAsync"/>.</summary>
    Task<ExchangeGatewayReceipt> CancelWithReceiptAsync(
        Order order,
        ulong newClOrdId,
        ExchangeGatewayFramePreparedCallback onFramePrepared,
        CancellationToken cancellationToken) =>
        Task.FromException<ExchangeGatewayReceipt>(
            ExchangeGatewayAttemptException.ReceiptNotSupported());

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

    /// <summary>Evidence-aware replace boundary. See <see cref="SubmitWithReceiptAsync"/>.</summary>
    Task<ExchangeGatewayReceipt> CancelReplaceWithReceiptAsync(
        Order original,
        ulong newClOrdId,
        long newQuantity,
        decimal? newPrice,
        TimeInForce? requestedTimeInForce,
        decimal? requestedStopPrice,
        DateTimeOffset? requestedGoodTillDate,
        ExchangeGatewayFramePreparedCallback onFramePrepared,
        CancellationToken cancellationToken) =>
        Task.FromException<ExchangeGatewayReceipt>(
            ExchangeGatewayAttemptException.ReceiptNotSupported());
}

/// <summary>
/// Marker for gateways that cannot attempt a wire send. Any exception from
/// such a gateway is therefore proven pre-send even when the gateway preserves
/// a legacy public exception type.
/// </summary>
public interface IExchangeGatewayPreSendOnly
{
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
