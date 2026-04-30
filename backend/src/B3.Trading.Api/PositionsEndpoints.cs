using B3.Trading.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

public static class PositionsEndpoints
{
    public static IEndpointRouteBuilder MapPositions(this IEndpointRouteBuilder app)
    {
        app.MapGet("/positions", (string login, EndClientRegistry registry, PositionKeeper positions) =>
        {
            if (!registry.TryResolve(login, out var owner) || owner is null)
                return Results.NotFound();

            var view = positions.ForEndClient(owner).Select(p => new
            {
                Owner = p.Owner.Value,
                p.Symbol,
                p.NetQuantity,
                p.AverageEntryPrice,
            });
            return Results.Ok(view);
        });

        return app;
    }
}
