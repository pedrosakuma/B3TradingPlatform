using System.Security.Claims;
using B3.Trading.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Auth.Totp;

/// <summary>
/// TOTP (RFC 6238) endpoints for #303. Three routes:
/// <list type="bullet">
///   <item><c>POST /auth/2fa/enroll</c> — start enrollment (JWT or
///   ForceEnroll token). Returns the base32 secret, otpauth URI and
///   one-time recovery codes.</item>
///   <item><c>POST /auth/2fa/verify</c> — dual-purpose: confirm a
///   pending enrollment (JWT mode) OR finish login (challenge-token
///   mode, accepts TOTP or recovery code).</item>
///   <item><c>POST /auth/2fa/disable</c> — remove TOTP (JWT, requires
///   current TOTP code).</item>
/// </list>
/// </summary>
public static class TotpEndpoints
{
    public static IEndpointRouteBuilder MapTotp(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/2fa/enroll", (
            HttpContext http,
            EnrollRequest? req,
            IUserStore users,
            IPendingTotpEnrollmentStore pending,
            ITotpChallengeStore challenges,
            ITotpService totp,
            ITotpSecretProtector protector,
            IOptions<TotpOptions> opts) =>
        {
            // Two auth modes:
            //   (a) JWT bearer — normal self-service enrollment.
            //   (b) enrollmentToken from a Require2FA-forced login —
            //       lets the user bootstrap before they own a JWT.
            // Either mode resolves to a username; both reuse the rest
            // of the body path.
            if (!TryResolveActor(http, req?.EnrollmentToken, challenges, TotpChallengeKind.ForceEnroll, out var username, out var enrollmentToken))
                return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

            if (!users.TryGet(username, out var user) || user is null)
                return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

            // Block re-enroll if a secret is already active — operators
            // must disable first. Returning 409 (not 403) matches the
            // "resource already exists" semantics signup uses.
            if (user.Totp is { EnrolledAt: not null })
                return Results.Conflict(new { error = "2fa already enrolled; disable first" });

            var options = opts.Value;
            var secret = totp.GenerateBase32Secret();
            var uri = totp.BuildOtpAuthUri(options.Issuer, user.Username, secret);
            var codes = totp.GenerateRecoveryCodes(options.RecoveryCodeCount);
            var hashes = codes.Select(totp.HashRecoveryCode).ToList();

            pending.Put(user.Username, new PendingTotpEnrollment(
                Base32Secret: secret,
                RecoveryCodes: codes,
                RecoveryCodeHashes: hashes,
                CreatedAt: TimeProviderFor(http).GetUtcNow()));

            // ForceEnroll token is consumed once enroll is issued —
            // client must keep the JWT-less ride going via the standard
            // /auth/2fa/verify (JWT mode is the alternative for already-
            // logged-in users).
            if (enrollmentToken is not null)
                challenges.Invalidate(enrollmentToken);

            return Results.Ok(new EnrollResponse(
                Secret: secret,
                OtpauthUri: uri,
                RecoveryCodes: codes));
        });

        app.MapPost("/auth/2fa/verify", (
            HttpContext http,
            VerifyRequest req,
            IUserStore users,
            JwtIssuer issuer,
            IPendingTotpEnrollmentStore pending,
            ITotpChallengeStore challenges,
            ITotpAttemptTracker lockout,
            ITotpService totp,
            ITotpSecretProtector protector,
            EndClientRegistry registry) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Code))
                return Results.BadRequest(new { error = "code required" });

            // Mode (b): login-flow second factor. Challenge token wins
            // over JWT — we never want a stale JWT (somehow held by an
            // attacker) to short-circuit a fresh 2FA challenge.
            if (!string.IsNullOrEmpty(req.TotpChallengeToken))
            {
                var ch = challenges.Peek(req.TotpChallengeToken);
                if (ch is null || ch.Kind != TotpChallengeKind.Verify)
                    return Results.Json(new { error = "invalid or expired challenge" },
                        statusCode: StatusCodes.Status401Unauthorized);

                if (lockout.IsLocked(ch.Username, out var retry))
                    return TooManyRequests(retry);

                if (!users.TryGet(ch.Username, out var user) || user is null || user.Totp is null
                    || user.Totp.EnrolledAt is null || string.IsNullOrEmpty(user.Totp.SharedSecret))
                {
                    // Challenge referenced a user that has since lost
                    // their TOTP. Reject with a generic 401; do NOT
                    // fall through to JWT issuance — the client should
                    // re-POST /auth/login (which will not return a
                    // 2fa challenge for an un-enrolled user).
                    challenges.Invalidate(req.TotpChallengeToken);
                    return Results.Json(new { error = "invalid or expired challenge" },
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                string base32;
                try { base32 = protector.Unprotect(user.Totp.SharedSecret); }
                catch
                {
                    return Results.Json(new { error = "invalid or expired challenge" },
                    statusCode: StatusCodes.Status401Unauthorized);
                }

                var (totpOk, matchedStep) = totp.Verify(base32, req.Code);
                if (totpOk)
                {
                    // Atomic replay guard: persist matchedStep only if it
                    // is strictly greater than the prior step. Two
                    // concurrent verifies presenting the same valid code
                    // race here; exactly one wins, the loser is treated
                    // as an invalid-code attempt (lockout counter ticks).
                    if (!users.TryRecordTotpUse(ch.Username, matchedStep, out _))
                    {
                        lockout.RecordFailure(ch.Username);
                        return Results.Json(new { error = "invalid code" },
                            statusCode: StatusCodes.Status401Unauthorized);
                    }
                }

                var recoveryOk = false;
                if (!totpOk)
                {
                    recoveryOk = users.TryConsumeRecoveryCode(
                        user.Username, totp.HashRecoveryCode(req.Code), out _);
                }

                if (!totpOk && !recoveryOk)
                {
                    lockout.RecordFailure(ch.Username);
                    return Results.Json(new { error = "invalid code" },
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                lockout.RecordSuccess(ch.Username);
                challenges.Invalidate(req.TotpChallengeToken);

                registry.Register(user.Username);
                var (jwt, expires) = issuer.Issue(user.Username, user.Role, user.Firm);
                return Results.Ok(new LoginResponse(jwt, expires));
            }

            // Mode (a): confirm a pending enrollment. Requires JWT.
            var subject = http.User?.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            if (string.IsNullOrEmpty(subject))
                return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

            if (lockout.IsLocked(subject, out var retryE))
                return TooManyRequests(retryE);

            if (!users.TryGet(subject, out var jwtUser) || jwtUser is null)
                return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

            if (!pending.TryConsume(jwtUser.Username, out var enrollment) || enrollment is null)
                return Results.BadRequest(new { error = "no pending enrollment (expired or never started)" });

            var (enrollOk, enrollStep) = totp.Verify(enrollment.Base32Secret, req.Code);
            if (!enrollOk)
            {
                // Re-stash the pending enrollment so the user gets
                // another shot inside the same 5-min window — typing
                // the wrong code once shouldn't force a fresh /enroll.
                pending.Put(jwtUser.Username, enrollment);
                lockout.RecordFailure(jwtUser.Username);
                return Results.Json(new { error = "invalid code" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            lockout.RecordSuccess(jwtUser.Username);
            jwtUser.Totp = new UserTotpConfig
            {
                SharedSecret = protector.Protect(enrollment.Base32Secret),
                EnrolledAt = TimeProviderFor(http).GetUtcNow(),
                RecoveryCodes = enrollment.RecoveryCodeHashes.ToList(),
                // Seed LastUsedTimeStep with the enrollment-confirm step
                // so the same code cannot be replayed at login during
                // the same 30s window (defense-in-depth — the pending
                // store is single-use, but the property cannot rely on
                // that).
                LastUsedTimeStep = enrollStep,
            };
            users.TryUpdate(jwtUser);
            return Results.Ok(new { enrolled = true });
        });

        app.MapPost("/auth/2fa/disable", (
            HttpContext http,
            DisableRequest req,
            IUserStore users,
            IPendingTotpEnrollmentStore pending,
            ITotpAttemptTracker lockout,
            ITotpService totp,
            ITotpSecretProtector protector) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Code))
                return Results.BadRequest(new { error = "current TOTP code required" });

            var subject = http.User?.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            if (string.IsNullOrEmpty(subject))
                return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

            if (lockout.IsLocked(subject, out var retry)) return TooManyRequests(retry);

            if (!users.TryGet(subject, out var user) || user is null || user.Totp is null
                || user.Totp.EnrolledAt is null)
                return Results.BadRequest(new { error = "2fa not enrolled" });

            string base32;
            try { base32 = protector.Unprotect(user.Totp.SharedSecret); }
            catch { return Results.BadRequest(new { error = "2fa not enrolled" }); }

            var (ok, disableStep) = totp.Verify(base32, req.Code);
            if (ok)
            {
                // Replay guard: even on the disable path, a same-window
                // reuse must be rejected so a captured code cannot
                // disable + JWT-issue back-to-back.
                if (!users.TryRecordTotpUse(subject, disableStep, out _))
                {
                    lockout.RecordFailure(subject);
                    return Results.Json(new { error = "invalid code" }, statusCode: StatusCodes.Status401Unauthorized);
                }
            }
            else
            {
                // Recovery codes can also satisfy disable so a user
                // who lost their device but kept the codes isn't
                // stuck.
                ok = users.TryConsumeRecoveryCode(
                    user.Username, totp.HashRecoveryCode(req.Code), out _);
            }
            if (!ok)
            {
                lockout.RecordFailure(subject);
                return Results.Json(new { error = "invalid code" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            lockout.RecordSuccess(subject);
            user.Totp = null;
            users.TryUpdate(user);
            pending.Remove(user.Username);
            return Results.Ok(new { disabled = true });
        }).RequireAuthorization();

        return app;
    }

    private static bool TryResolveActor(
        HttpContext http,
        string? enrollmentToken,
        ITotpChallengeStore challenges,
        TotpChallengeKind expectedKind,
        out string username,
        out string? consumedToken)
    {
        username = string.Empty;
        consumedToken = null;

        // Prefer the enrollment token when supplied — operator-forced
        // first-time enrollment doesn't have a JWT yet.
        if (!string.IsNullOrEmpty(enrollmentToken))
        {
            var ch = challenges.Peek(enrollmentToken);
            if (ch is null || ch.Kind != expectedKind) return false;
            username = ch.Username;
            consumedToken = enrollmentToken;
            return true;
        }

        var sub = http.User?.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(sub) || http.User?.Identity?.IsAuthenticated != true) return false;
        username = sub;
        return true;
    }


    private static TimeProvider TimeProviderFor(HttpContext http)
        => http.RequestServices.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System;

    private static IResult TooManyRequests(TimeSpan retry)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds));
        return Results.Json(
            new { error = "too many attempts", retryAfterSeconds = seconds },
            statusCode: StatusCodes.Status429TooManyRequests,
            contentType: "application/json")
            .WithRetryAfter(seconds);
    }
}

internal static class TotpResultExtensions
{
    // Attach Retry-After through a tiny wrapper so the 429 path can
    // express the standard header without giving up Results.Json's
    // JSON serialization.
    public static IResult WithRetryAfter(this IResult inner, int seconds) =>
        new RetryAfterResult(inner, seconds);

    private sealed class RetryAfterResult : IResult
    {
        private readonly IResult _inner;
        private readonly int _seconds;
        public RetryAfterResult(IResult inner, int seconds) { _inner = inner; _seconds = seconds; }
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers["Retry-After"] = _seconds.ToString();
            return _inner.ExecuteAsync(httpContext);
        }
    }
}

public sealed record EnrollRequest(string? EnrollmentToken);
public sealed record EnrollResponse(string Secret, string OtpauthUri, IReadOnlyList<string> RecoveryCodes);
public sealed record VerifyRequest(string Code, string? TotpChallengeToken);
public sealed record DisableRequest(string Code);
