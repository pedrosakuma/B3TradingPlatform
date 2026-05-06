using System.Security.Claims;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

public static class BalanceEndpoints
{
    public static IEndpointRouteBuilder MapBalance(this IEndpointRouteBuilder app)
    {
        app.MapGet("/balance", [Authorize] (HttpContext ctx, EndClientRegistry registry, CashLedger cash) =>
        {
            var sub = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                      ?? throw new InvalidOperationException("Authenticated request missing sub claim.");
            var owner = registry.Register(sub);
            return Results.Ok(new BalanceDto(cash.GetAvailable(owner)));
        });

        return app;
    }
}
