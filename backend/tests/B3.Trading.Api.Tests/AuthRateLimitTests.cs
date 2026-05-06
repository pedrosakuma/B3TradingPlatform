using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Api.Auth;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Slice 2 of #97 — anti-abuse rate limit on /auth/signup and /auth/login.
/// Defaults disable the limiter inside <see cref="TestAppFactory"/>; each
/// test here opts into a specific policy via <c>WithOverrides</c> so the
/// rest of the suite is unaffected.
/// </summary>
public class AuthRateLimitTests
{
    private static string FreshUsername() => "u" + Guid.NewGuid().ToString("N")[..10];

    private static async Task<int?> RetryAfterFromBodyAsync(HttpResponseMessage resp)
    {
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.TryGetProperty("retryAfterSeconds", out var v) ? v.GetInt32() : null;
    }

    [Fact]
    public async Task Signup_PerIp_LimitTrips_Returns429_WithRetryAfter()
    {
        using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Auth:RateLimit:SignupPerIp:Enabled"] = "true",
            ["Trading:Auth:RateLimit:SignupPerIp:PermitLimit"] = "3",
            ["Trading:Auth:RateLimit:SignupPerIp:WindowSeconds"] = "60",
        });
        using var client = factory.CreateClient();

        // First 3 succeed (PermitLimit=3, fresh per-IP window, all from
        // localhost so they share the partition).
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync("/auth/signup",
                new SignupRequest(FreshUsername(), "wonderland-1"));
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        // 4th must be rejected before hitting the handler. We use a fresh
        // username to ensure the conflict path could not steal the
        // assertion if the limiter were silently disabled.
        var rejected = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest(FreshUsername(), "wonderland-1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"),
            "Retry-After header missing on 429 — required for slice 2 contract.");
        var retryAfter = await RetryAfterFromBodyAsync(rejected);
        Assert.NotNull(retryAfter);
        Assert.True(retryAfter > 0);
    }

    [Fact]
    public async Task Signup_Global_FuseTrips_Returns429()
    {
        // Per-IP at 100 (effectively disabled for this test) so we
        // exclusively exercise the global fuse at PermitLimit=2.
        using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Auth:RateLimit:SignupPerIp:Enabled"] = "true",
            ["Trading:Auth:RateLimit:SignupPerIp:PermitLimit"] = "100",
            ["Trading:Auth:RateLimit:SignupPerIp:WindowSeconds"] = "60",
            ["Trading:Auth:RateLimit:SignupGlobal:Enabled"] = "true",
            ["Trading:Auth:RateLimit:SignupGlobal:PermitLimit"] = "2",
            ["Trading:Auth:RateLimit:SignupGlobal:WindowSeconds"] = "60",
        });
        using var client = factory.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            var ok = await client.PostAsJsonAsync("/auth/signup",
                new SignupRequest(FreshUsername(), "wonderland-1"));
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }
        var rejected = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest(FreshUsername(), "wonderland-1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Login_PerIp_LimitTrips_Returns429()
    {
        using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Auth:RateLimit:LoginPerIp:Enabled"] = "true",
            ["Trading:Auth:RateLimit:LoginPerIp:PermitLimit"] = "3",
            ["Trading:Auth:RateLimit:LoginPerIp:WindowSeconds"] = "60",
        });
        using var client = factory.CreateClient();

        // 3 valid logins succeed (env-seeded alice). The 4th — even with
        // valid credentials — must be rejected at the limiter, proving
        // the gate runs BEFORE password verification (defense against
        // tying up PBKDF2 hashing under flood).
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync("/auth/login",
                new LoginRequest("alice", "wonderland"));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        var rejected = await client.PostAsJsonAsync("/auth/login",
            new LoginRequest("alice", "wonderland"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Limiter_DoesNotAffect_OtherEndpoints()
    {
        // Aggressive signup limit of 1 — must NOT bleed into other paths.
        // /positions is auth-protected so we go through login first
        // (login limit untouched here, default disabled).
        using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Auth:RateLimit:SignupPerIp:Enabled"] = "true",
            ["Trading:Auth:RateLimit:SignupPerIp:PermitLimit"] = "1",
            ["Trading:Auth:RateLimit:SignupPerIp:WindowSeconds"] = "60",
        });
        using var http = factory.CreateClient();

        var token = await factory.LoginAsync(http);
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Many calls to /positions: zero rate-limit interference.
        for (var i = 0; i < 20; i++)
        {
            var resp = await http.GetAsync("/positions");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, resp.StatusCode);
        }
    }

    [Fact]
    public async Task Limiter_Disabled_NoThrottling()
    {
        // Default TestAppFactory disables all three policies. Verify the
        // baseline so a future regression that flips defaults doesn't
        // silently break the rest of the suite.
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        for (var i = 0; i < 10; i++)
        {
            var resp = await client.PostAsJsonAsync("/auth/signup",
                new SignupRequest(FreshUsername(), "wonderland-1"));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }
    }
}
