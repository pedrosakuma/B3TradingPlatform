using System.Security.Claims;
using B3.Trading.Application.UserBots;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

/// <summary>
/// REST surface for user-issued bot credentials (sub-issue #169 of
/// RFC user-bot-fixp-listener-v0). The endpoint group is <c>[Authorize]</c>
/// — every operation acts on the authenticated user's <c>sub</c> claim.
/// Cross-user reads/writes always return 404 so the surface cannot be
/// used as a credential-id oracle.
/// </summary>
public static class UserBotCredentialsEndpoints
{
    /// <summary>
    /// Hard cap on the human-friendly label, enforced server-side.
    /// Picked deliberately low — labels are pure UI affordance, not
    /// content; the SBE Credentials field on FIXP Negotiate is 255
    /// bytes total and we have no reason to come close.
    /// </summary>
    public const int MaxLabelLength = 128;

    public static IEndpointRouteBuilder MapUserBotCredentials(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/user-bot-credentials").RequireAuthorization();

        group.MapPost("/", async (
            HttpContext ctx,
            CreateUserBotCredentialRequest req,
            IUserBotCredentialRegistry registry,
            CancellationToken ct) =>
        {
            var sub = RequireSub(ctx);

            if (req is null) return Results.BadRequest(new { error = "Body required." });
            var label = req.Label?.Trim();
            if (string.IsNullOrWhiteSpace(label))
                return Results.BadRequest(new { error = "Label is required." });
            if (label.Length > MaxLabelLength)
                return Results.BadRequest(new { error = $"Label exceeds {MaxLabelLength} characters." });

            var created = await registry.CreateAsync(sub, label, ct);
            var dto = new CreatedUserBotCredentialDto(
                Id: created.Credential.Id,
                Label: created.Credential.Label,
                CredShortId: created.Credential.CredShortId,
                CreatedAtUtc: created.Credential.CreatedAtUtc,
                PlainSecret: created.PlainToken);
            return Results.Created($"/api/user-bot-credentials/{dto.Id}", dto);
        });

        group.MapGet("/", (HttpContext ctx, IUserBotCredentialRegistry registry) =>
        {
            var sub = RequireSub(ctx);
            var rows = registry.ListByUser(sub)
                .Select(c => new UserBotCredentialDto(
                    c.Id, c.Label, c.CredShortId, c.CreatedAtUtc, c.RevokedAtUtc))
                .ToList();
            return Results.Ok(rows);
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            HttpContext ctx,
            IUserBotCredentialRegistry registry,
            CancellationToken ct) =>
        {
            var sub = RequireSub(ctx);
            var ok = await registry.RevokeAsync(sub, id, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }

    private static string RequireSub(HttpContext ctx)
    {
        var sub = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrWhiteSpace(sub))
            throw new InvalidOperationException("Authenticated request missing sub claim.");
        return sub;
    }
}

/// <summary>POST body for creating a credential.</summary>
public sealed record CreateUserBotCredentialRequest(string Label);

/// <summary>
/// 201 response from POST. <c>PlainSecret</c> is the only place the
/// platform ever returns the bearer half of the PAT — list/get never
/// include it.
/// </summary>
public sealed record CreatedUserBotCredentialDto(
    Guid Id,
    string Label,
    string CredShortId,
    DateTimeOffset CreatedAtUtc,
    string PlainSecret);

/// <summary>Public read-side DTO. No secret material.</summary>
public sealed record UserBotCredentialDto(
    Guid Id,
    string Label,
    string CredShortId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RevokedAt);
