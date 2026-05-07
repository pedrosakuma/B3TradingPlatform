using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Application;

/// <summary>
/// Slice 4 of #122. The "modify (cancel-replace) an order" pipeline,
/// counterpart to <see cref="OrderSubmissionService"/>. Both manual
/// modifies (PUT /orders/{clOrdId}) and any future engine-driven
/// replace flows funnel through here so risk evaluation, margin
/// coordination, in-flight tracking, WAL durability, and gateway
/// dispatch live in exactly one place.
///
/// <para>
/// The service is stateless apart from its injected collaborators.
/// All per-call mutable state lives on the returned
/// <see cref="OrderModifyResult"/> or in the underlying books and
/// registries. Callers translate the result into their transport
/// (HTTP status + JSON body for endpoints).
/// </para>
/// </summary>
public sealed class OrderModifyService
{
    private readonly ClOrdIdPrefixRegistry _clOrdIds;
    private readonly OrderOwnershipMap _ownership;
    private readonly WorkingOrderBook _book;
    private readonly IExchangeGateway _gateway;
    private readonly IExecutionEventSink _sink;
    private readonly RiskPipeline _risk;
    private readonly IReplaceMarginCoordinator _replaceMargin;
    private readonly PendingReplacementRegistry _replacements;
    private readonly EventDispatcher _dispatcher;
    private readonly Lifecycle.IDrainGate _drain;
    private readonly ILogger<OrderModifyService> _logger;

