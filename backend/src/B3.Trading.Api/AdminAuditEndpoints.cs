using System.Buffers.Text;
using System.Globalization;
using System.Text;
using B3.Trading.Application.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

/// <summary>
/// Q4.5 (#305). Read-only admin surface over <see cref="AuditLogKeeper"/>.
/// Mounted under <c>/admin/audit</c>, gated by the existing <c>"admin"</c>
/// authorization policy (anonymous → 401, non-admin → 403). The endpoint
/// returns events newest-first with opaque base64 cursor pagination.
///
/// <para>Scope is global by design: a single admin role exists today, so
/// audit is visible across firms. Once #314 introduces a dedicated
/// compliance role this should be retightened. Filters: <c>since</c>,
/// <c>until</c>, <c>user</c>, <c>type</c> (exact or <c>prefix.*</c>),
/// <c>outcome</c>, <c>limit</c> (default 100, max 500), <c>cursor</c>.</para>
/// </summary>
public static class AdminAuditEndpoints
{
    public static IEndpointRouteBuilder MapAdminAudit(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin").RequireAuthorization("admin");

        group.MapGet("/audit", (HttpContext ctx, AuditLogKeeper keeper) =>
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

            var result = keeper.Query(since, until, user, type, outcome, limit, cursorSeq);
            return Results.Ok(new
            {
                entries = result.Entries,
                nextCursor = result.NextCursorSeq is long ns ? EncodeCursor(ns) : null,
            });
        });

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
