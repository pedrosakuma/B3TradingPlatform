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
            var newVer = await sessions.BumpVersionAsync(credentialId, "operator", ct);
            return Results.Ok(new { credentialId, newVersion = newVer });
        });

        group.MapPost("/sessions/{credentialId:guid}/terminate", (Guid credentialId, HttpContext ctx) =>
        {
            var dir = ctx.RequestServices.GetService<BotSessionConnectionDirectory>();
            if (dir is null) return Results.NotFound();
            var terminated = dir.TryForceTerminate(credentialId);
            return terminated ? Results.Ok(new { credentialId, terminated = true })
                             : Results.NotFound();
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
}
