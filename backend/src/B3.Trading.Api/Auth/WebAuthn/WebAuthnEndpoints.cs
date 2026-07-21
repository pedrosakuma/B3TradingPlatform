using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using B3.Trading.Api.Auth.Totp;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Persistence;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Auth.WebAuthn;

public static class WebAuthnEndpoints
{
    public static IEndpointRouteBuilder MapWebAuthn(this IEndpointRouteBuilder app)
    {
        var authOptions = app.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (!authOptions.IsWebAuthnEnabled())
            return app;

        app.MapPost("/auth/webauthn/register", RegisterAsync)
            .RequireAuthorization();
        app.MapPost("/auth/webauthn/authenticate", AuthenticateAsync);

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext http,
        WebAuthnRegistrationRequest request,
        IUserStore users,
        IFido2 fido2,
        IWebAuthnChallengeStore challenges,
        IWebAuthnCredentialProtector protector,
        ITotpAttemptTracker attempts,
        ITotpService recoveryCodes,
        IOptions<TotpOptions> totpOptions,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var username = http.User.FindFirstValue(
            System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(username)
            || !users.TryGet(username, out var user)
            || user is null)
            return Unauthorized();

        if (attempts.IsLocked(username, out var retry))
            return TooManyRequests(retry);

        if (request.Credential is null)
        {
            var credentialName = string.IsNullOrWhiteSpace(request.Name)
                ? $"Passkey {user.WebAuthnCredentials.Count + 1}"
                : request.Name.Trim();
            if (credentialName.Length > 100)
                return Results.BadRequest(new { error = "passkey name is too long" });

            var descriptors = new List<PublicKeyCredentialDescriptor>();
            foreach (var credential in user.WebAuthnCredentials)
            {
                try
                {
                    descriptors.Add(new PublicKeyCredentialDescriptor(
                        protector.Unprotect(credential.ProtectedCredentialId)));
                }
                catch (CryptographicException)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status500InternalServerError,
                        title: "stored passkey could not be decrypted");
                }
            }

