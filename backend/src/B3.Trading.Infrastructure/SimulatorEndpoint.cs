using B3.Trading.Application;
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
        MockEntryPointClient mock)
    {
        if (req is null)
            return Results.BadRequest(new { error = "missing_body" });
        if (req.ClOrdId == 0)
            return Results.BadRequest(new { error = "missing_clOrdId" });
        if (string.IsNullOrWhiteSpace(req.Type))
            return Results.BadRequest(new { error = "missing_type" });

        if (!Enum.TryParse<EpExecType>(req.Type, ignoreCase: true, out var execType))
            return Results.BadRequest(new { error = "invalid_type", detail = $"unknown ExecType '{req.Type}'" });

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
                return Results.BadRequest(new { error = "missing_origClOrdId", detail = "origClOrdId is required for type=Replaced." });
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
            return Results.Accepted(value: new
            {
                clOrdId = req.ClOrdId,
                execType = EpExecType.Replaced.ToString(),
                origClOrdId = req.OrigClOrdId.Value,
                leavesQuantity = leavesR,
            });
        }

        if (!book.TryGet(req.ClOrdId, out var order) || order is null)
            return Results.NotFound(new { error = "unknown_clOrdId", clOrdId = req.ClOrdId });

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
                    return Results.BadRequest(new { error = "missing_lastQty", detail = "lastQty must be > 0 for Fill/PartialFill." });
                cum = order.CumulativeQuantity + lastQty;
                if (cum > order.Quantity)
                    return Results.BadRequest(new { error = "overfill", detail = $"cum {cum} would exceed order quantity {order.Quantity}." });
                leaves = order.Quantity - cum;
                if (execType == EpExecType.PartialFill && leaves == 0)
                    return Results.BadRequest(new { error = "partial_consumes_full", detail = "PartialFill would zero leaves; use type=Fill instead." });
                break;

            case EpExecType.Canceled:
                leaves = 0;
                cum = order.CumulativeQuantity;
                lastQty = 0;
                lastPx = 0m;
                break;

            case EpExecType.Rejected:
                if (string.IsNullOrWhiteSpace(rejectReason))
                    return Results.BadRequest(new { error = "missing_rejectReason", detail = "rejectReason is required for type=Rejected." });
                leaves = order.LeavesQuantity;
                cum = order.CumulativeQuantity;
                lastQty = 0;
                lastPx = 0m;
                break;

            default:
                return Results.BadRequest(new { error = "invalid_type", detail = $"ExecType '{execType}' not supported." });
        }

        var envelope = new ExecutionReportEnvelope(
            ClOrdId: req.ClOrdId,
            ExecType: execType,
            LeavesQuantity: leaves,
            CumulativeQuantity: cum,
            LastQuantity: lastQty,
            LastPrice: lastPx,
            RejectReason: rejectReason);

        mock.EmitExecutionReport(envelope);

        return Results.Accepted(value: new
        {
            clOrdId = req.ClOrdId,
            execType = execType.ToString(),
            leavesQuantity = leaves,
            cumulativeQuantity = cum,
        });
    }
}
