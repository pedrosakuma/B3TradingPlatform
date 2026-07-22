using System.Security.Claims;
using B3.Trading.Api.Auth;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

/// <summary>
/// Q4.7 (#307). Read-only REST surface over <see cref="FillProjection"/>
/// — exposes the best-execution book-touch evidence captured for every
/// Fill / PartialFill ER. Single endpoint today
/// (<c>GET /api/fills/{id}/touch</c>) keyed by the canonical fill id
/// <c>{ClOrdId}:{cumulativeQuantityAfterFill}</c>.
///
/// <para><b>Firm scope.</b> A caller authenticated as <c>user</c> only
/// sees fills booked under its own firm (the <c>firm</c> JWT claim);
/// any other firm yields 404 so the endpoint does not leak existence
/// across firm boundaries. The <c>admin</c> role may additionally pass
/// <c>?firmId=</c> to scope to a specific firm; without the override
/// the admin sees fills in its own firm (typically "default"). The
/// 404-vs-403 choice matches the rest of the host's firm-scoped reads
/// (<c>GET /api/orders</c>, <c>GET /api/positions</c>) — they silently drop
/// rows belonging to other firms instead of distinguishing
/// "exists-elsewhere" from "does-not-exist".</para>
/// </summary>
public static class FillsEndpoints
{
    public static IEndpointRouteBuilder MapFills(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/fills").RequireAuthorization();

        group.MapGet("/{id}/touch", (string id, HttpContext ctx, FillProjection fills) =>
        {
            if (!fills.TryGet(id, out var record))
                return Results.NotFound();

            // Firm-scope enforcement. Admin may pass ?firmId= to scope
            // cross-firm; everyone else (including admin with no
            // override) is bound to the firm in their JWT.
            var callerFirm = ResolveFirm(ctx);
            var isAdmin = ctx.User.IsInRole(Roles.Admin);
            var requestedFirm = ctx.Request.Query.TryGetValue("firmId", out var qf) && !string.IsNullOrWhiteSpace(qf)
                ? qf.ToString()
                : null;
            string scopeFirm;
            if (requestedFirm is not null)
            {
                if (!isAdmin) return Results.Forbid();
                scopeFirm = requestedFirm;
            }
            else
            {
                scopeFirm = callerFirm;
            }

            // The projection's per-fill FirmId may be null on legacy /
            // test paths that constructed orders without a firm — fall
            // back to "default" (same fallback OrdersEndpoints uses for
            // the caller's firm) so the scope check still bites.
            var fillFirm = record.FirmId ?? "default";
            if (!string.Equals(fillFirm, scopeFirm, StringComparison.Ordinal))
                return Results.NotFound();

            var touch = record.BookTouch;
            if (touch is null)
            {
                // Q4.7 (#307). A fill that exists but has no captured
                // touch (legacy WAL segment, or a unit-test fixture that
                // bypassed the router) still surfaces with the
                // documented JSON shape so clients don't branch on
                // missing fields — Stale=true, all prices null.
                return Results.Ok(new BookTouchDto(
                    BestBid: null,
                    BestAsk: null,
                    MidPrice: null,
                    LastTradePrice: null,
                    CapturedAtUtc: record.TimestampUtc,
                    Stale: true));
            }
            return Results.Ok(new BookTouchDto(
                touch.BestBid,
                touch.BestAsk,
                touch.MidPrice,
                touch.LastTradePrice,
                touch.CapturedAtUtc,
                touch.Stale));
        });

        return app;
    }

    private static string ResolveFirm(HttpContext ctx) =>
        ctx.User.FindFirstValue(JwtIssuer.FirmClaim) ?? "default";
}
