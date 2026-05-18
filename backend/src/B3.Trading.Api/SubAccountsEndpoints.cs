using System.Security.Claims;
using B3.Trading.Api.Auth;
using B3.Trading.Application;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

/// <summary>
/// Q4.1 (#301). REST surface for the sub-account master-broker /
/// sub-account model. <c>GET</c> is open to any authenticated caller
/// (trader UIs need the list to populate the dropdown), <c>POST</c>
/// and <c>DELETE</c> require the <c>admin</c> policy — the spec
/// reserves sub-account lifecycle to operators. Every mutation
/// appends a WAL event so a snapshot+tail recovery converges on
/// identical registry state.
/// </summary>
public static class SubAccountsEndpoints
{
    public static IEndpointRouteBuilder MapSubAccounts(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/sub-accounts").RequireAuthorization();

        group.MapGet("/", (HttpContext ctx, SubAccountsRegistry registry, bool? includeDeactivated) =>
        {
            var firm = ResolveFirm(ctx);
            var rows = registry.ListForFirm(firm);
            if (includeDeactivated is not true)
                rows = rows.Where(r => r.Active).ToList();
            return Results.Ok(rows.Select(r => new SubAccountDto(
                r.Id, r.DisplayName, r.Active)));
        });

        var admin = app.MapGroup("/sub-accounts").RequireAuthorization("admin");

        admin.MapPost("/", (
            SubAccountCreateRequest? req,
            HttpContext ctx,
            SubAccountsRegistry registry,
            EventDispatcher dispatcher,
            IAuditLogger audit) =>
        {
            if (req is null) return Results.BadRequest(new { error = "missing body" });
            SubAccountId id;
            try { id = new SubAccountId(req.Id); }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            var firm = ResolveFirm(ctx);
            var actor = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            try
            {
                // Pass-1 review (#322) P1.2. Audit-first ordering —
                // emit the operator's sub-account create intent
                // BEFORE the WAL business event so a backpressured
                // audit append refuses the registry mutation with
                // 503 rather than committing a sub-account
                // un-audited.
                audit.LogOrFail(new AuditLogEvent
                {
                    EventType = AuditEventTypes.AdminSubAccountCreate,
                    Outcome = AuditOutcomes.Success,
                    ActorUserId = actor,
                    ActorUsername = actor,
                    ActorFirm = firm,
                    ActorRole = ctx.User.FindFirstValue(JwtIssuer.RoleClaim),
                    SourceIp = ctx.Connection.RemoteIpAddress?.ToString(),
                    ResourcePath = "/sub-accounts",
                    Details = new Dictionary<string, string>
                    {
                        ["firm"] = firm,
                        ["sub_account_id"] = id.Value,
                        ["display_name"] = req.DisplayName ?? "",
                    },
                });
                dispatcher.Dispatch(
                    new SubAccountCreatedEvent
                    {
                        FirmId = firm,
                        Id = id.Value,
                        DisplayName = req.DisplayName,
                        ActorUserId = actor,
                    },
                    () => registry.ApplyCreated(firm, id.Value, req.DisplayName));
                return Results.Created($"/sub-accounts/{id.Value}",
                    new SubAccountDto(id.Value, req.DisplayName, Active: true));
            }
            catch (WalBackpressureException ex)
            {
                return Results.Json(
                    new { error = "system busy (WAL backpressure)", detail = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        admin.MapDelete("/{id}", (
            string id,
            HttpContext ctx,
            SubAccountsRegistry registry,
            EventDispatcher dispatcher,
            IAuditLogger audit) =>
        {
            SubAccountId sub;
            try { sub = new SubAccountId(id); }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            var firm = ResolveFirm(ctx);
            if (!registry.TryGet(firm, sub.Value, out _))
                return Results.NotFound(new { error = $"sub-account '{sub.Value}' not found for firm" });
            var actor = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            try
            {
                // Pass-1 review (#322) P1.2. Audit-first ordering —
                // see create handler above.
                audit.LogOrFail(new AuditLogEvent
                {
                    EventType = AuditEventTypes.AdminSubAccountDeactivate,
                    Outcome = AuditOutcomes.Success,
                    ActorUserId = actor,
                    ActorUsername = actor,
                    ActorFirm = firm,
                    ActorRole = ctx.User.FindFirstValue(JwtIssuer.RoleClaim),
                    SourceIp = ctx.Connection.RemoteIpAddress?.ToString(),
                    ResourcePath = $"/sub-accounts/{sub.Value}",
                    Details = new Dictionary<string, string>
                    {
                        ["firm"] = firm,
                        ["sub_account_id"] = sub.Value,
                    },
                });
                dispatcher.Dispatch(
                    new SubAccountDeactivatedEvent
                    {
                        FirmId = firm,
                        Id = sub.Value,
                        ActorUserId = actor,
                    },
                    () => registry.ApplyDeactivated(firm, sub.Value));
                return Results.NoContent();
            }
            catch (WalBackpressureException ex)
            {
                return Results.Json(
                    new { error = "system busy (WAL backpressure)", detail = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        return app;
    }

    private static string ResolveFirm(HttpContext ctx) =>
        ctx.User.FindFirstValue(JwtIssuer.FirmClaim) ?? "default";
}

public sealed record SubAccountCreateRequest(string Id, string? DisplayName = null);

public sealed record SubAccountDto(string Id, string? DisplayName, bool Active);
