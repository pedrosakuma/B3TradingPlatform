using B3.Trading.Domain;

using B3.Trading.Application;
using B3.Trading.Application.Outbound;
using System.Security.Cryptography;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Bootstrap-only stub gateway. No wire I/O; logs the call and returns.
/// Kept for tests that want a no-op gateway, and so the Host can fall back
/// to it via config when no firm sessions are configured.
/// </summary>
public sealed class StubExchangeGateway : IExchangeGateway
{
    private long _seq;
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

    public async Task<ExchangeGatewayReceipt> SubmitWithReceiptAsync(
        OutboundNewOrderCommand command,
        ExchangeGatewayFramePreparedCallback onFramePrepared,
        CancellationToken cancellationToken)
    {
        var seq = checked((ulong)Interlocked.Increment(ref _seq));
        var hash = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                $"{command.FirmId}|{command.Canonical.ClOrdId}|{seq}")))
            .ToLowerInvariant();
        var frame = new ExchangeGatewayFrameIdentity(
            command.FirmId,
            sessionId: 1,
            sessionVerId: 1,
            outboundSeqNum: seq,
            ExchangeGatewayOperation.NewOrder,
            command.Canonical.ClOrdId,
            encodedFrameLength: 1,
            hash);
        await onFramePrepared(frame, cancellationToken).ConfigureAwait(false);
        return new ExchangeGatewayReceipt(
            frame,
            ExchangeGatewayAttemptStage.TransportWriteCompleted);
    }

    public Task<ExchangeGatewayReceipt> CancelWithReceiptAsync(
        Order order,
        ulong newClOrdId,
        ExchangeGatewayFramePreparedCallback onFramePrepared,
        CancellationToken cancellationToken) =>
        Unsupported();

    public Task<ExchangeGatewayReceipt> CancelWithReceiptAsync(
        OutboundCancelCommand command,
        ExchangeGatewayFramePreparedCallback onFramePrepared,
        CancellationToken cancellationToken) =>
        CompleteAsync(
            command.FirmId,
            command.Canonical.ClOrdId,
            ExchangeGatewayOperation.Cancel,
            onFramePrepared,
            cancellationToken);

    public Task<ExchangeGatewayReceipt> CancelReplaceWithReceiptAsync(
        Order original, ulong newClOrdId, long newQuantity, decimal? newPrice,
        TimeInForce? requestedTimeInForce, decimal? requestedStopPrice, DateTimeOffset? requestedGoodTillDate,
        ExchangeGatewayFramePreparedCallback onFramePrepared,
        CancellationToken cancellationToken) =>
        Unsupported();

    public Task<ExchangeGatewayReceipt> CancelReplaceWithReceiptAsync(
        OutboundReplaceCommand command,
        ExchangeGatewayFramePreparedCallback onFramePrepared,
        CancellationToken cancellationToken) =>
        CompleteAsync(
            command.FirmId,
            command.Canonical.ClOrdId,
            ExchangeGatewayOperation.Replace,
            onFramePrepared,
            cancellationToken);

    private async Task<ExchangeGatewayReceipt> CompleteAsync(
        string firmId,
        ulong clOrdId,
        ExchangeGatewayOperation operation,
        ExchangeGatewayFramePreparedCallback onFramePrepared,
        CancellationToken cancellationToken)
    {
        var seq = checked((ulong)Interlocked.Increment(ref _seq));
        var hash = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{firmId}|{clOrdId}|{seq}")))
            .ToLowerInvariant();
        var frame = new ExchangeGatewayFrameIdentity(
            firmId,
            sessionId: 1,
            sessionVerId: 1,
            outboundSeqNum: seq,
            operation,
            clOrdId,
            encodedFrameLength: 1,
            hash);
        await onFramePrepared(frame, cancellationToken).ConfigureAwait(false);
        return new ExchangeGatewayReceipt(
            frame,
            ExchangeGatewayAttemptStage.TransportWriteCompleted);
    }

    private static Task<ExchangeGatewayReceipt> Unsupported() =>
        Task.FromException<ExchangeGatewayReceipt>(
            ExchangeGatewayAttemptException.ReceiptNotSupported());
}
