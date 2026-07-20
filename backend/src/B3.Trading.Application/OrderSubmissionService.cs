using B3.Trading.Application.Observability;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Application.UserBots;
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
    private readonly Lifecycle.IDrainController _drain;
    private readonly IUserBotOrderMappingRegistry? _botMappings;
    private readonly Scheduling.GtdExpirationScheduler? _gtdScheduler;
    private readonly Scheduling.IocFokWatchdog? _iocWatchdog;
    private readonly Routing.IRoutingInstructionResolver? _routingResolver;
    private readonly SymbolDirectory? _symbolDirectory;
    private readonly ILogger<OrderSubmissionService> _logger;
    private readonly OutboundMutationLedger? _outboundLedger;
    private readonly NewOrderApprovalFactory? _approvalFactory;
    private readonly NewOrderOutboundCoordinator? _outboundCoordinator;
    private readonly RestOrderIdempotencyStore? _restIdempotency;
    private readonly TimeProvider _clock;
    private readonly IOutboundRecoveryGate _outboundRecovery;

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
        Lifecycle.IDrainController drain,
        ILogger<OrderSubmissionService> logger,
        IUserBotOrderMappingRegistry? botMappings = null,
        Scheduling.GtdExpirationScheduler? gtdScheduler = null,
        Scheduling.IocFokWatchdog? iocWatchdog = null,
        Routing.IRoutingInstructionResolver? routingInstructionResolver = null,
        SymbolDirectory? symbolDirectory = null,
        OutboundMutationLedger? outboundLedger = null,
        NewOrderApprovalFactory? approvalFactory = null,
        NewOrderOutboundCoordinator? outboundCoordinator = null,
        RestOrderIdempotencyStore? restIdempotency = null,
        TimeProvider? clock = null,
        IOutboundRecoveryGate? outboundRecovery = null)
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
        _botMappings = botMappings;
        _gtdScheduler = gtdScheduler;
        _iocWatchdog = iocWatchdog;
        _routingResolver = routingInstructionResolver;
        _symbolDirectory = symbolDirectory;
        _logger = logger;
        _outboundLedger = outboundLedger;
        _approvalFactory = approvalFactory;
        _outboundCoordinator = outboundCoordinator;
        _restIdempotency = restIdempotency;
        _clock = clock ?? TimeProvider.System;
        _outboundRecovery = outboundRecovery ?? ImmediateOutboundRecoveryGate.Instance;
    }

    /// <summary>
    /// Runs the full submit pipeline for one order. The caller is
    /// responsible for parsing/validating the request shape; this method
    /// validates business invariants (positive quantity, non-zero
    /// SecurityId) and short-circuits with the appropriate result.
    /// </summary>
    public async Task<OrderSubmissionResult> SubmitAsync(OrderSubmissionRequest req, CancellationToken ct)
    {
        if (req is null
                ? !_outboundRecovery.IsReady
                : !_outboundRecovery.IsBusinessIngressOpen(req.FirmId))
        {
            return OrderSubmissionResult.Drained;
        }
        ArgumentNullException.ThrowIfNull(req);

        if (req.Source == OrderSubmissionSource.Algo)
        {
            if (req.AlgoOriginIdentity is not { } algoOrigin
                || algoOrigin.ActionKind != AlgoOutboundActionKind.NewChild
                || algoOrigin.ParentAlgoId != req.ParentAlgoId
                || algoOrigin.Sequence != req.AlgoSliceSeq)
            {
                return OrderSubmissionResult.BadRequest("algo origin identity must match the child slice");
            }
            if (_outboundLedger?.TryGetByAlgoOrigin(req.FirmId, algoOrigin, out var existing) == true
                && existing is not null)
            {
                var existingClOrdId = existing.Attempts.LastOrDefault()?.ClOrdId
                    ?? existing.PrimaryClOrdId;
                return existing.State is OutboundMutationState.VenueAcknowledged
                    or OutboundMutationState.TransportWriteCompleted
                    ? OrderSubmissionResult.Accepted(existing.MutationId, existingClOrdId)
                    : OrderSubmissionResult.ReconciliationRequired(existing.MutationId, existingClOrdId);
            }
        }

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

        // Q3.4 (#284) — pass-1 review (#297) follow-up #298. Defensive
        // gate covering non-REST callers (algo engine, FIXP bot intake):
        // the B3.EntryPoint.Client SDK 0.14.3 has no refresh-policy
        // field, so any iceberg whose policy is not Always would be
        // silently downgraded to Always on the wire. Reject here so the
        // semantic discrepancy never enters the WAL. The Domain enum
        // (DisplayResetPolicy.Always/OnPartialFill/Never) is retained
        // so this guard can be lifted once the SDK exposes the field.
        if (req.DisplayResetPolicy is { } drp && drp != Domain.DisplayResetPolicy.Always)
            return OrderSubmissionResult.BadRequest(
                $"displayResetPolicy={drp} is not supported by the current entrypoint SDK; " +
                "supported: Always. Track issue #298.");

        var mutationId = req.IdempotencyContext?.Binding.MutationId
            ?? OutboundMutationId.New();
        var clOrdId = _clOrdIds.Generate(req.Owner);
        // #108 — DuplicateClOrdID defensive guard. The registry's
        // per-end-client counter is allocated atomically, so two
        // concurrent submits never collide here. The realistic
        // failure mode is a snapshot/WAL-replay regression where
        // the counter watermark fell behind the persisted state at
        // recovery — we'd then re-allocate IDs already in the book.
        // Reject pre-WAL: no event appended, no order created, no
        // gateway message sent. Operators alert on the metric and
        // the next host restart with a fixed snapshot recovers.
        if (_book.TryGet(clOrdId, out _))
        {
            MetricsRegistry.ClOrdIdDuplicateDetected.Add(1,
                new KeyValuePair<string, object?>("op", "submit"),
                new KeyValuePair<string, object?>("scope", "book"));
            _logger.LogError(
                "Duplicate ClOrdID {ClOrdId} for firm {Firm}; refusing submit. " +
                "Likely snapshot/WAL-replay regression — investigate ClOrdIdPrefixRegistry watermark.",
                clOrdId, req.FirmId);
            return OrderSubmissionResult.DuplicateClOrdId(clOrdId);
        }
        Order order;
        var recordedAt = _clock.GetUtcNow();
        try
        {
            order = new Order(
                clOrdId, req.Owner, req.Symbol, req.SecurityId, req.Side, req.Type,
                req.Quantity, req.Price, req.FirmId,
                parentAlgoId: req.ParentAlgoId, algoSliceSeq: req.AlgoSliceSeq,
                timeInForce: req.TimeInForce, stopPrice: req.StopPrice, goodTillDate: req.GoodTillDate,
                displayQty: req.DisplayQty, displayResetPolicy: req.DisplayResetPolicy,
                subAccountId: req.SubAccountId,
                minQty: req.MinQty);
        }
        catch (ArgumentException ex)
        {
            // Q1.1 (#253). Cross-field invariants (StopPrice required iff
            // Stop*; GoodTillDate required iff GTD) are enforced inside
            // the Order ctor so WAL replay can't reconstitute illegal
            // combinations. At the live submit boundary we surface them
            // as BadRequest so REST callers get a clear 400 rather than
            // a 500 from an uncaught exception.
            return OrderSubmissionResult.BadRequest(ex.Message);
        }

        try
        {
            // Sub-issue #171 (E): when the order originates from the FIXP
            // listener, the WAL event carries the BotMapping side-record
            // (RFC §4.6 / §4.8) and the apply callback registers the
            // forward+reverse lookups in the in-memory registry under the
            // same dispatcher lock as the WAL append. REST/WS submissions
            // pass req.BotOrigin == null and the field serialises as null.
            BotOrderMapping? botMapping = req.BotOrigin is { } origin
                ? new BotOrderMapping(origin.CredentialId, origin.ExternalClOrdId)
                : null;

            var restIdempotency = req.IdempotencyContext is { Binding: { } pendingBinding }
                ? pendingBinding with
                {
                    ClOrdId = clOrdId,
                    BoundAtUtc = recordedAt,
                }
                : null;
            var submitted = new OrderSubmittedEvent
            {
                MutationId = mutationId,
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
                BotMapping = botMapping,
                TimeInForce = req.TimeInForce.ToString(),
                StopPrice = req.StopPrice,
                GoodTillDate = req.GoodTillDate,
                DisplayQty = order.DisplayQty,
                DisplayResetPolicy = order.DisplayResetPolicy?.ToString(),
                SubAccountId = order.SubAccountId?.Value,
                MinQty = order.MinQty,
                RestIdempotency = restIdempotency,
                TimestampUtc = recordedAt,
            };
            _dispatcher.DispatchCommitted(
                submitted,
                () =>
                {
                    if (!_book.TryAdd(order))
                    {
                        // Belt-and-suspenders: pre-flight TryGet
                        // already handled the expected case. If we
                        // somehow reach here with a collision, the
                        // WAL is already appended — log critical so
                        // the inconsistency surfaces in postmortem.
                        MetricsRegistry.ClOrdIdDuplicateDetected.Add(1,
                            new KeyValuePair<string, object?>("op", "submit"),
                            new KeyValuePair<string, object?>("scope", "callback"));
                        _logger.LogCritical(
                            "WorkingOrderBook.TryAdd failed for {ClOrdId} after pre-flight passed; book/WAL diverged.",
                            clOrdId);
                    }
                    _ownership.Register(clOrdId, req.Owner);
                    if (botMapping is not null && _botMappings is not null)
                    {
                        // Same lock-held call shape on live submit and on
                        // WAL replay (EventReplayer invokes the same
                        // method) so the registry is rehydrated by exactly
                        // the same code path that populated it originally.
                        _botMappings.RegisterOrderInternal(
                            clOrdId,
                            botMapping.CredentialId,
                            botMapping.ExternalClOrdId,
                            recordedAt,
                            mutationId);
                    }
                    if (restIdempotency is not null)
                        _restIdempotency?.Apply(restIdempotency);
                },
                CancellationToken.None);
        }
        catch (WalBackpressureException ex)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site",
                    req.Source == OrderSubmissionSource.Algo ? "algo.submit" : "orders.submit"),
                new KeyValuePair<string, object?>("firmId", req.FirmId));
            return OrderSubmissionResult.WalBackpressure(ex.Message);
        }
        catch (WalFaultedException ex)
        {
            _logger.LogCritical(ex, "WAL is faulted; refusing order submit for firm {FirmId}.", req.FirmId);
            return OrderSubmissionResult.WalFaulted(ex.Message);
        }

        // OPT-F (#488). Classify the symbol so equity vs option flow
        // can be split on dashboards. SymbolDirectory is optional —
        // when not injected (most tests), the tag value is "unknown"
        // and the option-specific counter is silenced. Unknown symbols
        // (not in the directory) also tag as "unknown" so onboarding
        // a new ticker is observable in surveillance.
        var securityTypeTag = "unknown";
        var isOption = false;
        OptionMetadata? optionMeta = null;
        if (_symbolDirectory is { } dir && dir.TryGetSpec(req.Symbol, out var spec))
        {
            isOption = spec.SecurityType == SecurityType.Option;
            securityTypeTag = isOption ? "option" : "equity";
            optionMeta = spec.Option;
        }

        MetricsRegistry.OrdersSubmitted.Add(1,
            new KeyValuePair<string, object?>("symbol", req.Symbol),
            new KeyValuePair<string, object?>("side", req.Side.ToString()),
            new KeyValuePair<string, object?>("source",
                req.Source == OrderSubmissionSource.Algo ? "algo" : "manual"),
            new KeyValuePair<string, object?>("firmId", req.FirmId),
            new KeyValuePair<string, object?>("security_type", securityTypeTag));

        // OPT-F (#488). Cabinet / worthless-OTM closeout surveillance
        // counter. OPT-C (#485) relaxed MinNotional for option orders
        // at price=0; this metric makes the (small) population of such
        // orders observable per (symbol, side, firm, put_call) so a
        // sudden spike — which would be unusual for a healthy desk —
        // can trip a compliance alert. Only fires for orders we can
        // classify as Option AND that carry an explicit price of 0.
        if (isOption && req.Price is 0m)
        {
            MetricsRegistry.OptionZeroPriceOrdersSubmitted.Add(1,
                new KeyValuePair<string, object?>("symbol", req.Symbol),
                new KeyValuePair<string, object?>("side", req.Side.ToString()),
                new KeyValuePair<string, object?>("firmId", req.FirmId),
                new KeyValuePair<string, object?>("put_call",
                    optionMeta?.PutOrCall.ToString().ToLowerInvariant() ?? "unknown"));
        }

        // Ordering note (RFC docs/rfcs/risk-pipeline-ordering-v0.md, #262):
        // risk evaluation runs *post-WAL* on the submit path. On reject we
        // emit a synthetic ExecutionReportReceivedEvent so the rejection is
        // recoverable from the WAL (FE executions log, /executions/history,
        // CVM 35/505, drop-copy, best-exec touch). This is intentionally
        // asymmetric with OrderModifyService, which evaluates pre-WAL — see
        // RFC §1.3 and the open audit-gap follow-up.
        var riskCtx = new RiskContext(
            req.Owner, req.FirmId, req.Symbol, req.Side, req.Type, req.Quantity, req.Price,
            TimeInForce: req.TimeInForce,
            StopPrice: req.StopPrice,
            GoodTillDate: req.GoodTillDate,
            SubAccountId: req.SubAccountId,
            // #473. Resolve the routing instruction once here so the
            // pre-trade RoutingInstructionAllowedCheck can gate it
            // against the per-scope whitelist before the gateway
            // ever sees the order. The gateway will resolve again at
            // approval-freeze time — resolvers MUST be deterministic
            // per-Order (see IRoutingInstructionResolver doc).
            RoutingInstruction: _routingResolver?.TryResolve(order),
            ParentAlgoId: req.Source == OrderSubmissionSource.Algo ? req.ParentAlgoId : null,
            AlgoType: req.Source == OrderSubmissionSource.Algo
                ? req.AlgoTypeTag
                : null,
            EvaluatedClOrdId: clOrdId);
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
            var reason = decision.Reason ?? "risk_rejected";
            var code = decision.Code ?? "risk_rejected";
            MetricsRegistry.OrdersRejectedByRisk.Add(1,
                new KeyValuePair<string, object?>("reason", reason),
                new KeyValuePair<string, object?>("code", code),
                new KeyValuePair<string, object?>("firmId", req.FirmId));
            var walFailure = PublishSyntheticRejection(mutationId, order, reason);
            if (walFailure is not null) return walFailure;
            return OrderSubmissionResult.Rejected(mutationId, clOrdId, reason, code);
        }

        if (req.UseDurableOutboundCoordinator && _outboundCoordinator is not null)
        {
            if (_outboundLedger is null || _approvalFactory is null)
            {
                return TerminalizeProvenNoWrite(
                    mutationId,
                    order,
                    marginReserved,
                    "outbound_composition_invalid",
                    "outbound_composition_invalid");
            }
            var approvedAt = _clock.GetUtcNow();
            OutboundApprovedEvent approved;
            try
            {
                var frozen = _approvalFactory.Create(mutationId, order, approvedAt);
                approved = new OutboundApprovedEvent
                {
                    MutationId = mutationId,
                    MutationKind = OutboundMutationKind.New,
                    FirmId = req.FirmId,
                    EndClientRef = frozen.EndClientRef,
                    Origin = req.Source == OrderSubmissionSource.Algo
                        ? OutboundMutationOrigin.Algo
                        : req.BotOrigin is not null
                            ? OutboundMutationOrigin.UserBotFixp
                            : OutboundMutationOrigin.Rest,
                    AlgoOriginIdentity = req.AlgoOriginIdentity,
                    BotBusinessIdentity = req.BotOrigin is { } botOrigin
                        ? new OutboundBotBusinessIdentity(
                            botOrigin.CredentialId,
                            botOrigin.ExternalClOrdId)
                        : null,
                    PrimaryClOrdId = clOrdId,
                    RecordedAtUtc = recordedAt,
                    Approval = frozen.Approval,
                    TimestampUtc = approvedAt,
                };
                _dispatcher.DispatchCommitted(
                    approved,
                    () => _outboundLedger.Apply(approved),
                    CancellationToken.None);
            }
            catch (WalBackpressureException ex)
            {
                _logger.LogError(
                    ex,
                    "New-order approval was not committed for {ClOrdId}; terminalising as proven no-write.",
                    clOrdId);
                return TerminalizeProvenNoWrite(
                    mutationId,
                    order,
                    marginReserved,
                    "outbound_approval_not_committed",
                    "outbound_approval_not_committed");
            }
            catch (WalFaultedException ex)
            {
                _logger.LogCritical(
                    ex,
                    "New-order approval WAL commit faulted for {ClOrdId}; attempting proven no-write terminalisation.",
                    clOrdId);
                return TerminalizeProvenNoWrite(
                    mutationId,
                    order,
                    marginReserved,
                    "outbound_approval_not_committed",
                    "outbound_approval_not_committed");
            }
            catch (OutboundCommandEnvelopeException ex)
            {
                _logger.LogCritical(
                    ex,
                    "New-order approval command protection failed for firm {FirmId}.",
                    req.FirmId);
                return TerminalizeProvenNoWrite(
                    mutationId,
                    order,
                    marginReserved,
                    "outbound_command_protection_unavailable",
                    "outbound_command_protection_unavailable");
            }

            _accountant.RecordAccepted(riskCtx);
            NewOrderDispatchResult dispatch;
            try
            {
                dispatch = await _outboundCoordinator.EnqueueAsync(mutationId, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            if (dispatch.Outcome != NewOrderDispatchOutcome.TransportWriteCompleted)
            {
                MetricsRegistry.OrdersGatewayFailed.Add(
                    1,
                    new KeyValuePair<string, object?>("firmId", req.FirmId));
            }
            if (dispatch.Outcome == NewOrderDispatchOutcome.ProvenUnsent)
            {
                var terminal = TerminalizeProvenNoWrite(
                    mutationId,
                    order,
                    marginReserved,
                    "gateway_proven_unsent",
                    "gateway_proven_unsent");
                return req.Source == OrderSubmissionSource.Algo
                    && terminal.Kind == OrderSubmissionResultKind.Rejected
                    ? OrderSubmissionResult.ReconciliationRequired(mutationId, clOrdId)
                    : terminal;
            }
            return dispatch.Outcome == NewOrderDispatchOutcome.TransportWriteCompleted
                ? OrderSubmissionResult.Accepted(mutationId, clOrdId)
                : OrderSubmissionResult.ReconciliationRequired(mutationId, clOrdId);
        }

        _accountant.RecordAccepted(riskCtx);

        try
        {
            await _gateway.SubmitAsync(order, ct);
        }
        catch (Exception ex)
        {
            MetricsRegistry.OrdersGatewayFailed.Add(1,
                new KeyValuePair<string, object?>("firmId", req.FirmId));
            _logger.LogError(ex,
                "Legacy gateway submit failed for {ClOrdId}; attempting durable synthetic rejection.",
                clOrdId);
            if (marginReserved) _margin.ReleaseReservation(clOrdId);
            var walFailure = PublishSyntheticRejection(mutationId, order, "gateway_unavailable");
            if (walFailure is not null) return walFailure;
            return OrderSubmissionResult.GatewayFailed(mutationId, clOrdId, ex);
        }

        // Q1.3 (#255). Arm the GTD scheduler ONLY after the gateway submit
        // returns without throwing. Doing this earlier (e.g. right after
        // the WAL append) lets a near-term GTD timer race the in-flight
        // SubmitAsync — the scheduler would issue a cancel for a clOrdId
        // the venue has not yet seen. By placing the hook here the
        // semantic is: "submit success" == SubmitAsync completed without
        // throwing. Venue ACK remains async (NewReject ER drives the
        // reject path, which marks the order terminal and the
        // OnOrderTerminal subscription removes it from the heap).
        // No-ops for non-GTD orders; no-ops when the scheduler is not
        // wired (test contexts that don't need expiry firing).
        _gtdScheduler?.OnOrderTracked(order);

        // #351 — Defensive watchdog for IOC/FOK. Upstream matching
        // (B3MatchingPlatform#357) can silently drop an IOC aggressor
        // that finds no liquidity, leaving the order pinned in
        // WorkingOrderBook with a never-released margin reservation.
        // Arming the watchdog AFTER the gateway submit succeeds
        // mirrors the GTD scheduler ordering: the timer should only
        // run once we believe the venue has the order, otherwise a
        // submit-side failure synthesises its own rejection above.
        _iocWatchdog?.Register(order);

        return OrderSubmissionResult.Accepted(mutationId, clOrdId);
    }

    private OrderSubmissionResult? PublishSyntheticRejection(
        OutboundMutationId mutationId,
        Order order,
        string reason,
        bool outboundProvenNoWrite = false)
    {
        try
        {
            var rejected = new ExecutionReportReceivedEvent
            {
                ClOrdId = order.ClOrdId,
                ExecKind = ExecKind.Rejected.ToString(),
                LeavesQuantity = order.LeavesQuantity,
                CumulativeQuantity = order.CumulativeQuantity,
                LastQuantity = 0,
                LastPrice = 0m,
                RejectReason = reason,
                Synthetic = true,
                OutboundProvenNoWrite = outboundProvenNoWrite,
                OutboundMutationId = outboundProvenNoWrite ? mutationId : null,
                TimestampUtc = _clock.GetUtcNow(),
            };
            _dispatcher.DispatchCommitted(
                rejected,
                () =>
                {
                    if (outboundProvenNoWrite)
                    {
                        var ledgerResult = _outboundLedger?.ApplyVenueAcknowledgement(rejected);
                        if (ledgerResult?.Status == InboundVenueEvidenceApplyStatus.RecordedConflicting)
                            throw new InvalidOperationException(
                                "Proven no-write terminal evidence conflicted with the outbound ledger.");
                    }
                    order.MarkRejected();
                    _sink.Publish(new ExecutionEvent(
                        order.Owner, order.ClOrdId, order.Symbol, order.Side, order.Status, ExecKind.Rejected,
                        order.LeavesQuantity, order.CumulativeQuantity, 0, 0m,
                        reason, DateTimeOffset.UtcNow, IsNativeStp: false, FirmId: order.FirmId));
                },
                CancellationToken.None);
            return null;
        }
        catch (WalBackpressureException ex)
        {
            return FailDrainForReconciliation(mutationId, order, ex);
        }
        catch (WalFaultedException ex)
        {
            return FailDrainForReconciliation(mutationId, order, ex);
        }
        catch (InvalidOperationException ex)
        {
            return FailDrainForReconciliation(mutationId, order, ex);
        }
    }

    private OrderSubmissionResult TerminalizeProvenNoWrite(
        OutboundMutationId mutationId,
        Order order,
        bool marginReserved,
        string reason,
        string code)
    {
        var walFailure = PublishSyntheticRejection(
            mutationId,
            order,
            reason,
            outboundProvenNoWrite: true);
        if (walFailure is not null)
            return walFailure;
        if (marginReserved)
            _margin.ReleaseReservation(order.ClOrdId);
        return OrderSubmissionResult.Rejected(mutationId, order.ClOrdId, reason, code);
    }

    private OrderSubmissionResult FailDrainForReconciliation(
        OutboundMutationId mutationId,
        Order order,
        Exception exception)
    {
        _drain.BeginDrain("wal_synthetic_terminal_reconciliation_required");
        _logger.LogCritical(exception,
            "Durable synthetic rejection failed for {ClOrdId}; ingress is draining and operator reconciliation is required.",
            order.ClOrdId);
        return OrderSubmissionResult.ReconciliationRequired(mutationId, order.ClOrdId);
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
    int? AlgoSliceSeq = null,
    TimeInForce TimeInForce = TimeInForce.Day,
    decimal? StopPrice = null,
    DateTimeOffset? GoodTillDate = null,
    /// <summary>
    /// Q3.4 (#284). Native iceberg / reserve display quantity. Null
    /// = full disclosure (no reserve). Validated by
    /// <see cref="Order"/>'s constructor: <c>0 &lt; DisplayQty &lt;= Quantity</c>.
    /// </summary>
    long? DisplayQty = null,
    /// <summary>
    /// Q3.4 (#284). Refresh policy for the visible portion of an
    /// iceberg order. Null iff <see cref="DisplayQty"/> is null;
    /// otherwise defaults to
    /// <see cref="Domain.DisplayResetPolicy.Always"/>.
    /// </summary>
    DisplayResetPolicy? DisplayResetPolicy = null,
    /// <summary>
    /// Q4.1 (#301). Optional sub-account bucket the order is booked
    /// against. <c>null</c> = master bucket (every legacy caller).
    /// When non-null, the submit pipeline records the id on the WAL,
    /// the order, the per-sub-account position keeper, and runs the
    /// per-sub-account risk gates ON TOP OF the existing master ones
    /// — reject-on-either-fail (see <c>SubAccountLimitsCheck</c>).
    /// </summary>
    SubAccountId? SubAccountId = null,
    /// <summary>
    /// #457. Optional minimum execution quantity (FIX MinQty). Null
    /// = no minimum. Validated by <see cref="Order"/>'s constructor:
    /// <c>0 &lt; MinQty &lt;= Quantity</c>.
    /// </summary>
    long? MinQty = null,
    /// <summary>
    /// #435. Lowercase algo-type label (iceberg/twap/pegged/...) when
    /// <see cref="Source"/> is <see cref="OrderSubmissionSource.Algo"/>.
    /// Forwarded to <c>RiskContext.AlgoType</c> so the throttle checks
    /// can resolve <c>RiskOptions.RollingNotional.PerAlgoType</c> and
    /// <c>RiskOptions.OrderRate.PerAlgoType</c> per algo strategy.
    /// </summary>
    string? AlgoTypeTag = null)
{
    public RestOrderIdempotencyContext? IdempotencyContext { get; init; }
    public bool UseDurableOutboundCoordinator { get; init; }
    public AlgoOutboundOriginIdentity? AlgoOriginIdentity { get; init; }

    /// <summary>
    /// Sub-issue #171 (E). When non-null, the request originates from
    /// the FIXP listener on behalf of a user-bot credential. The submit
    /// pipeline ignores this for matching/risk purposes; it is recorded
    /// on the <see cref="Persistence.OrderSubmittedEvent.BotMapping"/>
    /// side-record so sub-issue F can reverse-route ERs back to the
    /// originating bot session. REST/WS callers pass <c>null</c>.
    /// </summary>
    public BotOrigin? BotOrigin { get; init; }
}

/// <summary>
/// Sub-issue #171 (E). FIXP-origin annotation on
/// <see cref="OrderSubmissionRequest"/>. <see cref="ExternalClOrdId"/>
/// is the bot's own wire ClOrdID (uint64 per the SBE schema); the
/// platform's internal <c>ulong</c> ClOrdID is allocated independently
/// by <see cref="ClOrdIdPrefixRegistry"/> and remains the on-the-wire
/// identifier for the gateway and the WAL.
/// </summary>
public sealed record BotOrigin(Guid CredentialId, ulong ExternalClOrdId);

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
    public OutboundMutationId MutationId { get; }
    public OrderSubmissionResultKind Kind { get; }
    public ulong ClOrdId { get; }
    public string? Reason { get; }
    /// <summary>
    /// #288 — stable machine-readable code (e.g. <c>min_tick_size</c>,
    /// <c>kill_switch</c>) on rejection paths. Null on accept.
    /// </summary>
    public string? Code { get; }
    public Exception? GatewayException { get; }

    private OrderSubmissionResult(
        OutboundMutationId mutationId,
        OrderSubmissionResultKind kind,
        ulong clOrdId,
        string? reason,
        string? code,
        Exception? ex)
    {
        MutationId = mutationId;
        Kind = kind;
        ClOrdId = clOrdId;
        Reason = reason;
        Code = code;
        GatewayException = ex;
    }

    public static OrderSubmissionResult Accepted(OutboundMutationId mutationId, ulong clOrdId) =>
        new(mutationId, OrderSubmissionResultKind.Accepted, clOrdId, null, null, null);
    public static OrderSubmissionResult Rejected(
        OutboundMutationId mutationId,
        ulong clOrdId,
        string reason,
        string? code = null) =>
        new(mutationId, OrderSubmissionResultKind.Rejected, clOrdId, reason, code, null);
    public static OrderSubmissionResult GatewayFailed(
        OutboundMutationId mutationId,
        ulong clOrdId,
        Exception? ex) =>
        new(mutationId, OrderSubmissionResultKind.GatewayFailed, clOrdId, "gateway_unavailable", "gateway_unavailable", ex);
    public static OrderSubmissionResult WalBackpressure(
        string detail,
        OutboundMutationId mutationId = default,
        ulong clOrdId = 0) =>
        new(mutationId, OrderSubmissionResultKind.WalBackpressure, clOrdId, detail, "wal_backpressure", null);
    public static OrderSubmissionResult WalFaulted(
        string detail,
        OutboundMutationId mutationId = default,
        ulong clOrdId = 0) =>
        new(mutationId, OrderSubmissionResultKind.WalBackpressure, clOrdId, detail, "wal_faulted", null);
    public static OrderSubmissionResult ReconciliationRequired(
        OutboundMutationId mutationId,
        ulong clOrdId) =>
        new(
            mutationId,
            OrderSubmissionResultKind.ReconciliationRequired,
            clOrdId,
            "outbound mutation requires operator reconciliation",
            "wal_reconciliation_required",
            null);
    public static OrderSubmissionResult BadRequest(string reason) =>
        new(default, OrderSubmissionResultKind.BadRequest, 0, reason, "bad_request", null);
    public static OrderSubmissionResult Drained { get; } =
        new(default, OrderSubmissionResultKind.Drained, 0, "service draining", "service_draining", null);
    /// <summary>
    /// #108 — DuplicateClOrdID guard. The just-allocated ClOrdID
    /// already exists in the <see cref="WorkingOrderBook"/>. No WAL
    /// event was appended, no order was created, no gateway call was
    /// made. Endpoints should map this to <c>409 Conflict</c> so
    /// callers can distinguish it from ordinary input validation
    /// failures and so operators can spot the invariant breach.
    /// </summary>
    public static OrderSubmissionResult DuplicateClOrdId(ulong clOrdId) =>
        new(default, OrderSubmissionResultKind.DuplicateClOrdId, clOrdId, "duplicate_clordid", "duplicate_clordid", null);
}

public enum OrderSubmissionResultKind
{
    Accepted,
    Rejected,
    GatewayFailed,
    WalBackpressure,
    BadRequest,
    Drained,
    DuplicateClOrdId,
    ReconciliationRequired,
}
