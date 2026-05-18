using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Xml;
using B3.Trading.Api.Auth;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Reports.Cvm;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

/// <summary>
/// Q4.8 (#308). On-demand CVM 35 (negociações) / CVM 505 (fundos)
/// transaction-report export. Streams a fixed-shape XML
/// (see <see cref="CvmReportWriter"/>) over the response body so the
/// host never materialises the whole document in memory regardless of
/// fill count. Generated lazily from the WAL — nothing is persisted to
/// disk; the regulator-facing 7-year retention is satisfied
/// implicitly by WAL segment retention.
///
/// <list type="bullet">
///   <item><c>GET /reports/cvm/35/{date:yyyy-MM-dd}?firmId=</c></item>
///   <item><c>GET /reports/cvm/505/{date:yyyy-MM-dd}?firmId=</c></item>
/// </list>
///
/// <para><b>Auth.</b> Requires the <c>ComplianceOrAdmin</c> policy.
/// Firm scope: defaults to the caller's JWT firm claim; admin and
/// compliance principals may pass <c>?firmId=</c> to scope to
/// another firm. A non-admin/non-compliance caller is rejected at
/// the policy boundary; a compliance caller whose JWT firm does not
/// match the requested <c>?firmId=</c> is rejected with 403
/// (compliance is firm-scoped — only admin may scope cross-firm via
/// the override). A scope mismatch is also audited as a
/// <c>denied</c> outcome so investigators can spot probing.</para>
///
/// <para><b>Audit.</b> Every successful download emits a
/// <see cref="AuditEventTypes.ReportCvmDownload"/> envelope via
/// <see cref="IAuditLogger.LogOrFail"/> BEFORE the body is streamed —
/// matching the audit-first ordering contract used by admin
/// mutating endpoints (a WAL-backpressured audit append surfaces as
/// HTTP 503 and the body is never written, so an undocumented
/// download is not possible).</para>
/// </summary>
public static class CvmReportEndpoints
{
    public const string PolicyName = "ComplianceOrAdmin";

    public static IEndpointRouteBuilder MapCvmReports(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/reports/cvm").RequireAuthorization(PolicyName);

        group.MapGet("/35/{date}", (string date, HttpContext ctx,
                CvmReportSource source, CvmReportWriter writer, IAuditLogger audit, IEventStore store) =>
            HandleAsync(CvmReportType.Cvm35, date, ctx, source, writer, audit, store));

        group.MapGet("/505/{date}", (string date, HttpContext ctx,
                CvmReportSource source, CvmReportWriter writer, IAuditLogger audit, IEventStore store) =>
            HandleAsync(CvmReportType.Cvm505, date, ctx, source, writer, audit, store));

        return app;
    }

