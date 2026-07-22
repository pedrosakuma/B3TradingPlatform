using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_HTTP_Simulator;

/// <summary>
/// Spec — Simulator. Smoke scenario for the slice-4 simulator gateway:
/// (1) submit a real order via <c>POST /api/orders</c>,
/// (2) inject a synthetic Fill via <c>POST /api/admin/simulator/er</c>,
/// (3) verify <c>GET /api/orders/</c> reflects the fill.
/// Skipped unless the operator declares the host opted into ER
/// injection via <c>B3T_ER_INJECTION=true</c> (legacy
/// <c>B3T_SIMULATOR_MODE=true</c> honored as fallback after #163).
/// </summary>
[Trait("Category", "Conformance")]
public class SimulatorErInjectionSpecTests
{
    [ConformanceFact(RequiresAdmin = true, RequiresErInjection = true)]
    public async Task SimulatorEr_FullFill_DrivesOrderToFilled()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };

        var userAuth = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);
        var adminAuth = await LoginHelper.LoginAsync(http, peer.AdminUsername!, peer.AdminPassword!);

        using var submit = new HttpRequestMessage(HttpMethod.Post, "/api/orders/")
        {
            Content = JsonContent.Create(new
            {
                Symbol = "PETR4",
                SecurityId = 4321UL,
                Side = "Buy",
                Type = "Limit",
                Quantity = 100,
                Price = 30m,
            }),
        };
        submit.Headers.Authorization = userAuth;
        var submitResp = await http.SendAsync(submit);
        Assert.Equal(HttpStatusCode.Accepted, submitResp.StatusCode);
        var ack = await submitResp.Content.ReadFromJsonAsync<JsonElement>();
        var clOrdId = ulong.Parse(ack.GetProperty("clOrdId").GetString()!);

        using var inject = new HttpRequestMessage(HttpMethod.Post, "/api/admin/simulator/er")
        {
            Content = JsonContent.Create(new
            {
                ClOrdId = clOrdId,
                Type = "Fill",
                LastQty = 100L,
                LastPx = 30m,
            }),
        };
        inject.Headers.Authorization = adminAuth;
        var injectResp = await http.SendAsync(inject);
        Assert.Equal(HttpStatusCode.Accepted, injectResp.StatusCode);

        using var list = new HttpRequestMessage(HttpMethod.Get, "/api/orders/");
        list.Headers.Authorization = userAuth;
        var listResp = await http.SendAsync(list);
        listResp.EnsureSuccessStatusCode();
        var orders = await listResp.Content.ReadFromJsonAsync<JsonElement[]>();
        var order = orders!.Single(o => o.GetProperty("clOrdId").GetString() == clOrdId.ToString());
        Assert.Equal("Filled", order.GetProperty("status").GetString());
        Assert.Equal(100, order.GetProperty("cumulativeQuantity").GetInt64());
    }
}
