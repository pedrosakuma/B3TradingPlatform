using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.Schema;
using B3.Trading.Api.Auth;
using B3.Trading.Application.Reports.Cvm;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q4.8 (#308). End-to-end coverage of the CVM 35/505 transaction
/// reporting export. The FileEventStore is enabled so the report
/// pipeline scans the same WAL the host writes to under load, and
/// the simulator ER injector is used to land deterministic fills
/// for the test day.
/// </summary>
public class CvmReportEndpointTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "b3-cvm-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IDictionary<string, string?> Overrides() => new Dictionary<string, string?>
    {
        ["Trading:Exchange:Mode"] = "Mock",
        ["Trading:Exchange:AllowErInjection"] = "true",
        ["Trading:Persistence:Enabled"] = "true",
        ["Trading:Persistence:DataDirectory"] = _dataDir,
        ["Trading:Persistence:FirmId"] = "test",
        ["Trading:Persistence:SnapshotInterval"] = "00:10:00",
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static async Task<string> SubmitOrder(HttpClient http, string token, decimal price = 30m, int qty = 10, string symbol = "PETR4")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new
            {
                Symbol = symbol,
                SecurityId = 4321UL,
                Side = "Buy",
                Type = "Limit",
                Quantity = qty,
                Price = price,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("clOrdId").GetString()!;
    }

    private static async Task InjectEr(HttpClient http, string token, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/simulator/er")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    private static async Task SeedOneFillAsync(HttpClient userHttp, string userToken, HttpClient adminHttp, string adminToken)
    {
        var clOrdIdStr = await SubmitOrder(userHttp, userToken);
        var clOrdId = ulong.Parse(clOrdIdStr);
        await InjectEr(adminHttp, adminToken, new { ClOrdId = clOrdId, Type = "New" });
        await InjectEr(adminHttp, adminToken, new
        {
            ClOrdId = clOrdId,
            Type = "Fill",
            LastQty = 10L,
            LastPx = 30.0m,
        });

        // /admin/simulator/er returns 202 once the ER is enqueued on the
        // mock gateway channel; the ExecutionReportProcessor drains the
        // channel on a background task. Without an explicit wait the
        // CVM report query can race past the in-flight Fill, see zero
        // rows, and return 404. Poll /executions/history (user scope)
        // until the fill is observable. Bounded at ~3s wall clock;
        // empirically completes in <20ms locally.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/executions/history?limit=10");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
            using var resp = await userHttp.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                    return;
            }
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException("SeedOneFillAsync timed out waiting for fill to be visible in /executions/history.");
    }

    private static DateOnly TodayUtc()
    {
        // CvmReportSource buckets fills by São Paulo business day
        // (UTC-3). When the test runs late UTC evening (e.g. ~00:00
        // UTC == ~21:00 SP), a fill landing "today" UTC is
        // bucketed to "yesterday" SP. Compute the date the source
        // will use so the query and the fill agree.
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
        catch { tz = TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "BRT", "BRT"); }
        var nowSp = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        return DateOnly.FromDateTime(nowSp.DateTime);
    }

    [Fact]
    public async Task UserRole_IsForbidden_ByPolicy()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = await f.CreateAuthedClientAsync(); // alice (user)
        var resp = await http.GetAsync($"/reports/cvm/35/{TodayUtc():yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var http = f.CreateClient();
        var resp = await http.GetAsync($"/reports/cvm/35/{TodayUtc():yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_NoFills_Returns404()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var admin = await f.CreateAuthedClientAsync("admin");
        var resp = await admin.GetAsync($"/reports/cvm/35/{TodayUtc():yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_BadDate_Returns400()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var admin = await f.CreateAuthedClientAsync("admin");
        var resp = await admin.GetAsync("/reports/cvm/35/not-a-date");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_WithFills_ReturnsXml_ValidAgainstXsd_AndAttachmentFilename()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var user = await f.CreateAuthedClientAsync(); // alice (firm=default)
        using var admin = await f.CreateAuthedClientAsync("admin"); // firm=default
        await SeedOneFillAsync(user, await f.LoginAsync(user, "alice"), admin, await f.LoginAsync(admin, "admin"));

        var today = TodayUtc();
        var resp = await admin.GetAsync($"/reports/cvm/35/{today:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/xml", resp.Content.Headers.ContentType?.MediaType);
        var disp = resp.Content.Headers.ContentDisposition;
        Assert.NotNull(disp);
        Assert.Equal("attachment", disp!.DispositionType);
        Assert.Equal($"cvm-35-default-{today:yyyyMMdd}.xml", disp.FileName);

        var xml = await resp.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);

        XNamespace ns = CvmReportWriter.Namespace;
        Assert.Equal("CvmReport", doc.Root!.Name.LocalName);
        Assert.Equal("35", doc.Root.Attribute("reportType")!.Value);
        Assert.Equal("default", doc.Root.Attribute("firmId")!.Value);
        var tx = doc.Root.Element(ns + "Transactions")!.Elements(ns + "Transaction").ToList();
        Assert.NotEmpty(tx);
        Assert.All(tx, t => Assert.Equal("B3-CCP", t.Element(ns + "Counterparty")!.Value));

        // Validate against the embedded XSD.
        var schemas = new XmlSchemaSet();
        schemas.Add(CvmReportWriter.LoadSchema());
        var errors = new List<string>();
        doc.Validate(schemas, (_, ev) => errors.Add(ev.Message));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Cvm505_SameFills_DifferentReportType_AndFilename()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var user = await f.CreateAuthedClientAsync();
        using var admin = await f.CreateAuthedClientAsync("admin");
        await SeedOneFillAsync(user, await f.LoginAsync(user, "alice"), admin, await f.LoginAsync(admin, "admin"));

        var today = TodayUtc();
        var resp = await admin.GetAsync($"/reports/cvm/505/{today:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal($"cvm-505-default-{today:yyyyMMdd}.xml",
            resp.Content.Headers.ContentDisposition!.FileName);

        var xml = await resp.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);
        Assert.Equal("505", doc.Root!.Attribute("reportType")!.Value);

        XNamespace ns = CvmReportWriter.Namespace;
        var tx = doc.Root.Element(ns + "Transactions")!.Elements(ns + "Transaction").ToList();
        Assert.NotEmpty(tx);
        // 505 placeholder Fund column present (empty) on every row.
        Assert.All(tx, t => Assert.NotNull(t.Element(ns + "Fund")));
    }

    [Fact]
    public async Task Compliance_CrossFirmOverride_IsForbidden()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        // dave is compliance @ FIRM01; attempting to scope to "default"
        // (admin's firm) must be denied.
        using var dave = await f.CreateAuthedClientAsync("dave");
        var resp = await dave.GetAsync($"/reports/cvm/35/{TodayUtc():yyyy-MM-dd}?firmId=default");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Compliance_NoOverride_BoundToOwnFirm()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var dave = await f.CreateAuthedClientAsync("dave"); // compliance @ FIRM01
        // No fills exist for FIRM01 in this fixture, so we expect 404
        // (rather than a 200 leaking other firms' data).
        var resp = await dave.GetAsync($"/reports/cvm/35/{TodayUtc():yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_FirmIdOverride_Works()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var user = await f.CreateAuthedClientAsync();
        using var admin = await f.CreateAuthedClientAsync("admin");
        await SeedOneFillAsync(user, await f.LoginAsync(user, "alice"), admin, await f.LoginAsync(admin, "admin"));

        var today = TodayUtc();
        var resp = await admin.GetAsync($"/reports/cvm/35/{today:yyyy-MM-dd}?firmId=default");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal($"cvm-35-default-{today:yyyyMMdd}.xml",
            resp.Content.Headers.ContentDisposition!.FileName);
    }

    [Fact]
    public async Task Download_EmitsAuditEvent_WithKind_ReportCvmDownload()
    {
        using var f = TestAppFactory.WithOverrides(Overrides());
        using var user = await f.CreateAuthedClientAsync();
        using var admin = await f.CreateAuthedClientAsync("admin");
        await SeedOneFillAsync(user, await f.LoginAsync(user, "alice"), admin, await f.LoginAsync(admin, "admin"));

        var today = TodayUtc();
        var resp = await admin.GetAsync($"/reports/cvm/35/{today:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var auditResp = await admin.GetAsync("/admin/audit?type=report.cvm.download&limit=50");
        auditResp.EnsureSuccessStatusCode();
        var page = await auditResp.Content.ReadFromJsonAsync<AuditPage>(Json);
        Assert.NotNull(page);
        Assert.Contains(page!.Entries, e =>
            e.EventType == "report.cvm.download" &&
            e.Outcome == "success" &&
            e.Details != null &&
            e.Details.TryGetValue("reportType", out var rt) && rt == "35" &&
            e.Details.TryGetValue("firmId", out var fid) && fid == "default");
    }

    private sealed record AuditPage(List<AuditEntryDto> Entries, string? NextCursor);
    private sealed record AuditEntryDto(
        long Seq,
        string Id,
        DateTimeOffset TimestampUtc,
        string EventType,
        string Outcome,
        string? ActorUserId,
        string? ActorUsername,
        string? ActorFirm,
        string? ActorRole,
        string? SourceIp,
        string? ResourcePath,
        string? ReasonCode,
        Dictionary<string, string>? Details);
}