    private static async Task<IResult> HandleAsync(
        CvmReportType reportType,
        string dateString,
        HttpContext ctx,
        CvmReportSource source,
        CvmReportWriter writer,
        IAuditLogger audit,
        IEventStore store)
    {
        if (!DateOnly.TryParseExact(dateString, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return Results.BadRequest(new { error = "date must be yyyy-MM-dd" });

        var callerFirm = ctx.User.FindFirstValue(JwtIssuer.FirmClaim) ?? "default";
        var actorUserId = ctx.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        var role = ctx.User.FindFirstValue(JwtIssuer.RoleClaim);
        var isAdmin = string.Equals(role, Roles.Admin, StringComparison.OrdinalIgnoreCase);
        var isCompliance = string.Equals(role, Roles.Compliance, StringComparison.OrdinalIgnoreCase);

        string targetFirm;
        if (ctx.Request.Query.TryGetValue("firmId", out var qf) && !string.IsNullOrWhiteSpace(qf))
        {
            var requested = qf.ToString();
            // Admin: any firm. Compliance: must match own firm
            // (compliance is firm-scoped read-only oversight; only
            // admin crosses firm boundaries via the override).
            if (!isAdmin && !string.Equals(requested, callerFirm, StringComparison.Ordinal))
            {
                // Pass-2 review (#325) P2. WAL-backpressured audit must
                // surface as a structured 503 (matches AdminEndpoints),
                // not an unhandled 500.
                try
                {
                    EmitAudit(audit, ctx, reportType, requested, date, rowCount: 0,
                        outcome: AuditOutcomes.Denied, reasonCode: "cross_firm_denied", actorUserId);
                }
                catch (WalBackpressureException ex)
                {
                    MetricsRegistry.WalBackpressure.Add(1,
                        new KeyValuePair<string, object?>("call_site", "reports.cvm.audit.cross_firm_denied"));
                    return Results.Json(
                        new { error = "system busy (WAL backpressure)", detail = ex.Message },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                return Results.Forbid();
            }
            targetFirm = requested;
        }
        else
        {
            targetFirm = callerFirm;
        }

        // Drain the writer so any in-flight ER that happened on the
        // requested day is durable + visible to ReadFromAsync. Cheap
        // (no-op once the WAL is idle); critical for "report run
        // minutes after EOD" correctness.
        await store.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);

        // Buffer to a memory stream first so we can: (a) detect the
        // empty case and return 404 without writing a partial body,
        // and (b) keep audit-first ordering (audit emit BEFORE we
        // start streaming any bytes back). For very large reports a
        // future iteration can switch to a true response-stream pipe
        // once we have a cheap pre-count of rows on the source.
        var stopwatch = Stopwatch.StartNew();
        await using var buffer = new MemoryStream();
        int rowCount;
        var xmlSettings = new XmlWriterSettings
        {
            Async = true,
            Indent = false,
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = false,
        };
        await using (var xw = XmlWriter.Create(buffer, xmlSettings))
        {
            rowCount = await writer.WriteAsync(
                xw,
                reportType,
                targetFirm,
                date,
                source.EnumerateAsync(targetFirm, date, ctx.RequestAborted),
                generatedAtUtc: DateTimeOffset.UtcNow,
                ct: ctx.RequestAborted).ConfigureAwait(false);
        }
        stopwatch.Stop();

        if (rowCount == 0)
        {
            // No fills for the requested date — emit a denied-style
            // audit so an attacker probing dates can be spotted, and
            // 404 (consistent with the rest of the firm-scoped read
            // surface). Pass-1 review (#325) P2: wrap audit emit so a
            // WAL-backpressured audit surfaces as HTTP 503 instead of
            // an unhandled 500 (mirrors AdminEndpoints pattern).
            try
            {
                EmitAudit(audit, ctx, reportType, targetFirm, date, rowCount: 0,
                    outcome: AuditOutcomes.Denied, reasonCode: "no_rows", actorUserId);
            }
            catch (WalBackpressureException ex)
            {
                MetricsRegistry.WalBackpressure.Add(1,
                    new KeyValuePair<string, object?>("call_site", "reports.cvm.audit.no_rows"));
                return Results.Json(
                    new { error = "system busy (WAL backpressure)", detail = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            return Results.NotFound();
        }

        // Audit-first ordering: emit BEFORE streaming the body so a
        // WAL-backpressured audit (HTTP 503) cannot leave a
        // downloaded report unaudited. Pass-1 review (#325) P2:
        // translate WalBackpressureException → 503 so the contract
        // holds across audit and download paths.
        try
        {
            EmitAudit(audit, ctx, reportType, targetFirm, date, rowCount,
                outcome: AuditOutcomes.Success, reasonCode: null, actorUserId);
        }
        catch (WalBackpressureException ex)
        {
            MetricsRegistry.WalBackpressure.Add(1,
                new KeyValuePair<string, object?>("call_site", "reports.cvm.audit.success"));
            return Results.Json(
                new { error = "system busy (WAL backpressure)", detail = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        MetricsRegistry.CvmReportsGenerated.Add(1,
            new KeyValuePair<string, object?>("type", reportType.WireCode()),
            new KeyValuePair<string, object?>("firm_id", targetFirm));
        MetricsRegistry.CvmReportGenerationSeconds.Record(stopwatch.Elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("type", reportType.WireCode()));

        var bytes = buffer.ToArray();
        var fileName = $"cvm-{reportType.WireCode()}-{targetFirm}-{date:yyyyMMdd}.xml";
        return Results.File(bytes, contentType: "application/xml", fileDownloadName: fileName);
    }

    private static void EmitAudit(
        IAuditLogger audit,
        HttpContext ctx,
        CvmReportType reportType,
        string firmId,
        DateOnly date,
        int rowCount,
        string outcome,
        string? reasonCode,
        string? actorUserId)
    {
        var details = new Dictionary<string, string>
        {
            ["firmId"] = firmId,
            ["reportType"] = reportType.WireCode(),
            ["date"] = date.ToString("yyyy-MM-dd"),
            ["rowCount"] = rowCount.ToString(CultureInfo.InvariantCulture),
        };
        // Q4.14 (#314) pass-3: do NOT duplicate actor id into Details.
        // The top-level ActorUserId/ActorUsername fields are the
        // canonical actor identity and are redacted by
        // AdminAuditEndpoints.ProjectForCompliance when surfacing a
        // cross-firm action to the target firm. A second copy under
        // Details["actorUserId"] would bypass that redaction because
        // ProjectForCompliance only strips foreign-firm values from
        // FirmDetailKeys, not actor identity keys.

        audit.LogOrFail(new AuditLogEvent
        {
            EventType = AuditEventTypes.ReportCvmDownload,
            Outcome = outcome,
            ActorUserId = actorUserId,
            ActorUsername = actorUserId,
            ActorFirm = ctx.User.FindFirstValue(JwtIssuer.FirmClaim),
            ActorRole = ctx.User.FindFirstValue(JwtIssuer.RoleClaim),
            SourceIp = ctx.Connection.RemoteIpAddress?.ToString(),
            ResourcePath = ctx.Request.Path.Value,
            ReasonCode = reasonCode,
            Details = details,
        });
    }
}
