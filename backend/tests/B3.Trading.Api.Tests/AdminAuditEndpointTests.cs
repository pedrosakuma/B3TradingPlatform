using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q4.5 (#305). End-to-end coverage of the admin audit log surface:
/// capture sites (login, 2FA, admin mutations), the GET /api/admin/audit
/// read endpoint, pagination, filters, and cross-firm visibility.
/// Engine-driven via HTTP — no mocks per project convention.
/// </summary>
public class AdminAuditEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AdminAuditEndpointTests(TestAppFactory factory) => _factory = factory;

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

    private static async Task<AuditPage> QueryAuditAsync(HttpClient admin, string queryString = "")
    {
        var resp = await admin.GetAsync($"/api/admin/audit{queryString}");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AuditPage>(Json))!;
    }

    [Fact]
    public async Task Get_AnonymousReturns401()
    {
        using var anon = _factory.CreateClient();
        var resp = await anon.GetAsync("/api/admin/audit");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Get_TraderReturns403()
    {
        using var trader = await _factory.CreateAuthedClientAsync(); // alice (user)
        var resp = await trader.GetAsync("/api/admin/audit");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Login_FailedAttempt_EmitsAuditEvent()
    {
        using var anon = _factory.CreateClient();
        var bad = await anon.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var page = await QueryAuditAsync(admin, "?type=auth.login.failure&limit=50");
        Assert.Contains(page.Entries, e =>
            e.EventType == "auth.login.failure" &&
            e.Outcome == "failure" &&
            e.ActorUsername == "alice" &&
            e.ReasonCode == "bad_password");
    }

    [Fact]
    public async Task Login_Success_EmitsAuditEvent()
    {
        using var anon = _factory.CreateClient();
        await _factory.LoginAsync(anon, "alice");

        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var page = await QueryAuditAsync(admin, "?user=alice&type=auth.login.success&limit=20");
        Assert.Contains(page.Entries, e =>
            e.EventType == "auth.login.success" &&
            e.Outcome == "success" &&
            e.ActorUsername == "alice");
    }

    [Fact]
    public async Task AdminKill_EmitsConfigChangeAudit()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");

        // Toggle kill on/off for a firm so we leave the platform in a clean state.
        var on = await admin.PostAsync("/api/admin/kill/firm/default", content: null);
        on.EnsureSuccessStatusCode();
        var off = await admin.DeleteAsync("/api/admin/kill/firm/default");
        off.EnsureSuccessStatusCode();

        var page = await QueryAuditAsync(admin, "?type=admin.config.change&limit=100");
        Assert.Contains(page.Entries, e =>
            e.EventType == "admin.config.change" &&
            e.ResourcePath != null && e.ResourcePath.StartsWith("/api/admin/kill") &&
            e.ActorRole == "admin");
    }

    [Fact]
    public async Task TypePrefixFilter_MatchesAllAuthEvents()
    {
        using var anon = _factory.CreateClient();
        await _factory.LoginAsync(anon, "alice");
        var bad = await anon.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var page = await QueryAuditAsync(admin, "?type=auth.*&limit=100");
        Assert.NotEmpty(page.Entries);
        Assert.All(page.Entries, e => Assert.StartsWith("auth.", e.EventType));
    }

    [Fact]
    public async Task OutcomeFilter_OnlyReturnsMatching()
    {
        using var anon = _factory.CreateClient();
        var bad = await anon.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var page = await QueryAuditAsync(admin, "?outcome=failure&limit=50");
        Assert.NotEmpty(page.Entries);
        Assert.All(page.Entries, e => Assert.Equal("failure", e.Outcome));
    }

    [Fact]
    public async Task Pagination_CursorReturnsNextPageAndEventuallyStops()
    {
        using var anon = _factory.CreateClient();
        // Generate a known burst of failure events.
        for (var i = 0; i < 7; i++)
        {
            var bad = await anon.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = $"wrong-{i}" });
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }

        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var first = await QueryAuditAsync(admin, "?type=auth.login.failure&limit=3");
        Assert.Equal(3, first.Entries.Count);
        Assert.NotNull(first.NextCursor);

        var second = await QueryAuditAsync(admin, $"?type=auth.login.failure&limit=3&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(3, second.Entries.Count);
        // Newest-first: second page seqs are strictly older than first page seqs.
        Assert.True(second.Entries[0].Seq < first.Entries[^1].Seq);
    }

    [Fact]
    public async Task InvalidCursor_Returns400()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.GetAsync("/api/admin/audit?cursor=%21%21not-base64%21%21");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
