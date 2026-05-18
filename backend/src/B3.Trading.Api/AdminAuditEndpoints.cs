using System.Buffers.Text;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using B3.Trading.Api.Auth;
using B3.Trading.Application.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

/// <summary>
/// Q4.5 (#305) + Q4.14 (#314). Read-only admin/compliance surface over
/// <see cref="AuditLogKeeper"/>. Mounted at <c>/admin/audit</c>, gated
/// by the <c>"admin-or-compliance"</c> authorization policy
/// (anonymous → 401, plain user → 403). The endpoint returns events
/// newest-first with opaque base64 cursor pagination.
///
/// <para><b>Scope.</b> Admin sees everything across firms (and may
/// pass <c>?firmId=</c> to narrow). Compliance is firm-scoped at the
/// server: the caller's JWT <c>firm</c> claim is forced as the
/// <c>firmFilter</c> on <see cref="AuditLogKeeper.Query"/>, regardless
/// of what they pass on the query string. The <c>?firmId=</c> query
/// argument is ignored for compliance — it cannot be used to peek at
/// another firm's actor names (LGPD).</para>
///
/// <para>Filters: <c>since</c>, <c>until</c>, <c>user</c>, <c>type</c>
/// (exact or <c>prefix.*</c>), <c>outcome</c>, <c>limit</c> (default
/// 100, max 500), <c>cursor</c>, and (admin-only) <c>firmId</c>.</para>
/// </summary>
public static class AdminAuditEndpoints
{
    public static IEndpointRouteBuilder MapAdminAudit(this IEndpointRouteBuilder app)
    {
        // Mounted as a standalone endpoint (NOT under the /admin
        // group) so its policy stays orthogonal to the broader admin
        // surface: compliance must reach /admin/audit but must NOT
        // reach /admin/kill, /admin/halts, /admin/firms, etc.
        app.MapGet("/admin/audit", (HttpContext ctx, AuditLogKeeper keeper) =>
        {
            var q = ctx.Request.Query;
            var now = DateTimeOffset.UtcNow;

            DateTimeOffset since = now.AddHours(-24);
            DateTimeOffset until = now;
            if (q.TryGetValue("since", out var s) && DateTimeOffset.TryParse(s.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedSince))
                since = parsedSince;
            if (q.TryGetValue("until", out var u) && DateTimeOffset.TryParse(u.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedUntil))
                until = parsedUntil;

            int limit = 100;
            if (q.TryGetValue("limit", out var l) && int.TryParse(l.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLimit))
                limit = parsedLimit;

            string? user = q.TryGetValue("user", out var us) && !string.IsNullOrWhiteSpace(us) ? us.ToString() : null;
            string? type = q.TryGetValue("type", out var t) && !string.IsNullOrWhiteSpace(t) ? t.ToString() : null;
            string? outcome = q.TryGetValue("outcome", out var o) && !string.IsNullOrWhiteSpace(o) ? o.ToString() : null;

            long? cursorSeq = null;
            if (q.TryGetValue("cursor", out var c) && !string.IsNullOrWhiteSpace(c))
            {
                if (!TryDecodeCursor(c.ToString(), out var decoded))
                    return Results.BadRequest(new { error = "invalid cursor" });
                cursorSeq = decoded;
            }

            // Compliance is forced to its own JWT firm; admin may
            // optionally narrow with ?firmId=. Plain admin (no
            // firmId) sees ALL firms — backwards-compatible with
            // Q4.5 (#305).
            var role = ctx.User.FindFirstValue(JwtIssuer.RoleClaim);
            var isAdmin = string.Equals(role, Roles.Admin, StringComparison.OrdinalIgnoreCase);
            string? firmFilter;
            if (isAdmin)
            {
                firmFilter = q.TryGetValue("firmId", out var qf) && !string.IsNullOrWhiteSpace(qf)
                    ? qf.ToString()
                    : null;
            }
            else
            {
                // Compliance (only other role admitted by the policy).
                // Force the caller's own firm; ignore any ?firmId=.
                firmFilter = ctx.User.FindFirstValue(JwtIssuer.FirmClaim) ?? "default";
            }

            var result = keeper.Query(since, until, user, type, outcome, limit, cursorSeq, firmFilter);
            return Results.Ok(new
            {
                entries = result.Entries,
                nextCursor = result.NextCursorSeq is long ns ? EncodeCursor(ns) : null,
            });
        }).RequireAuthorization("admin-or-compliance");

        return app;
    }

    private static string EncodeCursor(long seq)
    {
        var raw = Encoding.UTF8.GetBytes(seq.ToString(CultureInfo.InvariantCulture));
        return Convert.ToBase64String(raw);
    }

    private static bool TryDecodeCursor(string cursor, out long seq)
    {
        seq = 0;
        try
        {
            var raw = Convert.FromBase64String(cursor);
            var str = Encoding.UTF8.GetString(raw);
            return long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out seq);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
