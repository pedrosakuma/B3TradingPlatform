using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Api.Auth.WebAuthn;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace B3.Trading.Api.Tests;

public class WebAuthnEndpointTests
{
    [Fact]
    public async Task Registration_IssuesChallenge_VerifiesAndPersistsEncryptedCredential()
    {
        var dataDir = Path.Combine(
            Directory.GetCurrentDirectory(), ".test-artifacts", "webauthn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        try
        {
            var usersPath = Path.Combine(dataDir, "users.json");
            await using var factory = CreateFactory(new Dictionary<string, string?>
            {
                ["Trading:Auth:UserStore:Enabled"] = "true",
                ["Trading:Auth:UserStore:FilePath"] = usersPath,
                ["Trading:Persistence:DataDirectory"] = dataDir,
            });
            var http = factory.CreateClient();
            var signup = await http.PostAsJsonAsync("/auth/signup",
                new { username = "passkey-user", password = "Wonderland1!" });
            signup.EnsureSuccessStatusCode();
            var session = (await signup.Content.ReadFromJsonAsync<LoginResponse>())!;
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", session.Token);

            var registration = await RegisterAsync(http, "laptop", "credential-one");

            Assert.True(registration.Registered);
            Assert.Equal("laptop", registration.Name);
            Assert.Equal(10, registration.RecoveryCodes.Count);
            var json = await File.ReadAllTextAsync(usersPath);
            Assert.Contains("\"ProtectedCredentialId\"", json);
            Assert.Contains("\"ProtectedPublicKey\"", json);
            Assert.DoesNotContain(
                Convert.ToBase64String(Encoding.UTF8.GetBytes("credential-one")), json);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task MultiplePasskeys_AndTotp_AreListedAsSupportedFactors()
    {
        await using var factory = CreateFactory();
        var authed = await factory.CreateAuthedClientAsync();
        await RegisterAsync(authed, "phone", "credential-phone");
        var second = await RegisterAsync(authed, "security key", "credential-key");
        Assert.Empty(second.RecoveryCodes);

        var totpEnroll = await authed.PostAsJsonAsync("/auth/2fa/enroll", new { });
        totpEnroll.EnsureSuccessStatusCode();
        var totp = await totpEnroll.Content.ReadFromJsonAsync<JsonElement>();
        var secret = totp.GetProperty("secret").GetString()!;
        var code = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret)).ComputeTotp();
        (await authed.PostAsJsonAsync("/auth/2fa/verify", new { code })).EnsureSuccessStatusCode();

        var plain = factory.CreateClient();
        var login = await plain.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" });
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        var factors = body.GetProperty("factors")
            .EnumerateArray().Select(static item => item.GetString()).ToArray();
        Assert.Equal(new[] { "totp", "webauthn" }, factors);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("challengeToken").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("totpChallengeToken").GetString()));
    }

    [Fact]
    public async Task Authentication_VerifiesAssertion_UpdatesCounter_AndIsSingleUse()
    {
        await using var factory = CreateFactory();
        var authed = await factory.CreateAuthedClientAsync();
        await RegisterAsync(authed, "primary", "credential-auth");

        var plain = factory.CreateClient();
        var login = await LoginForChallengeAsync(plain);
        var options = await StartAuthenticationAsync(plain, login, "credential-auth");
        var assertion = Assertion("credential-auth", UserHandle("alice"));
        var authenticated = await plain.PostAsJsonAsync("/auth/webauthn/authenticate", new
        {
            ceremonyToken = options.CeremonyToken,
            credential = assertion,
        });
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.False(string.IsNullOrEmpty(
            (await authenticated.Content.ReadFromJsonAsync<LoginResponse>())!.Token));

        var replay = await plain.PostAsJsonAsync("/auth/webauthn/authenticate", new
        {
            ceremonyToken = options.CeremonyToken,
            credential = assertion,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Authentication_RejectsWrongRpTamperedChallengeAndStaleCounter()
    {
        await using var factory = CreateFactory();
        var authed = await factory.CreateAuthedClientAsync();
        await RegisterAsync(authed, "primary", "credential-security");
        await RegisterAsync(authed, "stale", "stale-counter");
        var plain = factory.CreateClient();

        var login = await LoginForChallengeAsync(plain);
        var wrongRpOptions = await StartAuthenticationAsync(plain, login, "credential-security");
        var wrongRp = await plain.PostAsJsonAsync("/auth/webauthn/authenticate", new
        {
            ceremonyToken = wrongRpOptions.CeremonyToken,
            credential = Assertion(
                "credential-security",
                UserHandle("alice"),
                """{"type":"webauthn.get","origin":"https://evil.example"}"""),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongRp.StatusCode);

        var tampered = await plain.PostAsJsonAsync("/auth/webauthn/authenticate", new
        {
            ceremonyToken = "tampered-token",
            credential = Assertion("credential-security", UserHandle("alice")),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);

        var staleLogin = await LoginForChallengeAsync(plain);
        var staleOptions = await StartAuthenticationAsync(plain, staleLogin, "stale-counter");
        var stale = await plain.PostAsJsonAsync("/auth/webauthn/authenticate", new
        {
            ceremonyToken = staleOptions.CeremonyToken,
            credential = Assertion("stale-counter", UserHandle("alice")),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    [Fact]
    public async Task RegistrationChallenge_Expires_AndCannotBeRedeemed()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Trading:Auth:WebAuthn:ChallengeTtl"] = "00:00:00.1",
        });
        var http = await factory.CreateAuthedClientAsync();
        var started = await StartRegistrationAsync(http, "expiring");
        await Task.Delay(250);
        var expired = await CompleteRegistrationAsync(
            http, started.CeremonyToken, "credential-expired");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task InvalidAttestation_IsRejectedByFido2NetLib()
    {
        await using var factory = TestAppFactory.WithOverrides(
            new Dictionary<string, string?>
            {
                ["Trading:Auth:WebAuthn:RelyingPartyId"] = "localhost",
                ["Trading:Auth:WebAuthn:Origins:0"] = "http://localhost",
            });
        var http = await factory.CreateAuthedClientAsync();
        var started = await StartRegistrationAsync(http, "invalid");
        var rejected = await CompleteRegistrationAsync(
            http, started.CeremonyToken, "not-a-real-attestation");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Fact]
    public async Task PasskeyOnlyRecoveryCode_CompletesLoginOnce()
    {
        await using var factory = CreateFactory();
        var authed = await factory.CreateAuthedClientAsync();
        var registration = await RegisterAsync(authed, "primary", "credential-recovery");
        var recoveryCode = registration.RecoveryCodes[0];

        var plain = factory.CreateClient();
        var login = await LoginForChallengeAsync(plain);
        var recovered = await plain.PostAsJsonAsync("/auth/2fa/verify", new
        {
            code = recoveryCode,
            totpChallengeToken = login,
        });
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);

        var secondLogin = await LoginForChallengeAsync(plain);
        var replay = await plain.PostAsJsonAsync("/auth/2fa/verify", new
        {
            code = recoveryCode,
            totpChallengeToken = secondLogin,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    private static TestAppFactory CreateFactory(
        IDictionary<string, string?>? overrides = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["Trading:Auth:WebAuthn:RelyingPartyId"] = "localhost",
            ["Trading:Auth:WebAuthn:Origins:0"] = "http://localhost",
        };
        if (overrides is not null)
        {
            foreach (var item in overrides)
                config[item.Key] = item.Value;
        }

        return TestAppFactory.WithOverrides(config, services =>
        {
            services.RemoveAll<IFido2>();
            services.AddSingleton<IFido2>(new FakeFido2());
        });
    }

    private static async Task<WebAuthnRegistrationResponse> RegisterAsync(
        HttpClient http,
        string name,
        string credentialId)
    {
        var started = await StartRegistrationAsync(http, name);
        var completed = await CompleteRegistrationAsync(
            http, started.CeremonyToken, credentialId);
        completed.EnsureSuccessStatusCode();
        return (await completed.Content.ReadFromJsonAsync<WebAuthnRegistrationResponse>())!;
    }

    private static async Task<OptionsDto> StartRegistrationAsync(
        HttpClient http,
        string name)
    {
        var response = await http.PostAsJsonAsync(
            "/auth/webauthn/register", new { name });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "localhost",
            body.GetProperty("options").GetProperty("rp").GetProperty("id").GetString());
        Assert.False(string.IsNullOrEmpty(
            body.GetProperty("options").GetProperty("challenge").GetString()));
        return new OptionsDto(body.GetProperty("ceremonyToken").GetString()!);
    }

    private static Task<HttpResponseMessage> CompleteRegistrationAsync(
        HttpClient http,
        string ceremonyToken,
        string credentialId) =>
        http.PostAsJsonAsync("/auth/webauthn/register", new
        {
            ceremonyToken,
            credential = Attestation(credentialId),
        });

    private static async Task<string> LoginForChallengeAsync(HttpClient http)
    {
        var response = await http.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(
            body.GetProperty("factors").EnumerateArray(),
            static factor => factor.GetString() == "webauthn");
        return body.GetProperty("challengeToken").GetString()!;
    }

    private static async Task<OptionsDto> StartAuthenticationAsync(
        HttpClient http,
        string challengeToken,
        string expectedCredential)
    {
        var response = await http.PostAsJsonAsync(
            "/auth/webauthn/authenticate", new { challengeToken });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("localhost", body.GetProperty("options").GetProperty("rpId").GetString());
        Assert.Contains(
            body.GetProperty("options").GetProperty("allowCredentials").EnumerateArray(),
            item => Base64UrlDecode(item.GetProperty("id").GetString()!)
                .SequenceEqual(Encoding.UTF8.GetBytes(expectedCredential)));
        return new OptionsDto(body.GetProperty("ceremonyToken").GetString()!);
    }

    private static AuthenticatorAttestationRawResponse Attestation(string id)
    {
        var rawId = Encoding.UTF8.GetBytes(id);
        return new AuthenticatorAttestationRawResponse
        {
            Id = Base64Url(rawId),
            RawId = rawId,
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAttestationRawResponse.AttestationResponse
            {
                AttestationObject = new byte[] { 1 },
                ClientDataJson = Encoding.UTF8.GetBytes(
                    """{"type":"webauthn.create","origin":"http://localhost"}"""),
                Transports = Array.Empty<AuthenticatorTransport>(),
            },
            ClientExtensionResults = new AuthenticationExtensionsClientOutputs(),
        };
    }

    private static AuthenticatorAssertionRawResponse Assertion(
        string id,
        byte[] userHandle,
        string? clientData = null)
    {
        var rawId = Encoding.UTF8.GetBytes(id);
        return new AuthenticatorAssertionRawResponse
        {
            Id = Base64Url(rawId),
            RawId = rawId,
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAssertionRawResponse.AssertionResponse
            {
                AuthenticatorData = new byte[] { 1 },
                Signature = new byte[] { 2 },
                ClientDataJson = Encoding.UTF8.GetBytes(clientData
                    ?? """{"type":"webauthn.get","origin":"http://localhost"}"""),
                UserHandle = userHandle,
            },
            ClientExtensionResults = new AuthenticationExtensionsClientOutputs(),
        };
    }

    private static byte[] UserHandle(string username) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            $"B3.Trading.Api.WebAuthn.User.v1:{username}"));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private sealed record OptionsDto(string CeremonyToken);

    private sealed class FakeFido2 : IFido2
    {
        private readonly Fido2 _options = new(new Fido2Configuration
        {
            ServerDomain = "localhost",
            ServerName = "B3 Trading Platform",
            Origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "http://localhost",
            },
            ChallengeSize = 32,
        });

        public AssertionOptions GetAssertionOptions(GetAssertionOptionsParams parameters) =>
            _options.GetAssertionOptions(parameters);

        public CredentialCreateOptions RequestNewCredential(RequestNewCredentialParams parameters) =>
            _options.RequestNewCredential(parameters);

        public async Task<RegisteredPublicKeyCredential> MakeNewCredentialAsync(
            MakeNewCredentialParams parameters,
            CancellationToken cancellationToken = default)
        {
            var unique = await parameters.IsCredentialIdUniqueToUserCallback(
                new IsCredentialIdUniqueToUserParams(
                    parameters.AttestationResponse.RawId,
                    parameters.OriginalOptions.User),
                cancellationToken);
            if (!unique)
                throw new Fido2VerificationException("duplicate credential");

            return new RegisteredPublicKeyCredential
            {
                Id = parameters.AttestationResponse.RawId,
                PublicKey = SHA256.HashData(parameters.AttestationResponse.RawId),
                SignCount = 1,
                User = parameters.OriginalOptions.User,
                Transports = Array.Empty<AuthenticatorTransport>(),
                AaGuid = Guid.NewGuid(),
            };
        }

        public async Task<VerifyAssertionResult> MakeAssertionAsync(
            MakeAssertionParams parameters,
            CancellationToken cancellationToken = default)
        {
            var clientData = Encoding.UTF8.GetString(
                parameters.AssertionResponse.Response.ClientDataJson);
            if (clientData.Contains("evil.example", StringComparison.Ordinal)
                || clientData.Contains("tampered", StringComparison.Ordinal))
                throw new Fido2VerificationException("origin or challenge mismatch");

            var owns = await parameters.IsUserHandleOwnerOfCredentialIdCallback(
                new IsUserHandleOwnerOfCredentialIdParams(
                    parameters.AssertionResponse.RawId,
                    parameters.AssertionResponse.Response.UserHandle ?? Array.Empty<byte>()),
                cancellationToken);
            if (!owns)
                throw new Fido2VerificationException("wrong user handle");

            var id = Encoding.UTF8.GetString(parameters.AssertionResponse.RawId);
            return new VerifyAssertionResult
            {
                CredentialId = parameters.AssertionResponse.RawId,
                SignCount = id == "stale-counter"
                    ? parameters.StoredSignatureCounter
                    : parameters.StoredSignatureCounter + 1,
            };
        }
    }
}
