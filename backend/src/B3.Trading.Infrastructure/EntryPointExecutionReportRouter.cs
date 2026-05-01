using B3.Trading.Application;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Bridges <see cref="IEntryPointClient.ExecutionReportReceived"/> to the
/// wire-agnostic <see cref="ExecutionReportProcessor"/> in Application.
/// Lives in Infrastructure because it knows the wire enum
/// (<see cref="EpExecType"/>); the Application side stays unaware of it.
/// Every ER is persisted through the <see cref="EventDispatcher"/> so
/// state mutations and WAL appends share the same consistency boundary.
/// </summary>
public sealed class EntryPointExecutionReportRouter : IDisposable
{
    private readonly IEntryPointClient _client;
    private readonly ExecutionReportProcessor _processor;
    private readonly EventDispatcher _dispatcher;
    private readonly Action<ExecutionReportEnvelope> _handler;

    public EntryPointExecutionReportRouter(
        IEntryPointClient client,
        ExecutionReportProcessor processor,
        EventDispatcher dispatcher)
    {
        _client = client;
        _processor = processor;
        _dispatcher = dispatcher;
        _handler = OnExecutionReport;
        _client.ExecutionReportReceived += _handler;
    }

    public void Dispose() => _client.ExecutionReportReceived -= _handler;

    private void OnExecutionReport(ExecutionReportEnvelope er)
    {
        MetricsRegistry.ExecutionReportsReceived.Add(1,
            new KeyValuePair<string, object?>("exec_type", er.ExecType.ToString()));

        var kind = er.ExecType switch
        {
            EpExecType.New => ExecKind.New,
            EpExecType.PartialFill => ExecKind.PartialFill,
            EpExecType.Fill => ExecKind.Fill,
            EpExecType.Canceled => ExecKind.Canceled,
            EpExecType.Replaced => ExecKind.Replaced,
            EpExecType.Rejected => ExecKind.Rejected,
            _ => throw new ArgumentOutOfRangeException(nameof(er.ExecType)),
        };

        try
        {
            _dispatcher.Dispatch(
                new ExecutionReportReceivedEvent
                {
                    ClOrdId = er.ClOrdId,
                    ExecKind = kind.ToString(),
                    LeavesQuantity = er.LeavesQuantity,
                    CumulativeQuantity = er.CumulativeQuantity,
                    LastQuantity = er.LastQuantity,
                    LastPrice = er.LastPrice,
                    RejectReason = er.RejectReason,
                    Synthetic = false,
                    OrigClOrdId = er.OrigClOrdId,
                },
                () => _processor.Apply(er.ClOrdId, kind, er.LeavesQuantity, er.CumulativeQuantity,
                    er.LastQuantity, er.LastPrice, er.RejectReason, er.OrigClOrdId));
        }
        catch (WalBackpressureException)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "er.router"));
            // ER inbound is single-direction — losing audit on backpressure
            // is preferable to dropping the state mutation. Apply directly;
            // this is a "log dropped, state intact" branch and shows up in
            // metrics as a backpressure event.
            _processor.Apply(er.ClOrdId, kind, er.LeavesQuantity, er.CumulativeQuantity,
                er.LastQuantity, er.LastPrice, er.RejectReason, er.OrigClOrdId);
        }
    }
}
