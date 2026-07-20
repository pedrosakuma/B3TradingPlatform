using System.Collections.Concurrent;

using B3.Trading.Application;
using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.MarketData;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Outbound;
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
    private readonly Action<NotAppliedEnvelope> _notAppliedHandler;
    private readonly ConcurrentDictionary<(string FirmId, ulong? SessionId, uint? SessionVerId, ulong SeqNum), byte>
        _seenBusinessRejects = new();
    private readonly ConcurrentDictionary<(string FirmId, ulong SessionId, uint SessionVerId, ulong FromSeqNo, uint Count), byte>
        _seenNotApplied = new();
    private readonly OutboundMutationLedger? _outboundLedger;
    // Q4.7 (#307). Both optional so the legacy two-arg test ctor keeps
    // working. When wired, the router captures a top-of-book snapshot
    // for every Fill / PartialFill and threads it through both the WAL
    // ER event (for replay) and the processor (for the live ExecutionEvent).
    private readonly WorkingOrderBook? _orders;
    private readonly PegBookTopCache? _bookTop;
    private readonly IDrainController? _drain;

    public EntryPointExecutionReportRouter(
        IEntryPointClient client,
        ExecutionReportProcessor processor,
        EventDispatcher dispatcher)
        : this(
            client,
            processor,
            dispatcher,
            orders: null,
            bookTop: null,
            drain: null,
            outboundLedger: null)
    {
    }

    public EntryPointExecutionReportRouter(
        IEntryPointClient client,
        ExecutionReportProcessor processor,
        EventDispatcher dispatcher,
        WorkingOrderBook? orders,
        PegBookTopCache? bookTop,
        IDrainController? drain = null,
        OutboundMutationLedger? outboundLedger = null)
    {
        _client = client;
        _processor = processor;
        _dispatcher = dispatcher;
        _orders = orders;
        _bookTop = bookTop;
        _drain = drain;
        _outboundLedger = outboundLedger;
        _handler = OnExecutionReport;
        _businessRejectHandler = OnBusinessReject;
        _notAppliedHandler = OnNotApplied;
        _client.ExecutionReportReceived += _handler;
        _client.BusinessRejectReceived += _businessRejectHandler;
        _client.NotAppliedReceived += _notAppliedHandler;
    }

    public void Dispose()
    {
        _client.ExecutionReportReceived -= _handler;
        _client.BusinessRejectReceived -= _businessRejectHandler;
        _client.NotAppliedReceived -= _notAppliedHandler;
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

        // RFC §5.2 (F2). Use the committed outcome-capture Dispatch overload:
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
        try
        {
            var walEvent = new ExecutionReportReceivedEvent
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
                SessionId = er.SessionId,
                SessionVerId = er.SessionVerId,
                InboundSeqNum = er.InboundSeqNum,
                VenueSendingTime = er.SendingTime,
                PossibleResend = er.PossibleResend,
                VenueOrderId = er.VenueOrderId,
                BookTouch = bookTouch,
            };
            InboundVenueEvidenceApplyResult? result = null;
            _dispatcher.DispatchCommitted(
                walEvent,
                fanOut =>
                {
                    result = _outboundLedger?.ApplyVenueAcknowledgement(walEvent);
                    if (result?.ShouldApplyDomain != false)
                    {
                        _processor.Apply(
                            er.ClOrdId,
                            kind,
                            er.LeavesQuantity,
                            er.CumulativeQuantity,
                            er.LastQuantity,
                            er.LastPrice,
                            er.RejectReason,
                            er.OrigClOrdId,
                            fanOut,
                            envelopeFirmId: er.FirmId,
                            bookTouch: bookTouch);
                    }
                });
            RecordEvidenceMetric(result, InboundVenueEvidenceKind.ExecutionReport, er.FirmId);
            if (result?.ReopenedReconciliation == true)
            {
                MetricsRegistry.OutboundContradictoryEvidence.Add(
                    1,
                    new("firm", er.FirmId),
                    new("evidence_type", "execution_report"));
            }
        }
        catch (Exception ex) when (ex is WalBackpressureException or WalFaultedException)
        {
            _drain?.BeginDrain("wal_execution_report_rejected");
            throw;
        }
    }

    // BusinessReject is committed before exact frame correlation. It remains
    // replay-inert for domain order state, but it can resolve the outbound
    // ledger only through its full firm/session/version/RefSeqNum identity.
    private void OnBusinessReject(BusinessRejectEnvelope br)
    {
        var firmId = br.FirmId ?? "default";
        if (_outboundLedger is null
            && !_seenBusinessRejects.TryAdd(
                (firmId, br.SessionId, br.SessionVerId, br.SeqNum), 0))
            return;

        var walEvent = new BusinessRejectReceivedEvent
        {
            FirmId = firmId,
            RefSeqNum = br.RefSeqNum,
            RejectReason = br.RejectReason,
            Text = br.Text,
            SeqNum = br.SeqNum,
            SendingTime = br.SendingTime,
            SessionId = br.SessionId,
            SessionVerId = br.SessionVerId,
            PossibleResend = br.PossibleResend,
        };

        try
        {
            InboundVenueEvidenceApplyResult? result = null;
            _dispatcher.DispatchCommitted(
                walEvent,
                () => result = _outboundLedger?.ApplyBusinessReject(walEvent));
            if (result?.Status != InboundVenueEvidenceApplyStatus.Duplicate)
                B3EntryPointClientGateway.RecordBusinessReject(firmId, br.RejectReason);
            RecordEvidenceMetric(result, InboundVenueEvidenceKind.BusinessReject, firmId);
        }
        catch (Exception ex) when (ex is WalBackpressureException or WalFaultedException)
        {
            _drain?.BeginDrain("wal_business_reject_rejected");
            throw;
        }
    }

    private void OnNotApplied(NotAppliedEnvelope notApplied)
    {
        if (_outboundLedger is null
            && !_seenNotApplied.TryAdd(
                (
                    notApplied.FirmId,
                    notApplied.SessionId,
                    notApplied.SessionVerId,
                    notApplied.FromSeqNo,
                    notApplied.Count),
                0))
        {
            return;
        }

        var walEvent = new NotAppliedReceivedEvent
        {
            FirmId = notApplied.FirmId,
            SessionId = notApplied.SessionId,
            SessionVerId = notApplied.SessionVerId,
            FromSeqNo = notApplied.FromSeqNo,
            Count = notApplied.Count,
            ObservedAtUtc = notApplied.ObservedAtUtc,
            TimestampUtc = notApplied.ObservedAtUtc,
        };
        try
        {
            InboundVenueEvidenceApplyResult? result = null;
            _dispatcher.DispatchCommitted(
                walEvent,
                () => result = _outboundLedger?.ApplyNotApplied(walEvent));
            RecordEvidenceMetric(
                result,
                InboundVenueEvidenceKind.NotApplied,
                notApplied.FirmId);
        }
        catch (Exception ex) when (ex is WalBackpressureException or WalFaultedException)
        {
            _drain?.BeginDrain("wal_not_applied_rejected");
            throw;
        }
    }

    private static void RecordEvidenceMetric(
        InboundVenueEvidenceApplyResult? result,
        InboundVenueEvidenceKind kind,
        string? firmId)
    {
        if (result?.Status != InboundVenueEvidenceApplyStatus.RecordedUnmatched)
            return;
        MetricsRegistry.OutboundUnmatchedVenueEvidence.Add(
            1,
            new KeyValuePair<string, object?>("firm", firmId ?? "unknown"),
            new KeyValuePair<string, object?>("kind", kind.ToString()));
    }
}
