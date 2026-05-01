using System.Security.Claims;
using B3.Trading.Api.Auth;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

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
            ClOrdIdPrefixRegistry clOrdIds,
            OrderOwnershipMap ownership,
            WorkingOrderBook book,
            IExchangeGateway gateway,
            IExecutionEventSink sink,
            RiskPipeline risk,
            IMarginProvider margin,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<OrderSide>(req.Side, ignoreCase: true, out var side))
                return Results.BadRequest(new { error = $"invalid side '{req.Side}'" });
            if (!Enum.TryParse<OrderType>(req.Type, ignoreCase: true, out var type))
                return Results.BadRequest(new { error = $"invalid type '{req.Type}'" });
            if (req.Quantity <= 0)
                return Results.BadRequest(new { error = "quantity must be positive" });

            var owner = ResolveOwner(ctx, registry);
            var firm = ResolveFirm(ctx);
            var clOrdId = clOrdIds.Generate(owner);
            var order = new Order(clOrdId, owner, req.Symbol, side, type, req.Quantity, req.Price);

            // Order in the book + ownership registered BEFORE the gateway
            // call so an immediate ER from the wire (synchronous mock or
            // very-low-latency real client) cannot race the routing path.
            book.TryAdd(order);
            ownership.Register(clOrdId, owner);

            // Pre-trade risk: synchronous pipeline + async margin provider.
            // Rejection synthesizes an ER through the same sink real
            // exchange rejections use, so the WS client can't tell them
            // apart structurally.
            var riskCtx = new RiskContext(owner, firm, req.Symbol, side, type, req.Quantity, req.Price);
            var decision = risk.Evaluate(riskCtx);
            if (decision.Approved)
            {
                var marginDecision = await margin.CheckAsync(riskCtx, ct);
                if (!marginDecision.Approved) decision = marginDecision;
            }
            if (!decision.Approved)
            {
                order.MarkRejected();
                sink.Publish(new ExecutionEvent(
                    owner, clOrdId, order.Symbol, order.Side, order.Status, ExecKind.Rejected,
                    order.LeavesQuantity, order.CumulativeQuantity, 0, 0m,
                    decision.Reason, DateTimeOffset.UtcNow));
                // Per acceptance criteria: 202 Accepted; the client learns
                // about the synthetic rejection on the executions.me WS
                // channel — same shape as exchange-originated rejections.
                return Results.Accepted($"/orders/{clOrdId}",
                    new { ClOrdId = clOrdId, Status = "Rejected", Reason = decision.Reason });
            }

            try
            {
                await gateway.SubmitAsync(order, ct);
            }
            catch (Exception ex)
            {
                // Synthesize a rejection so subscribed clients see a
                // terminal state for this ClOrdID rather than a
                // permanently-PendingNew ghost order.
                loggerFactory.CreateLogger("OrdersEndpoints")
                    .LogError(ex, "Gateway submit failed for {ClOrdId}; synthesizing rejection.", clOrdId);
                order.MarkRejected();
                sink.Publish(new ExecutionEvent(
                    owner, clOrdId, order.Symbol, order.Side, order.Status, ExecKind.Rejected,
                    order.LeavesQuantity, order.CumulativeQuantity, 0, 0m,
                    "gateway_unavailable", DateTimeOffset.UtcNow));
                return Results.Json(
                    new { error = "gateway unavailable", clOrdId },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            return Results.Accepted($"/orders/{clOrdId}", new { ClOrdId = clOrdId });
        });

        group.MapDelete("/{clOrdId}", async (
            string clOrdId,
            HttpContext ctx,
            EndClientRegistry registry,
            WorkingOrderBook book,
            IExchangeGateway gateway,
            CancellationToken ct) =>
        {
            var owner = ResolveOwner(ctx, registry);
            if (!book.TryGet(clOrdId, out var order) || order is null)
                return Results.NotFound();

            // Cross-tenant guard: a caller with a valid token for end-client
            // A must not be able to cancel end-client B's order even by
            // guessing the ClOrdID. Return 404 (not 403) to avoid leaking
            // existence of foreign orders.
            if (order.Owner != owner)
                return Results.NotFound();

            await gateway.CancelAsync(clOrdId, ct);
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
    string Side,
    string Type,
    long Quantity,
    decimal? Price);

