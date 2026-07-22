using System.Net;
using System.Net.Http.Headers;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_HTTP_Auth;

/// <summary>
/// Spec — Auth (negative paths). The platform's Authorization contract
/// must reject anonymous, malformed, and insufficient-scope requests
/// before they reach business logic. Each scenario asserts the surface
/// status code, not internal behaviour, so the tests survive refactors
/// of the underlying handlers.
/// </summary>
[Trait("Category", "Conformance")]
public class AuthRejectionTests
{
    [ConformanceFact]
    public async Task ProtectedHttp_WithoutToken_Returns401()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };

        var resp = await http.GetAsync("/api/orders");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [ConformanceFact]
    public async Task ProtectedHttp_WithGarbageBearer_Returns401()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/orders");
        // Looks superficially like a JWT (3 base64url segments) but the
        // signature won't validate against the platform's signing key.
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJoYWNrZXIifQ.notavalidsignature");
        var resp = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [ConformanceFact]
    public async Task WebSocketUpgrade_WithoutToken_IsRejected()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };

        // Plain HTTP GET with an Upgrade hint: ASP.NET Core's auth
        // middleware runs before the WS handshake, so an unauthenticated
        // request should short-circuit with 401 (not the 101 Switching
        // Protocols an authenticated peer would see).
        using var req = new HttpRequestMessage(HttpMethod.Get, "/ws");
        req.Headers.Add("Upgrade", "websocket");
        req.Headers.Add("Connection", "Upgrade");
        req.Headers.Add("Sec-WebSocket-Version", "13");
        req.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        var resp = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [ConformanceFact]
    public async Task AdminEndpoint_WithUserRole_Returns403()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };
        var auth = await LoginHelper.LoginAsync(http, peer.Username, peer.Password);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/firms");
        req.Headers.Authorization = auth;
        var resp = await http.SendAsync(req);

        // Authenticated but lacking the admin role: must surface as 403,
        // not 401 (which would indicate a missing/invalid token instead
        // of an authorization decision).
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
