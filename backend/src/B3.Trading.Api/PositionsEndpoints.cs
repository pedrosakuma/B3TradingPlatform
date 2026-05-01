using System.Security.Claims;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

public static class PositionsEndpoints
{
    public static IEndpointRouteBuilder MapPositions(this IEndpointRouteBuilder app)
    {
        app.MapGet("/positions", [Authorize] (HttpContext ctx, EndClientRegistry registry, PositionKeeper positions) =>
        {
            var sub = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                      ?? throw new InvalidOperationException("Authenticated request missing sub claim.");
            var owner = registry.Register(sub);
            var view = positions.ForEndClient(owner).Select(p => p.ToDto());
            return Results.Ok(view);
        });

        return app;
    }
}
