using System.Diagnostics;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Identity;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace B3.Trading.Api.Auth;

public static class ExternalIdentityEndpoints
{
    public static IEndpointRouteBuilder MapExternalIdentityExchange(this IEndpointRouteBuilder app)
    {
        var opts = app.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (!opts.IsExchangeEnabled())
            return app;

        app.MapPost("/auth/exchange", async (
            HttpContext http,
            IExternalIdentityTokenValidator tokenValidator,
            ITradingUserDirectory directory,
            ITradingSessionIssuer sessionIssuer,
            IOptions<AuthOptions> authOptions,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var issuerAlias = BoundedIssuerAlias(authOptions.Value.ExternalIdentity.IssuerAlias);
            string reason = "success";
            try
            {
                if (!TryGetBearer(http, out var bearer))
                {
                    reason = "invalid_external_token";
                    EmitFailure(audit, http, reason, issuerAlias);
                    return Error(StatusCodes.Status401Unauthorized, reason);
                }

                var validation = await tokenValidator.ValidateAsync(bearer, ct);
                if (validation.Status != ExternalIdentityValidationStatus.Success)
                {
                    reason = validation.Code;
                    EmitFailure(audit, http, reason, issuerAlias);
                    var status = validation.Status == ExternalIdentityValidationStatus.IdentityProviderUnavailable
                        ? StatusCodes.Status503ServiceUnavailable
                        : StatusCodes.Status401Unauthorized;
                    return Error(status, validation.Code);
                }

                TradingUser? user;
                try
                {
                    user = await directory.ResolveExternalIdentityAsync(validation.Issuer!, validation.Subject!, ct);
                }
                catch (TradingUserDirectoryException)
                {
                    reason = "identity_directory_unavailable";
                    EmitFailure(audit, http, reason, issuerAlias);
                    return Error(StatusCodes.Status503ServiceUnavailable, reason);
                }

                if (user is null)
                {
                    reason = "account_not_provisioned";
                    EmitFailure(audit, http, reason, issuerAlias);
                    return Error(StatusCodes.Status403Forbidden, reason);
                }

                var session = sessionIssuer.IssueForExternalUser(user);
                if (!session.Succeeded)
                {
                    reason = session.ErrorCode ?? "account_incomplete";
                    EmitFailure(audit, http, reason, issuerAlias, user);
                    return Error(session.StatusCode, reason);
                }

                audit.Log(new AuditLogEvent
                {
                    EventType = AuditEventTypes.AuthExchangeSuccess,
                    Outcome = AuditOutcomes.Success,
                    ActorUserId = session.TradingUserId,
                    ActorUsername = session.TradingUserId,
                    ActorFirm = session.Firm,
                    ActorRole = session.Role,
                    SourceIp = http.Connection.RemoteIpAddress?.ToString(),
                    ResourcePath = "/auth/exchange",
                    Details = BuildDetails(issuerAlias, user, validation.Issuer, validation.Subject),
                });
                return Results.Ok(new LoginResponse(session.Token!, session.ExpiresAt!.Value));
            }
            finally
            {
                sw.Stop();
                MetricsRegistry.AuthExchangeRequests.Add(1,
                    new KeyValuePair<string, object?>("result", reason == "success" ? "success" : "failure"),
                    new KeyValuePair<string, object?>("reason", BoundedReason(reason)),
                    new KeyValuePair<string, object?>("issuer_alias", issuerAlias));
                MetricsRegistry.AuthExchangeDurationSeconds.Record(sw.Elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>("result", reason == "success" ? "success" : "failure"),
                    new KeyValuePair<string, object?>("reason", BoundedReason(reason)),
                    new KeyValuePair<string, object?>("issuer_alias", issuerAlias));
            }
        });

        return app;
    }

    private static bool TryGetBearer(HttpContext http, out string token)
    {
        token = string.Empty;
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;
        token = header["Bearer ".Length..].Trim();
        return token.Length > 0;
    }

    private static IResult Error(int statusCode, string code) =>
        Results.Json(new { error = code }, statusCode: statusCode, contentType: "application/json");

    private static void EmitFailure(
        IAuditLogger audit,
        HttpContext http,
        string reason,
        string issuerAlias,
        TradingUser? user = null)
    {
        audit.Log(new AuditLogEvent
        {
            EventType = AuditEventTypes.AuthExchangeFailure,
            Outcome = reason.StartsWith("account_", StringComparison.Ordinal)
                ? AuditOutcomes.Denied
                : AuditOutcomes.Failure,
            ActorUserId = user?.TradingUserId,
            ActorUsername = user?.TradingUserId,
            ActorFirm = user?.FirmId,
            ActorRole = user?.Role,
            SourceIp = http.Connection.RemoteIpAddress?.ToString(),
            ResourcePath = "/auth/exchange",
            ReasonCode = BoundedReason(reason),
            Details = new Dictionary<string, string> { ["issuer_alias"] = issuerAlias },
        });
    }

    private static Dictionary<string, string> BuildDetails(
        string issuerAlias,
        TradingUser user,
        string? issuer,
        string? subject)
    {
        var details = new Dictionary<string, string> { ["issuer_alias"] = issuerAlias };
        var binding = user.ExternalIdentities.FirstOrDefault(b =>
            string.Equals(b.Issuer, issuer, StringComparison.Ordinal)
            && string.Equals(b.Subject, subject, StringComparison.Ordinal));
        if (binding is not null)
            details["binding_id"] = binding.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return details;
    }

    private static string BoundedIssuerAlias(string? issuerAlias) =>
        IsSafeAlias(issuerAlias?.Trim()) ? issuerAlias!.Trim() : "entra";

    private static bool IsSafeAlias(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static string BoundedReason(string reason) => reason switch
    {
        "success" => "success",
        "invalid_external_token" => "invalid_external_token",
        "identity_provider_unavailable" => "identity_provider_unavailable",
        "identity_directory_unavailable" => "identity_directory_unavailable",
        "account_not_provisioned" => "account_not_provisioned",
        "account_disabled" => "account_disabled",
        "account_incomplete" => "account_incomplete",
        _ => "other",
    };
}
