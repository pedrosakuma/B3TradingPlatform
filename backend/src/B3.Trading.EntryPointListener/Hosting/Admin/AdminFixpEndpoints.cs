using System.Security.Claims;
using B3.Trading.Application.Audit;
using B3.Trading.Application.UserBots;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.EntryPointListener.Hosting.Admin;

/// <summary>
/// Admin endpoints for FIXP listener introspection and control.
/// Mounted at <c>/admin/fixp</c> with admin authorization.
///
/// <para>Lives in the EntryPointListener project (#188 layering refactor)
/// because every consumed type — <see cref="BotSessionConnectionDirectory"/>,
/// <see cref="BotOutboundCoordinator"/>, the listener-internal session
/// registry — is owned by this project. The Api layer no longer references
/// the listener; the Host composition root maps this extension when the
/// listener is enabled.</para>
/// </summary>
public static class AdminFixpEndpoints
{
    public static IEndpointRouteBuilder MapAdminFixp(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/fixp").RequireAuthorization("admin");

        group.MapGet("/sessions", (HttpContext ctx) =>
        {
            var dir = ctx.RequestServices.GetService<BotSessionConnectionDirectory>();
            var sessions = ctx.RequestServices.GetService<IUserBotSessionRegistry>();
            if (dir is null)
                return Results.Ok(Array.Empty<object>());

            var list = new List<object>();
            foreach (var credId in dir.RegisteredCredentialIds)
            {
                list.Add(new
                {
                    credentialId = credId,
                });
            }
            return Results.Ok(list);
        });

        group.MapPost("/sessions/{credentialId:guid}/bump", async (Guid credentialId, HttpContext ctx, CancellationToken ct) =>
        {
            var sessions = ctx.RequestServices.GetService<IUserBotSessionRegistry>();
            if (sessions is null) return Results.NotFound();
            try
            {
                // Pass-1 review (#322) P1.2. Audit-first ordering —
                // emit the operator's bump intent BEFORE the version
                // bump so a backpressured audit append refuses the
                // mutation with 503 rather than letting the version
                // counter advance un-audited.
                EmitAuditOrFailIfWired(ctx, "/admin/fixp/sessions/bump", AuditOutcomes.Success, new()
                {
                    ["credential_id"] = credentialId.ToString(),
                });
                var advance = await sessions.BumpVersionAsync(credentialId, "operator", ct);
                if (advance.DisplacedConnectionId is { } displacedConnectionId)
                {
                    ctx.RequestServices
                        .GetService<BotSessionConnectionDirectory>()
                        ?.TryForceTerminate(credentialId, displacedConnectionId);
                }
                else
                {
                    ctx.RequestServices
                        .GetService<BotSessionConnectionDirectory>()
                        ?.TryForceTerminate(credentialId);
                }
                return Results.Ok(new
                {
                    credentialId,
                    newVersion = advance.NewVersion,
                });
            }
            catch (B3.Trading.Application.Persistence.WalBackpressureException ex)
            {
                return Results.Json(
                    new { error = "system busy (WAL backpressure)", detail = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapPost("/sessions/{credentialId:guid}/terminate", (Guid credentialId, HttpContext ctx) =>
        {
            var dir = ctx.RequestServices.GetService<BotSessionConnectionDirectory>();
            if (dir is null) return Results.NotFound();
            try
            {
                // Pass-1 review (#322) P1.2. Audit-first ordering —
                // record the operator's terminate intent before the
                // dispatcher action. TryForceTerminate may still
                // return false (no live session matches) — that's
                // surfaced as 404 but the audit envelope already
                // captured the attempt.
                EmitAuditOrFailIfWired(ctx, "/admin/fixp/sessions/terminate", AuditOutcomes.Success, new()
                {
                    ["credential_id"] = credentialId.ToString(),
                });
                var terminated = dir.TryForceTerminate(credentialId);
                return terminated ? Results.Ok(new { credentialId, terminated = true })
                                 : Results.NotFound();
            }
            catch (B3.Trading.Application.Persistence.WalBackpressureException ex)
            {
                return Results.Json(
                    new { error = "system busy (WAL backpressure)", detail = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/credentials/{credentialId:guid}/buffer", (Guid credentialId, HttpContext ctx) =>
        {
            var coordinator = ctx.RequestServices.GetService<BotOutboundCoordinator>();
            if (coordinator is null) return Results.NotFound();
            var buffer = coordinator.GetOrCreateBuffer(credentialId);
            return Results.Ok(new
            {
                size = buffer.Count,
                isOverflowed = buffer.IsOverflowed,
            });
        });

        return app;
    }

    private static void EmitAuditOrFailIfWired(HttpContext ctx, string resourcePath, string outcome, Dictionary<string, string>? details = null)
    {
        // Pass-1 review (#322) P1.2. Fail-closed audit on /admin/fixp/*
        // mutating routes: a backpressured audit append propagates so
        // the endpoint can refuse the mutation with 503. We still
        // tolerate IAuditLogger not being wired (degenerate composition
        // / minimal-host tests) by short-circuiting.
        var audit = ctx.RequestServices.GetService<IAuditLogger>();
        if (audit is null) return;
        audit.LogOrFail(new B3.Trading.Application.Persistence.AuditLogEvent
        {
            EventType = AuditEventTypes.AdminConfigChange,
            Outcome = outcome,
            ActorUserId = ctx.User.FindFirstValue("sub"),
            ActorUsername = ctx.User.FindFirstValue("sub"),
            ActorFirm = ctx.User.FindFirstValue("firm"),
            ActorRole = ctx.User.FindFirstValue("role"),
            SourceIp = ctx.Connection.RemoteIpAddress?.ToString(),
            ResourcePath = resourcePath,
            Details = details,
        });
    }

    private static void EmitAuditIfWired(HttpContext ctx, string resourcePath, string outcome, Dictionary<string, string>? details = null)
    {
        var audit = ctx.RequestServices.GetService<IAuditLogger>();
        if (audit is null) return;
        audit.Log(new B3.Trading.Application.Persistence.AuditLogEvent
        {
            EventType = AuditEventTypes.AdminConfigChange,
            Outcome = outcome,
            ActorUserId = ctx.User.FindFirstValue("sub"),
            ActorUsername = ctx.User.FindFirstValue("sub"),
            ActorFirm = ctx.User.FindFirstValue("firm"),
            ActorRole = ctx.User.FindFirstValue("role"),
            SourceIp = ctx.Connection.RemoteIpAddress?.ToString(),
            ResourcePath = resourcePath,
            Details = details,
        });
    }
}
