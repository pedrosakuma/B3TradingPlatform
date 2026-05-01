using B3.Trading.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", (
            LoginRequest req,
            IOptions<AuthOptions> opts,
            JwtIssuer issuer,
            EndClientRegistry registry) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "username and password required" });

            var user = opts.Value.Users.FirstOrDefault(u =>
                string.Equals(u.Username, req.Username, StringComparison.OrdinalIgnoreCase));

            if (user is null || !PasswordHasher.Verify(req.Password, user.PasswordHash, user.Salt, user.Iterations))
                return Results.Json(new { error = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);

            // Pre-register so subsequent ER routing / WS subscribe work
            // immediately even before the first business call.
            registry.Register(user.Username);

            var (token, expires) = issuer.Issue(user.Username, user.Role, user.Firm);
            return Results.Ok(new LoginResponse(token, expires));
        });

        return app;
    }
}

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);
