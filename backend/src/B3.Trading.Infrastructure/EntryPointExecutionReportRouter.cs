using B3.Trading.Application;
using B3.Trading.Application.MarketData;
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
    // #432 — second subscription on the gateway: BusinessReject envelopes
    // travel on their own channel because they lack a ClOrdID anchor; the
    // router persists them to the WAL for operator audit but does not
    // hand them to the ExecutionReportProcessor (no order state changes).
    private readonly Action<BusinessRejectEnvelope> _businessRejectHandler;
    // Q4.7 (#307). Both optional so the legacy two-arg test ctor keeps
    // working. When wired, the router captures a top-of-book snapshot
    // for every Fill / PartialFill and threads it through both the WAL
    // ER event (for replay) and the processor (for the live ExecutionEvent).
    private readonly WorkingOrderBook? _orders;
    private readonly PegBookTopCache? _bookTop;

    public EntryPointExecutionReportRouter(
        IEntryPointClient client,
        ExecutionReportProcessor processor,
        EventDispatcher dispatcher)
        : this(client, processor, dispatcher, orders: null, bookTop: null)
    {
    }

    public EntryPointExecutionReportRouter(
        IEntryPointClient client,
        ExecutionReportProcessor processor,
        EventDispatcher dispatcher,
        WorkingOrderBook? orders,
        PegBookTopCache? bookTop)
    {
        _client = client;
        _processor = processor;
        _dispatcher = dispatcher;
        _orders = orders;
        _bookTop = bookTop;
        _handler = OnExecutionReport;
        _businessRejectHandler = OnBusinessReject;
        _client.ExecutionReportReceived += _handler;
        _client.BusinessRejectReceived += _businessRejectHandler;
    }

    public void Dispose()
    {
        _client.ExecutionReportReceived -= _handler;
        _client.BusinessRejectReceived -= _businessRejectHandler;
    }

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

        // Q4.7 (#307). Capture the top-of-book at the instant a fill
        // ER is observed. The snapshot is attached to BOTH the WAL ER
        // event (so replay re-folds the same BookTouch into
        // FillProjection) and the live ExecutionEvent constructed by
        // the processor (so /fills + fills.me + drop-copy can read it
        // without a second cache lookup). Capture happens BEFORE the
        // dispatcher lock so the cache lookup is not on the hot path.
        BookTouchSnapshot? bookTouch = null;
        if (kind is ExecKind.Fill or ExecKind.PartialFill && _bookTop is not null)
        {
            string? symbol = null;
            if (_orders is not null)
            {
                var lookupId = er.OrigClOrdId != 0 ? er.OrigClOrdId : er.ClOrdId;
                if (_orders.TryGet(lookupId, out var order) && order is not null)
                    symbol = order.Symbol;
            }
            if (symbol is not null)
                bookTouch = BookTouchSnapshot.Capture(_bookTop, symbol, DateTimeOffset.UtcNow);
        }

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
                    BookTouch = bookTouch,
                },
                fanOut => _processor.Apply(er.ClOrdId, kind, er.LeavesQuantity, er.CumulativeQuantity,
                    er.LastQuantity, er.LastPrice, er.RejectReason, er.OrigClOrdId, fanOut, envelopeFirmId: er.FirmId, bookTouch: bookTouch));
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
                    er.LastQuantity, er.LastPrice, er.RejectReason, er.OrigClOrdId, envelopeFirmId: er.FirmId, bookTouch: bookTouch));
        }
    }

    // #432. BusinessReject is replay-inert (no order state mutation), so the
    // router just persists it to the WAL and lets the dispatcher fan it out
    // to any subscribers (history projection, WS fan-out — both deferred to
    // follow-up issues). The dispatcher lock isn't needed because nothing
    // downstream consumes the in-memory ordering against ER right now;
    // ordering vs ER is preserved by the WAL seqnum anyway.
    private void OnBusinessReject(BusinessRejectEnvelope br)
    {
        var walEvent = new BusinessRejectReceivedEvent
        {
            FirmId = br.FirmId ?? "default",
            RefSeqNum = br.RefSeqNum,
            RejectReason = br.RejectReason,
            Text = br.Text,
            SeqNum = br.SeqNum,
            SendingTime = br.SendingTime,
        };

        try
        {
            _dispatcher.Dispatch(walEvent, _ => { });
        }
        catch (WalBackpressureException)
        {
            // BR is an audit signal, not a state mutation — losing it on
            // backpressure is acceptable. The metric makes the loss visible.
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "business_reject.router"));
        }
    }
}
