using B3.Trading.Api.Auth.Totp;
using B3.Trading.Application;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Identity;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
        var authOptions = app.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (authOptions.IsLocalLoginEnabled())
        {
            app.MapPost("/auth/login", async (
            HttpContext http,
            LoginRequest req,
            IUserStore users,
            ITradingSessionIssuer sessionIssuer,
            ILoginAttemptTracker lockout,
            ITotpChallengeStore totpChallenges,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var sourceIp = http.Connection.RemoteIpAddress?.ToString();
            if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            {
                audit.Log(new AuditLogEvent
                {
                    EventType = AuditEventTypes.AuthLoginFailure,
                    Outcome = AuditOutcomes.Failure,
                    ActorUsername = req?.Username,
                    SourceIp = sourceIp,
                    ResourcePath = "/auth/login",
                    ReasonCode = "missing_credentials",
                });
                return Results.BadRequest(new { error = "username and password required" });
            }

            // Lockout check uses the trimmed username so "alice " and
            // "alice" share the same bucket. Response is intentionally
            // identical to the wrong-password 401 — exposing a distinct
            // "locked" state would let an attacker enumerate which
            // usernames exist.
            var loginUsername = req.Username.Trim();
            if (lockout.IsLocked(loginUsername))
            {
                audit.Log(new AuditLogEvent
                {
                    EventType = AuditEventTypes.AuthLoginFailure,
                    Outcome = AuditOutcomes.Failure,
                    ActorUsername = loginUsername,
                    SourceIp = sourceIp,
                    ResourcePath = "/auth/login",
                    ReasonCode = "locked",
                });
                return Results.Json(new { error = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!users.TryGet(loginUsername, out var user) || user is null
                || !PasswordHasher.Verify(req.Password, user.PasswordHash, user.Salt, user.Iterations))
            {
                // Record failure under the username the client sent
                // (normalized). Recording even for unknown usernames is
                // intentional — otherwise an attacker can probe which
                // usernames exist by observing whether lockouts engage.
                lockout.RecordFailure(loginUsername);
                audit.Log(new AuditLogEvent
                {
                    EventType = AuditEventTypes.AuthLoginFailure,
                    Outcome = AuditOutcomes.Failure,
                    ActorUsername = loginUsername,
                    SourceIp = sourceIp,
                    ResourcePath = "/auth/login",
                    ReasonCode = user is null ? "unknown_user" : "bad_password",
                });
                return Results.Json(new { error = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // Password ok. Clear the password-side failure counter
            // before deciding the second-factor path so a repeated
            // 2FA-pending login doesn't pile up password failures.
            lockout.RecordSuccess(user.Username);

            // 2FA branch #1: user has an active TOTP enrollment. Mint a
            // short-lived challenge token and bail before issuing a JWT.
            if (user.Totp is { EnrolledAt: not null, SharedSecret.Length: > 0 })
            {
                var token = totpChallenges.Issue(user.Username, TotpChallengeKind.Verify);
                audit.Log(new AuditLogEvent
                {
                    EventType = AuditEventTypes.AuthLoginFailure,
                    Outcome = AuditOutcomes.Failure,
                    ActorUserId = user.Username,
                    ActorUsername = user.Username,
                    ActorFirm = user.Firm,
                    ActorRole = user.Role,
                    SourceIp = sourceIp,
                    ResourcePath = "/auth/login",
                    ReasonCode = "2fa_required",
                });
                return Results.Ok(new LoginTwoFactorRequiredResponse(
                    Requires2fa: true,
                    TotpChallengeToken: token));
            }

            // 2FA branch #2: user is REQUIRED to enroll (admin-forced)
            // but hasn't yet. Mint a ForceEnroll token; the client
            // calls /auth/2fa/enroll with that token instead of a JWT.
            if (user.Require2FA)
            {
                var token = totpChallenges.Issue(user.Username, TotpChallengeKind.ForceEnroll);
                audit.Log(new AuditLogEvent
                {
                    EventType = AuditEventTypes.AuthLoginFailure,
                    Outcome = AuditOutcomes.Failure,
                    ActorUserId = user.Username,
                    ActorUsername = user.Username,
                    ActorFirm = user.Firm,
                    ActorRole = user.Role,
                    SourceIp = sourceIp,
                    ResourcePath = "/auth/login",
                    ReasonCode = "2fa_required_but_missing",
                });
                return Results.Ok(new LoginEnrollmentRequiredResponse(
                    Requires2faEnrollment: true,
                    EnrollmentToken: token));
            }

            var session = await sessionIssuer.IssueForLocalUserAsync(user, ct);
            if (!session.Succeeded)
            {
                audit.Log(new AuditLogEvent
                {
                    EventType = AuditEventTypes.AuthLoginFailure,
                    Outcome = session.StatusCode == StatusCodes.Status403Forbidden ? AuditOutcomes.Denied : AuditOutcomes.Failure,
                    ActorUserId = user.Username,
                    ActorUsername = user.Username,
                    SourceIp = sourceIp,
                    ResourcePath = "/auth/login",
                    ReasonCode = session.ErrorCode,
                });
                return Error(session.StatusCode, session.ErrorCode ?? "identity_directory_unavailable");
            }

            audit.Log(new AuditLogEvent
            {
                EventType = AuditEventTypes.AuthLoginSuccess,
                Outcome = AuditOutcomes.Success,
                ActorUserId = session.TradingUserId,
                ActorUsername = session.TradingUserId,
                ActorFirm = session.Firm,
                ActorRole = session.Role,
                SourceIp = sourceIp,
                ResourcePath = "/auth/login",
            });
            return Results.Ok(new LoginResponse(session.Token!, session.ExpiresAt!.Value));
        });
        }

        if (authOptions.IsSignupEnabled())
        {
            app.MapPost("/auth/signup", async (
            SignupRequest req,
            IUserStore users,
            IOptions<AuthOptions> opts,
            IOptions<CashSeedOptions> cashOpts,
            ITradingUserDirectory directory,
            ITradingSessionIssuer sessionIssuer,
            EndClientRegistry registry,
            PositionKeeper positions,
            CashLedger cash,
            IReferencePrice refPrice,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
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

            if (opts.Value.ResolveMode() != AuthModeKind.Local)
            {
                try
                {
                    await directory.ImportLegacyUsersAsync(new[]
                    {
                        new LegacyTradingUserImport(newUser.Username, newUser.Username, newUser.Firm, newUser.Role),
                    }, ct);
                }
                catch (TradingUserDirectoryException)
                {
                    return Error(StatusCodes.Status503ServiceUnavailable, "identity_directory_unavailable");
                }
            }

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
                cashSeeded = cash.SeedIfAbsent(newUser.Firm, endClientId, initialCash);
            }

            var log = loggerFactory.CreateLogger("AuthEndpoints");
            log.LogInformation(
                "Self-service signup: user={Username} firm={Firm} role={Role} positionsSeeded=true cashSeeded={CashSeeded} initialCash={InitialCash}.",
                newUser.Username, newUser.Firm, newUser.Role, cashSeeded, cashSeeded ? initialCash : 0m);

            var session = await sessionIssuer.IssueForLocalUserAsync(newUser, ct);
            if (!session.Succeeded)
                return Error(session.StatusCode, session.ErrorCode ?? "identity_directory_unavailable");

            return Results.Created($"/auth/users/{newUser.Username}", new LoginResponse(session.Token!, session.ExpiresAt!.Value));
        });
        }

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

    internal static IResult Error(int statusCode, string code) =>
        Results.Json(new { error = code }, statusCode: statusCode, contentType: "application/json");
}

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);
public sealed record SignupRequest(string Username, string Password);

/// <summary>
/// Returned by <c>/auth/login</c> when the user has an active TOTP
/// enrollment. The client must POST <c>{ totpChallengeToken, code }</c>
/// to <c>/auth/2fa/verify</c> to receive the real JWT. (#303)
/// </summary>
public sealed record LoginTwoFactorRequiredResponse(bool Requires2fa, string TotpChallengeToken);

/// <summary>
/// Returned by <c>/auth/login</c> when the user has
/// <c>Require2FA=true</c> but no active enrollment. The client must
/// POST <c>{ enrollmentToken }</c> to <c>/auth/2fa/enroll</c>, then confirm the returned
/// TOTP challenge to receive a JWT. (#303)
/// </summary>
public sealed record LoginEnrollmentRequiredResponse(bool Requires2faEnrollment, string EnrollmentToken);
