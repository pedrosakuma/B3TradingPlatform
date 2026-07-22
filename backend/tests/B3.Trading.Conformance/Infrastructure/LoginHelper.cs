using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace B3.Trading.Conformance.Infrastructure;

/// <summary>
/// Small helper: <c>POST /api/auth/login</c> against the configured platform
/// and return an authorization header ready to apply to any request. Lets
/// individual conformance specs stay focused on the contract they're
/// asserting instead of re-implementing the login dance.
/// </summary>
internal static class LoginHelper
{
    private static readonly ConcurrentDictionary<string, CachedLogin> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan CacheSafetyWindow = TimeSpan.FromMinutes(1);

    public static async Task<AuthenticationHeaderValue> LoginAsync(
        HttpClient http, string username, string password)
    {
        var cacheKey = $"{http.BaseAddress}|{username}";
        if (Cache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresAt - CacheSafetyWindow > DateTimeOffset.UtcNow)
        {
            return new AuthenticationHeaderValue("Bearer", cached.Token);
        }

        var resp = await http.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.True(resp.IsSuccessStatusCode,
            $"login failed for '{username}': {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        var payload = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload!.Token));
        Cache[cacheKey] = new CachedLogin(payload.Token, payload.ExpiresAt);
        return new AuthenticationHeaderValue("Bearer", payload.Token);
    }

    public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);
    private sealed record CachedLogin(string Token, DateTimeOffset ExpiresAt);
}
