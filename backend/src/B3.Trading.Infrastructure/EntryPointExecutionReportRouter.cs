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
            // RFC §5.2 (F2). Use the outcome-capture Dispatch overload:
            // the apply callback records the resulting ExecutionEvent(s)
            // onto the supplied writer, and the dispatcher TryWrites
            // each entry into every per-sink fan-out channel WHILE
            // STILL HOLDING the dispatcher lock. Per-sink drain order
            // therefore matches WAL append order even though the
            // expensive publish work (subscriber walk + DTO build for
            // the WS hub; SBE encode + outbound enqueue for the bot
            // router) runs OFF the lock on each sink's drain thread.
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
                    FirmId = er.FirmId,
                },
                fanOut => _processor.Apply(er.ClOrdId, kind, er.LeavesQuantity, er.CumulativeQuantity,
                    er.LastQuantity, er.LastPrice, er.RejectReason, er.OrigClOrdId, fanOut, envelopeFirmId: er.FirmId));
        }
        catch (WalBackpressureException)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "er.router"));
            // ER inbound is single-direction — losing audit on backpressure
            // is preferable to dropping the state mutation. Apply directly
            // (legacy synchronous-publish path: no fanOut writer); this is
            // a "log dropped, state intact" branch and shows up in metrics
            // as a backpressure event.
            //
            // Pass-2 review (#278) P1#1. Take the dispatcher lock for the
            // whole fallback Apply via RunExclusive so the same
            // serialisation discipline that the regular Dispatch path
            // gives us still holds: nested Dispatch calls for derived
            // fee/PnL events (reentrant on this thread) interleave
            // safely with concurrent live ER dispatches on other
            // threads, and there is no AB-BA inversion against any
            // downstream lock the keepers take. The WAL append is
            // intentionally skipped here — we are at backpressure —
            // so holding the dispatcher lock involves no I/O.
            _dispatcher.RunExclusive(() =>
                _processor.Apply(er.ClOrdId, kind, er.LeavesQuantity, er.CumulativeQuantity,
                    er.LastQuantity, er.LastPrice, er.RejectReason, er.OrigClOrdId, envelopeFirmId: er.FirmId));
        }
    }
}
