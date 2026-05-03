using System.Security.Claims;
using B3.Trading.Api.Auth;
using B3.Trading.Api.Lifecycle;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
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
            EventDispatcher dispatcher,
            DrainState drain,
            SymbolDirectory symbols,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (drain.IsDraining)
            {
                MetricsRegistry.DrainRejections.Add(1,
                    new KeyValuePair<string, object?>("route", "POST /orders"));
                return Results.Json(
                    new { error = "service draining" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!Enum.TryParse<OrderSide>(req.Side, ignoreCase: true, out var side))
                return Results.BadRequest(new { error = $"invalid side '{req.Side}'" });
            if (!Enum.TryParse<OrderType>(req.Type, ignoreCase: true, out var type))
                return Results.BadRequest(new { error = $"invalid type '{req.Type}'" });
            if (req.Quantity <= 0)
                return Results.BadRequest(new { error = "quantity must be positive" });

            // SecurityId resolution: explicit non-zero in the payload
            // wins (preserves the conformance contract). Otherwise look
            // up the directory by symbol — that is the path the trader
            // UI takes, since the ticket form does not expose the
            // numeric SecurityId.
            var securityId = req.SecurityId;
            if (securityId == 0 && symbols.TryResolve(req.Symbol, out var resolved))
                securityId = resolved;
            if (securityId == 0)
                return Results.BadRequest(new { error = "securityId is required" });

            var owner = ResolveOwner(ctx, registry);
            var firm = ResolveFirm(ctx);
            var clOrdId = clOrdIds.Generate(owner);
            var order = new Order(clOrdId, owner, req.Symbol, securityId, side, type, req.Quantity, req.Price, firm);

            // Persist order intent + register ownership atomically. The
            // dispatcher serialises this with snapshot capture so a crash
            // mid-window cannot leave the book and the WAL out of sync.
            // Backpressure surfaces here as 503 — disk lag becomes a
            // visible reject, never silent latency creep.
            try
            {
                dispatcher.Dispatch(
                    new OrderSubmittedEvent
                    {
                        ClOrdId = clOrdId,
                        EndClientId = owner.Value,
                        FirmId = firm,
                        Symbol = req.Symbol,
                        SecurityId = securityId,
                        Side = side.ToString(),
                        Type = type.ToString(),
                        Quantity = req.Quantity,
                        Price = req.Price,
                    },
                    () =>
                    {
                        book.TryAdd(order);
                        ownership.Register(clOrdId, owner);
                    });
            }
            catch (WalBackpressureException ex)
            {
                MetricsRegistry.WalBackpressure.Add(1,
                    new KeyValuePair<string, object?>("call_site", "orders.submit"));
                return Results.Json(
                    new { error = "system busy (WAL backpressure)", detail = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            MetricsRegistry.OrdersSubmitted.Add(1,
                new KeyValuePair<string, object?>("symbol", req.Symbol),
                new KeyValuePair<string, object?>("side", side.ToString()));

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
                MetricsRegistry.OrdersRejectedByRisk.Add(1,
                    new KeyValuePair<string, object?>("reason", decision.Reason ?? "risk_rejected"));
                PublishSyntheticRejection(dispatcher, sink, order, owner, decision.Reason ?? "risk_rejected");
                return Results.Accepted($"/orders/{clOrdId}",
                    new { ClOrdId = clOrdId.ToString(), Status = "Rejected", Reason = decision.Reason });
            }

            try
            {
                await gateway.SubmitAsync(order, ct);
            }
            catch (Exception ex)
            {
                MetricsRegistry.OrdersGatewayFailed.Add(1);
                loggerFactory.CreateLogger("OrdersEndpoints")
                    .LogError(ex, "Gateway submit failed for {ClOrdId}; synthesizing rejection.", clOrdId);
                PublishSyntheticRejection(dispatcher, sink, order, owner, "gateway_unavailable");
                return Results.Json(
                    new { error = "gateway unavailable", clOrdId = clOrdId.ToString() },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            return Results.Accepted($"/orders/{clOrdId}", new { ClOrdId = clOrdId.ToString() });
        });

        group.MapDelete("/{clOrdId}", async (
            string clOrdId,
            HttpContext ctx,
            EndClientRegistry registry,
            ClOrdIdPrefixRegistry clOrdIds,
            WorkingOrderBook book,
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

            var cancelClOrdId = clOrdIds.Generate(owner);
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

    private static void PublishSyntheticRejection(
        EventDispatcher dispatcher,
        IExecutionEventSink sink,
        Order order,
        EndClientId owner,
        string reason)
    {
        // Synthetic rejections (risk decline, gateway failure) flow through
        // the same WAL+sink path as real exchange ERs: identical recovery
        // and audit semantics, identical client-facing shape.
        try
        {
            dispatcher.Dispatch(
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
                    sink.Publish(new ExecutionEvent(
                        owner, order.ClOrdId, order.Symbol, order.Side, order.Status, ExecKind.Rejected,
                        order.LeavesQuantity, order.CumulativeQuantity, 0, 0m,
                        reason, DateTimeOffset.UtcNow));
                });
        }
        catch (WalBackpressureException)
        {
            // The order is already accepted in the WAL but the rejection
            // can't be persisted. Mark + publish anyway so the client sees
            // a terminal state; the missing audit entry is preferable to a
            // ghost order. Surfaces in metrics as a backpressure event.
            order.MarkRejected();
            sink.Publish(new ExecutionEvent(
                owner, order.ClOrdId, order.Symbol, order.Side, order.Status, ExecKind.Rejected,
                order.LeavesQuantity, order.CumulativeQuantity, 0, 0m,
                reason, DateTimeOffset.UtcNow));
        }
    }
}

public sealed record SubmitOrderRequest(
    string Symbol,
    ulong SecurityId,
    string Side,
    string Type,
    long Quantity,
    decimal? Price);