            var userHandle = SHA256.HashData(
                Encoding.UTF8.GetBytes($"B3.Trading.Api.WebAuthn.User.v1:{user.Username}"));
            var options = fido2.RequestNewCredential(new RequestNewCredentialParams
            {
                User = new Fido2User
                {
                    Name = user.Username,
                    DisplayName = user.Username,
                    Id = userHandle,
                },
                ExcludeCredentials = descriptors,
                AuthenticatorSelection = AuthenticatorSelection.Default,
                AttestationPreference = AttestationConveyancePreference.None,
            });
            var ceremonyToken = challenges.PutRegistration(
                user.Username, credentialName, options);
            return Results.Ok(new WebAuthnOptionsResponse<CredentialCreateOptions>(
                ceremonyToken, options));
        }

        if (!challenges.TryConsumeRegistration(
                request.CeremonyToken ?? string.Empty, out var pending)
            || pending is null
            || !string.Equals(pending.Username, username, StringComparison.OrdinalIgnoreCase))
            return InvalidChallenge();
        if (request.Credential.RawId is not { Length: > 0 }
            || request.Credential.Response is null)
            return Results.BadRequest(new { error = "invalid passkey response" });

        try
        {
            var registered = await fido2.MakeNewCredentialAsync(
                new MakeNewCredentialParams
                {
                    AttestationResponse = request.Credential,
                    OriginalOptions = pending.Options,
                    IsCredentialIdUniqueToUserCallback = (args, _) =>
                        Task.FromResult(users.IsWebAuthnCredentialIdUnique(
                            protector.HashCredentialId(args.CredentialId))),
                },
                ct);

            var plainRecoveryCodes = recoveryCodes.GenerateRecoveryCodes(
                totpOptions.Value.RecoveryCodeCount);
            var recoveryHashes = plainRecoveryCodes.Select(recoveryCodes.HashRecoveryCode).ToList();
            var storedCredential = new UserWebAuthnCredential
            {
                Name = pending.CredentialName,
                CredentialIdHash = protector.HashCredentialId(registered.Id),
                ProtectedCredentialId = protector.Protect(registered.Id),
                ProtectedPublicKey = protector.Protect(registered.PublicKey),
                ProtectedUserHandle = protector.Protect(registered.User.Id),
                SignatureCounter = registered.SignCount,
                RegisteredAt = TimeProviderFor(http).GetUtcNow(),
                AaGuid = registered.AaGuid,
                IsBackupEligible = registered.IsBackupEligible,
                IsBackedUp = registered.IsBackedUp,
            };
            if (!users.TryAddWebAuthnCredential(
                    username,
                    storedCredential,
                    recoveryHashes,
                    out var recoveryCodesStored,
                    out _))
            {
                attempts.RecordFailure(username);
                return Results.Conflict(new { error = "passkey already registered" });
            }

            attempts.RecordSuccess(username);
            audit.Log(new AuditLogEvent
            {
                EventType = AuditEventTypes.AuthTwoFactorEnrollConfirm,
                Outcome = AuditOutcomes.Success,
                ActorUserId = user.Username,
                ActorUsername = user.Username,
                ActorFirm = user.Firm,
                ActorRole = user.Role,
                SourceIp = http.Connection.RemoteIpAddress?.ToString(),
                ResourcePath = "/auth/webauthn/register",
                Details = new Dictionary<string, string> { ["factor"] = "webauthn" },
            });
            return Results.Ok(new WebAuthnRegistrationResponse(
                Registered: true,
                Name: pending.CredentialName,
                RecoveryCodes: recoveryCodesStored ? plainRecoveryCodes : Array.Empty<string>()));
        }
        catch (Fido2VerificationException)
        {
            attempts.RecordFailure(username);
            return InvalidCredential();
        }
        catch (CryptographicException)
        {
            attempts.RecordFailure(username);
            return InvalidCredential();
        }
    }

    private static async Task<IResult> AuthenticateAsync(
        HttpContext http,
        WebAuthnAuthenticationRequest request,
        IUserStore users,
        ITradingSessionIssuer sessionIssuer,
        IFido2 fido2,
        IWebAuthnChallengeStore challenges,
        IWebAuthnCredentialProtector protector,
        ITotpChallengeStore loginChallenges,
        ITotpAttemptTracker attempts,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (request.Credential is null)
        {
            var loginChallenge = loginChallenges.Peek(request.ChallengeToken ?? string.Empty);
            if (loginChallenge is null || loginChallenge.Kind != TotpChallengeKind.Verify)
                return InvalidChallenge();
            if (attempts.IsLocked(loginChallenge.Username, out var retry))
                return TooManyRequests(retry);
            if (!users.TryGet(loginChallenge.Username, out var user)
                || user is null
                || user.WebAuthnCredentials.Count == 0)
                return InvalidChallenge();

            var descriptors = new List<PublicKeyCredentialDescriptor>();
            try
            {
                descriptors.AddRange(user.WebAuthnCredentials.Select(credential =>
                    new PublicKeyCredentialDescriptor(
                        protector.Unprotect(credential.ProtectedCredentialId))));
            }
            catch (CryptographicException)
            {
                return InvalidChallenge();
            }

            var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
            {
                AllowedCredentials = descriptors,
                UserVerification = UserVerificationRequirement.Required,
            });
            var ceremonyToken = challenges.PutAuthentication(
                user.Username, request.ChallengeToken!, options);
            return Results.Ok(new WebAuthnOptionsResponse<AssertionOptions>(
                ceremonyToken, options));
        }

        if (!challenges.TryConsumeAuthentication(
                request.CeremonyToken ?? string.Empty, out var pending)
            || pending is null)
            return InvalidChallenge();
        if (request.Credential.RawId is not { Length: > 0 }
            || request.Credential.Response is null)
            return Results.BadRequest(new { error = "invalid passkey response" });
        var loginChallengeAfterResponse = loginChallenges.Peek(pending.LoginChallengeToken);
        if (loginChallengeAfterResponse is null
            || loginChallengeAfterResponse.Kind != TotpChallengeKind.Verify
            || !string.Equals(
                loginChallengeAfterResponse.Username,
                pending.Username,
                StringComparison.OrdinalIgnoreCase))
            return InvalidChallenge();
        if (attempts.IsLocked(pending.Username, out var retryAfter))
            return TooManyRequests(retryAfter);
        if (!users.TryGet(pending.Username, out var authenticatingUser)
            || authenticatingUser is null)
            return InvalidChallenge();

        var credentialHash = protector.HashCredentialId(request.Credential.RawId);
        var storedCredential = authenticatingUser.WebAuthnCredentials.FirstOrDefault(item =>
            string.Equals(item.CredentialIdHash, credentialHash, StringComparison.Ordinal));
        if (storedCredential is null)
        {
            attempts.RecordFailure(pending.Username);
            return InvalidCredential();
        }

        try
        {
            var publicKey = protector.Unprotect(storedCredential.ProtectedPublicKey);
            var userHandle = protector.Unprotect(storedCredential.ProtectedUserHandle);
            var result = await fido2.MakeAssertionAsync(
                new MakeAssertionParams
                {
                    AssertionResponse = request.Credential,
                    OriginalOptions = pending.Options,
                    StoredPublicKey = publicKey,
                    StoredSignatureCounter = storedCredential.SignatureCounter,
                    IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                        Task.FromResult(
                            CryptographicOperations.FixedTimeEquals(args.UserHandle, userHandle)
                            && CryptographicOperations.FixedTimeEquals(
                                args.CredentialId, request.Credential.RawId)),
                },
                ct);

            if (!loginChallenges.TryConsume(
                    pending.LoginChallengeToken,
                    TotpChallengeKind.Verify,
                    out var consumed)
                || consumed is null
                || !string.Equals(
                    consumed.Username, pending.Username, StringComparison.OrdinalIgnoreCase))
                return InvalidChallenge();

            if (!users.TryUpdateWebAuthnCounter(
                    pending.Username,
                    credentialHash,
                    storedCredential.SignatureCounter,
                    result.SignCount,
                    result.IsBackedUp,
                    out var updatedUser)
                || updatedUser is null)
            {
                attempts.RecordFailure(pending.Username);
                return InvalidCredential();
            }

            attempts.RecordSuccess(pending.Username);
            var session = await sessionIssuer.IssueForLocalUserAsync(updatedUser, ct);
            if (!session.Succeeded)
                return AuthEndpoints.Error(
                    session.StatusCode,
                    session.ErrorCode ?? "identity_directory_unavailable");

            audit.Log(new AuditLogEvent
            {
                EventType = AuditEventTypes.AuthTwoFactorVerifySuccess,
                Outcome = AuditOutcomes.Success,
                ActorUserId = session.TradingUserId,
                ActorUsername = session.TradingUserId,
                ActorFirm = session.Firm,
                ActorRole = session.Role,
                SourceIp = http.Connection.RemoteIpAddress?.ToString(),
                ResourcePath = "/auth/webauthn/authenticate",
                Details = new Dictionary<string, string> { ["factor"] = "webauthn" },
            });
            return Results.Ok(new LoginResponse(session.Token!, session.ExpiresAt!.Value));
        }
        catch (Fido2VerificationException)
        {
            attempts.RecordFailure(pending.Username);
            return InvalidCredential();
        }
        catch (CryptographicException)
        {
            attempts.RecordFailure(pending.Username);
            return InvalidCredential();
        }
    }

    private static IResult Unauthorized() =>
        Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult InvalidChallenge() =>
        Results.Json(
            new { error = "invalid or expired challenge" },
            statusCode: StatusCodes.Status401Unauthorized);

    private static IResult InvalidCredential() =>
        Results.Json(
            new { error = "invalid passkey" },
            statusCode: StatusCodes.Status401Unauthorized);

    private static IResult TooManyRequests(TimeSpan retry)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds));
        return Results.Json(
                new { error = "too many attempts", retryAfterSeconds = seconds },
                statusCode: StatusCodes.Status429TooManyRequests)
            .WithRetryAfter(seconds);
    }

    private static TimeProvider TimeProviderFor(HttpContext http) =>
        http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
}

public sealed class WebAuthnRegistrationRequest
{
    public string? Name { get; set; }
    public string? CeremonyToken { get; set; }
    public AuthenticatorAttestationRawResponse? Credential { get; set; }
}

public sealed class WebAuthnAuthenticationRequest
{
    public string? ChallengeToken { get; set; }
    public string? CeremonyToken { get; set; }
    public AuthenticatorAssertionRawResponse? Credential { get; set; }
}

public sealed record WebAuthnOptionsResponse<T>(string CeremonyToken, T Options);

public sealed record WebAuthnRegistrationResponse(
    bool Registered,
    string Name,
    IReadOnlyList<string> RecoveryCodes);
