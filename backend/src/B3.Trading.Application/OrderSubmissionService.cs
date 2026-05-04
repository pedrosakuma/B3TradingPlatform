using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Application;

/// <summary>
/// The "submit an order" pipeline extracted from <c>POST /orders</c> so it
/// can be reused by the algo engine (RFC algo-orders-v0 §4.3). Manual
/// submissions and engine-driven child slices share the same WAL writes,
/// risk pipeline, margin reservation, gateway dispatch, and synthetic
/// rejection plumbing — there must be exactly one path that orders take
/// from intent to wire, otherwise audit/recovery semantics diverge.
///
/// <para>
/// The service is stateless apart from its injected collaborators; all
/// per-call mutable state lives on the returned <see cref="OrderSubmissionResult"/>
/// or in the underlying books. Callers translate the result into their own
/// transport (HTTP status + JSON body for endpoints, signal updates for
/// the engine).
/// </para>
/// </summary>
public sealed class OrderSubmissionService
{
    private readonly ClOrdIdPrefixRegistry _clOrdIds;
    private readonly OrderOwnershipMap _ownership;
    private readonly WorkingOrderBook _book;
    private readonly IExchangeGateway _gateway;
    private readonly IExecutionEventSink _sink;
    private readonly RiskPipeline _risk;
    private readonly IMarginProvider _margin;
    private readonly CompositeRiskAccountant _accountant;
    private readonly EventDispatcher _dispatcher;
    private readonly Lifecycle.IDrainGate _drain;
    private readonly ILogger<OrderSubmissionService> _logger;

    public OrderSubmissionService(
        ClOrdIdPrefixRegistry clOrdIds,
        OrderOwnershipMap ownership,
        WorkingOrderBook book,
        IExchangeGateway gateway,
        IExecutionEventSink sink,
        RiskPipeline risk,
        IMarginProvider margin,
        CompositeRiskAccountant accountant,
        EventDispatcher dispatcher,
        Lifecycle.IDrainGate drain,
        ILogger<OrderSubmissionService> logger)
    {
        _clOrdIds = clOrdIds;
        _ownership = ownership;
        _book = book;
        _gateway = gateway;
        _sink = sink;
        _risk = risk;
        _margin = margin;
        _accountant = accountant;
        _dispatcher = dispatcher;
        _drain = drain;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full submit pipeline for one order. The caller is
    /// responsible for parsing/validating the request shape; this method
    /// validates business invariants (positive quantity, non-zero
    /// SecurityId) and short-circuits with the appropriate result.
    /// </summary>
    public async Task<OrderSubmissionResult> SubmitAsync(OrderSubmissionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (_drain.IsDraining)
        {
            MetricsRegistry.DrainRejections.Add(1,
                new KeyValuePair<string, object?>("route", req.Source == OrderSubmissionSource.Algo ? "algo.submit" : "POST /orders"));
            return OrderSubmissionResult.Drained;
        }

        if (req.Quantity <= 0)
            return OrderSubmissionResult.BadRequest("quantity must be positive");
        if (req.SecurityId == 0)
            return OrderSubmissionResult.BadRequest("securityId is required");
        if (string.IsNullOrWhiteSpace(req.Symbol))
            return OrderSubmissionResult.BadRequest("symbol is required");

        var clOrdId = _clOrdIds.Generate(req.Owner);
        var order = new Order(
            clOrdId, req.Owner, req.Symbol, req.SecurityId, req.Side, req.Type,
            req.Quantity, req.Price, req.FirmId,
            parentAlgoId: req.ParentAlgoId, algoSliceSeq: req.AlgoSliceSeq);

        try
        {
            _dispatcher.Dispatch(
                new OrderSubmittedEvent
                {
                    ClOrdId = clOrdId,
                    EndClientId = req.Owner.Value,
                    FirmId = req.FirmId,
                    Symbol = req.Symbol,
                    SecurityId = req.SecurityId,
                    Side = req.Side.ToString(),
                    Type = req.Type.ToString(),
                    Quantity = req.Quantity,
                    Price = req.Price,
                    ParentAlgoId = req.ParentAlgoId,
                    AlgoSliceSeq = req.AlgoSliceSeq,
                },
                () =>
                {
                    _book.TryAdd(order);
                    _ownership.Register(clOrdId, req.Owner);
                });
        }
        catch (WalBackpressureException ex)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site",
                    req.Source == OrderSubmissionSource.Algo ? "algo.submit" : "orders.submit"));
            return OrderSubmissionResult.WalBackpressure(ex.Message);
        }

        MetricsRegistry.OrdersSubmitted.Add(1,
            new KeyValuePair<string, object?>("symbol", req.Symbol),
            new KeyValuePair<string, object?>("side", req.Side.ToString()),
            new KeyValuePair<string, object?>("source",
                req.Source == OrderSubmissionSource.Algo ? "algo" : "manual"));

        var riskCtx = new RiskContext(req.Owner, req.FirmId, req.Symbol, req.Side, req.Type, req.Quantity, req.Price);
        var decision = _risk.Evaluate(riskCtx);
        var marginReserved = false;
        if (decision.Approved)
        {
            var marginDecision = await _margin.TryReserveAsync(clOrdId, riskCtx, ct);
            if (marginDecision.Approved) marginReserved = true;
            else decision = marginDecision;
        }
        if (!decision.Approved)
        {
            MetricsRegistry.OrdersRejectedByRisk.Add(1,
                new KeyValuePair<string, object?>("reason", decision.Reason ?? "risk_rejected"));
            PublishSyntheticRejection(order, decision.Reason ?? "risk_rejected");
            return OrderSubmissionResult.Rejected(clOrdId, decision.Reason ?? "risk_rejected");
        }

        _accountant.RecordAccepted(riskCtx);

        try
        {
            await _gateway.SubmitAsync(order, ct);
        }
        catch (Exception ex)
        {
            MetricsRegistry.OrdersGatewayFailed.Add(1);
            _logger.LogError(ex, "Gateway submit failed for {ClOrdId}; synthesizing rejection.", clOrdId);
            if (marginReserved) _margin.ReleaseReservation(clOrdId);
            PublishSyntheticRejection(order, "gateway_unavailable");
            return OrderSubmissionResult.GatewayFailed(clOrdId, ex);
        }

        return OrderSubmissionResult.Accepted(clOrdId);
    }

    private void PublishSyntheticRejection(Order order, string reason)
    {
        try
        {
            _dispatcher.Dispatch(
                new ExecutionReportReceivedEvent
                {
                    ClOrdId = order.ClOrdId,
                    ExecKind = ExecKind.Rejected.ToString(),
                    LeavesQuantity = order.LeavesQuantity,
                    CumulativeQuantity = order.CumulativeQuantity,
                    LastQuantity = 0,
                    LastPrice = 0m,
                    RejectReason = reason,
                    Synthetic = true,
                },
                () =>
                {
                    order.MarkRejected();
                    _sink.Publish(new ExecutionEvent(
                        order.Owner, order.ClOrdId, order.Symbol, order.Side, order.Status, ExecKind.Rejected,
                        order.LeavesQuantity, order.CumulativeQuantity, 0, 0m,
                        reason, DateTimeOffset.UtcNow));
                });
        }
        catch (WalBackpressureException)
        {
            order.MarkRejected();
            _sink.Publish(new ExecutionEvent(
                order.Owner, order.ClOrdId, order.Symbol, order.Side, order.Status, ExecKind.Rejected,
                order.LeavesQuantity, order.CumulativeQuantity, 0, 0m,
                reason, DateTimeOffset.UtcNow));
        }
    }
}

