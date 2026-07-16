using System.Collections.Concurrent;

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
    private readonly ConcurrentDictionary<(string FirmId, ulong SeqNum), byte> _seenBusinessRejects = new();
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

        // RFC §5.2 (F2). Use the outcome-capture Dispatch overload:
        // the apply callback records the resulting ExecutionEvent(s)
        // onto the supplied writer, and the dispatcher TryWrites
        // each entry into every per-sink fan-out channel WHILE
        // STILL HOLDING the dispatcher lock. Per-sink drain order
        // therefore matches WAL append order even though the
        // expensive publish work (subscriber walk + DTO build for
        // the WS hub; SBE encode + outbound enqueue for the bot
        // router) runs OFF the lock on each sink's drain thread.
        //
        // WAL rejection deliberately propagates. Applying an ER without
        // its durable event would make a Fill visible only in memory.
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

    // #432. BusinessReject is replay-inert (no order state mutation), so the
    // router just persists it to the WAL and lets the dispatcher fan it out
    // to any subscribers (history projection, WS fan-out — both deferred to
    // follow-up issues). The dispatcher lock isn't needed because nothing
    // downstream consumes the in-memory ordering against ER right now;
    // ordering vs ER is preserved by the WAL seqnum anyway.
    private void OnBusinessReject(BusinessRejectEnvelope br)
    {
        var firmId = br.FirmId ?? "default";
        if (!_seenBusinessRejects.TryAdd((firmId, br.SeqNum), 0))
            return;

        B3EntryPointClientGateway.RecordBusinessReject(firmId, br.RejectReason);

        var walEvent = new BusinessRejectReceivedEvent
        {
            FirmId = firmId,
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
