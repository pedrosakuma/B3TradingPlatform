using B3.Trading.Application;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

public static class OrdersEndpoints
{
    public static IEndpointRouteBuilder MapOrders(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/orders");

        group.MapGet("/", (string login, WorkingOrderBook book, EndClientRegistry registry) =>
        {
            if (!registry.TryResolve(login, out var owner) || owner is null)
                return Results.NotFound();

            var orders = book.ForEndClient(owner).Select(o => new
            {
                o.ClOrdId,
                Owner = o.Owner.Value,
                o.Symbol,
                Side = o.Side.ToString(),
                Type = o.Type.ToString(),
                o.Quantity,
                o.LeavesQuantity,
                o.CumulativeQuantity,
                Status = o.Status.ToString(),
                o.Price,
            });
            return Results.Ok(orders);
        });

        group.MapPost("/", async (
            SubmitOrderRequest req,
            EndClientRegistry registry,
            ClOrdIdPrefixRegistry clOrdIds,
            OrderOwnershipMap ownership,
            WorkingOrderBook book,
            IExchangeGateway gateway,
            CancellationToken ct) =>
        {
            var owner = registry.Register(req.Login);
            var clOrdId = clOrdIds.Generate(owner);
            var order = new Order(
                clOrdId,
                owner,
                req.Symbol,
                Enum.Parse<OrderSide>(req.Side, ignoreCase: true),
                Enum.Parse<OrderType>(req.Type, ignoreCase: true),
                req.Quantity,
                req.Price);

            // Order in the book + ownership registered BEFORE the gateway
            // call so an immediate ER from the wire (synchronous mock or
            // very-low-latency real client) cannot race the routing path.
            book.TryAdd(order);
            ownership.Register(clOrdId, owner);

            await gateway.SubmitAsync(order, ct);

            return Results.Accepted($"/orders/{clOrdId}", new { ClOrdId = clOrdId });
        });

        group.MapDelete("/{clOrdId}", async (string clOrdId, WorkingOrderBook book, IExchangeGateway gateway, CancellationToken ct) =>
        {
            if (!book.TryGet(clOrdId, out var order) || order is null)
                return Results.NotFound();

            await gateway.CancelAsync(clOrdId, ct);
            // Note: status transition to Cancelled happens when the
            // exchange ER arrives, not synchronously here.
            return Results.NoContent();
        });

        return app;
    }
}

public sealed record SubmitOrderRequest(
    string Login,
    string Symbol,
    string Side,
    string Type,
    long Quantity,
    decimal? Price);

