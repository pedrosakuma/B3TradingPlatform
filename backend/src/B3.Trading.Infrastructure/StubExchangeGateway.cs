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
    public Task CancelReplaceAsync(Order original, ulong newClOrdId, long newQuantity, decimal? newPrice, CancellationToken cancellationToken) => Task.CompletedTask;
}
