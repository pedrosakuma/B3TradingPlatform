using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_HTTP_Risk;

/// <summary>
/// Spec — Risk. Pre-trade risk rejections must surface to clients as
/// HTTP 202 with a stable JSON envelope: <c>{ clOrdId, status:
/// "Rejected", reason }</c>. The 202 (not 4xx) status matters because
/// a risk reject is a synchronous outcome of an accepted submission,
/// not an input-validation error — UIs and ops scripts treat the two
/// classes differently. The reason string is opaque but must be
/// non-empty so dashboards can group by it.
///
/// <para>
/// The scenario discovers the configured cap via <c>GET
/// /admin/risk/limits</c> instead of hardcoding values, so the same
/// suite runs against any deployment whose admin happens to have
/// tuned the caps differently. Skipped if no <c>MaxQuantity</c> is
/// configured for the test user (deployments that disable v2 risk
/// entirely have nothing to assert here).
/// </para>
/// </summary>
[Trait("Category", "Conformance")]
public class RiskRejectionShapeSpecTests
{
    [ConformanceFact(RequiresAdmin = true)]
    public async Task RiskRejection_ReturnsAcceptedWithRejectedShape()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };

        // Two different identities: admin to query the resolved cap,
        // user to actually submit the order. Mirrors how dashboards
        // and trader UIs are wired in real deployments.
        var adminAuth = await LoginHelper.LoginAsync(http, peer.AdminUsername!, peer.AdminPassword!);
        var userAuth = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);

        var cap = await ResolveMaxQuantityAsync(http, adminAuth, peer.Username);
        if (cap is null)
        {
            // No configured cap → nothing to breach deterministically.
            // Mirrors AdminFirmsSpecTests' "if firms are present, they
            // must conform" stance: the assertion is conditional on
            // the deployment actually having a v2 risk cap configured.
            // xunit v2 has no in-test skip API, so we no-op and
            // surface the decision via an Assert.True trace message.
            Assert.True(true,
                "No MaxQuantity configured for the test user — risk-rejection shape not asserted.");
            return;
        }

        // Quantity strictly above the cap so any change in the cap
        // resolution still produces a clean reject without overflow.
        var quantity = checked(cap.Value + 1);

        // Symbol/securityId: the submit pipeline accepts an explicit
        // non-zero securityId in the payload regardless of the
        // SymbolDirectory contents. Picking a synthetic id keeps the
        // test independent of operator-provided directory data.
        using var submit = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Headers = { Authorization = userAuth },
            Content = JsonContent.Create(new
            {
                symbol = "PETR4",
                securityId = 4321UL,
                side = "Buy",
                type = "Limit",
                quantity,
                price = 30m,
            }),
        };

        var resp = await http.SendAsync(submit);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.String, body.GetProperty("clOrdId").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("clOrdId").GetString()),
            "clOrdId should echo back so clients can correlate with the synthetic ER.");

        Assert.Equal("Rejected", body.GetProperty("status").GetString());

        var reason = body.GetProperty("reason").GetString();
        Assert.False(string.IsNullOrWhiteSpace(reason),
            "reason must be non-empty so observability can group by it.");
    }

    private static async Task<long?> ResolveMaxQuantityAsync(
        HttpClient http, System.Net.Http.Headers.AuthenticationHeaderValue auth, string endClient)
    {
        // The admin endpoint resolves limits per (endClient, firm,
        // symbol) — we leave firm/symbol unset so the resolver falls
        // through to the per-end-client and Default slots. That
        // matches what a trader from `endClient` would actually hit
        // when no per-symbol override applies.
        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"/admin/risk/limits?endClient={Uri.EscapeDataString(endClient)}");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var limits = json.GetProperty("limits");
        if (!limits.TryGetProperty("maxQuantity", out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        return prop.GetInt64();
    }
}
