using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using B3.Trading.Api.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Boots the full Host with deterministic in-memory test config: stub
/// gateway disabled (mock client + ER router), known JWT signing key,
/// and a known user "alice" / "wonderland".
/// </summary>
public class TestAppFactory : WebApplicationFactory<Program>
{
    public const string TestUser = "alice";
    public const string TestPassword = "wonderland";
    public const string TestSigningKey = "test-signing-key-must-be-at-least-32-bytes-long-okay";
    public const int TestIterations = 10_000; // fast for tests

    private IDictionary<string, string?>? _configOverrides;

    /// <summary>
    /// Builds a factory with extra config keys layered on top of the
    /// deterministic test defaults. Both dictionaries are pushed through
    /// the SAME <c>ConfigureAppConfiguration</c> call so the override is
    /// added last and wins, regardless of provider ordering. Static factory
    /// (not a public ctor) so xUnit's <c>IClassFixture</c> still binds.
    /// </summary>
    public static TestAppFactory WithOverrides(IDictionary<string, string?> configOverrides)
        => new TestAppFactoryWithOverrides(configOverrides);

    private sealed class TestAppFactoryWithOverrides : TestAppFactory
    {
        public TestAppFactoryWithOverrides(IDictionary<string, string?> overrides)
        {
            base._configOverrides = overrides;
        }
    }

    private static readonly Lazy<(string Hash, string Salt)> Pbkdf2 = new(() =>
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(TestPassword),
            salt,
            TestIterations,
            HashAlgorithmName.SHA256,
            32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    });

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var (hash, salt) = Pbkdf2.Value;

        builder.ConfigureAppConfiguration((_, cb) =>
        {
            cb.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trading:Auth:Issuer"] = "b3-trading-test",
                ["Trading:Auth:Audience"] = "b3-trading-test",
                ["Trading:Auth:SigningKey"] = TestSigningKey,
                ["Trading:Auth:TokenLifetimeMinutes"] = "60",
                ["Trading:Auth:Users:0:Username"] = TestUser,
                ["Trading:Auth:Users:0:PasswordHash"] = hash,
                ["Trading:Auth:Users:0:Salt"] = salt,
                ["Trading:Auth:Users:0:Iterations"] = TestIterations.ToString(),
                ["Trading:Auth:Users:0:Role"] = "user",
                ["Trading:Auth:Users:1:Username"] = "bob",
                ["Trading:Auth:Users:1:PasswordHash"] = hash,
                ["Trading:Auth:Users:1:Salt"] = salt,
                ["Trading:Auth:Users:1:Iterations"] = TestIterations.ToString(),
                ["Trading:Auth:Users:1:Role"] = "user",
                ["Trading:Auth:Users:2:Username"] = "admin",
                ["Trading:Auth:Users:2:PasswordHash"] = hash,
                ["Trading:Auth:Users:2:Salt"] = salt,
                ["Trading:Auth:Users:2:Iterations"] = TestIterations.ToString(),
                ["Trading:Auth:Users:2:Role"] = "admin",
                ["Trading:Exchange:UseStubGateway"] = "false",
                ["Trading:Exchange:Firms:0:FirmId"] = "TEST",
                ["Trading:Risk:Default:MaxQuantity"] = "1000",
                ["Trading:Risk:Default:MaxNotional"] = "1000000",
                ["Trading:Risk:Default:PriceCollarPercent"] = "10",
                ["Trading:Risk:Default:PositionLimit"] = "5000",
                ["Trading:Risk:ReferencePrices:PETR4"] = "30.0",
                ["Trading:Persistence:Enabled"] = "false",
                // Slice 2 of #97 ships rate limits enabled by default.
                // The default suite hits /auth/login and /auth/signup
                // hundreds of times per run; disable here so individual
                // tests opt-in via WithOverrides when they want to
                // exercise the limiter behavior.
                ["Trading:Auth:RateLimit:SignupPerIp:Enabled"] = "false",
                ["Trading:Auth:RateLimit:SignupGlobal:Enabled"] = "false",
                ["Trading:Auth:RateLimit:LoginPerIp:Enabled"] = "false",
                // Slice 3 of #97: keep tests on the in-memory user store
                // so they don't write a users.json into the repo. Tests
                // exercising the file-backed store opt in via WithOverrides.
                ["Trading:Auth:UserStore:Enabled"] = "false",
                // Slice 4 of #97: lockout disabled by default so tests
                // hammering /auth/login with bad creds don't trip the
                // gate. Lockout-specific tests opt in via WithOverrides.
                ["Trading:Auth:LoginLockout:Enabled"] = "false",
            });

            if (_configOverrides is not null)
                cb.AddInMemoryCollection(_configOverrides);
        });
        return base.CreateHost(builder);
    }

    public async Task<string> LoginAsync(HttpClient client, string user = TestUser, string password = TestPassword)
    {
        var resp = await client.PostAsJsonAsync("/auth/login", new LoginRequest(user, password));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        return body!.Token;
    }

    public async Task<HttpClient> CreateAuthedClientAsync(string user = TestUser, string password = TestPassword)
    {
        var client = CreateClient();
        var token = await LoginAsync(client, user, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
