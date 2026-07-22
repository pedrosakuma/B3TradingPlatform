using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Identity;
using B3.Trading.Application.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api.Auth;

public static class IdentityAdminEndpoints
{
    private const int MaxExternalTokenChars = 32 * 1024;

    public static IEndpointRouteBuilder MapIdentityAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/identity").RequireAuthorization("admin");

        group.MapGet("/users", async (ITradingUserDirectory directory, CancellationToken ct) =>
        {
            try
            {
                var users = await directory.ListUsersAsync(ct);
                return Results.Ok(new IdentityUsersResponse(users.Select(ToDto).ToArray()));
            }
            catch (TradingUserDirectoryException)
            {
                return Error(StatusCodes.Status503ServiceUnavailable, "identity_directory_unavailable");
            }
        });

        group.MapPost("/users/{tradingUserId}/external-bindings", async (
            string tradingUserId,
            BindExternalIdentityRequest req,
            HttpContext http,
            ITradingUserDirectory directory,
            IExternalIdentityTokenValidator validator,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (req is null || req.ExpectedRowVersion <= 0 || string.IsNullOrWhiteSpace(req.ExternalAccessToken))
                return Error(StatusCodes.Status400BadRequest, "invalid_request");
            if (req.ExternalAccessToken.Length > MaxExternalTokenChars)
                return Error(StatusCodes.Status413PayloadTooLarge, "external_token_too_large");

            var before = await SafeGetUserAsync(directory, tradingUserId, ct);
            var validation = await validator.ValidateAsync(req.ExternalAccessToken, ct);
            if (validation.Status != ExternalIdentityValidationStatus.Success)
            {
                var status = validation.Status == ExternalIdentityValidationStatus.IdentityProviderUnavailable
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status401Unauthorized;
                return Error(status, validation.Code);
            }

            try
            {
                AuditIdentityMutation(
                    audit,
                    http,
                    AuditEventTypes.IdentityBindingCreate,
                    "/api/admin/identity/users/{tradingUserId}/external-bindings",
                    tradingUserId,
                    before,
                    IntendedSummary(before, rowVersionDelta: 1, bindingCountDelta: 1));
                var binding = await directory.BindExternalIdentityAsync(
                    tradingUserId,
                    new ExternalIdentityBindingRequest(
                        validation.Issuer!,
                        validation.Subject!,
                        validation.TenantId,
                        validation.ObjectId),
                    req.ExpectedRowVersion,
                    ct);
                return Results.Created(
                    $"/api/admin/identity/users/{Uri.EscapeDataString(tradingUserId)}/external-bindings/{binding.Id}",
                    ToDto(binding));
            }
            catch (WalBackpressureException)
            {
                return Error(StatusCodes.Status503ServiceUnavailable, "audit_backpressure");
            }
            catch (TradingUserDirectoryException ex)
            {
                return DirectoryError(ex);
            }
        });

        group.MapDelete("/users/{tradingUserId}/external-bindings/{bindingId:long}", async (
            string tradingUserId,
            long bindingId,
            ExpectedRowVersionRequest req,
            HttpContext http,
            ITradingUserDirectory directory,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (req is null || req.ExpectedRowVersion <= 0)
                return Error(StatusCodes.Status400BadRequest, "invalid_request");

            var before = await SafeGetUserAsync(directory, tradingUserId, ct);
            try
            {
                AuditIdentityMutation(
                    audit,
                    http,
                    AuditEventTypes.IdentityBindingDelete,
                    "/api/admin/identity/users/{tradingUserId}/external-bindings/{bindingId}",
                    tradingUserId,
                    before,
                    IntendedSummary(before, rowVersionDelta: 1, bindingCountDelta: -1),
                    bindingId);
                await directory.UnbindExternalIdentityAsync(tradingUserId, bindingId, req.ExpectedRowVersion, ct);
                return Results.NoContent();
            }
            catch (WalBackpressureException)
            {
                return Error(StatusCodes.Status503ServiceUnavailable, "audit_backpressure");
            }
            catch (TradingUserDirectoryException ex)
            {
                return DirectoryError(ex);
            }
        });

        group.MapPut("/users/{tradingUserId}/status", async (
            string tradingUserId,
            SetUserStatusRequest req,
            HttpContext http,
            ITradingUserDirectory directory,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (req is null || req.ExpectedRowVersion <= 0 || string.IsNullOrWhiteSpace(req.Status))
                return Error(StatusCodes.Status400BadRequest, "invalid_request");

            var before = await SafeGetUserAsync(directory, tradingUserId, ct);
            try
            {
                AuditIdentityMutation(
                    audit,
                    http,
                    AuditEventTypes.IdentityUserStatusChange,
                    "/api/admin/identity/users/{tradingUserId}/status",
                    tradingUserId,
                    before,
                    IntendedSummary(before, rowVersionDelta: 1, status: req.Status));
                await directory.SetStatusAsync(tradingUserId, req.Status, req.ExpectedRowVersion, ct);
                var after = await SafeGetUserAsync(directory, tradingUserId, ct);
                return Results.Ok(ToDto(after!));
            }
            catch (WalBackpressureException)
            {
                return Error(StatusCodes.Status503ServiceUnavailable, "audit_backpressure");
            }
            catch (TradingUserDirectoryException ex)
            {
                return DirectoryError(ex);
            }
        });

