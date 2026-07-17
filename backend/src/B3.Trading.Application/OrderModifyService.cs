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
    private readonly Lifecycle.IDrainController? _reconciliationDrain;
    private readonly ReconciliationResolutionWriter _resolutionWriter;
    private readonly Routing.IRoutingInstructionResolver? _routingResolver;
    private readonly CompositeRiskAccountant? _accountant;
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
        ILogger<OrderModifyService> logger,
        Routing.IRoutingInstructionResolver? routingInstructionResolver = null,
        CompositeRiskAccountant? accountant = null,
        Lifecycle.IDrainController? reconciliationDrain = null,
        ReconciliationResolutionWriter? resolutionWriter = null)
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
        _reconciliationDrain = reconciliationDrain ?? drain as Lifecycle.IDrainController;
        _resolutionWriter = resolutionWriter ?? new ReconciliationResolutionWriter(
            new InMemoryReconciliationMarkerStore(),
            dispatcher,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                ReconciliationResolutionWriter>.Instance);
        _routingResolver = routingInstructionResolver;
        _accountant = accountant;
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
    ///   <item>Dispatch to the gateway. A proven pre-send failure is
    ///     terminalised by a second WAL event before returning failure.
    ///     An unclassified exception remains a durable ambiguous intent
    ///     for late-ER reconciliation.</item>
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

        // PR #316 P1. Reject cross-firm modifies — a JWT sub
        // registered in two firms knowing a ClOrdId from another
        // firm must not be able to mutate it. Treated as NotFound
        // (same as cross-owner) so existence is not leaked.
        if (req.FirmId is not null && !string.Equals(orig.FirmId, req.FirmId, StringComparison.Ordinal))
            return OrderModifyResult.NotFound;

        if (orig.Status is OrderStatus.Filled or OrderStatus.Cancelled
            or OrderStatus.Rejected or OrderStatus.Replaced)
        {
            return OrderModifyResult.Conflict("order is terminal");
        }

        // Q1.1 (#253) — pre-validate the optional TIF/StopPrice/
        // GoodTillDate overrides against the original's invariants so a
        // bad request fails BEFORE we burn a ClOrdID, run risk/margin,
        // append to the WAL or hit the gateway. Mirror of the merge that
        // Order.HydrateReplacement runs when the venue acks; throwing
        // there would surface as an opaque replace-rejected ER, here it
        // surfaces as a clean 400 with the invariant message.
        try
        {
            _ = Order.MergeReplacementOptionals(
                orig.Type, orig.TimeInForce, orig.StopPrice, orig.GoodTillDate,
                req.NewTimeInForce, req.NewStopPrice, req.NewGoodTillDate);
        }
        catch (ArgumentException ex)
        {
            return OrderModifyResult.BadRequest(ex.Message);
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

        try
        {
            Order.ValidatePriceForType(orig.Type, req.NewPrice ?? orig.Price);
        }
        catch (ArgumentException ex)
        {
            return OrderModifyResult.BadRequest(ex.Message);
        }

        if (!_replacements.TryClaimOriginal(req.OriginalClOrdId))
        {
            return OrderModifyResult.Conflict("a modify for this order is already in flight");
        }

        try
        {
            return await ModifyClaimedAsync(req, orig, req.NewPrice ?? orig.Price, ct).ConfigureAwait(false);
        }
        finally
        {
            _replacements.ReleaseOriginalClaim(req.OriginalClOrdId);
        }
    }

    private async Task<OrderModifyResult> ModifyClaimedAsync(
        OrderModifyRequest req,
        Order orig,
        decimal? effectivePrice,
        CancellationToken ct)
    {
        var newClOrdId = _clOrdIds.Generate(req.Owner);
        // #108 — DuplicateClOrdID defensive guard. The new ID must
        // be unique against BOTH the working book AND in-flight
        // pending replacements (an upstream NewClOrdId collision in
        // PendingReplacementRegistry.TryAdd would otherwise be
        // silently lost in the dispatch callback). Reject before
        // risk/margin to avoid burning a margin reserve we'd then
        // have to abort. See rubber-duck critique on PR for #153
        // follow-up — same class of "ignored TryAdd return" bug.
        if (_book.TryGet(newClOrdId, out _))
        {
            MetricsRegistry.ClOrdIdDuplicateDetected.Add(1,
                new KeyValuePair<string, object?>("op", "modify"),
                new KeyValuePair<string, object?>("scope", "book"));
            _logger.LogError(
                "Duplicate ClOrdID {NewClOrdId} for modify of {OriginalClOrdId} (owner {Owner}); refusing.",
                newClOrdId, req.OriginalClOrdId, req.Owner.Value);
            return OrderModifyResult.DuplicateClOrdId(newClOrdId);
        }
        if (_replacements.TryGet(newClOrdId, out _))
        {
            MetricsRegistry.ClOrdIdDuplicateDetected.Add(1,
                new KeyValuePair<string, object?>("op", "modify"),
                new KeyValuePair<string, object?>("scope", "pending"));
            _logger.LogError(
                "Duplicate ClOrdID {NewClOrdId} collides with in-flight pending replacement; refusing modify of {OriginalClOrdId}.",
                newClOrdId, req.OriginalClOrdId);
            return OrderModifyResult.DuplicateClOrdId(newClOrdId);
        }
        var effectiveLeaves = req.NewQuantity - orig.CumulativeQuantity;
        // Q1.2 (#254). Resolve the effective TIF/StopPrice/GoodTillDate
        // through the same merge the domain ctor will use (null on the
        // request = inherit the original) so the pipeline's stop-trigger,
        // IOC/FOK leftover, GFA-phase and GTD-bounds checks see the same
        // post-replace values that would land on the new Order.
        TimeInForce effTif;
        decimal? effStop;
        DateTimeOffset? effGtd;
        try
        {
            (effTif, effStop, effGtd) = Order.MergeReplacementOptionals(
                orig.Type, orig.TimeInForce, orig.StopPrice, orig.GoodTillDate,
                req.NewTimeInForce, req.NewStopPrice, req.NewGoodTillDate);
        }
        catch (ArgumentException ex)
        {
            // Same posture as submit: domain cross-field invariants
            // surface as BadRequest (400) before any WAL append.
            return OrderModifyResult.BadRequest(ex.Message);
        }
        // PR #316 P1. Forward the original's SubAccountId (the
        // replacement inherits it — see Order.cs ctor) so
        // SubAccountLimitsCheck enforces per-(firm, sub-account)
        // position/notional/open-order caps AND rejects modifies
        // targeting a deactivated sub-account. Without this the
        // check no-ops on null and the sub-account gate is bypassed.
        var riskCtx = new RiskContext(
            req.Owner, orig.FirmId, orig.Symbol, orig.Side, orig.Type,
            req.NewQuantity, effectivePrice,
            ReplaceOriginalClOrdId: req.OriginalClOrdId,
            EffectiveLeavesQuantity: effectiveLeaves,
            TimeInForce: effTif,
            StopPrice: effStop,
            GoodTillDate: effGtd,
            SubAccountId: orig.SubAccountId,
            // #473. Resolve the routing instruction for the modify so
            // the per-scope whitelist gates the replace identically to
            // a fresh submit. Resolved off the original Order since
            // replace inherits routing intent (owner/firm/sub-account
            // are immutable across modify).
            RoutingInstruction: _routingResolver?.TryResolve(orig));

        // Ordering note (RFC docs/rfcs/risk-pipeline-ordering-v0.md, #262):
        // risk evaluation runs *pre-WAL* on the modify path. #337 closed
        // the audit gap below: a rejected modify dispatches an
        // OrderReplaceRejectedEvent so /executions/history, the CVM /
        // drop-copy / touch consumers and the FE blotter all observe
        // the burned ClOrdId + reason. Replay treats the event as a
        // pure no-op for book/ownership/margin state (advances the
        // ClOrdId watermark only).
        var decision = _risk.Evaluate(riskCtx);
        if (!decision.Approved)
        {
            var reason = decision.Reason ?? "risk_rejected";
            var code = decision.Code ?? "risk_rejected";
            MetricsRegistry.OrdersRejectedByRisk.Add(1,
                new KeyValuePair<string, object?>("reason", reason),
                new KeyValuePair<string, object?>("code", code),
                new KeyValuePair<string, object?>("path", "modify"),
                new KeyValuePair<string, object?>("firmId", orig.FirmId));
            PublishReplaceRejected(req, orig, newClOrdId, "risk", reason);
            return OrderModifyResult.RiskRejected(reason, code);
        }

        // Margin Prepare: reserve only the upsize delta. The
        // coordinator no-ops on sells / markets / non-positive notionals.
        var newRemainingNotional = (orig.Side == OrderSide.Buy
                                    && orig.Type.IsMarginBearing()
                                    && effectivePrice is { } px)
            ? px * effectiveLeaves
            : 0m;
        var marginDecision = await _replaceMargin.PrepareReplaceAsync(
            req.OriginalClOrdId,
            newClOrdId,
            req.Owner,
            orig.FirmId,
            newRemainingNotional,
            ct);
        if (!marginDecision.Approved)
        {
            var reason = marginDecision.Reason ?? "margin_rejected";
            var code = marginDecision.Code ?? "margin_rejected";
            MetricsRegistry.OrdersRejectedByRisk.Add(1,
                new KeyValuePair<string, object?>("reason", reason),
                new KeyValuePair<string, object?>("code", code),
                new KeyValuePair<string, object?>("path", "modify"),
                new KeyValuePair<string, object?>("firmId", orig.FirmId));
            PublishReplaceRejected(req, orig, newClOrdId, "margin", reason);
            return OrderModifyResult.RiskRejected(reason, code);
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
            NewPrice: effectivePrice,
            FirmId: orig.FirmId,
            ParentAlgoId: orig.ParentAlgoId,
            AlgoSliceSeq: orig.AlgoSliceSeq,
            RequestedTimeInForce: req.NewTimeInForce,
            RequestedStopPrice: req.NewStopPrice,
            RequestedGoodTillDate: req.NewGoodTillDate);

        try
        {
            var dispatched = _dispatcher.DispatchIf(
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
                    NewPrice = effectivePrice,
                    ParentAlgoId = orig.ParentAlgoId,
                    AlgoSliceSeq = orig.AlgoSliceSeq,
                    RequestedTimeInForce = req.NewTimeInForce?.ToString(),
                    RequestedStopPrice = req.NewStopPrice,
                    RequestedGoodTillDate = req.NewGoodTillDate,
                },
                () => orig.Status is not (OrderStatus.Filled or OrderStatus.Cancelled
                    or OrderStatus.Rejected or OrderStatus.Replaced)
                    && req.NewQuantity > orig.CumulativeQuantity,
                () =>
                {
                    if (!_replacements.TryAddClaimed(intent))
                        throw new InvalidOperationException(
                            $"Original ClOrdID {req.OriginalClOrdId} was not exclusively claimed.");
                    _ownership.RegisterReplaceLink(req.OriginalClOrdId, newClOrdId);
                });
            if (!dispatched.Applied)
            {
                _replaceMargin.AbortReplace(newClOrdId);
                return OrderModifyResult.Conflict("order became terminal or changed while modify was processing");
            }
        }
        catch (WalBackpressureException ex)
        {
            // Roll back the margin Prepare since neither the registry
            // nor the ownership link was populated.
            _replaceMargin.AbortReplace(newClOrdId);
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "orders.modify"),
                new KeyValuePair<string, object?>("firmId", orig.FirmId));
            return OrderModifyResult.WalBackpressure(ex.Message);
        }

        _accountant?.RecordAccepted(riskCtx);

        try
        {
            await _gateway.CancelReplaceAsync(
                orig, newClOrdId, req.NewQuantity, effectivePrice,
                req.NewTimeInForce, req.NewStopPrice, req.NewGoodTillDate, ct);
        }
        catch (Exception ex) when (
            ex is ExchangeGatewayPreSendException || _gateway is IExchangeGatewayPreSendOnly)
        {
            MetricsRegistry.OrdersGatewayFailed.Add(1,
                new KeyValuePair<string, object?>("path", "modify"),
                new KeyValuePair<string, object?>("firmId", orig.FirmId));
            _logger.LogError(ex,
                "Gateway proved cancel-replace was not sent for orig {OrigClOrdId} new {NewClOrdId}; terminalising.",
                req.OriginalClOrdId, newClOrdId);
            var marker = new ReconciliationMarker(
                ReconciliationMarkerKind.ReplacePreSend,
                req.OriginalClOrdId,
                newClOrdId,
                req.Owner.Value);
            ReconciliationResolutionResult resolution;
            try
            {
                resolution = await _resolutionWriter.ResolveAsync(
                    marker,
                    new OrderReplacePreSendFailedEvent
                    {
                        OriginalClOrdId = req.OriginalClOrdId,
                        NewClOrdId = newClOrdId,
                        EndClientId = req.Owner.Value,
                        Reason = "gateway_unavailable",
                    },
                    () =>
                    {
                        _replacements.TryConsume(newClOrdId, out _);
                        _ownership.RemoveCancelLink(newClOrdId);
                        _replaceMargin.AbortReplace(newClOrdId);
                        _sink.Publish(new ExecutionEvent(
                            req.Owner, newClOrdId, orig.Symbol, orig.Side,
                            OrderStatus.Rejected, ExecKind.Rejected,
                            LeavesQuantity: 0, CumulativeQuantity: 0,
                            LastQuantity: 0, LastPrice: 0m,
                            RejectReason: "gateway_unavailable",
                            TimestampUtc: DateTimeOffset.UtcNow,
                            FirmId: orig.FirmId));
                    }).ConfigureAwait(false);
            }
            catch (Exception resolutionEx)
            {
                return FailResolutionForReconciliation(
                    newClOrdId, "pre_send_resolution_not_durable", resolutionEx);
            }
            if (!resolution.Durable)
            {
                if (resolution.MarkerDurable)
                {
                    _dispatcher.RunExclusive(() =>
                    {
                        _replacements.TryConsume(newClOrdId, out _);
                        _ownership.RemoveCancelLink(newClOrdId);
                        _replaceMargin.AbortReplace(newClOrdId);
                    });
                }
                return FailResolutionForReconciliation(
                    newClOrdId, "pre_send_resolution_not_durable",
                    resolution.Failure!);
            }
            return OrderModifyResult.GatewayFailed(newClOrdId, ex);
        }
        catch (Exception ex)
        {
            MetricsRegistry.OrdersGatewayFailed.Add(1,
                new KeyValuePair<string, object?>("path", "modify"),
                new KeyValuePair<string, object?>("firmId", orig.FirmId));
            _logger.LogWarning(ex,
                "Gateway cancel-replace outcome is ambiguous for orig {OrigClOrdId} new {NewClOrdId}; retaining intent.",
                req.OriginalClOrdId, newClOrdId);

            var heldAt = DateTimeOffset.UtcNow;
            var marker = new ReconciliationMarker(
                ReconciliationMarkerKind.ReplaceAmbiguous,
                req.OriginalClOrdId,
                newClOrdId,
                req.Owner.Value,
                newRemainingNotional,
                heldAt);
            ReconciliationResolutionResult resolution;
            try
            {
                resolution = await _resolutionWriter.ResolveAsync(
                    marker,
                    new OrderReplaceAmbiguousMarginHeldEvent
                    {
                        NewClOrdId = newClOrdId,
                        OriginalClOrdId = req.OriginalClOrdId,
                        EndClientId = req.Owner.Value,
                        NewRemainingNotional = newRemainingNotional,
                        HeldAtUtc = heldAt,
                    },
                    () => _replacements.MarkAmbiguousMarginHeld(
                        newClOrdId, heldAt, newRemainingNotional))
                    .ConfigureAwait(false);
            }
            catch (Exception resolutionEx)
            {
                return FailResolutionForReconciliation(
                    newClOrdId, "ambiguous_resolution_not_durable", resolutionEx);
            }
            if (!resolution.Durable)
            {
                if (resolution.MarkerDurable)
                {
                    _dispatcher.RunExclusive(() =>
                        _replacements.MarkAmbiguousMarginHeld(
                            newClOrdId, heldAt, newRemainingNotional));
                }
                return FailResolutionForReconciliation(
                    newClOrdId, "ambiguous_resolution_not_durable",
                    resolution.Failure!);
            }
            return OrderModifyResult.GatewayAmbiguous(newClOrdId, ex);
        }

        MetricsRegistry.OrdersModifyRequested.Add(1,
            new KeyValuePair<string, object?>("symbol", orig.Symbol),
            new KeyValuePair<string, object?>("side", orig.Side.ToString()),
            new KeyValuePair<string, object?>("firmId", orig.FirmId));

        return OrderModifyResult.Accepted(newClOrdId);
    }

    private OrderModifyResult FailResolutionForReconciliation(
        ulong newClOrdId,
        string reason,
        Exception exception)
    {
        if (exception is WalBackpressureException)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "orders.modify.resolution"));
        }
        _reconciliationDrain?.BeginDrain("wal_replace_resolution_reconciliation_required");
        _logger.LogCritical(exception,
            "Replace resolution {Reason} for new ClOrdID {NewClOrdId}; ingress is draining and operator reconciliation is required.",
            reason, newClOrdId);
        return OrderModifyResult.ReconciliationRequired(newClOrdId, reason, exception);
    }

    /// <summary>
    /// #337 — durable audit row for a modify rejected pre-WAL by the
    /// risk pipeline or margin coordinator. Mirrors the submit-side
    /// <c>OrderSubmissionService.PublishSyntheticRejection</c> shape:
    /// dispatch the structured WAL event, and in the same commit
    /// callback emit the live <see cref="ExecutionEvent"/> the FE
    /// blotter listens on. WAL backpressure falls back to a sink-only
    /// publish so the trader still observes the reject even when WAL
    /// append is degraded (same posture as the submit path).
    /// </summary>
    private void PublishReplaceRejected(
        OrderModifyRequest req,
        Order orig,
        ulong newClOrdId,
        string source,
        string reason)
    {
        var evt = new OrderReplaceRejectedEvent
        {
            OriginalClOrdId = req.OriginalClOrdId,
            NewClOrdId = newClOrdId,
            EndClientId = req.Owner.Value,
            FirmId = orig.FirmId,
            Symbol = orig.Symbol,
            SecurityId = orig.SecurityId,
            Side = orig.Side.ToString(),
            Type = orig.Type.ToString(),
            RequestedQuantity = req.NewQuantity,
            RequestedPrice = req.NewPrice,
            RequestedTimeInForce = req.NewTimeInForce?.ToString(),
            RequestedStopPrice = req.NewStopPrice,
            RequestedGoodTillDate = req.NewGoodTillDate,
            Source = source,
            Reason = reason,
            ParentAlgoId = orig.ParentAlgoId,
            AlgoSliceSeq = orig.AlgoSliceSeq,
        };

        Action publishLive = () => _sink.Publish(new ExecutionEvent(
            req.Owner, newClOrdId, orig.Symbol, orig.Side,
            // Status is the ORIGINAL order's current status — the
            // replace was rejected; the original keeps Working /
            // PartiallyFilled / etc.
            orig.Status, ExecKind.Rejected,
            LeavesQuantity: orig.LeavesQuantity,
            CumulativeQuantity: orig.CumulativeQuantity,
            LastQuantity: 0, LastPrice: 0m,
            RejectReason: reason,
            TimestampUtc: DateTimeOffset.UtcNow,
            FirmId: orig.FirmId));

        try
        {
            _dispatcher.Dispatch(evt, publishLive);
        }
        catch (WalBackpressureException)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "orders.modify.reject"),
                new KeyValuePair<string, object?>("firmId", orig.FirmId));
            publishLive();
        }
    }
}

