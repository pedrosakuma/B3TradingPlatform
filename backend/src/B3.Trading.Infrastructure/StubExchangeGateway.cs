using B3.Trading.Domain;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Bootstrap-only stub gateway. No wire I/O; logs the call and returns.
/// Will be replaced by an <c>EntryPointClientGateway</c> backed by the
/// <c>B3EntryPointClient</c> lib once that lib is wired in.
/// </summary>
public sealed class StubExchangeGateway : IExchangeGateway
{
    public Task SubmitAsync(Order order, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CancelAsync(string clOrdId, CancellationToken cancellationToken) => Task.CompletedTask;
}
