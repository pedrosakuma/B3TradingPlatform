using System.Security.Claims;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

public static class PositionsEndpoints
{
    public static IEndpointRouteBuilder MapPositions(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/positions", [Authorize] (
            HttpContext ctx,
            EndClientRegistry registry,
            PositionKeeper positions,
            SubAccountPositionKeeper subPositions,
            SubAccountsRegistry subAccounts,
            string? subAccount) =>
        {
            var sub = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                      ?? throw new InvalidOperationException("Authenticated request missing sub claim.");
            var owner = registry.Register(sub);
            // Q4.1 (#301). When ?subAccount=X is supplied, validate the
            // id and return only that bucket. Without the filter the
            // legacy master-aggregate view is returned (sum across
            // sub-accounts + untagged) — pre-#301 wire shape preserved.
            if (!string.IsNullOrWhiteSpace(subAccount))
            {
                SubAccountId saId;
                try { saId = new SubAccountId(subAccount); }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = $"invalid subAccount: {ex.Message}" });
                }
                var firm = ctx.User.FindFirstValue(Auth.JwtIssuer.FirmClaim) ?? "default";
                if (!subAccounts.TryGet(firm, saId.Value, out _))
                    return Results.BadRequest(new { error = $"sub-account '{saId.Value}' is not registered for firm" });
                var view = subPositions.ForSubAccount(firm, owner, saId)
                    .Where(p => p.NetQuantity != 0)
                    .Select(p => p.ToDto(saId));
                return Results.Ok(view);
            }
            var legacyFirm = ctx.User.FindFirstValue(Auth.JwtIssuer.FirmClaim) ?? "default";
            // PR #316 P1. Firm-scope the unfiltered view so the same
            // JWT sub registered in two firms doesn't see the other
            // firm's positions (the master keeper bucket is keyed by
            // firmId since #316).
            var legacy = positions.ForEndClientAndFirm(legacyFirm, owner).Select(p => p.ToDto());
            return Results.Ok(legacy);
        });

        return app;
    }
}