    public OrderModifyService(
        ClOrdIdPrefixRegistry clOrdIds,
        OrderOwnershipMap ownership,
        WorkingOrderBook book,
        IExchangeGateway gateway,
        IExecutionEventSink sink,
        RiskPipeline risk,
        IReplaceMarginCoordinator replaceMargin,
        PendingReplacementRegistry replacements,
        EventDispatcher dispatcher,
        Lifecycle.IDrainGate drain,
        ILogger<OrderModifyService> logger)
    {
        _clOrdIds = clOrdIds;
        _ownership = ownership;
        _book = book;
        _gateway = gateway;
        _sink = sink;
        _risk = risk;
        _replaceMargin = replaceMargin;
        _replacements = replacements;
        _dispatcher = dispatcher;
        _drain = drain;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full modify pipeline for one order. Side-effect order
    /// is intentional and matches the rationale in the slice-2
    /// rubber-duck pass:
    /// <list type="number">
    ///   <item>Validate ownership + non-terminal status.</item>
    ///   <item>Reject if a modify for the same orig is already in
    ///     flight (prevents two pending replaces from racing the
    ///     venue, which the FIXP spec doesn't disambiguate).</item>
    ///   <item>Allocate a new ClOrdID up-front so risk + margin can
    ///     bind to it.</item>
    ///   <item>Run the pre-trade risk pipeline with
    ///     <see cref="RiskContext.ReplaceOriginalClOrdId"/> set so
    ///     <c>NoNakedShortCheck</c> projects the swap.</item>
    ///   <item>Prepare margin (delta-only reservation; downsize is
    ///     a no-op).</item>
    ///   <item>Persist the WAL event AND mutate the registry +
    ///     ownership map in a single dispatch — both happen or
    ///     neither does.</item>
    ///   <item>Dispatch to the gateway. On exception, abort margin +
    ///     remove the in-flight intent and synthesize a rejection so
    ///     the trader sees the failure in the blotter; the WAL
    ///     event stays (replay tolerates "intent without resolution"
    ///     the same way a synthetic-rejected ER terminates the orig
    ///     today).</item>
    /// </list>
    /// </summary>
    public async Task<OrderModifyResult> ModifyAsync(OrderModifyRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (_drain.IsDraining)
        {
            MetricsRegistry.DrainRejections.Add(1,
                new KeyValuePair<string, object?>("route", "PUT /orders"));
            return OrderModifyResult.Drained;
        }

        if (req.NewQuantity <= 0)
            return OrderModifyResult.BadRequest("quantity must be positive");

        if (!_book.TryGet(req.OriginalClOrdId, out var orig) || orig is null)
            return OrderModifyResult.NotFound;

        if (orig.Owner != req.Owner)
            return OrderModifyResult.NotFound; // do not leak existence cross-owner

        if (orig.Status is OrderStatus.Filled or OrderStatus.Cancelled
            or OrderStatus.Rejected or OrderStatus.Replaced)
        {
            return OrderModifyResult.Conflict("order is terminal");
        }

        // Slice 1 of #132: refuse Modify against a stale-flagged order.
        // Same rationale as the cancel gate: the venue most likely
        // doesn't know the original ClOrdID, so a CancelReplace would
        // just burn a new ID. Operator clears stale (admin endpoint or
        // real terminal ER auto-clear) before reissuing.
        if (orig.IsStale)
            return OrderModifyResult.Conflict("order is marked stale");

        if (req.NewQuantity <= orig.CumulativeQuantity)
        {
            // Modifying the total qty to or below already-filled cum
            // means there is nothing left to leave on the venue —
            // semantically a cancel, not a modify, and the venue
            // will reject anyway.
            return OrderModifyResult.BadRequest(
                $"new quantity ({req.NewQuantity}) must exceed already-filled quantity ({orig.CumulativeQuantity})");
        }

        if (_replacements.IsOriginalInFlight(req.OriginalClOrdId))
        {
            return OrderModifyResult.Conflict("a modify for this order is already in flight");
        }

        var newClOrdId = _clOrdIds.Generate(req.Owner);
        var effectiveLeaves = req.NewQuantity - orig.CumulativeQuantity;
        var riskCtx = new RiskContext(
            req.Owner, orig.FirmId, orig.Symbol, orig.Side, orig.Type,
            req.NewQuantity, req.NewPrice,
            ReplaceOriginalClOrdId: req.OriginalClOrdId,
            EffectiveLeavesQuantity: effectiveLeaves);

        var decision = _risk.Evaluate(riskCtx);
        if (!decision.Approved)
        {
            MetricsRegistry.OrdersRejectedByRisk.Add(1,
                new KeyValuePair<string, object?>("reason", decision.Reason ?? "risk_rejected"),
                new KeyValuePair<string, object?>("path", "modify"));
            return OrderModifyResult.RiskRejected(decision.Reason ?? "risk_rejected");
        }

        // Margin Prepare: reserve only the upsize delta. The
        // coordinator no-ops on sells / markets / non-positive notionals.
        var newRemainingNotional = (orig.Side == OrderSide.Buy
                                    && orig.Type == OrderType.Limit
                                    && req.NewPrice is { } px)
            ? px * effectiveLeaves
            : 0m;
        var marginDecision = await _replaceMargin.PrepareReplaceAsync(
            req.OriginalClOrdId, newClOrdId, req.Owner, newRemainingNotional, ct);
        if (!marginDecision.Approved)
        {
            MetricsRegistry.OrdersRejectedByRisk.Add(1,
                new KeyValuePair<string, object?>("reason", marginDecision.Reason ?? "margin_rejected"),
                new KeyValuePair<string, object?>("path", "modify"));
            return OrderModifyResult.RiskRejected(marginDecision.Reason ?? "margin_rejected");
        }

        var intent = new OrderReplacementIntent(
            OriginalClOrdId: req.OriginalClOrdId,
            NewClOrdId: newClOrdId,
            Owner: req.Owner,
            Symbol: orig.Symbol,
            SecurityId: orig.SecurityId,
            Side: orig.Side,
            Type: orig.Type,
            NewQuantity: req.NewQuantity,
            NewPrice: req.NewPrice,
            FirmId: orig.FirmId,
            ParentAlgoId: orig.ParentAlgoId,
            AlgoSliceSeq: orig.AlgoSliceSeq);

        try
        {
            _dispatcher.Dispatch(
                new OrderReplaceRequestedEvent
                {
                    OriginalClOrdId = req.OriginalClOrdId,
                    NewClOrdId = newClOrdId,
                    EndClientId = req.Owner.Value,
                    FirmId = orig.FirmId,
                    Symbol = orig.Symbol,
                    SecurityId = orig.SecurityId,
                    Side = orig.Side.ToString(),
                    Type = orig.Type.ToString(),
                    NewQuantity = req.NewQuantity,
                    NewPrice = req.NewPrice,
                    ParentAlgoId = orig.ParentAlgoId,
                    AlgoSliceSeq = orig.AlgoSliceSeq,
                },
                () =>
                {
                    _replacements.TryAdd(intent);
                    _ownership.RegisterReplaceLink(req.OriginalClOrdId, newClOrdId);
                });
        }
        catch (WalBackpressureException ex)
        {
            // Roll back the margin Prepare since neither the registry
            // nor the ownership link was populated.
            _replaceMargin.AbortReplace(newClOrdId);
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "orders.modify"));
            return OrderModifyResult.WalBackpressure(ex.Message);
        }

