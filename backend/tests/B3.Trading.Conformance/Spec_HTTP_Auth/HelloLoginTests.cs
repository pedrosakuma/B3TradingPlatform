using System.Net.Http.Json;
using B3.Trading.Conformance.Infrastructure;

namespace B3.Trading.Conformance.Spec_HTTP_Auth;

/// <summary>
/// Spec — Auth. Bootstrap "Hello-Login": <c>POST /auth/login</c> with valid
/// credentials returns a JWT, and that JWT is accepted on a protected
/// endpoint. The smallest possible end-to-end check that the platform is
/// up, the JWT pipeline is wired, and the user store is loaded.
/// </summary>
[Trait("Category", "Conformance")]
public class HelloLoginTests
{
    [ConformanceFact]
    public async Task Login_ReturnsJwt_AcceptedByProtectedEndpoint()
    {
        var peer = PlatformEndpoint.TryResolve()!;
        using var http = new HttpClient { BaseAddress = peer.BaseUrl };

        var loginResp = await http.PostAsJsonAsync("/auth/login",
            new { username = peer.Username, password = peer.Password });

        Assert.True(loginResp.IsSuccessStatusCode,
            $"login failed: {(int)loginResp.StatusCode} {await loginResp.Content.ReadAsStringAsync()}");
        var payload = await loginResp.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload!.Token), "token must be non-empty");
        Assert.True(payload.ExpiresAt > DateTimeOffset.UtcNow, "token must expire in the future");

        // Protected endpoint must accept the freshly-issued token. /orders
        // is the smallest protected GET on the platform.
        using var protectedReq = new HttpRequestMessage(HttpMethod.Get, "/orders");
        protectedReq.Headers.Authorization = new("Bearer", payload.Token);
        var protectedResp = await http.SendAsync(protectedReq);

        Assert.True(protectedResp.IsSuccessStatusCode,
            $"protected GET /orders rejected the token: {(int)protectedResp.StatusCode}");
    }

    private sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);
}
