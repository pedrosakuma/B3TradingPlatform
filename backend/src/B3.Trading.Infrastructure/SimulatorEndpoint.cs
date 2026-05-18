using System.Security.Claims;
using B3.Trading.Application;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Synthetic ER injection (formerly <see cref="ExchangeMode.Simulator"/>;
/// merged into <see cref="ExchangeMode.Mock"/> + <c>AllowErInjection</c>
/// in #163). Caller supplies a lean payload
/// (<c>clOrdId</c>, <c>type</c>, optional <c>lastQty</c>/<c>lastPx</c>);
/// the server reads the resting <see cref="B3.Trading.Domain.Order"/> from
/// <see cref="WorkingOrderBook"/> to fill SecurityId/Side/firm and computes
/// <c>leaves</c>/<c>cum</c> server-side. This inverts the failure mode of
/// "caller forgot to bump cum" — engines like Iceberg refill on
/// <c>leaves==0</c> and silent drift would be a nasty bug to hunt.
///
/// <para>Lives in the Infrastructure project (#188 layering refactor)
/// because the endpoint is the only consumer of the Mock-mode
/// <see cref="MockEntryPointClient"/> + <see cref="ExecutionReportEnvelope"/>
/// concretions. Mapped from the composition root via
/// <see cref="MapSimulatorEndpoints"/>; the Api layer no longer references
/// Infrastructure.</para>
/// </summary>
public static class SimulatorEndpoint
{
    public sealed record InjectRequest(
        ulong ClOrdId,
        string Type,
        long? LastQty,
        decimal? LastPx,
        string? RejectReason,
        // Q3.5 (#285). Required when Type==Replaced — the engine's
        // cancel-replace flow allocates a brand-new ClOrdID for the
        // replacement and the venue echoes the original as OrigClOrdID
        // on the Replaced ack. The processor's PendingReplacementRegistry
        // lookup is keyed on the new ClOrdID, but the new Order is
        // hydrated under that new id only if OrigClOrdID resolves to an
        // existing original — caller must supply it.
        ulong? OrigClOrdId = null,
        // Pass-1 review (#299) P1-A. Optional cumulative-quantity echo
        // for Type==Replaced — the venue's view of how much of the
        // original order had been filled at replace-acceptance time. The
        // processor seeds the replacement Order's CumulativeQuantity
        // from this value so subsequent fills advance from the correct
        // baseline. Defaults to 0 (no carry-over) for back-compat with
        // tests that exercise the "modify before any fill" path.
        long? CumQty = null);

    /// <summary>
    /// Q4.5 (#305) — canonical event type for simulator ER injection
    /// audit envelopes. Picked as a sub-namespace of
    /// <c>admin.config.change</c> so existing <c>admin.*</c> read
    /// filters surface these too, but distinct enough that ops
    /// dashboards can split them out.
    /// </summary>
    public const string SimulatorErEventType = "admin.simulator.er_inject";

    /// <summary>
    /// Maps <c>POST /admin/simulator/er</c> under the admin authorization
    /// policy. URL kept stable for conformance-contract compatibility
    /// (#163) — callers must check <c>ExchangeOptions.ResolveMode()==Mock
    /// &amp;&amp; AllowErInjection</c> before invoking; the validator
    /// already refuses Mode=Real/Stub/Unavailable + the flag at startup so
    /// reaching this branch implies a Mock gateway is in DI.
    /// </summary>
    public static IEndpointRouteBuilder MapSimulatorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/simulator/er", Inject)
            .RequireAuthorization("admin");
        return app;
    }

    public static IResult Inject(
        [FromBody] InjectRequest req,
        WorkingOrderBook book,
        MockEntryPointClient mock,
        HttpContext ctx,
        IAuditLogger audit)
    {
        // Pass-1 review (#322) P2. The simulator endpoint is gated on
        // Mock + AllowErInjection (dev/test-only) and is intentionally
        // routed through the best-effort audit mode — operator
        // visibility of every accepted/rejected injection is the
        // goal; fail-closed gating against a backpressured WAL would
        // be the wrong default for a dev surface. Both happy-path
        // and validation-failure branches funnel through EmitAudit
        // so the audit trail records what the operator attempted
        // regardless of the outcome.
        if (req is null)
            return RejectWithAudit(audit, ctx, req: null, AuditOutcomes.Failure, "missing_body", new { error = "missing_body" });
        if (req.ClOrdId == 0)
            return RejectWithAudit(audit, ctx, req, AuditOutcomes.Failure, "missing_clOrdId", new { error = "missing_clOrdId" });
        if (string.IsNullOrWhiteSpace(req.Type))
            return RejectWithAudit(audit, ctx, req, AuditOutcomes.Failure, "missing_type", new { error = "missing_type" });

        if (!Enum.TryParse<EpExecType>(req.Type, ignoreCase: true, out var execType))
            return RejectWithAudit(audit, ctx, req, AuditOutcomes.Failure, "invalid_type",
                new { error = "invalid_type", detail = $"unknown ExecType '{req.Type}'" });

        // Q3.5 (#285). Replaced ER lookup is keyed on the NEW
        // ClOrdID via PendingReplacementRegistry; the new order is
        // not in WorkingOrderBook yet (it's hydrated by
        // ApplyReplaceAccepted from the intent). Skip the
        // pre-flight book lookup for this exec type — leaves / cum
        // come from the request body (the venue echoes them on the
        // wire) and we rely on the processor to validate against
        // the intent and the original order.
        if (execType == EpExecType.Replaced)
        {
            if ((req.OrigClOrdId ?? 0UL) == 0UL)
                return RejectWithAudit(audit, ctx, req, AuditOutcomes.Failure, "missing_origClOrdId",
                    new { error = "missing_origClOrdId", detail = "origClOrdId is required for type=Replaced." });
            var leavesR = req.LastQty ?? 0L;
            var cumR = req.CumQty ?? 0L;
            var envR = new ExecutionReportEnvelope(
                ClOrdId: req.ClOrdId,
                ExecType: EpExecType.Replaced,
                LeavesQuantity: leavesR,
                CumulativeQuantity: cumR,
                LastQuantity: 0,
                LastPrice: 0m,
                RejectReason: null,
                OrigClOrdId: req.OrigClOrdId!.Value);
            mock.EmitExecutionReport(envR);
            EmitAudit(audit, ctx, req, execType, AuditOutcomes.Success, reasonCode: null);
            return Results.Accepted(value: new
            {
                clOrdId = req.ClOrdId,
                execType = EpExecType.Replaced.ToString(),
                origClOrdId = req.OrigClOrdId.Value,
                leavesQuantity = leavesR,
            });
        }

        if (!book.TryGet(req.ClOrdId, out var order) || order is null)
            return RejectWithAudit(audit, ctx, req, AuditOutcomes.Failure, "unknown_clOrdId",
                new { error = "unknown_clOrdId", clOrdId = req.ClOrdId }, execType: execType, status: StatusCodes.Status404NotFound);

        long leaves;
        long cum;
        long lastQty = req.LastQty ?? 0;
        decimal lastPx = req.LastPx ?? 0m;
        string? rejectReason = req.RejectReason;

        switch (execType)
        {
            case EpExecType.New:
                leaves = order.Quantity;
                cum = 0;
                lastQty = 0;
                lastPx = 0m;
                break;

            case EpExecType.PartialFill:
            case EpExecType.Fill:
                if (lastQty <= 0)
                    return RejectWithAudit(audit, ctx, req, AuditOutcomes.Failure, "missing_lastQty",
                        new { error = "missing_lastQty", detail = "lastQty must be > 0 for Fill/PartialFill." }, execType: execType);
                cum = order.CumulativeQuantity + lastQty;
                if (cum > order.Quantity)
                    return RejectWithAudit(audit, ctx, req, AuditOutcomes.Failure, "overfill",
                        new { error = "overfill", detail = $"cum {cum} would exceed order quantity {order.Quantity}." }, execType: execType);
                leaves = order.Quantity - cum;
                if (execType == EpExecType.PartialFill && leaves == 0)
                    return RejectWithAudit(audit, ctx, req, AuditOutcomes.Failure, "partial_consumes_full",
                        new { error = "partial_consumes_full", detail = "PartialFill would zero leaves; use type=Fill instead." }, execType: execType);
                break;

            case EpExecType.Canceled:
                leaves = 0;
                cum = order.CumulativeQuantity;
                lastQty = 0;
                lastPx = 0m;
                break;

            case EpExecType.Rejected:
                if (string.IsNullOrWhiteSpace(rejectReason))
                    return RejectWithAudit(audit, ctx, req, AuditOutcomes.Failure, "missing_rejectReason",
                        new { error = "missing_rejectReason", detail = "rejectReason is required for type=Rejected." }, execType: execType);
                leaves = order.LeavesQuantity;
                cum = order.CumulativeQuantity;
                lastQty = 0;
                lastPx = 0m;
                break;

            default:
                return RejectWithAudit(audit, ctx, req, AuditOutcomes.Failure, "invalid_type",
                    new { error = "invalid_type", detail = $"ExecType '{execType}' not supported." }, execType: execType);
        }

        var envelope = new ExecutionReportEnvelope(
            ClOrdId: req.ClOrdId,
            ExecType: execType,
            LeavesQuantity: leaves,
            CumulativeQuantity: cum,
            LastQuantity: lastQty,
            LastPrice: lastPx,
            RejectReason: rejectReason,
            FirmId: order.FirmId);

        mock.EmitExecutionReport(envelope);
        EmitAudit(audit, ctx, req, execType, AuditOutcomes.Success, reasonCode: null);

        return Results.Accepted(value: new
        {
            clOrdId = req.ClOrdId,
            execType = execType.ToString(),
            leavesQuantity = leaves,
            cumulativeQuantity = cum,
        });
    }

    private static IResult RejectWithAudit(
        IAuditLogger audit,
        HttpContext ctx,
        InjectRequest? req,
        string outcome,
        string reasonCode,
        object body,
        EpExecType? execType = null,
        int status = StatusCodes.Status400BadRequest)
    {
        EmitAudit(audit, ctx, req, execType, outcome, reasonCode);
        return status == StatusCodes.Status404NotFound
            ? Results.NotFound(body)
            : Results.BadRequest(body);
    }

    private static void EmitAudit(
        IAuditLogger audit,
        HttpContext ctx,
        InjectRequest? req,
        EpExecType? execType,
        string outcome,
        string? reasonCode)
    {
        // Pass-1 review (#322) P2. Best-effort capture — see Inject
        // doc for the LogOrFail/Log rationale.
        var details = new Dictionary<string, string>();
        if (req is not null)
        {
            details["cl_ord_id"] = req.ClOrdId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(req.Type)) details["type"] = req.Type;
            if (execType is not null) details["exec_type"] = execType.Value.ToString();
            if (req.LastQty is long lq) details["last_qty"] = lq.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (req.LastPx is decimal lp) details["last_px"] = lp.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (req.OrigClOrdId is ulong oc && oc != 0UL) details["orig_cl_ord_id"] = oc.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (req.CumQty is long cq) details["cum_qty"] = cq.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(req.RejectReason)) details["reject_reason"] = req.RejectReason;
        }
        audit.Log(new AuditLogEvent
        {
            EventType = SimulatorErEventType,
            Outcome = outcome,
            ActorUserId = ctx.User.FindFirstValue("sub"),
            ActorUsername = ctx.User.FindFirstValue("sub"),
            ActorFirm = ctx.User.FindFirstValue("firm"),
            ActorRole = ctx.User.FindFirstValue("role"),
            SourceIp = ctx.Connection.RemoteIpAddress?.ToString(),
            ResourcePath = "/admin/simulator/er",
            ReasonCode = reasonCode,
            Details = details.Count == 0 ? null : details,
        });
    }
}
