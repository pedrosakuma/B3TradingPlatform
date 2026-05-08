using System.Security.Claims;
using B3.Trading.Api.Auth;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.Observability;
using B3.Trading.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

public static class OrdersEndpoints
{
    public static IEndpointRouteBuilder MapOrders(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/orders").RequireAuthorization();

        group.MapGet("/", (HttpContext ctx, WorkingOrderBook book, EndClientRegistry registry) =>
        {
            var owner = ResolveOwner(ctx, registry);
            var orders = book.ForEndClient(owner).Select(o => o.ToDto());
            return Results.Ok(orders);
        });

        group.MapPost("/", async (
            SubmitOrderRequest req,
            HttpContext ctx,
            EndClientRegistry registry,
            OrderSubmissionService submitter,
            SymbolDirectory symbols,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<OrderSide>(req.Side, ignoreCase: true, out var side))
                return Results.BadRequest(new { error = $"invalid side '{req.Side}'" });
            if (!Enum.TryParse<OrderType>(req.Type, ignoreCase: true, out var type))
                return Results.BadRequest(new { error = $"invalid type '{req.Type}'" });

            // SecurityId resolution: explicit non-zero in the payload
            // wins (preserves the conformance contract). Otherwise look
            // up the directory by symbol — that is the path the trader
            // UI takes, since the ticket form does not expose the
            // numeric SecurityId.
            var securityId = req.SecurityId;
            if (securityId == 0 && symbols.TryResolve(req.Symbol, out var resolved))
                securityId = resolved;

            var owner = ResolveOwner(ctx, registry);
            var firm = ResolveFirm(ctx);

            var result = await submitter.SubmitAsync(new OrderSubmissionRequest(
                owner, firm, req.Symbol, securityId, side, type,
                req.Quantity, req.Price, OrderSubmissionSource.Manual), ct);

            return result.Kind switch
            {
                OrderSubmissionResultKind.Accepted =>
                    Results.Accepted($"/orders/{result.ClOrdId}", new { ClOrdId = result.ClOrdId.ToString() }),
                OrderSubmissionResultKind.Rejected =>
                    Results.Accepted($"/orders/{result.ClOrdId}",
                        new { ClOrdId = result.ClOrdId.ToString(), Status = "Rejected", Reason = result.Reason }),
                OrderSubmissionResultKind.GatewayFailed =>
                    Results.Json(
                        new { error = "gateway unavailable", clOrdId = result.ClOrdId.ToString() },
                        statusCode: StatusCodes.Status502BadGateway),
                OrderSubmissionResultKind.WalBackpressure =>
                    Results.Json(
                        new { error = "system busy (WAL backpressure)", detail = result.Reason },
                        statusCode: StatusCodes.Status503ServiceUnavailable),
                OrderSubmissionResultKind.Drained =>
                    Results.Json(
                        new { error = "service draining" },
                        statusCode: StatusCodes.Status503ServiceUnavailable),
                OrderSubmissionResultKind.BadRequest =>
                    Results.BadRequest(new { error = result.Reason }),
                OrderSubmissionResultKind.DuplicateClOrdId =>
                    Results.Conflict(new { error = result.Reason, clOrdId = result.ClOrdId.ToString() }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        });

        group.MapPut("/{clOrdId}", async (
            string clOrdId,
            ModifyOrderRequest req,
            HttpContext ctx,
            EndClientRegistry registry,
            OrderModifyService modifier,
            CancellationToken ct) =>
        {
            if (!ulong.TryParse(clOrdId, out var clOrdIdU))
                return Results.NotFound();

            var owner = ResolveOwner(ctx, registry);
            var result = await modifier.ModifyAsync(
                new OrderModifyRequest(owner, clOrdIdU, req.Quantity, req.Price), ct);

            return result.Kind switch
            {
                OrderModifyResultKind.Accepted =>
                    Results.Accepted(
                        $"/orders/{result.NewClOrdId}",
                        new { ClOrdId = result.NewClOrdId.ToString(), OriginalClOrdId = clOrdIdU.ToString() }),
                OrderModifyResultKind.NotFound =>
                    Results.NotFound(),
                OrderModifyResultKind.Conflict =>
                    Results.Conflict(new { error = result.Reason }),
                OrderModifyResultKind.BadRequest =>
                    Results.BadRequest(new { error = result.Reason }),
                OrderModifyResultKind.RiskRejected =>
                    Results.UnprocessableEntity(new { error = result.Reason }),
                OrderModifyResultKind.GatewayFailed =>
                    Results.Json(
                        new { error = "gateway unavailable", clOrdId = result.NewClOrdId.ToString() },
                        statusCode: StatusCodes.Status502BadGateway),
                OrderModifyResultKind.WalBackpressure =>
                    Results.Json(
                        new { error = "system busy (WAL backpressure)", detail = result.Reason },
                        statusCode: StatusCodes.Status503ServiceUnavailable),
                OrderModifyResultKind.Drained =>
                    Results.Json(
                        new { error = "service draining" },
                        statusCode: StatusCodes.Status503ServiceUnavailable),
                OrderModifyResultKind.DuplicateClOrdId =>
                    Results.Conflict(new { error = result.Reason, newClOrdId = result.NewClOrdId.ToString() }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        });

        group.MapDelete("/{clOrdId}", async (
            string clOrdId,
            HttpContext ctx,
            EndClientRegistry registry,
            ClOrdIdPrefixRegistry clOrdIds,
            WorkingOrderBook book,
            OrderOwnershipMap ownership,
            IExchangeGateway gateway,
            CancellationToken ct) =>
        {
            if (!ulong.TryParse(clOrdId, out var clOrdIdU))
                return Results.NotFound();

            var owner = ResolveOwner(ctx, registry);
            if (!book.TryGet(clOrdIdU, out var order) || order is null)
                return Results.NotFound();

            if (order.Owner != owner)
                return Results.NotFound();

            // Slice 1 of #132. Reject Cancel against an order the operator
            // has flagged as suspected-stale-by-venue: the venue most
            // likely doesn't know the original ClOrdID either, and we'd
            // burn a fresh ClOrdID for nothing. Operator must explicitly
            // clear the stale overlay first (admin endpoint) when the
            // venue catches up, or rely on the eventual real terminal ER
            // to auto-clear.
            if (order.IsStale)
                return Results.Conflict(new { error = "order is marked stale", reason = order.StaleReason });

            var cancelClOrdId = clOrdIds.Generate(owner);
            // Record the cancel-side → original mapping BEFORE sending so
            // the cancel-ack ER can resolve back to the right order even
            // when upstream omits OrigClOrdID on the wire.
            ownership.RegisterCancelLink(cancelClOrdId, order.ClOrdId);
            await gateway.CancelAsync(order, cancelClOrdId, ct);
            MetricsRegistry.OrdersCancelRequested.Add(1);
            // Status transition to Cancelled happens when the exchange ER
            // arrives, not synchronously here.
            return Results.NoContent();
        });

        return app;
    }

    private static EndClientId ResolveOwner(HttpContext ctx, EndClientRegistry registry)
    {
        var sub = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                  ?? throw new InvalidOperationException("Authenticated request missing sub claim.");
        return registry.Register(sub);
    }

    private static string ResolveFirm(HttpContext ctx) =>
        ctx.User.FindFirstValue(JwtIssuer.FirmClaim) ?? "default";
}

public sealed record SubmitOrderRequest(
    string Symbol,
    ulong SecurityId,
    string Side,
    string Type,
    long Quantity,
    decimal? Price);

public sealed record ModifyOrderRequest(
    long Quantity,
    decimal? Price);

