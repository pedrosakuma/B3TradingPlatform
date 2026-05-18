using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
        // EVERY surfaced entry must be from FIRM01 — no cross-firm leak.
        Assert.All(page.Entries, e =>
            Assert.Equal("FIRM01", e.ActorFirm, ignoreCase: true));
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
            Assert.Equal("FIRM01", e.ActorFirm, ignoreCase: true));
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
}