public enum OrderSubmissionSource
{
    Manual,
    Algo,
}

public sealed record OrderSubmissionRequest(
    EndClientId Owner,
    string FirmId,
    string Symbol,
    ulong SecurityId,
    OrderSide Side,
    OrderType Type,
    long Quantity,
    decimal? Price,
    OrderSubmissionSource Source = OrderSubmissionSource.Manual,
    ulong? ParentAlgoId = null,
    int? AlgoSliceSeq = null);

/// <summary>
/// Outcome of <see cref="OrderSubmissionService.SubmitAsync"/>. The
/// discriminator is the <see cref="Kind"/> property; callers branch on it
/// to map to their transport (HTTP status / engine-side state transition).
/// All terminal cases also carry the <see cref="ClOrdId"/> when one was
/// allocated, so the caller can echo it to the user even when the order
/// was rejected synthetically.
/// </summary>
public sealed class OrderSubmissionResult
{
    public OrderSubmissionResultKind Kind { get; }
    public ulong ClOrdId { get; }
    public string? Reason { get; }
    public Exception? GatewayException { get; }

    private OrderSubmissionResult(OrderSubmissionResultKind kind, ulong clOrdId, string? reason, Exception? ex)
    {
        Kind = kind;
        ClOrdId = clOrdId;
        Reason = reason;
        GatewayException = ex;
    }

    public static OrderSubmissionResult Accepted(ulong clOrdId) =>
        new(OrderSubmissionResultKind.Accepted, clOrdId, null, null);
    public static OrderSubmissionResult Rejected(ulong clOrdId, string reason) =>
        new(OrderSubmissionResultKind.Rejected, clOrdId, reason, null);
    public static OrderSubmissionResult GatewayFailed(ulong clOrdId, Exception ex) =>
        new(OrderSubmissionResultKind.GatewayFailed, clOrdId, "gateway_unavailable", ex);
    public static OrderSubmissionResult WalBackpressure(string detail) =>
        new(OrderSubmissionResultKind.WalBackpressure, 0, detail, null);
    public static OrderSubmissionResult BadRequest(string reason) =>
        new(OrderSubmissionResultKind.BadRequest, 0, reason, null);
    public static OrderSubmissionResult Drained { get; } =
        new(OrderSubmissionResultKind.Drained, 0, "service draining", null);
}

public enum OrderSubmissionResultKind
{
    Accepted,
    Rejected,
    GatewayFailed,
    WalBackpressure,
    BadRequest,
    Drained,
}
