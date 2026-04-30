using B3.Trading.Domain;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Boundary toward <c>B3EntryPointClient</c> (the wire-puro FIXP/SBE library
/// in the companion repo). Real implementation will adapt EntryPoint
/// NewOrder/CancelReplace/Cancel + ExecutionReport to/from the domain;
/// the interface lives in Infrastructure so Application stays wire-agnostic.
/// </summary>
public interface IExchangeGateway
{
    /// <summary>
    /// Submit a freshly-built order to the exchange. Implementations are
    /// responsible for ClOrdID allocation policy (per-end-client prefixing —
    /// see issue #1 §1).
    /// </summary>
    Task SubmitAsync(Order order, CancellationToken cancellationToken);

    /// <summary>
    /// Cancel a working order by ClOrdID.
    /// </summary>
    Task CancelAsync(string clOrdId, CancellationToken cancellationToken);
}
