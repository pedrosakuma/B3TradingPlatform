using System.Buffers.Binary;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using B3.Trading.Api.Auth;
using B3.Trading.Application.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

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
/// <para><b>Compliance projection (#327 pass-1 hardening).</b> For
/// compliance principals each surfaced entry is projected through a
/// redaction pass:
/// <list type="bullet">
///   <item>The platform-wide <c>Seq</c> field is omitted, and the
///   pagination cursor is AES-GCM-encrypted with a key derived from
///   the JWT signing key, so the underlying sequence number is not
///   recoverable from the wire and a compliance caller cannot infer
///   cross-firm event volume by comparing successive cursors.</item>
///   <item>When the entry is surfaced because the action targeted the
///   compliance firm (actor was in another firm), the actor identity
///   is replaced with an opaque <c>(other firm)</c> sentinel — the
///   audit fact survives without leaking who/where.</item>
///   <item>Any <c>firm</c>/<c>firmId</c>/<c>firm_id</c>/<c>target_firm</c>
///   details key whose value is not the caller's firm is dropped.</item>
/// </list>
/// Admin responses keep the legacy shape (Seq exposed, plain base64
/// cursor) for backward compatibility with operator tooling.</para>
///
/// <para>Filters: <c>since</c>, <c>until</c>, <c>user</c>, <c>type</c>
/// (exact or <c>prefix.*</c>), <c>outcome</c>, <c>limit</c> (default
/// 100, max 500), <c>cursor</c>, and (admin-only) <c>firmId</c>.</para>
/// </summary>
public static class AdminAuditEndpoints
{
    private const string OtherFirmActor = "(other firm)";

    public static IEndpointRouteBuilder MapAdminAudit(this IEndpointRouteBuilder app)
    {
        // Mounted as a standalone endpoint (NOT under the /admin
        // group) so its policy stays orthogonal to the broader admin
        // surface: compliance must reach /admin/audit but must NOT
        // reach /admin/kill, /admin/halts, /admin/firms, etc.
        app.MapGet("/admin/audit", (HttpContext ctx, AuditLogKeeper keeper, IOptions<AuthOptions> authOpts) =>
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

            // Cursor decoding picks the format per-role: plain
            // base64(seq) for admin (legacy); HMAC-signed for
            // compliance so a tampered or forged cursor is rejected
            // and the seq value is not directly readable.
            long? cursorSeq = null;
            if (q.TryGetValue("cursor", out var c) && !string.IsNullOrWhiteSpace(c))
            {
                bool ok = isAdmin
                    ? TryDecodeAdminCursor(c.ToString(), out var decodedAdmin) && (cursorSeq = decodedAdmin) is not null
                    : TryDecodeComplianceCursor(c.ToString(), ResolveCursorKey(authOpts.Value), out var decodedComp) && (cursorSeq = decodedComp) is not null;
                if (!ok)
                    return Results.BadRequest(new { error = "invalid cursor" });
            }

            var result = keeper.Query(since, until, user, type, outcome, limit, cursorSeq, firmFilter, restrictUserToFirm: !isAdmin);

            if (isAdmin)
            {
                return Results.Ok(new
                {
                    entries = result.Entries,
                    nextCursor = result.NextCursorSeq is long ns ? EncodeAdminCursor(ns) : null,
                });
            }

            // Compliance projection: omit Seq, redact cross-firm
            // identifiers, HMAC-sign the cursor.
            var callerFirm = firmFilter!;
            var redacted = new List<object>(result.Entries.Count);
            foreach (var e in result.Entries)
                redacted.Add(ProjectForCompliance(e, callerFirm));

            return Results.Ok(new
            {
                entries = redacted,
                nextCursor = result.NextCursorSeq is long cs
                    ? EncodeComplianceCursor(cs, ResolveCursorKey(authOpts.Value))
                    : null,
            });
        }).RequireAuthorization("admin-or-compliance");

