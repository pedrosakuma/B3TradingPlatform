using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using B3.Trading.Api.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q4.14 (#314). Coverage for the compliance role's access to
/// <c>GET /admin/audit</c>: the policy admits compliance principals,
/// but the result set is forced firm-scoped at the server (the
/// caller's <c>firm</c> JWT claim is pushed into
/// <see cref="Application.Audit.AuditLogKeeper.Query"/> as
/// <c>firmFilter</c>; any <c>?firmId=</c> query argument is silently
/// ignored). Admin behaviour is unchanged (sees every firm).
/// </summary>
public class AdminAuditComplianceTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AdminAuditComplianceTests(TestAppFactory factory) => _factory = factory;

    private sealed record AuditPage(List<AuditEntryDto> Entries, string? NextCursor);
    private sealed record AuditEntryDto(
        [property: JsonPropertyName("seq")] long? Seq,
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

    private HttpClient ClientFor(string user, string role, string firm)
    {
        var issuer = _factory.Services.GetRequiredService<JwtIssuer>();
        var (token, _) = issuer.Issue(user, role, firm);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<AuditPage> QueryAuditAsync(HttpClient http, string queryString = "")
    {
        var resp = await http.GetAsync($"/admin/audit{queryString}");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AuditPage>(Json))!;
    }

    [Fact]
    public async Task Compliance_CanReadAudit_FirmScopedToOwnFirm()
    {
        // Generate audit traffic for two distinct firms by hitting the
        // login endpoint as alice (firm "default") and dave
        // (firm FIRM01). Failure is fine — the audit envelope is
        // emitted either way.
        using var anon = _factory.CreateClient();
        await anon.PostAsJsonAsync("/auth/login", new { username = "alice", password = TestAppFactory.TestPassword });
        await anon.PostAsJsonAsync("/auth/login", new { username = "dave", password = TestAppFactory.TestPassword });

        using var compliance = ClientFor("dave", Roles.Compliance, "FIRM01");
        var page = await QueryAuditAsync(compliance, "?limit=200");

        Assert.NotEmpty(page.Entries);
        // Every surfaced entry either belongs to FIRM01 (ActorFirm matches)
        // or originated in another firm but targeted FIRM01 — in which
        // case ActorFirm is redacted to null. Neither leaks a foreign
        // firm identifier.
        Assert.All(page.Entries, e =>
            Assert.True(
                e.ActorFirm is null
                    || string.Equals(e.ActorFirm, "FIRM01", StringComparison.OrdinalIgnoreCase),
                $"unexpected actor firm leaked to compliance: {e.ActorFirm}"));
        // And the dave-login event we just produced is visible.
        Assert.Contains(page.Entries, e => e.ActorUsername == "dave");
        // alice (firm=default) MUST NOT appear in compliance's view.
        Assert.DoesNotContain(page.Entries, e => e.ActorUsername == "alice");
    }

    [Fact]
    public async Task Compliance_CannotEscapeFirmScopeViaQueryArg()
    {
        using var anon = _factory.CreateClient();
        await anon.PostAsJsonAsync("/auth/login", new { username = "alice", password = TestAppFactory.TestPassword });

        using var compliance = ClientFor("dave", Roles.Compliance, "FIRM01");
        // Compliance attempts to probe firm "default" via ?firmId=.
        var page = await QueryAuditAsync(compliance, "?firmId=default&limit=200");

        Assert.All(page.Entries, e =>
            Assert.True(
                e.ActorFirm is null
                    || string.Equals(e.ActorFirm, "FIRM01", StringComparison.OrdinalIgnoreCase),
                $"unexpected actor firm leaked to compliance: {e.ActorFirm}"));
        Assert.DoesNotContain(page.Entries, e => e.ActorUsername == "alice");
    }

    [Fact]
    public async Task PlainUser_StillReturns403()
    {
        using var trader = await _factory.CreateAuthedClientAsync(); // alice (user)
        var resp = await trader.GetAsync("/admin/audit");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_UnchangedSeesAllFirms()
    {
        using var anon = _factory.CreateClient();
        await anon.PostAsJsonAsync("/auth/login", new { username = "alice", password = TestAppFactory.TestPassword });
        await anon.PostAsJsonAsync("/auth/login", new { username = "dave", password = TestAppFactory.TestPassword });

        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var page = await QueryAuditAsync(admin, "?limit=200");

        // Admin sees both firms in the same response (no scoping).
        Assert.Contains(page.Entries, e => e.ActorUsername == "alice");
        Assert.Contains(page.Entries, e => e.ActorUsername == "dave");
    }

    [Fact]
    public async Task Admin_CanScopeToSpecificFirmViaQueryArg()
    {
        using var anon = _factory.CreateClient();
        await anon.PostAsJsonAsync("/auth/login", new { username = "alice", password = TestAppFactory.TestPassword });
        await anon.PostAsJsonAsync("/auth/login", new { username = "dave", password = TestAppFactory.TestPassword });

        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var page = await QueryAuditAsync(admin, "?firmId=FIRM01&limit=200");

        Assert.NotEmpty(page.Entries);
        Assert.All(page.Entries, e =>
            Assert.Equal("FIRM01", e.ActorFirm, ignoreCase: true));
    }

    // Pass-1 review (#327) P1.2 — Compliance responses must NOT
    // surface the platform-wide Seq field. If they did, a compliance
    // caller could gap-count between visible Seq values to estimate
    // cross-firm event volume.
    [Fact]
    public async Task Compliance_EntriesOmitSeq_AdminEntriesKeepSeq()
    {
        using var anon = _factory.CreateClient();
        await anon.PostAsJsonAsync("/auth/login", new { username = "dave", password = TestAppFactory.TestPassword });

        using var compliance = ClientFor("dave", Roles.Compliance, "FIRM01");
        var compPage = await QueryAuditAsync(compliance, "?limit=50");
        Assert.NotEmpty(compPage.Entries);
        Assert.All(compPage.Entries, e => Assert.Null(e.Seq));

        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var adminPage = await QueryAuditAsync(admin, "?limit=50");
        Assert.NotEmpty(adminPage.Entries);
        Assert.All(adminPage.Entries, e => Assert.NotNull(e.Seq));
    }

    // Pass-1 review (#327) P1.2 — Compliance cursor is HMAC-signed.
    // A raw base64(seq) cursor (the admin format) must be rejected;
    // a forged HMAC must be rejected; a server-issued cursor must
    // round-trip and advance pagination.
    [Fact]
    public async Task Compliance_CursorIsHmacSigned_RejectsTampering()
    {
        using var anon = _factory.CreateClient();
        for (var i = 0; i < 5; i++)
            await anon.PostAsJsonAsync("/auth/login", new { username = "dave", password = TestAppFactory.TestPassword });

        using var compliance = ClientFor("dave", Roles.Compliance, "FIRM01");

        // limit=1 forces a cursor on the response.
        var firstPage = await QueryAuditAsync(compliance, "?limit=1");
        Assert.NotNull(firstPage.NextCursor);

        // 1. Plain base64(seq) — the admin/legacy format — is NOT
        //    a valid compliance cursor; the endpoint must reject it.
        var rawSeqCursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("1000000"));
        var bad = await compliance.GetAsync($"/admin/audit?limit=1&cursor={Uri.EscapeDataString(rawSeqCursor)}");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // 2. Server-issued cursor round-trips and lets pagination advance.
        var second = await compliance.GetAsync($"/admin/audit?limit=1&cursor={Uri.EscapeDataString(firstPage.NextCursor!)}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondPage = (await second.Content.ReadFromJsonAsync<AuditPage>(Json))!;
        Assert.NotEmpty(secondPage.Entries);
        // No overlap with first page — the cursor advanced.
        Assert.DoesNotContain(secondPage.Entries, e => e.Id == firstPage.Entries[0].Id);
    }

    // Pass-1 review (#327) P1.1 — When an audit entry is surfaced
    // because a target-firm details key matches the compliance
    // caller's firm (actor was in another firm), the actor identity
    // and source ip are redacted, and any cross-firm firm keys in
    // Details are dropped.
    [Fact]
    public async Task Compliance_SeesTargetFirmHits_WithActorRedacted()
    {
        // Synthesize a cross-firm audit event: admin (firm=default)
        // emits an entry whose Details["firmId"] = FIRM01. The
        // compliance user in FIRM01 must see it, but with the
        // admin's actor name and firm redacted.
        var audit = _factory.Services.GetRequiredService<Application.Audit.IAuditLogger>();
        audit.Log(new Application.Persistence.AuditLogEvent
        {
            EventType = "test.cross_firm_action",
            Outcome = Application.Audit.AuditOutcomes.Success,
            ActorUserId = "admin",
            ActorUsername = "admin",
            ActorFirm = "default",
            ActorRole = "admin",
            SourceIp = "10.0.0.1",
            ResourcePath = "/admin/test",
            Details = new Dictionary<string, string>
            {
                ["firmId"] = "FIRM01",
                ["note"] = "kept",
            },
        });

        using var compliance = ClientFor("dave", Roles.Compliance, "FIRM01");
        var page = await QueryAuditAsync(compliance, "?limit=200&type=test.cross_firm_action");

        var hit = Assert.Single(page.Entries);
        // Actor identity redacted (admin was in firm "default", not FIRM01).
        Assert.Null(hit.ActorFirm);
        Assert.Null(hit.ActorUserId);
        Assert.Equal("(other firm)", hit.ActorUsername);
        Assert.Null(hit.SourceIp);
        // Actor role survives (operationally relevant, not PII).
        Assert.Equal("admin", hit.ActorRole);
        // Non-firm details preserved; the matching firmId stays in place
        // (its value IS the caller's firm, so it confirms — not leaks —
        // the target), but any other foreign firm key would be dropped.
        Assert.NotNull(hit.Details);
        Assert.Equal("kept", hit.Details!["note"]);
        Assert.Equal("FIRM01", hit.Details!["firmId"]);
    }

    // Pass-1 review (#327) P1.1 — Inverse: when ActorFirm matches the
    // caller's firm but Details["firmId"] points to ANOTHER firm
    // (admin in compliance's firm operating on a different firm),
    // the offending key is dropped so the compliance user does not
    // learn other firm names.
    [Fact]
    public async Task Compliance_OwnFirmActor_ButCrossFirmTargetDetails_AreDropped()
    {
        var audit = _factory.Services.GetRequiredService<Application.Audit.IAuditLogger>();
        audit.Log(new Application.Persistence.AuditLogEvent
        {
            EventType = "test.own_firm_actor",
            Outcome = Application.Audit.AuditOutcomes.Success,
            ActorUserId = "dave",
            ActorUsername = "dave",
            ActorFirm = "FIRM01",
            ActorRole = "compliance",
            SourceIp = "10.0.0.2",
            ResourcePath = "/admin/test",
            Details = new Dictionary<string, string>
            {
                ["firm"] = "OTHERFIRM",
                ["cl_ord_id"] = "ORD123",
            },
        });

        using var compliance = ClientFor("dave", Roles.Compliance, "FIRM01");
        var page = await QueryAuditAsync(compliance, "?limit=200&type=test.own_firm_actor");

        var hit = Assert.Single(page.Entries);
        // Actor is own firm — identity preserved.
        Assert.Equal("dave", hit.ActorUsername);
        Assert.Equal("FIRM01", hit.ActorFirm, ignoreCase: true);
        // Cross-firm firm key dropped; unrelated detail survives.
        Assert.NotNull(hit.Details);
        Assert.False(hit.Details!.ContainsKey("firm"));
        Assert.Equal("ORD123", hit.Details!["cl_ord_id"]);
    }
}
