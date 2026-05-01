namespace B3.Trading.Infrastructure;

/// <summary>
/// Internal seam between the wire layer and our ER router. Production
/// uses <see cref="B3EntryPointClientGateway"/> wrapping the upstream
/// <c>B3.EntryPoint.Client.EntryPointClient</c>. Tests use
/// <see cref="MockEntryPointClient"/> to drive ERs without TCP.
///
/// Records here mirror the upstream package's shape (ulong ClOrdId,
/// ulong SecurityId, FIX-style enums) so translation at the boundary
/// is a pure field-mapping exercise.
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
    ulong ClOrdId,
    ulong SecurityId,
    string Symbol,
    EpSide Side,
    EpOrderType Type,
    long Quantity,
    decimal? Price,
    string FirmId);

public sealed record OrderCancelRequest(
    ulong ClOrdId,
    ulong OrigClOrdId,
    ulong SecurityId,
    EpSide Side,
    string FirmId);

public sealed record OrderCancelReplaceRequest(
    ulong OriginalClOrdId,
    ulong NewClOrdId,
    ulong SecurityId,
    EpSide Side,
    long NewQuantity,
    decimal? NewPrice,
    string FirmId);

public sealed record ExecutionReportEnvelope(
    ulong ClOrdId,
    EpExecType ExecType,
    long LeavesQuantity,
    long CumulativeQuantity,
    long LastQuantity,
    decimal LastPrice,
    string? RejectReason);
