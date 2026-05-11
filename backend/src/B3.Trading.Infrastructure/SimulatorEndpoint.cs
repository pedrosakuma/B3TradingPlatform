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
        string? RejectReason);

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

        if (execType == EpExecType.Replaced)
            return Results.BadRequest(new { error = "unsupported_type", detail = "Replaced is out of v0 scope; future RFC will define semantics." });

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
