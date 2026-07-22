using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_HTTP_Admin;

/// <summary>
/// Spec — Admin. <c>GET /api/admin/firms</c> is the operator-facing
/// inventory of configured exchange firms. Its shape is part of the
/// platform's public contract because dashboards and ops scripts depend
/// on it; this scenario locks in <c>{ mode, firms[] }</c> with the
/// per-firm field set documented for each entry.
/// </summary>
[Trait("Category", "Conformance")]
public class AdminFirmsSpecTests
{
    [ConformanceFact(RequiresAdmin = true)]
    public async Task AdminFirms_ReturnsModeAndFirmsArrayShape()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };
        var auth = await LoginHelper.LoginAsync(http, peer.AdminUsername!, peer.AdminPassword!);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/firms");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        // Parse as JsonElement to assert on the wire shape, not on a DTO
        // that only the suite knows about. The dashboard side will do
        // the same: pluck `.mode` and `.firms[].firmId`.
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.String, json.GetProperty("mode").ValueKind);
        var firms = json.GetProperty("firms");
        Assert.Equal(JsonValueKind.Array, firms.ValueKind);

        // Every firm entry — if any — must carry the documented keys.
        // The default compose stack runs Mode=Unavailable with no firms,
        // so the array can be empty; if a deployment configures firms
        // they must conform to the shape.
        foreach (var firm in firms.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, firm.GetProperty("firmId").ValueKind);
            Assert.True(firm.TryGetProperty("endpoint", out _),
                "firm entry missing 'endpoint'");
            Assert.True(firm.TryGetProperty("sessionId", out _),
                "firm entry missing 'sessionId'");
            // sessionState/sessionVerId/reconnecting are optional in semantics
            // (null when no live registry attached for Mock/Stub/Unavailable
            // modes) but the keys must be present so the schema is stable.
            Assert.True(firm.TryGetProperty("sessionState", out _));
            Assert.True(firm.TryGetProperty("sessionVerId", out _));
            Assert.True(firm.TryGetProperty("reconnecting", out _));
        }
    }
}
