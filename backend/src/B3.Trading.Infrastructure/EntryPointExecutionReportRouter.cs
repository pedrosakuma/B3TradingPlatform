using B3.Trading.Application;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Bridges <see cref="IEntryPointClient.ExecutionReportReceived"/> to the
/// wire-agnostic <see cref="ExecutionReportProcessor"/> in Application.
/// Lives in Infrastructure because it knows the wire enum
/// (<see cref="EpExecType"/>); the Application side stays unaware of it.
/// </summary>
public sealed class EntryPointExecutionReportRouter : IDisposable
{
    private readonly IEntryPointClient _client;
    private readonly ExecutionReportProcessor _processor;
    private readonly Action<ExecutionReportEnvelope> _handler;

    public EntryPointExecutionReportRouter(IEntryPointClient client, ExecutionReportProcessor processor)
    {
        _client = client;
        _processor = processor;
        _handler = OnExecutionReport;
        _client.ExecutionReportReceived += _handler;
    }

    public void Dispose() => _client.ExecutionReportReceived -= _handler;

    private void OnExecutionReport(ExecutionReportEnvelope er) =>
        _processor.Apply(
            er.ClOrdId,
            er.ExecType switch
            {
                EpExecType.New => ExecKind.New,
                EpExecType.PartialFill => ExecKind.PartialFill,
                EpExecType.Fill => ExecKind.Fill,
                EpExecType.Canceled => ExecKind.Canceled,
                EpExecType.Replaced => ExecKind.Replaced,
                EpExecType.Rejected => ExecKind.Rejected,
                _ => throw new ArgumentOutOfRangeException(nameof(er.ExecType)),
            },
            er.LeavesQuantity,
            er.CumulativeQuantity,
            er.LastQuantity,
            er.LastPrice,
            er.RejectReason);
}
