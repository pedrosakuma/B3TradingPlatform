using System.Net;
using System.Net.Http.Headers;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_HTTP_Auth;

/// <summary>
/// Spec — Auth (expired tokens). An authentically-signed JWT whose
/// <c>exp</c> claim is in the past must be rejected by every protected
/// surface — HTTP and WebSocket alike. Asserting this requires the
/// suite to mint tokens with the same key the host validates against;
/// scenarios skip when <c>B3T_AUTH_SIGNING_KEY</c> isn't wired in.
/// </summary>
/// <remarks>
/// The failure mode this guards is subtle: garbage-bearer rejection
/// (covered by <see cref="AuthRejectionTests"/>) only proves that
/// signature validation runs. It does NOT prove that lifetime validation
/// is wired. A misconfiguration where <c>ValidateLifetime=false</c>
/// (e.g. someone debugging in production) would let a valid-signed
/// token live forever — these tests catch exactly that regression.
/// </remarks>
[Trait("Category", "Conformance")]
public class ExpiredTokenTests
{
    [ConformanceFact(RequiresAuthSigningKey = true)]
    public async Task ProtectedHttp_WithExpiredToken_Returns401()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };

        var expired = JwtMinter.MintExpired();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/orders");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", expired);
        var resp = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [ConformanceFact(RequiresAuthSigningKey = true)]
    public async Task WebSocketUpgrade_WithExpiredToken_Returns401()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };

        var expired = JwtMinter.MintExpired();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/ws");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", expired);
        // Standard WS upgrade hint set: even with a "valid-looking" upgrade
        // request, an expired token must short-circuit at the auth layer
        // before the handshake gets a chance to switch protocols.
        req.Headers.Add("Upgrade", "websocket");
        req.Headers.Add("Connection", "Upgrade");
        req.Headers.Add("Sec-WebSocket-Version", "13");
        req.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        var resp = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
