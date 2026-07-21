using System.Security.Claims;
using B3.Trading.Application;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
        var authOptions = app.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (!authOptions.IsTotpEnabled())
            return app;

        app.MapGet("/auth/2fa/status", (
            HttpContext http,
            IUserStore users) =>
        {
            var subject = http.User?.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            if (string.IsNullOrEmpty(subject))
                return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

            if (!users.TryGet(subject, out var user) || user is null)
                return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

            return Results.Ok(new TotpStatusResponse(
                Enrolled: user.Totp is { EnrolledAt: not null }));
        }).RequireAuthorization();

        app.MapPost("/auth/2fa/enroll", (
            HttpContext http,
            EnrollRequest? req,
            IUserStore users,
            IPendingTotpEnrollmentStore pending,
            ITotpChallengeStore challenges,
            ITotpService totp,
            ITotpSecretProtector protector,
            IOptions<TotpOptions> opts,
            IAuditLogger audit) =>
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

            if (enrollmentToken is not null
                && (!challenges.TryConsume(enrollmentToken, TotpChallengeKind.ForceEnroll, out var consumed)
                    || consumed is null
                    || !string.Equals(consumed.Username, user.Username, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
            }

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

            var verificationToken = enrollmentToken is null
                ? null
                : challenges.Issue(user.Username, TotpChallengeKind.VerifyEnrollment);

            audit.Log(new AuditLogEvent
            {
                EventType = AuditEventTypes.AuthTwoFactorEnrollStart,
                Outcome = AuditOutcomes.Success,
                ActorUserId = user.Username,
                ActorUsername = user.Username,
                ActorFirm = user.Firm,
                ActorRole = user.Role,
                SourceIp = http.Connection.RemoteIpAddress?.ToString(),
                ResourcePath = "/auth/2fa/enroll",
                Details = enrollmentToken is null
                    ? null
                    : new Dictionary<string, string> { ["mode"] = "force_enroll_token" },
            });

            return Results.Ok(new EnrollResponse(
                Secret: secret,
                OtpauthUri: uri,
                RecoveryCodes: codes,
                TotpChallengeToken: verificationToken));
        });

        app.MapPost("/auth/2fa/verify", async (
            HttpContext http,
            VerifyRequest req,
            IUserStore users,
            ITradingSessionIssuer sessionIssuer,
            IPendingTotpEnrollmentStore pending,
            ITotpChallengeStore challenges,
            ITotpAttemptTracker lockout,
            ITotpService totp,
            ITotpSecretProtector protector,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Code))
                return Results.BadRequest(new { error = "code required" });

            // Mode (b): login-flow second factor. Challenge token wins
            // over JWT — we never want a stale JWT (somehow held by an
            // attacker) to short-circuit a fresh 2FA challenge.
            if (!string.IsNullOrEmpty(req.TotpChallengeToken))
            {
                var ch = challenges.Peek(req.TotpChallengeToken);
                if (ch is null)
                    return Results.Json(new { error = "invalid or expired challenge" },
                        statusCode: StatusCodes.Status401Unauthorized);

                if (ch.Kind == TotpChallengeKind.VerifyEnrollment)
                {
                    return await CompleteForcedEnrollmentAsync(
                        http, req, ch, users, sessionIssuer, pending, challenges,
                        lockout, totp, protector, audit, ct);
                }

                if (ch.Kind != TotpChallengeKind.Verify)
                    return Results.Json(new { error = "invalid or expired challenge" },
                        statusCode: StatusCodes.Status401Unauthorized);

                if (lockout.IsLocked(ch.Username, out var retry))
                    return TooManyRequests(retry);

                if (!users.TryGet(ch.Username, out var user) || user is null)
                {
                    challenges.Invalidate(req.TotpChallengeToken);
                    return Results.Json(new { error = "invalid or expired challenge" },
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                var hasTotp = user.Totp is { EnrolledAt: not null, SharedSecret.Length: > 0 };
                var hasWebAuthn = user.WebAuthnCredentials.Count > 0;
                if (!hasTotp && !hasWebAuthn)
                {
                    challenges.Invalidate(req.TotpChallengeToken);
                    return Results.Json(new { error = "invalid or expired challenge" },
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                var totpOk = false;
                long matchedStep = 0;
                if (hasTotp)
                {
                    try
                    {
                        var base32 = protector.Unprotect(user.Totp!.SharedSecret);
                        (totpOk, matchedStep) = totp.Verify(base32, req.Code);
                    }
                    catch
                    {
                        return Results.Json(new { error = "invalid or expired challenge" },
                            statusCode: StatusCodes.Status401Unauthorized);
                    }
                }

                var recoveryHash = totpOk ? null : totp.HashRecoveryCode(req.Code);
                var recoveryCandidate = recoveryHash is not null
                    && user.Totp?.RecoveryCodes.Contains(recoveryHash, StringComparer.Ordinal) == true;
                var recoveryAlreadyConsumed = recoveryHash is not null
                    && user.Totp?.ConsumedRecoveryCodes.Contains(recoveryHash, StringComparer.Ordinal) == true;

                if (!totpOk && !recoveryCandidate)
                {
                    // Race-loser / replay-after-success: the hash WAS a
                    // real recovery code for this user, just not anymore.
                    // Reject with the same generic 401 (no info leak —
                    // identical body to wrong-code) but skip the lockout
                    // counter so a 10-way concurrent burst presenting
                    // ONE valid code doesn't auto-lock the user out.
                    // Trade-off documented on
                    // UserTotpConfig.ConsumedRecoveryCodes: an attacker
                    // who already knows a USED code can spam without
                    // lockout, but that knowledge is strictly weaker
                    // than the JWT the code produced, and the
                    // alternative lets attackers brute-force lockout.
                    if (!recoveryAlreadyConsumed)
                        lockout.RecordFailure(ch.Username);
                    audit.Log(new AuditLogEvent
                    {
                        EventType = AuditEventTypes.AuthTwoFactorVerifyFailure,
                        Outcome = AuditOutcomes.Failure,
                        ActorUserId = user.Username,
                        ActorUsername = user.Username,
                        ActorFirm = user.Firm,
                        ActorRole = user.Role,
                        SourceIp = http.Connection.RemoteIpAddress?.ToString(),
                        ResourcePath = "/auth/2fa/verify",
                        ReasonCode = recoveryAlreadyConsumed ? "recovery_code_replayed" : "2fa_wrong_code",
                    });
                    return Results.Json(new { error = "invalid code" },
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                if (!challenges.TryConsume(req.TotpChallengeToken, TotpChallengeKind.Verify, out var consumedChallenge)
                    || consumedChallenge is null
                    || !string.Equals(consumedChallenge.Username, ch.Username, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(new { error = "invalid or expired challenge" },
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                var recoveryOk = false;
                if (totpOk)
                {
                    if (!users.TryRecordTotpUse(ch.Username, matchedStep, out _))
                    {
                        lockout.RecordFailure(ch.Username);
                        return Results.Json(new { error = "invalid code" },
                            statusCode: StatusCodes.Status401Unauthorized);
                    }
                }
                else
                {
                    recoveryOk = users.TryConsumeRecoveryCode(
                        user.Username, recoveryHash!, out _) == RecoveryCodeConsumeResult.Consumed;
                    if (!recoveryOk)
                    {
                        audit.Log(new AuditLogEvent
                        {
                            EventType = AuditEventTypes.AuthTwoFactorVerifyFailure,
                            Outcome = AuditOutcomes.Failure,
                            ActorUserId = user.Username,
                            ActorUsername = user.Username,
                            ActorFirm = user.Firm,
                            ActorRole = user.Role,
                            SourceIp = http.Connection.RemoteIpAddress?.ToString(),
                            ResourcePath = "/auth/2fa/verify",
                            ReasonCode = "recovery_code_replayed",
                        });
                        return Results.Json(new { error = "invalid code" },
                            statusCode: StatusCodes.Status401Unauthorized);
                    }
                }

                lockout.RecordSuccess(ch.Username);

                var session = await sessionIssuer.IssueForLocalUserAsync(user, ct);
                if (!session.Succeeded)
                {
                    audit.Log(new AuditLogEvent
                    {
                        EventType = AuditEventTypes.AuthTwoFactorVerifyFailure,
                        Outcome = session.StatusCode == StatusCodes.Status403Forbidden ? AuditOutcomes.Denied : AuditOutcomes.Failure,
                        ActorUserId = user.Username,
                        ActorUsername = user.Username,
                        SourceIp = http.Connection.RemoteIpAddress?.ToString(),
                        ResourcePath = "/auth/2fa/verify",
                        ReasonCode = session.ErrorCode,
                    });
                    return AuthEndpoints.Error(session.StatusCode, session.ErrorCode ?? "identity_directory_unavailable");
                }
                audit.Log(new AuditLogEvent
                {
                    EventType = recoveryOk
                        ? AuditEventTypes.AuthTwoFactorRecoveryCodeConsumed
                        : AuditEventTypes.AuthTwoFactorVerifySuccess,
                    Outcome = AuditOutcomes.Success,
                    ActorUserId = session.TradingUserId,
                    ActorUsername = session.TradingUserId,
                    ActorFirm = session.Firm,
                    ActorRole = session.Role,
                    SourceIp = http.Connection.RemoteIpAddress?.ToString(),
                    ResourcePath = "/auth/2fa/verify",
                });
                return Results.Ok(new LoginResponse(session.Token!, session.ExpiresAt!.Value));
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
            audit.Log(new AuditLogEvent
            {
                EventType = AuditEventTypes.AuthTwoFactorEnrollConfirm,
                Outcome = AuditOutcomes.Success,
                ActorUserId = jwtUser.Username,
                ActorUsername = jwtUser.Username,
                ActorFirm = jwtUser.Firm,
                ActorRole = jwtUser.Role,
                SourceIp = http.Connection.RemoteIpAddress?.ToString(),
                ResourcePath = "/auth/2fa/verify",
            });
            return Results.Ok(new { enrolled = true });
        });

        app.MapPost("/auth/2fa/disable", (
            HttpContext http,
            DisableRequest req,
            IUserStore users,
            IPendingTotpEnrollmentStore pending,
            ITotpAttemptTracker lockout,
            ITotpService totp,
            ITotpSecretProtector protector,
            IAuditLogger audit) =>
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
            var recoveryAlreadyConsumed = false;
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
                var consumeResult = users.TryConsumeRecoveryCode(
                    user.Username, totp.HashRecoveryCode(req.Code), out _);
                ok = consumeResult == RecoveryCodeConsumeResult.Consumed;
                recoveryAlreadyConsumed = consumeResult == RecoveryCodeConsumeResult.AlreadyConsumed;
            }
            if (!ok)
            {
                // Mirror the verify path: AlreadyConsumed is a benign
                // replay / race-loser and must not tick lockout.
                if (!recoveryAlreadyConsumed)
                    lockout.RecordFailure(subject);
                return Results.Json(new { error = "invalid code" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            lockout.RecordSuccess(subject);
            user.Totp = user.WebAuthnCredentials.Count == 0
                ? null
                : new UserTotpConfig
                {
                    RecoveryCodes = user.Totp.RecoveryCodes,
                    ConsumedRecoveryCodes = user.Totp.ConsumedRecoveryCodes,
                };
            users.TryUpdate(user);
            pending.Remove(user.Username);
            audit.Log(new AuditLogEvent
            {
                EventType = AuditEventTypes.AuthTwoFactorDisable,
                Outcome = AuditOutcomes.Success,
                ActorUserId = user.Username,
                ActorUsername = user.Username,
                ActorFirm = user.Firm,
                ActorRole = user.Role,
                SourceIp = http.Connection.RemoteIpAddress?.ToString(),
                ResourcePath = "/auth/2fa/disable",
            });
            return Results.Ok(new { disabled = true });
        }).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> CompleteForcedEnrollmentAsync(
        HttpContext http,
        VerifyRequest req,
        TotpChallenge challenge,
        IUserStore users,
        ITradingSessionIssuer sessionIssuer,
        IPendingTotpEnrollmentStore pending,
        ITotpChallengeStore challenges,
        ITotpAttemptTracker lockout,
        ITotpService totp,
        ITotpSecretProtector protector,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (lockout.IsLocked(challenge.Username, out var retry))
            return TooManyRequests(retry);

        if (!users.TryGet(challenge.Username, out var user) || user is null || !user.Require2FA
            || user.Totp is { EnrolledAt: not null })
        {
            challenges.Invalidate(req.TotpChallengeToken!);
            return Results.Json(new { error = "invalid or expired challenge" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!pending.TryConsume(user.Username, out var enrollment) || enrollment is null)
            return Results.BadRequest(new { error = "no pending enrollment (expired or never started)" });

        var (ok, matchedStep) = totp.Verify(enrollment.Base32Secret, req.Code);
        if (!ok)
        {
            pending.Put(user.Username, enrollment);
            lockout.RecordFailure(user.Username);
            audit.Log(new AuditLogEvent
            {
                EventType = AuditEventTypes.AuthTwoFactorVerifyFailure,
                Outcome = AuditOutcomes.Failure,
                ActorUserId = user.Username,
                ActorUsername = user.Username,
                ActorFirm = user.Firm,
                ActorRole = user.Role,
                SourceIp = http.Connection.RemoteIpAddress?.ToString(),
                ResourcePath = "/auth/2fa/verify",
                ReasonCode = "2fa_wrong_code",
            });
            return Results.Json(new { error = "invalid code" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!challenges.TryConsume(req.TotpChallengeToken!, TotpChallengeKind.VerifyEnrollment, out var consumed)
            || consumed is null
            || !string.Equals(consumed.Username, user.Username, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new { error = "invalid or expired challenge" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        lockout.RecordSuccess(user.Username);
        user.Totp = new UserTotpConfig
        {
            SharedSecret = protector.Protect(enrollment.Base32Secret),
            EnrolledAt = TimeProviderFor(http).GetUtcNow(),
            RecoveryCodes = enrollment.RecoveryCodeHashes.ToList(),
            LastUsedTimeStep = matchedStep,
        };
        users.TryUpdate(user);
        audit.Log(new AuditLogEvent
        {
            EventType = AuditEventTypes.AuthTwoFactorEnrollConfirm,
            Outcome = AuditOutcomes.Success,
            ActorUserId = user.Username,
            ActorUsername = user.Username,
            ActorFirm = user.Firm,
            ActorRole = user.Role,
            SourceIp = http.Connection.RemoteIpAddress?.ToString(),
            ResourcePath = "/auth/2fa/verify",
            Details = new Dictionary<string, string> { ["mode"] = "force_enroll_token" },
        });

        var session = await sessionIssuer.IssueForLocalUserAsync(user, ct);
        if (!session.Succeeded)
        {
            audit.Log(new AuditLogEvent
            {
                EventType = AuditEventTypes.AuthTwoFactorVerifyFailure,
                Outcome = session.StatusCode == StatusCodes.Status403Forbidden ? AuditOutcomes.Denied : AuditOutcomes.Failure,
                ActorUserId = user.Username,
                ActorUsername = user.Username,
                SourceIp = http.Connection.RemoteIpAddress?.ToString(),
                ResourcePath = "/auth/2fa/verify",
                ReasonCode = session.ErrorCode,
            });
            return AuthEndpoints.Error(session.StatusCode, session.ErrorCode ?? "identity_directory_unavailable");
        }

        audit.Log(new AuditLogEvent
        {
            EventType = AuditEventTypes.AuthTwoFactorVerifySuccess,
            Outcome = AuditOutcomes.Success,
            ActorUserId = session.TradingUserId,
            ActorUsername = session.TradingUserId,
            ActorFirm = session.Firm,
            ActorRole = session.Role,
            SourceIp = http.Connection.RemoteIpAddress?.ToString(),
            ResourcePath = "/auth/2fa/verify",
        });
        return Results.Ok(new LoginResponse(session.Token!, session.ExpiresAt!.Value));
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
public sealed record TotpStatusResponse(bool Enrolled);
public sealed record EnrollResponse(
    string Secret,
    string OtpauthUri,
    IReadOnlyList<string> RecoveryCodes,
    string? TotpChallengeToken);
public sealed record VerifyRequest(string Code, string? TotpChallengeToken);
public sealed record DisableRequest(string Code);