/// <summary>
/// Inputs for <see cref="OrderModifyService.ModifyAsync"/>.
///
/// <para>
/// Q1.1 (#253). <see cref="NewPrice"/> and the trailing optionals —
/// <see cref="NewTimeInForce"/>,
/// <see cref="NewStopPrice"/>, <see cref="NewGoodTillDate"/> — follow
/// follow the modify-pipeline override convention: <b>null = inherit the
/// original order's value</b> across the cancel-replace boundary;
/// <b>non-null = replace it with the supplied value</b>. Domain
/// invariants (StopPrice required iff Stop*; GoodTillDate required
/// iff TIF==GTD; auto-cleared when TIF moves away from GTD) are
/// re-evaluated on the merged result and a violation surfaces as
/// <see cref="OrderModifyResultKind.BadRequest"/> before any WAL
/// append or gateway dispatch.
/// </para>
/// </summary>
public sealed record OrderModifyRequest(
    EndClientId Owner,
    ulong OriginalClOrdId,
    long NewQuantity,
    decimal? NewPrice,
    TimeInForce? NewTimeInForce = null,
    decimal? NewStopPrice = null,
    DateTimeOffset? NewGoodTillDate = null,
    /// <summary>
    /// PR #316 P1. Caller's firm scope. When non-null, the service
    /// rejects (as NotFound) modifies whose original order belongs
    /// to a different firm — same isolation guard as
    /// <c>OrdersEndpoints</c>' GET path. Optional for back-compat
    /// with internal callers (algo engine, GTD scheduler) that
    /// already operate on a known order; user-facing transports
    /// (REST, FIXP) must populate it.
    /// </summary>
    string? FirmId = null);

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
    /// <summary>
    /// #288 — stable machine-readable code (e.g. <c>min_tick_size</c>,
    /// <c>price_collar</c>) on risk-rejection paths. Null on accept.
    /// </summary>
    public string? Code { get; }
    public Exception? GatewayException { get; }

    private OrderModifyResult(OrderModifyResultKind kind, ulong newClOrdId, string? reason, string? code, Exception? ex)
    {
        Kind = kind;
        NewClOrdId = newClOrdId;
        Reason = reason;
        Code = code;
        GatewayException = ex;
    }

    public static OrderModifyResult Accepted(ulong newClOrdId) =>
        new(OrderModifyResultKind.Accepted, newClOrdId, null, null, null);
    public static OrderModifyResult RiskRejected(string reason, string? code = null) =>
        new(OrderModifyResultKind.RiskRejected, 0, reason, code, null);
    public static OrderModifyResult GatewayFailed(ulong newClOrdId, Exception ex) =>
        new(OrderModifyResultKind.GatewayFailed, newClOrdId, "gateway_unavailable", "gateway_unavailable", ex);
    public static OrderModifyResult GatewayAmbiguous(ulong newClOrdId, Exception ex) =>
        new(OrderModifyResultKind.GatewayAmbiguous, newClOrdId, "send_ambiguous", "send_ambiguous", ex);
    public static OrderModifyResult ReconciliationRequired(
        ulong newClOrdId, string reason, Exception ex) =>
        new(OrderModifyResultKind.ReconciliationRequired, newClOrdId, reason, "reconciliation_required", ex);
    public static OrderModifyResult WalBackpressure(string detail) =>
        new(OrderModifyResultKind.WalBackpressure, 0, detail, "wal_backpressure", null);
    public static OrderModifyResult BadRequest(string reason) =>
        new(OrderModifyResultKind.BadRequest, 0, reason, "bad_request", null);
    public static OrderModifyResult Conflict(string reason) =>
        new(OrderModifyResultKind.Conflict, 0, reason, "conflict", null);
    public static OrderModifyResult NotFound { get; } =
        new(OrderModifyResultKind.NotFound, 0, null, null, null);
    public static OrderModifyResult Drained { get; } =
        new(OrderModifyResultKind.Drained, 0, "service draining", "service_draining", null);
    /// <summary>
    /// #108 — DuplicateClOrdID guard. The just-allocated new ClOrdID
    /// already exists in <see cref="WorkingOrderBook"/> or
    /// <see cref="PendingReplacementRegistry"/>. No risk/margin run,
    /// no WAL event, no gateway call. Endpoints map to <c>409 Conflict</c>.
    /// </summary>
    public static OrderModifyResult DuplicateClOrdId(ulong newClOrdId) =>
        new(OrderModifyResultKind.DuplicateClOrdId, newClOrdId, "duplicate_clordid", "duplicate_clordid", null);
}

public enum OrderModifyResultKind
{
    Accepted,
    NotFound,
    Conflict,
    BadRequest,
    RiskRejected,
    GatewayFailed,
    GatewayAmbiguous,
    ReconciliationRequired,
    WalBackpressure,
    Drained,
    DuplicateClOrdId,
}
