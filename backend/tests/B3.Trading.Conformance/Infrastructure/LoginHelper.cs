using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace B3.Trading.Conformance.Infrastructure;

/// <summary>
/// Small helper: <c>POST /auth/login</c> against the configured platform
/// and return an authorization header ready to apply to any request. Lets
/// individual conformance specs stay focused on the contract they're
/// asserting instead of re-implementing the login dance.
/// </summary>
internal static class LoginHelper
{
    public static async Task<AuthenticationHeaderValue> LoginAsync(
        HttpClient http, string username, string password)
    {
        var resp = await http.PostAsJsonAsync("/auth/login", new { username, password });
        Assert.True(resp.IsSuccessStatusCode,
            $"login failed for '{username}': {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        var payload = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload!.Token));
        return new AuthenticationHeaderValue("Bearer", payload.Token);
    }

    public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);
}