        group.MapPut("/users/{tradingUserId}/authorization", async (
            string tradingUserId,
            SetUserAuthorizationRequest req,
            HttpContext http,
            ITradingUserDirectory directory,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (req is null || req.ExpectedRowVersion <= 0 || string.IsNullOrWhiteSpace(req.FirmId) || string.IsNullOrWhiteSpace(req.Role))
                return Error(StatusCodes.Status400BadRequest, "invalid_request");

            var before = await SafeGetUserAsync(directory, tradingUserId, ct);
            try
            {
                AuditIdentityMutation(
                    audit,
                    http,
                    AuditEventTypes.IdentityUserAuthorizationChange,
                    "/api/admin/identity/users/{tradingUserId}/authorization",
                    tradingUserId,
                    before,
                    IntendedSummary(before, rowVersionDelta: 1, firmId: req.FirmId, role: req.Role));
                await directory.SetFirmAndRoleAsync(tradingUserId, req.FirmId, req.Role, req.ExpectedRowVersion, ct);
                var after = await SafeGetUserAsync(directory, tradingUserId, ct);
                return Results.Ok(ToDto(after!));
            }
            catch (WalBackpressureException)
            {
                return Error(StatusCodes.Status503ServiceUnavailable, "audit_backpressure");
            }
            catch (TradingUserDirectoryException ex)
            {
                return DirectoryError(ex);
            }
        });

        return app;
    }

    private static async Task<TradingUser?> SafeGetUserAsync(ITradingUserDirectory directory, string tradingUserId, CancellationToken ct)
    {
        try { return await directory.GetUserAsync(tradingUserId, ct); }
        catch (TradingUserDirectoryException) { return null; }
    }

    private static void AuditIdentityMutation(
        IAuditLogger audit,
        HttpContext http,
        string eventType,
        string resourcePath,
        string targetTradingUserId,
        TradingUser? before,
        string intendedAfter,
        long? bindingId = null)
    {
        var details = new Dictionary<string, string>
        {
            ["target_trading_user_id"] = targetTradingUserId,
            ["before"] = Summary(before),
            ["after"] = intendedAfter,
        };
        if (bindingId is not null)
            details["binding_id"] = bindingId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        audit.LogOrFail(new AuditLogEvent
        {
            EventType = eventType,
            Outcome = AuditOutcomes.Success,
            ActorUserId = http.User.FindFirstValue(JwtRegisteredClaimNames.Sub),
            ActorUsername = http.User.FindFirstValue(JwtRegisteredClaimNames.Sub),
            ActorFirm = http.User.FindFirstValue(JwtIssuer.FirmClaim),
            ActorRole = http.User.FindFirstValue(JwtIssuer.RoleClaim),
            SourceIp = http.Connection.RemoteIpAddress?.ToString(),
            ResourcePath = resourcePath,
            Details = details,
        });
    }

    private static string Summary(TradingUser? user) =>
        user is null
            ? "missing"
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"row_version={user.RowVersion};status={user.Status};firm={user.FirmId};role={user.Role};bindings={user.ExternalIdentities.Count}");

    private static string IntendedSummary(
        TradingUser? before,
        long rowVersionDelta,
        int bindingCountDelta = 0,
        string? status = null,
        string? firmId = null,
        string? role = null)
    {
        if (before is null)
            return "missing";

        var bindingCount = Math.Max(0, before.ExternalIdentities.Count + bindingCountDelta);
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"row_version={before.RowVersion + rowVersionDelta};status={status ?? before.Status};firm={firmId ?? before.FirmId};role={role ?? before.Role};bindings={bindingCount}");
    }

    private static IResult DirectoryError(TradingUserDirectoryException ex) => ex switch
    {
        TradingUserDirectoryConcurrencyException => Error(StatusCodes.Status409Conflict, "row_version_conflict"),
        TradingUserDirectoryConflictException => Error(StatusCodes.Status409Conflict, "identity_binding_conflict"),
        TradingUserDirectoryLastAdminException => Error(StatusCodes.Status409Conflict, "last_admin_conflict"),
        TradingUserDirectoryValidationException => Error(StatusCodes.Status400BadRequest, "invalid_request"),
        _ => Error(StatusCodes.Status503ServiceUnavailable, "identity_directory_unavailable"),
    };

    private static IResult Error(int statusCode, string code) =>
        Results.Json(new { error = code }, statusCode: statusCode, contentType: "application/json");

    private static IdentityUserDto ToDto(TradingUser user) =>
        new(
            user.TradingUserId,
            user.DisplayName,
            user.FirmId,
            user.Status,
            user.Role,
            user.RowVersion,
            user.CreatedAt,
            user.UpdatedAt,
            user.ExternalIdentities.Select(ToDto).ToArray());

    private static ExternalIdentityBindingDto ToDto(ExternalIdentityBinding binding) =>
        new(
            binding.Id,
            binding.Issuer,
            binding.Subject,
            binding.TradingUserId,
            binding.TenantId,
            binding.ObjectId,
            binding.CreatedAt);
}

public sealed record IdentityUsersResponse(IReadOnlyList<IdentityUserDto> Users);

public sealed record IdentityUserDto(
    string TradingUserId,
    string DisplayName,
    string FirmId,
    string Status,
    string Role,
    long RowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ExternalIdentityBindingDto> ExternalIdentities);

public sealed record ExternalIdentityBindingDto(
    long Id,
    string Issuer,
    string Subject,
    string TradingUserId,
    string? TenantId,
    string? ObjectId,
    DateTimeOffset CreatedAt);

public sealed class BindExternalIdentityRequest
{
    public string ExternalAccessToken { get; init; } = string.Empty;
    public long ExpectedRowVersion { get; init; }

    public override string ToString() => $"BindExternalIdentityRequest {{ ExpectedRowVersion = {ExpectedRowVersion}, ExternalAccessToken = <redacted> }}";
}

public sealed record ExpectedRowVersionRequest(long ExpectedRowVersion);
public sealed record SetUserStatusRequest(string Status, long ExpectedRowVersion);
public sealed record SetUserAuthorizationRequest(string FirmId, string Role, long ExpectedRowVersion);