        try
        {
            await _gateway.CancelReplaceAsync(orig, newClOrdId, req.NewQuantity, req.NewPrice, ct);
        }
        catch (Exception ex)
        {
            MetricsRegistry.OrdersGatewayFailed.Add(1,
                new KeyValuePair<string, object?>("path", "modify"));
            _logger.LogError(ex,
                "Gateway CancelReplaceAsync failed for orig {OrigClOrdId} new {NewClOrdId}; rolling back.",
                req.OriginalClOrdId, newClOrdId);
            // Roll back: drop intent, abort margin delta. Original
            // order keeps its pre-modify state (Working /
            // PartiallyFilled). Surface a synthetic Rejected ER under
            // the new ClOrdID so the trader's blotter shows the
            // failure rather than a phantom "modify pending forever".
            _replacements.TryConsume(newClOrdId, out _);
            _replaceMargin.AbortReplace(newClOrdId);
            _sink.Publish(new ExecutionEvent(
                req.Owner, newClOrdId, orig.Symbol, orig.Side,
                OrderStatus.Rejected, ExecKind.Rejected,
                LeavesQuantity: 0, CumulativeQuantity: 0,
                LastQuantity: 0, LastPrice: 0m,
                RejectReason: "gateway_unavailable",
                TimestampUtc: DateTimeOffset.UtcNow));
            return OrderModifyResult.GatewayFailed(newClOrdId, ex);
        }

        MetricsRegistry.OrdersModifyRequested.Add(1,
            new KeyValuePair<string, object?>("symbol", orig.Symbol),
            new KeyValuePair<string, object?>("side", orig.Side.ToString()));

        return OrderModifyResult.Accepted(newClOrdId);
    }
}

public sealed record OrderModifyRequest(
    EndClientId Owner,
    ulong OriginalClOrdId,
    long NewQuantity,
    decimal? NewPrice);

/// <summary>
/// Outcome of <see cref="OrderModifyService.ModifyAsync"/>. The
/// discriminator is the <see cref="Kind"/> property; callers branch
/// on it to map to their transport.
/// </summary>
public sealed class OrderModifyResult
{
    public OrderModifyResultKind Kind { get; }
    public ulong NewClOrdId { get; }
    public string? Reason { get; }
    public Exception? GatewayException { get; }

    private OrderModifyResult(OrderModifyResultKind kind, ulong newClOrdId, string? reason, Exception? ex)
    {
        Kind = kind;
        NewClOrdId = newClOrdId;
        Reason = reason;
        GatewayException = ex;
    }

    public static OrderModifyResult Accepted(ulong newClOrdId) =>
        new(OrderModifyResultKind.Accepted, newClOrdId, null, null);
    public static OrderModifyResult RiskRejected(string reason) =>
        new(OrderModifyResultKind.RiskRejected, 0, reason, null);
    public static OrderModifyResult GatewayFailed(ulong newClOrdId, Exception ex) =>
        new(OrderModifyResultKind.GatewayFailed, newClOrdId, "gateway_unavailable", ex);
    public static OrderModifyResult WalBackpressure(string detail) =>
        new(OrderModifyResultKind.WalBackpressure, 0, detail, null);
    public static OrderModifyResult BadRequest(string reason) =>
        new(OrderModifyResultKind.BadRequest, 0, reason, null);
    public static OrderModifyResult Conflict(string reason) =>
        new(OrderModifyResultKind.Conflict, 0, reason, null);
    public static OrderModifyResult NotFound { get; } =
        new(OrderModifyResultKind.NotFound, 0, null, null);
    public static OrderModifyResult Drained { get; } =
        new(OrderModifyResultKind.Drained, 0, "service draining", null);
}

public enum OrderModifyResultKind
{
    Accepted,
    NotFound,
    Conflict,
    BadRequest,
    RiskRejected,
    GatewayFailed,
    WalBackpressure,
    Drained,
}
