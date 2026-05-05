using B3.Trading.Application;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Auth;

public static class AuthEndpoints
{
    // v0 self-service signup is hardcoded to FIRM01 ("inicialmente users
    // dentro firm01"). Multi-firm signup is a tracked follow-up.
    private const string SignupFirm = "FIRM01";
    private const string SignupRole = "user";

    // Default opening positions for fresh signups, mirroring the alice/bob
    // seed in docker-compose.real.yml. Lets a brand-new user immediately
    // submit a Sell without tripping NoNakedShortCheck during dogfood.
    // Quantities deliberately match the operator-seeded defaults so
    // anyone signing up gets the same baseline as the demo accounts.
    private static readonly (string Symbol, long Quantity)[] SignupSeedPositions =
    {
        ("PETR4", 2000),
        ("VALE3", 2000),
    };

    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", (
            LoginRequest req,
            IUserStore users,
            JwtIssuer issuer,
            EndClientRegistry registry) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "username and password required" });

            if (!users.TryGet(req.Username, out var user) || user is null
                || !PasswordHasher.Verify(req.Password, user.PasswordHash, user.Salt, user.Iterations))
                return Results.Json(new { error = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);

            // Pre-register so subsequent ER routing / WS subscribe work
            // immediately even before the first business call.
            registry.Register(user.Username);

            var (token, expires) = issuer.Issue(user.Username, user.Role, user.Firm);
            return Results.Ok(new LoginResponse(token, expires));
        });

        app.MapPost("/auth/signup", (
            SignupRequest req,
            IUserStore users,
            IOptions<AuthOptions> opts,
            JwtIssuer issuer,
            EndClientRegistry registry,
            PositionKeeper positions,
            IReferencePrice refPrice,
            ILoggerFactory loggerFactory) =>
        {
            if (req is null
                || string.IsNullOrWhiteSpace(req.Username)
                || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "username and password required" });

            // Minimal hygiene only — full policy (length/complexity/captcha
            // /rate-limit) is a tracked hardening follow-up. Reject strings
            // we know will break downstream consumers (claim parsing,
            // ClOrdID emission, WS topic routing).
            var username = req.Username.Trim();
            if (username.Length > 64 || username.Any(c => char.IsWhiteSpace(c) || c == ':' || c == '"' || c == '\\'))
                return Results.BadRequest(new { error = "username contains invalid characters" });

            var iterations = opts.Value.Pbkdf2Iterations > 0 ? opts.Value.Pbkdf2Iterations : 600_000;
            var (hash, salt) = PasswordHasher.Hash(req.Password, iterations);
            var newUser = new UserConfig
            {
                Username = username,
                PasswordHash = hash,
                Salt = salt,
                Iterations = iterations,
                Role = SignupRole,
                Firm = SignupFirm,
            };

            if (!users.TryAdd(newUser))
                return Results.Conflict(new { error = "username already taken" });

            var endClientId = registry.Register(newUser.Username);

            // Seed default opening positions. Avg cost = current ref price
            // when known (so P&L doesn't show a fake gain/loss); zero is
            // a fine fallback (matches the dogfood overlay's choice for
            // symbols without a configured ref price).
            foreach (var (symbol, qty) in SignupSeedPositions)
            {
                refPrice.TryGet(symbol, out var avgPx);
                positions.SeedIfAbsent(endClientId, symbol, qty, avgPx);
            }

            var log = loggerFactory.CreateLogger("AuthEndpoints");
            log.LogInformation(
                "Self-service signup: user={Username} firm={Firm} role={Role} (positions seeded).",
                newUser.Username, newUser.Firm, newUser.Role);

            var (token, expires) = issuer.Issue(newUser.Username, newUser.Role, newUser.Firm);
            return Results.Created($"/auth/users/{newUser.Username}", new LoginResponse(token, expires));
        });

        return app;
    }
}

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);
public sealed record SignupRequest(string Username, string Password);
