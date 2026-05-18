using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Api.Auth.Totp;
using OtpNet;
using Xunit;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Integration tests for the TOTP 2FA flow (#303). Cover enrollment,
/// login second factor, recovery codes, lockout, disable, pending
/// expiry, encrypted-at-rest, multi-firm, and the non-enrolled
/// no-change path.
/// </summary>
public class TotpEndpointTests
{
    private static string ComputeCode(string base32)
    {
        var totp = new OtpNet.Totp(Base32Encoding.ToBytes(base32));
        return totp.ComputeTotp();
    }

    private sealed record EnrollResponseDto(string Secret, string OtpauthUri, List<string> RecoveryCodes);
    private sealed record LoginRequiresDto(bool Requires2fa, string TotpChallengeToken);

    [Fact]
    public async Task NonEnrolledLogin_BehavesAsBefore_NoRequires2fa()
    {
        await using var factory = new TestAppFactory();
        var http = factory.CreateClient();
        var resp = await http.PostAsJsonAsync("/auth/login", new { username = "alice", password = "wonderland" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("token", out var tokenEl));
        Assert.False(string.IsNullOrEmpty(tokenEl.GetString()));
        Assert.False(body.TryGetProperty("requires2fa", out _));
    }

    [Fact]
    public async Task Enroll_Verify_Activates_AndSubsequentLoginReturnsRequires2fa()
    {
        await using var factory = new TestAppFactory();
        var http = await factory.CreateAuthedClientAsync();

        var enrollResp = await http.PostAsJsonAsync("/auth/2fa/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, enrollResp.StatusCode);
        var enroll = await enrollResp.Content.ReadFromJsonAsync<EnrollResponseDto>();
        Assert.NotNull(enroll);
        Assert.Equal(10, enroll!.RecoveryCodes.Count);
        Assert.StartsWith("otpauth://totp/B3:alice?", enroll.OtpauthUri);

        var code = ComputeCode(enroll.Secret);
        var verifyResp = await http.PostAsJsonAsync("/auth/2fa/verify", new { code });
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);

        // Subsequent login: returns challenge token, NOT a JWT.
        var plainClient = factory.CreateClient();
        var login = await plainClient.PostAsJsonAsync("/auth/login", new { username = "alice", password = "wonderland" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginBody = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(loginBody.GetProperty("requires2fa").GetBoolean());
        var challenge = loginBody.GetProperty("totpChallengeToken").GetString();
        Assert.False(string.IsNullOrEmpty(challenge));

        // Now /auth/2fa/verify with the challenge + a fresh code → real JWT.
        var freshCode = ComputeCode(enroll.Secret);
        var second = await plainClient.PostAsJsonAsync("/auth/2fa/verify",
            new { code = freshCode, totpChallengeToken = challenge });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var jwtBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(jwtBody.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task Login_InvalidCode_Rejected_FiveWrong_Lock429()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Auth:TotpLockout:Enabled"] = "true",
            ["Trading:Auth:TotpLockout:MaxFailedAttempts"] = "5",
            ["Trading:Auth:TotpLockout:Window"] = "00:05:00",
            ["Trading:Auth:TotpLockout:LockoutDuration"] = "00:05:00",
        });
        var http = await factory.CreateAuthedClientAsync();
        var enroll = (await (await http.PostAsJsonAsync("/auth/2fa/enroll", new { }))
            .Content.ReadFromJsonAsync<EnrollResponseDto>())!;
        await http.PostAsJsonAsync("/auth/2fa/verify", new { code = ComputeCode(enroll.Secret) });

        var plain = factory.CreateClient();
        var loginBody = await (await plain.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" }))
            .Content.ReadFromJsonAsync<LoginRequiresDto>();
        var challenge = loginBody!.TotpChallengeToken;

        for (var i = 0; i < 5; i++)
        {
            var r = await plain.PostAsJsonAsync("/auth/2fa/verify",
                new { code = "000000", totpChallengeToken = challenge });
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        var locked = await plain.PostAsJsonAsync("/auth/2fa/verify",
            new { code = ComputeCode(enroll.Secret), totpChallengeToken = challenge });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.NotNull(locked.Headers.RetryAfter);
    }

    [Fact]
    public async Task RecoveryCode_AcceptedOnce_SecondUseRejected()
    {
        await using var factory = new TestAppFactory();
        var http = await factory.CreateAuthedClientAsync();
        var enroll = (await (await http.PostAsJsonAsync("/auth/2fa/enroll", new { }))
            .Content.ReadFromJsonAsync<EnrollResponseDto>())!;
        await http.PostAsJsonAsync("/auth/2fa/verify", new { code = ComputeCode(enroll.Secret) });

        var recovery = enroll.RecoveryCodes[0];

        var plain = factory.CreateClient();
        var login1 = await (await plain.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" }))
            .Content.ReadFromJsonAsync<LoginRequiresDto>();
        var first = await plain.PostAsJsonAsync("/auth/2fa/verify",
            new { code = recovery, totpChallengeToken = login1!.TotpChallengeToken });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Second login attempt with the same recovery code: must be rejected.
        var login2 = await (await plain.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" }))
            .Content.ReadFromJsonAsync<LoginRequiresDto>();
        var second = await plain.PostAsJsonAsync("/auth/2fa/verify",
            new { code = recovery, totpChallengeToken = login2!.TotpChallengeToken });
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Disable_RequiresCurrentCode_ThenReEnrollWorks()
    {
        await using var factory = new TestAppFactory();
        var http = await factory.CreateAuthedClientAsync();
        var enroll = (await (await http.PostAsJsonAsync("/auth/2fa/enroll", new { }))
            .Content.ReadFromJsonAsync<EnrollResponseDto>())!;
        await http.PostAsJsonAsync("/auth/2fa/verify", new { code = ComputeCode(enroll.Secret) });

        // Wrong code: rejected.
        var bad = await http.PostAsJsonAsync("/auth/2fa/disable", new { code = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        // Correct code: disables.
        var ok = await http.PostAsJsonAsync("/auth/2fa/disable", new { code = ComputeCode(enroll.Secret) });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // Re-enroll works (no 409 because disabled cleared the prior state).
        var reenroll = await http.PostAsJsonAsync("/auth/2fa/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, reenroll.StatusCode);
    }

    [Fact]
    public async Task ReEnrollBlockedWhenAlreadyEnrolled()
    {
        await using var factory = new TestAppFactory();
        var http = await factory.CreateAuthedClientAsync();
        var enroll = (await (await http.PostAsJsonAsync("/auth/2fa/enroll", new { }))
            .Content.ReadFromJsonAsync<EnrollResponseDto>())!;
        await http.PostAsJsonAsync("/auth/2fa/verify", new { code = ComputeCode(enroll.Secret) });

        var reenroll = await http.PostAsJsonAsync("/auth/2fa/enroll", new { });
        Assert.Equal(HttpStatusCode.Conflict, reenroll.StatusCode);
    }

    [Fact]
    public async Task PendingEnrollment_ExpiresAfterTtl()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Auth:Totp:PendingEnrollmentTtl"] = "00:00:01",
        });
        var http = await factory.CreateAuthedClientAsync();
        var enroll = (await (await http.PostAsJsonAsync("/auth/2fa/enroll", new { }))
            .Content.ReadFromJsonAsync<EnrollResponseDto>())!;
        await Task.Delay(1500);

        var verify = await http.PostAsJsonAsync("/auth/2fa/verify",
            new { code = ComputeCode(enroll.Secret) });
        Assert.Equal(HttpStatusCode.BadRequest, verify.StatusCode);
    }

    [Fact]
    public async Task SharedSecret_PersistedEncrypted_NotPlaintextBase32()
    {
        // Use the file-backed user store + a signup user so we can
        // inspect users.json on disk.
        var dir = Path.Combine(Path.GetTempPath(), "b3-303-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var usersPath = Path.Combine(dir, "users.json");
            await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
            {
                ["Trading:Auth:UserStore:Enabled"] = "true",
                ["Trading:Auth:UserStore:FilePath"] = usersPath,
                ["Trading:Persistence:DataDirectory"] = dir,
            });

            var http = factory.CreateClient();
            var signup = await http.PostAsJsonAsync("/auth/signup",
                new { username = "carol", password = "Wonderland1!" });
            signup.EnsureSuccessStatusCode();
            var token = (await signup.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var enroll = (await (await http.PostAsJsonAsync("/auth/2fa/enroll", new { }))
                .Content.ReadFromJsonAsync<EnrollResponseDto>())!;
            await http.PostAsJsonAsync("/auth/2fa/verify", new { code = ComputeCode(enroll.Secret) });

            var json = await File.ReadAllTextAsync(usersPath);
            Assert.Contains("carol", json);
            // The plaintext base32 secret must NOT appear verbatim.
            Assert.DoesNotContain(enroll.Secret, json);
            // Sanity: the encrypted blob is present under SharedSecret.
            Assert.Contains("\"SharedSecret\"", json);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task MultiFirm_DifferentUsernamesHaveIndependentTotp()
    {
        // Design pin (#303): TOTP is keyed by username. Each username
        // maps to exactly one firm in the current user store, so this
        // is naturally per-(user, firm). Two users in different firms
        // enroll independently; one's secret does not validate the
        // other's challenge.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Auth:Users:1:Firm"] = "FIRM02",
        });
        var aliceClient = await factory.CreateAuthedClientAsync("alice", "wonderland");
        var bobClient = await factory.CreateAuthedClientAsync("bob", "wonderland");

        var aliceEnroll = (await (await aliceClient.PostAsJsonAsync("/auth/2fa/enroll", new { }))
            .Content.ReadFromJsonAsync<EnrollResponseDto>())!;
        var bobEnroll = (await (await bobClient.PostAsJsonAsync("/auth/2fa/enroll", new { }))
            .Content.ReadFromJsonAsync<EnrollResponseDto>())!;
        Assert.NotEqual(aliceEnroll.Secret, bobEnroll.Secret);

        await aliceClient.PostAsJsonAsync("/auth/2fa/verify", new { code = ComputeCode(aliceEnroll.Secret) });
        await bobClient.PostAsJsonAsync("/auth/2fa/verify", new { code = ComputeCode(bobEnroll.Secret) });

        // alice's challenge cannot be cleared with bob's code.
        var plain = factory.CreateClient();
        var ch = (await (await plain.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" }))
            .Content.ReadFromJsonAsync<LoginRequiresDto>())!;
        var crossed = await plain.PostAsJsonAsync("/auth/2fa/verify",
            new { code = ComputeCode(bobEnroll.Secret), totpChallengeToken = ch.TotpChallengeToken });
        Assert.Equal(HttpStatusCode.Unauthorized, crossed.StatusCode);
    }
}
