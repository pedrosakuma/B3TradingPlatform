namespace B3.Trading.Infrastructure;

/// <summary>
/// Placeholder for the surface that the upstream <c>B3EntryPointClient</c>
/// library is expected to expose. Defined here so this repo can build the
/// gateway, ER routing, and tests against a stable shape while the
/// upstream lib finalizes its API design (the upstream repo is starting
/// with API design + mocks first, on purpose, so consumers like this one
/// can lock in their boundary early).
///
/// Replace this interface with the real lib's type(s) once published —
/// the rest of the codebase only depends on the few request/response
/// POCOs declared here, all of which are pure data.
/// </summary>
public interface IEntryPointClient
{
    Task SubmitNewOrderAsync(NewOrderSingle request, CancellationToken cancellationToken);
    Task SubmitCancelAsync(OrderCancelRequest request, CancellationToken cancellationToken);
    Task SubmitCancelReplaceAsync(OrderCancelReplaceRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Raised whenever the exchange pushes an ExecutionReport (new, fill,
    /// cancel ack, reject, etc.) to the platform. Single-threaded by
    /// implementation contract; consumers do not have to lock.
    /// </summary>
    event Action<ExecutionReportEnvelope>? ExecutionReportReceived;
}

public enum EpSide
{
    Buy,
    Sell,
}

public enum EpOrderType
{
    Limit,
    Market,
}

public enum EpExecType
{
    New,
    PartialFill,
    Fill,
    Canceled,
    Replaced,
    Rejected,
}

public sealed record NewOrderSingle(
    string ClOrdId,
    string Symbol,
    EpSide Side,
    EpOrderType Type,
    long Quantity,
    decimal? Price,
    string FirmId);

public sealed record OrderCancelRequest(string ClOrdId, string FirmId);

public sealed record OrderCancelReplaceRequest(
    string OriginalClOrdId,
    string NewClOrdId,
    long NewQuantity,
    decimal? NewPrice,
    string FirmId);

public sealed record ExecutionReportEnvelope(
    string ClOrdId,
    EpExecType ExecType,
    long LeavesQuantity,
    long CumulativeQuantity,
    long LastQuantity,
    decimal LastPrice,
    string? RejectReason);
