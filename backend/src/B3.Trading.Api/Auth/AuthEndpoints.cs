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
            EndClientRegistry registry,
            ILoginAttemptTracker lockout) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "username and password required" });

            // Lockout check uses the trimmed username so "alice " and
            // "alice" share the same bucket. Response is intentionally
            // identical to the wrong-password 401 — exposing a distinct
            // "locked" state would let an attacker enumerate which
            // usernames exist.
            var loginUsername = req.Username.Trim();
            if (lockout.IsLocked(loginUsername))
                return Results.Json(new { error = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);

            if (!users.TryGet(loginUsername, out var user) || user is null
                || !PasswordHasher.Verify(req.Password, user.PasswordHash, user.Salt, user.Iterations))
            {
                // Record failure under the username the client sent
                // (normalized). Recording even for unknown usernames is
                // intentional — otherwise an attacker can probe which
                // usernames exist by observing whether lockouts engage.
                lockout.RecordFailure(loginUsername);
                return Results.Json(new { error = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // Successful auth wipes the failure counter for this user.
            lockout.RecordSuccess(user.Username);

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
            IOptions<CashSeedOptions> cashOpts,
            JwtIssuer issuer,
            EndClientRegistry registry,
            PositionKeeper positions,
            CashLedger cash,
            IReferencePrice refPrice,
            ILoggerFactory loggerFactory) =>
        {
            if (req is null
                || string.IsNullOrWhiteSpace(req.Username)
                || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "username and password required" });

            var validation = ValidateSignupRequest(req, opts.Value, out var username);
            if (validation is not null)
                return validation;

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
            //
            // PR #316 P1. Signup seeds intentionally land in
            // <see cref="PositionKeeper.DefaultFirmId"/> because
            // <see cref="PositionSeedOptions"/> has no firm field (signup
            // is firm-agnostic — the seed is the "every user starts here"
            // overlay, not a per-firm prefund). A signup that races a
            // firm-scoped fill on the same JWT sub stays partitioned:
            // the fill lands in the user's actual firm bucket, the seed
            // sits in default and surfaces only to clients that
            // authenticate without a firm claim. Adding a per-firm seed
            // config is tracked as a follow-up (out of scope here).
            foreach (var (symbol, qty) in SignupSeedPositions)
            {
                refPrice.TryGet(symbol, out var avgPx);
                positions.SeedIfAbsent(endClientId, symbol, qty, avgPx);
            }

            // Slice 3 of #107: pre-fund the new account from the
            // configured opening cash. We seed only when a positive
            // balance is configured; a zero seed would be skipped by
            // CashLedger.Snapshot (zero rows are pruned), which would
            // cause the margin provider to fall back to the legacy
            // RiskOptions.Margin.Initial after the next snapshot —
            // surprising and unsafe. Operators wanting a true-zero
            // signup balance can wait for slice 4 (deprecation of
            // Margin.Initial), at which point a missing ledger entry
            // unambiguously means zero.
            var initialCash = cashOpts.Value.SignupInitialBalance;
            var cashSeeded = false;
            if (initialCash > 0m)
            {
                cashSeeded = cash.SeedIfAbsent(endClientId, initialCash);
            }

            var log = loggerFactory.CreateLogger("AuthEndpoints");
            log.LogInformation(
                "Self-service signup: user={Username} firm={Firm} role={Role} positionsSeeded=true cashSeeded={CashSeeded} initialCash={InitialCash}.",
                newUser.Username, newUser.Firm, newUser.Role, cashSeeded, cashSeeded ? initialCash : 0m);

            var (token, expires) = issuer.Issue(newUser.Username, newUser.Role, newUser.Firm);
            return Results.Created($"/auth/users/{newUser.Username}", new LoginResponse(token, expires));
        });

        return app;
    }

    /// <summary>
    /// Slice 1 of #97 hardening: shape + reserved-name + password-policy
    /// validation for signup. Returns <c>null</c> on success and emits the
    /// normalized (trimmed) <paramref name="username"/> so the caller does
    /// not re-trim and risk drift between validate/store.
    /// </summary>
    private static IResult? ValidateSignupRequest(SignupRequest req, AuthOptions opts, out string username)
    {
        username = req.Username.Trim();

        // Existing minimal hygiene — reject strings we know will break
        // downstream consumers (claim parsing, ClOrdID emission, WS topic
        // routing).
        if (username.Length > 64 || username.Any(c => char.IsWhiteSpace(c) || c == ':' || c == '"' || c == '\\'))
            return Results.BadRequest(new { error = "username contains invalid characters" });

        // Reserved usernames + prefixes (case-insensitive). 409 mirrors
        // the duplicate-username UX; the body string distinguishes the
        // two cases for client-side messaging.
        if (IsReserved(username, opts))
            return Results.Conflict(new { error = "username is reserved" });

        var policyError = ValidatePassword(req.Password, opts.PasswordPolicy);
        if (policyError is not null)
            return Results.BadRequest(new { error = policyError });

        return null;
    }

    private static bool IsReserved(string username, AuthOptions opts)
    {
        foreach (var name in opts.ReservedUsernames ?? new())
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (string.Equals(username, name.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var prefix in opts.ReservedUsernamePrefixes ?? new())
        {
            if (string.IsNullOrWhiteSpace(prefix)) continue;
            if (username.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? ValidatePassword(string password, PasswordPolicyOptions policy)
    {
        var minLength = policy.EffectiveMinLength;
        if (password.Length < minLength)
            return $"password does not meet policy: minimum length is {minLength}";
        if (policy.RequireDigit && !password.Any(char.IsDigit))
            return "password does not meet policy: must contain a digit";
        if (policy.RequireLetter && !password.Any(char.IsLetter))
            return "password does not meet policy: must contain a letter";
        return null;
    }
}

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);
public sealed record SignupRequest(string Username, string Password);
