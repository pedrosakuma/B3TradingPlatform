using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_HTTP_Algo;

/// <summary>
/// Spec — POST /algo validation contract (RFC algo-orders-v0 §4.8).
/// The §4.8 quantity-rounding rule is part of the public contract: a
/// TWAP whose <c>floor(totalQuantity / sliceCount)</c> rounds to zero
/// MUST be rejected at submit time (before the engine ever sees it),
/// and the error body MUST echo <c>impliedSliceQuantity</c>,
/// <c>totalQuantity</c>, and <c>sliceCount</c> so callers can fix
/// either input without guessing.
///
/// <para>
/// No <c>RequiresSimulator</c> gate — this is a pure validation path
/// that does not depend on the exchange mode.
/// </para>
/// </summary>
[Trait("Category", "Conformance")]
public class AlgoValidationSpecTests
{
    [ConformanceFact]
    public async Task PostAlgoTwap_ImpliedSliceZero_Returns400WithEcho()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };
        var auth = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);

        // 2 / 5 = 0 (floor) → MUST be rejected. The window is well-formed
        // (1-minute, in the future) so the §4.8 check is the only path
        // that can fire.
        var now = DateTimeOffset.UtcNow;
        using var req = new HttpRequestMessage(HttpMethod.Post, "/algo/")
        {
            Content = JsonContent.Create(new
            {
                Symbol = "PETR4",
                SecurityId = 4321UL,
                Side = "Buy",
                Type = "Twap",
                TotalQuantity = 2,
                Twap = new
                {
                    StartUtc = now,
                    EndUtc = now.AddMinutes(1),
                    SliceCount = 5,
                    ChildOrderType = "Limit",
                    ChildPrice = 30m,
                },
            }),
        };
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("impliedSliceQuantity").GetInt64());
        Assert.Equal(2, body.GetProperty("totalQuantity").GetInt64());
        Assert.Equal(5, body.GetProperty("sliceCount").GetInt32());
    }
}