        return app;
    }

    private static object ProjectForCompliance(AuditEntry e, string callerFirm)
    {
        var actorFromOtherFirm = !string.IsNullOrEmpty(e.ActorFirm)
            && !string.Equals(e.ActorFirm, callerFirm, StringComparison.OrdinalIgnoreCase);

        IReadOnlyDictionary<string, string>? details = e.Details;
        if (details is not null)
        {
            Dictionary<string, string>? copy = null;
            foreach (var key in AuditLogKeeper.FirmDetailKeys)
            {
                if (!details.TryGetValue(key, out var v)) continue;
                if (string.Equals(v, callerFirm, StringComparison.OrdinalIgnoreCase)) continue;
                copy ??= new Dictionary<string, string>(details, StringComparer.Ordinal);
                copy.Remove(key);
            }
            if (copy is not null) details = copy;
        }

        // When the action originated from another firm but targeted
        // the caller's firm, redact actor identity & source ip.
        // ActorRole stays — it identifies the role of the actor (e.g.
        // "admin") which is operationally relevant without naming the
        // individual.
        return new
        {
            // No Seq — withholding the platform-wide sequence number
            // prevents compliance from gap-counting cross-firm volume.
            e.Id,
            e.TimestampUtc,
            e.EventType,
            e.Outcome,
            ActorUserId = actorFromOtherFirm ? null : e.ActorUserId,
            ActorUsername = actorFromOtherFirm ? OtherFirmActor : e.ActorUsername,
            ActorFirm = actorFromOtherFirm ? null : e.ActorFirm,
            e.ActorRole,
            SourceIp = actorFromOtherFirm ? null : e.SourceIp,
            e.ResourcePath,
            e.ReasonCode,
            Details = details,
        };
    }

    private static string EncodeAdminCursor(long seq)
    {
        var raw = Encoding.UTF8.GetBytes(seq.ToString(CultureInfo.InvariantCulture));
        return Convert.ToBase64String(raw);
    }

    private static bool TryDecodeAdminCursor(string cursor, out long seq)
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

    // ── Compliance cursor: AES-GCM-encrypted big-endian seq, with the
    // GCM tag providing both authentication and tamper rejection.
    // Format on the wire: base64( nonce(12) || ciphertext(8) || tag(16) ).
    //
    // Pass-3 (#327) hardening: the prior format (raw big-endian seq ||
    // HMAC) only authenticated the cursor; the sequence number was
    // still base64-decodable. A compliance caller could therefore walk
    // pages, decode each nextCursor, and gap-count hidden cross-firm
    // WAL volume across pages. Encrypting the seq under a per-cursor
    // random nonce makes successive cursors indistinguishable (same
    // seq → different ciphertext) and unrecoverable without the key.
    //
    // Key: SHA-256 of the JWT signing key (which AuthSigningKeyValidator
    // already constrains to ≥ 32 bytes); no second secret to manage.
    private const int ComplianceNonceLen = 12;
    private const int CompliancePlaintextLen = 8;
    private const int ComplianceTagLen = 16;
    private const int ComplianceCursorLen = ComplianceNonceLen + CompliancePlaintextLen + ComplianceTagLen;

    private static string EncodeComplianceCursor(long seq, byte[] key)
    {
        Span<byte> nonce = stackalloc byte[ComplianceNonceLen];
        RandomNumberGenerator.Fill(nonce);
        Span<byte> plaintext = stackalloc byte[CompliancePlaintextLen];
        BinaryPrimitives.WriteInt64BigEndian(plaintext, seq);
        Span<byte> ciphertext = stackalloc byte[CompliancePlaintextLen];
        Span<byte> tag = stackalloc byte[ComplianceTagLen];
        using var aes = new AesGcm(key, ComplianceTagLen);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        Span<byte> combined = stackalloc byte[ComplianceCursorLen];
        nonce.CopyTo(combined);
        ciphertext.CopyTo(combined.Slice(ComplianceNonceLen));
        tag.CopyTo(combined.Slice(ComplianceNonceLen + CompliancePlaintextLen));
        return Convert.ToBase64String(combined);
    }

    private static bool TryDecodeComplianceCursor(string cursor, byte[] key, out long seq)
    {
        seq = 0;
        byte[] raw;
        try { raw = Convert.FromBase64String(cursor); }
        catch (FormatException) { return false; }
        if (raw.Length != ComplianceCursorLen) return false;
        Span<byte> plaintext = stackalloc byte[CompliancePlaintextLen];
        try
        {
            using var aes = new AesGcm(key, ComplianceTagLen);
            aes.Decrypt(
                raw.AsSpan(0, ComplianceNonceLen),
                raw.AsSpan(ComplianceNonceLen, CompliancePlaintextLen),
                raw.AsSpan(ComplianceNonceLen + CompliancePlaintextLen, ComplianceTagLen),
                plaintext);
        }
        catch (CryptographicException)
        {
            return false;
        }
        seq = BinaryPrimitives.ReadInt64BigEndian(plaintext);
        return true;
    }

    private static byte[] ResolveCursorKey(AuthOptions opts)
    {
        // Derive a 32-byte AES key from the JWT signing key (which is
        // already required to be ≥ 32 bytes by AuthSigningKeyValidator).
        // SHA-256 gives us the right length without introducing a
        // second secret to manage.
        return SHA256.HashData(Encoding.UTF8.GetBytes(opts.SigningKey ?? string.Empty));
    }
}
