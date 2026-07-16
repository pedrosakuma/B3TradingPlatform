using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Application.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OtpNet;

namespace B3.Trading.Api.Tests;

public sealed class ExternalIdentityExchangeTests
{
    private const string Issuer = "https://tenant.ciamlogin.com/tenant-id/v2.0";
    private const string TenantId = "tenant-id";
    private const string Audience = "api://trading";
    private const string Scope = "Trading.Access";
    private const string ClientId = "spa-client-id";
    private const string Subject = "external-subject";

    [Fact]
    public async Task LocalMode_DoesNotMapExchange_AndLocalLoginStaysDefault()
    {
        await using var factory = new TestAppFactory();
        using var http = factory.CreateClient();

        var exchange = await http.PostAsync("/auth/exchange", null);
        Assert.Equal(HttpStatusCode.NotFound, exchange.StatusCode);

        var login = await http.PostAsJsonAsync("/auth/login", new LoginRequest("alice", "wonderland"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(body);
        Assert.True(body!.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task EntraMode_MapsOnlyExchangeAuthSurface()
    {
        using var keys = new SigningKeys();
        var config = HybridConfig();
        config["Trading:Auth:Mode"] = "Entra";
        await using var factory = TestAppFactory.WithOverrides(config, services =>
        {
            services.RemoveAll<IExternalIdentityConfigurationProvider>();
            services.AddSingleton<IExternalIdentityConfigurationProvider>(keys.Provider);
            services.RemoveAll<ITradingUserDirectory>();
            services.AddSingleton<ITradingUserDirectory>(_ =>
            {
                var directory = new InMemoryTradingUserDirectory();
                directory.InitializeAsync().GetAwaiter().GetResult();
                directory.ImportLegacyUsersAsync(new[]
                {
                    new LegacyTradingUserImport("admin", "admin", "default", TradingUserDirectoryConstants.RoleAdmin),
                }).GetAwaiter().GetResult();
                directory.BindExternalIdentityAsync(
                    "admin",
                    new ExternalIdentityBindingRequest(Issuer, Subject, TenantId, "object-id"),
                    expectedRowVersion: 1).GetAwaiter().GetResult();
                return directory;
            });
        });
        using var http = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound,
            (await http.PostAsJsonAsync("/auth/login", new LoginRequest("alice", "wonderland"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await http.PostAsJsonAsync("/auth/signup", new SignupRequest("new-user", "wonderland-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await http.GetAsync("/auth/2fa/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await http.PostAsync("/auth/exchange", null)).StatusCode);
    }

    [Fact]
    public void EntraMode_RefusesBootWithoutExternallyLinkedAdmin()
    {
        var config = HybridConfig();
        config["Trading:Auth:Mode"] = "Entra";
        using var keys = new SigningKeys();
        using var factory = TestAppFactory.WithOverrides(config, services =>
        {
            services.RemoveAll<IExternalIdentityConfigurationProvider>();
            services.AddSingleton<IExternalIdentityConfigurationProvider>(keys.Provider);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("requires at least one active admin", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HybridMode_LocalLoginDisabled_DoesNotMapTotp()
    {
        using var keys = new SigningKeys();
        var config = HybridConfig();
        config["Trading:Auth:LocalLoginEnabled"] = "false";
        await using var factory = TestAppFactory.WithOverrides(config, services =>
        {
            services.RemoveAll<IExternalIdentityConfigurationProvider>();
            services.AddSingleton<IExternalIdentityConfigurationProvider>(keys.Provider);
        });
        using var http = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound,
            (await http.PostAsJsonAsync("/auth/login", new LoginRequest("alice", "wonderland"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await http.GetAsync("/auth/2fa/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await http.PostAsync("/auth/exchange", null)).StatusCode);
    }

    [Fact]
    public async Task Exchange_ValidExternalToken_ReturnsInternalJwt_FromDirectoryOnly()
    {
        using var keys = new SigningKeys();
        await using var factory = HybridFactory(keys);
        using var http = factory.CreateClient();
        await BindAliceAsync(factory);

        var token = keys.IssueAccessToken(extraClaims: new[]
        {
            new Claim("firm", "EVIL"),
            new Claim("role", "admin"),
        });
        var resp = await ExchangeAsync(http, token);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(body);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body!.Token);
        Assert.Equal("alice", jwt.Subject);
        Assert.Contains(jwt.Claims, c => c.Type == JwtIssuer.FirmClaim && c.Value == "default");
        Assert.Contains(jwt.Claims, c => c.Type == JwtIssuer.RoleClaim && c.Value == "user");
        Assert.Contains(jwt.Claims, c => c.Type == "amr" && c.Value == TradingSessionIssuer.EntraExchangeAmr);
        Assert.True(body.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(11));
    }

    [Fact]
    public async Task Exchange_RejectsUnknownDisabledAndDirectoryOutage()
    {
        using var keys = new SigningKeys();
        await using var factory = HybridFactory(keys);
        using var http = factory.CreateClient();

        var unknown = await ExchangeAsync(http, keys.IssueAccessToken());
        await AssertErrorAsync(unknown, HttpStatusCode.Forbidden, "account_not_provisioned");

        await BindAliceAsync(factory);
        var directory = factory.Services.GetRequiredService<ITradingUserDirectory>();
        var alice = await directory.GetUserAsync("alice");
        await directory.SetStatusAsync("alice", TradingUserDirectoryConstants.StatusDisabled, alice!.RowVersion);

        var disabled = await ExchangeAsync(http, keys.IssueAccessToken());
        await AssertErrorAsync(disabled, HttpStatusCode.Forbidden, "account_disabled");

        using var outageKeys = new SigningKeys();
        outageKeys.Provider.Throw = true;
        await using var outageFactory = HybridFactory(outageKeys);
        using var outageHttp = outageFactory.CreateClient();
        var outage = await ExchangeAsync(outageHttp, outageKeys.IssueAccessToken());
        await AssertErrorAsync(outage, HttpStatusCode.ServiceUnavailable, "identity_provider_unavailable");
    }

    [Fact]
    public async Task Schemes_CannotBeConfused()
    {
        using var keys = new SigningKeys();
        await using var factory = HybridFactory(keys);
        using var http = factory.CreateClient();
        await BindAliceAsync(factory);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", keys.IssueAccessToken());
        var orders = await http.GetAsync("/orders");
        Assert.Equal(HttpStatusCode.Unauthorized, orders.StatusCode);

        var internalToken = await factory.LoginAsync(factory.CreateClient());
        var exchange = await ExchangeAsync(http, internalToken);
        await AssertErrorAsync(exchange, HttpStatusCode.Unauthorized, "invalid_external_token");
    }

    [Fact]
    public async Task Exchange_RateLimit_ReturnsStableCode()
    {
        using var keys = new SigningKeys();
        var config = HybridConfig();
        config["Trading:Auth:RateLimit:ExchangePerIp:Enabled"] = "true";
        config["Trading:Auth:RateLimit:ExchangePerIp:PermitLimit"] = "1";
        config["Trading:Auth:RateLimit:ExchangePerIp:WindowSeconds"] = "60";
        await using var factory = TestAppFactory.WithOverrides(config, services =>
        {
            services.RemoveAll<IExternalIdentityConfigurationProvider>();
            services.AddSingleton<IExternalIdentityConfigurationProvider>(keys.Provider);
        });
        using var http = factory.CreateClient();
        await BindAliceAsync(factory);

        var token = keys.IssueAccessToken();
        Assert.Equal(HttpStatusCode.OK, (await ExchangeAsync(http, token)).StatusCode);
        var limited = await ExchangeAsync(http, token);

        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, "rate_limited");
    }

    [Fact]
    public async Task HybridLogin_UsesDirectoryAuthority_AndTenMinuteTtl()
    {
        using var keys = new SigningKeys();
        await using var factory = HybridFactory(keys);
        using var http = factory.CreateClient();

        var directory = factory.Services.GetRequiredService<ITradingUserDirectory>();
        var alice = await directory.GetUserAsync("alice");
        await directory.SetFirmAndRoleAsync("alice", "FIRM77", TradingUserDirectoryConstants.RoleAdmin, alice!.RowVersion);

        var resp = await http.PostAsJsonAsync("/auth/login", new LoginRequest("alice", "wonderland"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body!.Token);

        Assert.Contains(jwt.Claims, c => c.Type == JwtIssuer.FirmClaim && c.Value == "FIRM77");
        Assert.Contains(jwt.Claims, c => c.Type == JwtIssuer.RoleClaim && c.Value == "admin");
        Assert.True(body.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(11));
    }

    [Fact]
    public async Task AdminIdentityEndpoints_BindAuditAndGuardLastLinkedAdmin()
    {
        using var keys = new SigningKeys();
        await using var factory = HybridFactory(keys);
        using var admin = await factory.CreateAuthedClientAsync("admin");

        var list = await admin.GetAsync("/admin/identity/users");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var users = await list.Content.ReadFromJsonAsync<JsonElement>();
        var adminUser = users.GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("tradingUserId").GetString() == "admin");
        var rowVersion = adminUser.GetProperty("rowVersion").GetInt64();

        var bind = await admin.PostAsJsonAsync(
            "/admin/identity/users/admin/external-bindings",
            new { externalAccessToken = keys.IssueAccessToken(), expectedRowVersion = rowVersion });
        Assert.Equal(HttpStatusCode.Created, bind.StatusCode);
        var binding = await bind.Content.ReadFromJsonAsync<JsonElement>();
        var bindingId = binding.GetProperty("id").GetInt64();

        var afterBind = await factory.Services.GetRequiredService<ITradingUserDirectory>().GetUserAsync("admin");
        Assert.NotNull(afterBind);
        var disable = await admin.PutAsJsonAsync(
            "/admin/identity/users/admin/status",
            new { status = TradingUserDirectoryConstants.StatusDisabled, expectedRowVersion = afterBind!.RowVersion });
        await AssertErrorAsync(disable, HttpStatusCode.Conflict, "last_admin_conflict");

        var downgrade = await admin.PutAsJsonAsync(
            "/admin/identity/users/admin/authorization",
            new { firmId = "default", role = TradingUserDirectoryConstants.RoleCompliance, expectedRowVersion = afterBind.RowVersion });
        await AssertErrorAsync(downgrade, HttpStatusCode.Conflict, "last_admin_conflict");

        using var unlinkReq = new HttpRequestMessage(HttpMethod.Delete, $"/admin/identity/users/admin/external-bindings/{bindingId}")
        {
            Content = JsonContent.Create(new { expectedRowVersion = afterBind.RowVersion }),
        };
        var unlink = await admin.SendAsync(unlinkReq);
        await AssertErrorAsync(unlink, HttpStatusCode.Conflict, "last_admin_conflict");
    }

    [Fact]
    public async Task AdminIdentityEndpoints_RejectConflictsDisabledAndUnknownUsers()
    {
        using var keys = new SigningKeys();
        await using var factory = HybridFactory(keys);
        using var admin = await factory.CreateAuthedClientAsync("admin");
        var directory = factory.Services.GetRequiredService<ITradingUserDirectory>();
        var alice = await directory.GetUserAsync("alice");

        var bind = await admin.PostAsJsonAsync(
            "/admin/identity/users/alice/external-bindings",
            new { externalAccessToken = keys.IssueAccessToken(), expectedRowVersion = alice!.RowVersion });
        Assert.Equal(HttpStatusCode.Created, bind.StatusCode);

        var bob = await directory.GetUserAsync("bob");
        var conflict = await admin.PostAsJsonAsync(
            "/admin/identity/users/bob/external-bindings",
            new { externalAccessToken = keys.IssueAccessToken(), expectedRowVersion = bob!.RowVersion });
        await AssertErrorAsync(conflict, HttpStatusCode.Conflict, "identity_binding_conflict");

        var stale = await admin.PutAsJsonAsync(
            "/admin/identity/users/alice/status",
            new { status = TradingUserDirectoryConstants.StatusDisabled, expectedRowVersion = alice.RowVersion });
        await AssertErrorAsync(stale, HttpStatusCode.Conflict, "row_version_conflict");

        var missing = await admin.PostAsJsonAsync(
            "/admin/identity/users/missing/external-bindings",
            new { externalAccessToken = keys.IssueAccessToken(), expectedRowVersion = 1 });
        await AssertErrorAsync(missing, HttpStatusCode.Conflict, "identity_binding_conflict");
    }

    [Fact]
    public async Task HybridTotpVerify_UsesDirectoryAuthority_AndDoesNotAddExchangeAmr()
    {
        using var keys = new SigningKeys();
        await using var factory = HybridFactory(keys);
        using var setup = factory.CreateClient();

        var directory = factory.Services.GetRequiredService<ITradingUserDirectory>();
        var alice = await directory.GetUserAsync("alice");
        await directory.SetFirmAndRoleAsync("alice", "FIRM88", TradingUserDirectoryConstants.RoleCompliance, alice!.RowVersion);

        var firstToken = await factory.LoginAsync(setup);
        setup.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firstToken);
        var enroll = await setup.PostAsJsonAsync("/auth/2fa/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        var enrollBody = await enroll.Content.ReadFromJsonAsync<JsonElement>();
        var secret = enrollBody.GetProperty("secret").GetString()!;
        var confirm = await setup.PostAsJsonAsync("/auth/2fa/verify", new { code = ComputeTotp(secret) });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        using var loginClient = factory.CreateClient();
        var login = await loginClient.PostAsJsonAsync("/auth/login", new LoginRequest("alice", "wonderland"));
        var challenge = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("totpChallengeToken")
            .GetString();
        var verify = await loginClient.PostAsJsonAsync("/auth/2fa/verify",
            new { code = ComputeTotp(secret, stepOffset: 1), totpChallengeToken = challenge });

        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var body = await verify.Content.ReadFromJsonAsync<LoginResponse>();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body!.Token);
        Assert.Contains(jwt.Claims, c => c.Type == JwtIssuer.FirmClaim && c.Value == "FIRM88");
        Assert.Contains(jwt.Claims, c => c.Type == JwtIssuer.RoleClaim && c.Value == "compliance");
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "amr" && c.Value == TradingSessionIssuer.EntraExchangeAmr);
    }

    [Fact]
    public async Task KeyRollover_RefreshesProvider_AndLastKnownGoodKeepsOldKeyWorking()
    {
        using var oldKeys = new SigningKeys();
        using var newKeys = new SigningKeys();
        await using var factory = HybridFactory(oldKeys);
        using var http = factory.CreateClient();
        await BindAliceAsync(factory);

        var oldToken = oldKeys.IssueAccessToken();
        Assert.Equal(HttpStatusCode.OK, (await ExchangeAsync(http, oldToken)).StatusCode);

        oldKeys.Provider.OnRefresh = () => oldKeys.Provider.SigningKeys = new[] { newKeys.SecurityKey };
        var newToken = newKeys.IssueAccessToken();
        Assert.Equal(HttpStatusCode.OK, (await ExchangeAsync(http, newToken)).StatusCode);

        oldKeys.Provider.Throw = true;
        Assert.Equal(HttpStatusCode.OK, (await ExchangeAsync(http, newToken)).StatusCode);
    }

    [Theory]
    [InlineData("wrong_audience")]
    [InlineData("wrong_issuer")]
    [InlineData("wrong_tid")]
    [InlineData("missing_scope")]
    [InlineData("wrong_azp")]
    [InlineData("bad_azpacr")]
    [InlineData("wrong_ver")]
    [InlineData("expired")]
    [InlineData("not_yet_valid")]
    [InlineData("hs256")]
    public async Task Validator_RejectsRfcFailures(string scenario)
    {
        using var keys = new SigningKeys();
        var validator = BuildValidator(keys.Provider);
        var token = scenario switch
        {
            "wrong_audience" => keys.IssueAccessToken(audience: "spa-client-id"),
            "wrong_issuer" => keys.IssueAccessToken(issuer: "https://evil.example/v2.0"),
            "wrong_tid" => keys.IssueAccessToken(extraClaims: new[] { new Claim("tid", "other-tenant") }),
            "missing_scope" => keys.IssueAccessToken(scopes: "Other.Scope"),
            "wrong_azp" => keys.IssueAccessToken(azp: "other-client"),
            "bad_azpacr" => keys.IssueAccessToken(extraClaims: new[] { new Claim("azpacr", "1") }),
            "wrong_ver" => keys.IssueAccessToken(extraClaims: new[] { new Claim("ver", "1.0") }),
            "expired" => keys.IssueAccessToken(notBefore: DateTime.UtcNow.AddMinutes(-10), expires: DateTime.UtcNow.AddMinutes(-2)),
            "not_yet_valid" => keys.IssueAccessToken(notBefore: DateTime.UtcNow.AddMinutes(2), expires: DateTime.UtcNow.AddMinutes(10)),
            "hs256" => keys.IssueSymmetricToken(),
            _ => throw new InvalidOperationException(scenario),
        };

        var result = await validator.ValidateAsync(token);

        Assert.Equal(ExternalIdentityValidationStatus.InvalidToken, result.Status);
        Assert.Equal("invalid_external_token", result.Code);
    }

    [Fact]
    public void SessionIssuer_RejectsIncompleteDirectoryUser()
    {
        var issuer = new TradingSessionIssuer(
            Options.Create(HybridOptions()),
            new InMemoryTradingUserDirectory(),
            new JwtIssuer(Options.Create(HybridOptions())),
            new B3.Trading.Application.EndClientRegistry());
        var user = new TradingUser(
            "alice",
            "Alice",
            "",
            TradingUserDirectoryConstants.StatusActive,
            TradingUserDirectoryConstants.RoleUser,
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Array.Empty<ExternalIdentityBinding>());

        var result = issuer.IssueForExternalUser(user);

        Assert.False(result.Succeeded);
        Assert.Equal("account_incomplete", result.ErrorCode);
    }

    private static TestAppFactory HybridFactory(SigningKeys keys) =>
        TestAppFactory.WithOverrides(HybridConfig(), services =>
        {
            services.RemoveAll<IExternalIdentityConfigurationProvider>();
            services.AddSingleton<IExternalIdentityConfigurationProvider>(keys.Provider);
        });

    private static IDictionary<string, string?> HybridConfig() => new Dictionary<string, string?>
    {
        ["Trading:Auth:Mode"] = "Hybrid",
        ["Trading:Auth:ExternalIdentity:Authority"] = "https://tenant.ciamlogin.com/tenant-id/v2.0",
        ["Trading:Auth:ExternalIdentity:Issuer"] = Issuer,
        ["Trading:Auth:ExternalIdentity:TenantId"] = TenantId,
        ["Trading:Auth:ExternalIdentity:Audience"] = Audience,
        ["Trading:Auth:ExternalIdentity:RequiredScope"] = Scope,
        ["Trading:Auth:ExternalIdentity:AllowedClientApplicationIds:0"] = ClientId,
    };

    private static AuthOptions HybridOptions() => new()
    {
        Mode = AuthModes.Hybrid,
        Issuer = "b3-trading-test",
        Audience = "b3-trading-test",
        SigningKey = TestAppFactory.TestSigningKey,
        ExternalIdentity = new ExternalIdentityOptions
        {
            Authority = "https://tenant.ciamlogin.com/tenant-id/v2.0",
            Issuer = Issuer,
            TenantId = TenantId,
            Audience = Audience,
            RequiredScope = Scope,
            AllowedClientApplicationIds = new() { ClientId },
        },
    };

    private static IExternalIdentityTokenValidator BuildValidator(IExternalIdentityConfigurationProvider provider) =>
        new ExternalIdentityTokenValidator(Options.Create(HybridOptions()), provider);

    private static async Task BindAliceAsync(TestAppFactory factory)
    {
        var directory = factory.Services.GetRequiredService<ITradingUserDirectory>();
        var alice = await directory.GetUserAsync("alice");
        Assert.NotNull(alice);
        await directory.BindExternalIdentityAsync("alice",
            new ExternalIdentityBindingRequest(Issuer, Subject, TenantId, "object-id"),
            alice!.RowVersion);
    }

    private static Task<HttpResponseMessage> ExchangeAsync(HttpClient http, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/auth/exchange");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http.SendAsync(req);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage resp, HttpStatusCode status, string code)
    {
        Assert.Equal(status, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("error").GetString());
    }

    private static string ComputeTotp(string base32, int stepOffset = 0) =>
        new Totp(Base32Encoding.ToBytes(base32)).ComputeTotp(DateTime.UtcNow.AddSeconds(stepOffset * 30.0));

    private sealed class FakeExternalIdentityConfigurationProvider : IExternalIdentityConfigurationProvider
    {
        public bool Throw { get; set; }
        public Action? OnRefresh { get; set; }
        public IReadOnlyCollection<SecurityKey> SigningKeys { get; set; }
        private ExternalIdentityConfiguration? _lastKnownGood;

        public FakeExternalIdentityConfigurationProvider(IReadOnlyCollection<SecurityKey> signingKeys)
        {
            SigningKeys = signingKeys;
        }

        public Task<ExternalIdentityConfiguration> GetConfigurationAsync(CancellationToken ct = default)
        {
            if (Throw)
            {
                if (_lastKnownGood is not null)
                    return Task.FromResult(_lastKnownGood);
                throw new InvalidOperationException("metadata unavailable");
            }

            _lastKnownGood = new ExternalIdentityConfiguration(Issuer, SigningKeys);
            return Task.FromResult(_lastKnownGood);
        }

        public void RequestRefresh() => OnRefresh?.Invoke();
    }

    private sealed class SigningKeys : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly SymmetricSecurityKey _symmetricKey =
            new("external-symmetric-test-key-at-least-32-bytes"u8.ToArray());

        public SigningKeys()
        {
            SecurityKey = new RsaSecurityKey(_rsa) { KeyId = Guid.NewGuid().ToString("N") };
            Provider = new FakeExternalIdentityConfigurationProvider(new[] { SecurityKey });
        }

        public RsaSecurityKey SecurityKey { get; }
        public FakeExternalIdentityConfigurationProvider Provider { get; }

        public string IssueAccessToken(
            string issuer = Issuer,
            string audience = Audience,
            string scopes = Scope,
            string azp = ClientId,
            DateTime? notBefore = null,
            DateTime? expires = null,
            IEnumerable<Claim>? extraClaims = null)
        {
            var claims = BaseClaims(scopes, azp).ToList();
            if (extraClaims is not null)
            {
                foreach (var claim in extraClaims)
                {
                    claims.RemoveAll(c => c.Type == claim.Type);
                    claims.Add(claim);
                }
            }

            return WriteToken(issuer, audience, claims,
                new SigningCredentials(SecurityKey, SecurityAlgorithms.RsaSha256),
                notBefore,
                expires);
        }

        public string IssueSymmetricToken() =>
            WriteToken(Issuer, Audience, BaseClaims(Scope, ClientId),
                new SigningCredentials(_symmetricKey, SecurityAlgorithms.HmacSha256),
                DateTime.UtcNow.AddMinutes(-1),
                DateTime.UtcNow.AddMinutes(10));

        private static IEnumerable<Claim> BaseClaims(string scopes, string azp) => new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, Subject),
            new Claim("tid", TenantId),
            new Claim("oid", "object-id"),
            new Claim("scp", scopes),
            new Claim("azp", azp),
            new Claim("ver", "2.0"),
            new Claim("azpacr", "0"),
        };

        private static string WriteToken(
            string issuer,
            string audience,
            IEnumerable<Claim> claims,
            SigningCredentials credentials,
            DateTime? notBefore,
            DateTime? expires)
        {
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(
                issuer,
                audience,
                claims,
                notBefore ?? now.AddMinutes(-1),
                expires ?? now.AddMinutes(10),
                credentials);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public void Dispose() => _rsa.Dispose();
    }
}
