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
    private static string ComputeCode(string base32, int stepOffset = 0)
    {
        var totp = new OtpNet.Totp(Base32Encoding.ToBytes(base32));
        // OtpNet computes against the supplied DateTime (UTC). Offset
        // by N * 30s to produce a code one or more RFC 6238 steps in
        // the future — used by tests that need a distinct step from a
        // prior verify (the server now rejects same-step replays).
        var when = DateTime.UtcNow.AddSeconds(stepOffset * 30.0);
        return totp.ComputeTotp(when);
    }

    private sealed record EnrollResponseDto(
        string Secret,
        string OtpauthUri,
        List<string> RecoveryCodes,
        string? TotpChallengeToken = null);
    private sealed record LoginRequiresDto(bool Requires2fa, string TotpChallengeToken);
    private sealed record LoginEnrollmentRequiredDto(bool Requires2faEnrollment, string EnrollmentToken);
    private sealed record TotpStatusDto(bool Enrolled);

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
    public async Task MandatoryEnrollment_CompletesLogin_AndChallengesAreOneTime()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Auth:Users:0:Require2FA"] = "true",
        });
        var http = factory.CreateClient();

        var login = await http.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var required = await login.Content.ReadFromJsonAsync<LoginEnrollmentRequiredDto>();
        Assert.True(required!.Requires2faEnrollment);

        var enrollResponse = await http.PostAsJsonAsync("/auth/2fa/enroll",
            new { enrollmentToken = required.EnrollmentToken });
        Assert.Equal(HttpStatusCode.OK, enrollResponse.StatusCode);
        var enroll = await enrollResponse.Content.ReadFromJsonAsync<EnrollResponseDto>();
        Assert.NotNull(enroll);
        Assert.False(string.IsNullOrEmpty(enroll!.TotpChallengeToken));

        var replayEnroll = await http.PostAsJsonAsync("/auth/2fa/enroll",
            new { enrollmentToken = required.EnrollmentToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replayEnroll.StatusCode);

        var verify = await http.PostAsJsonAsync("/auth/2fa/verify", new
        {
            code = ComputeCode(enroll.Secret),
            totpChallengeToken = enroll.TotpChallengeToken,
        });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var session = await verify.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrEmpty(session!.Token));

        var replayVerify = await http.PostAsJsonAsync("/auth/2fa/verify", new
        {
            code = ComputeCode(enroll.Secret, stepOffset: 1),
            totpChallengeToken = enroll.TotpChallengeToken,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, replayVerify.StatusCode);
    }

    [Fact]
    public async Task MandatoryEnrollmentChallenge_IsBoundToItsUser()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Auth:Users:0:Require2FA"] = "true",
            ["Trading:Auth:Users:1:Require2FA"] = "true",
        });
        var http = factory.CreateClient();

        var aliceRequired = (await (await http.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" }))
            .Content.ReadFromJsonAsync<LoginEnrollmentRequiredDto>())!;
        var aliceEnroll = (await (await http.PostAsJsonAsync("/auth/2fa/enroll",
            new { enrollmentToken = aliceRequired.EnrollmentToken }))
            .Content.ReadFromJsonAsync<EnrollResponseDto>())!;

        var bobRequired = (await (await http.PostAsJsonAsync("/auth/login",
            new { username = "bob", password = "wonderland" }))
            .Content.ReadFromJsonAsync<LoginEnrollmentRequiredDto>())!;
        var bobEnroll = (await (await http.PostAsJsonAsync("/auth/2fa/enroll",
            new { enrollmentToken = bobRequired.EnrollmentToken }))
            .Content.ReadFromJsonAsync<EnrollResponseDto>())!;

        var aliceWindow = new HashSet<string>(
            Enumerable.Range(-1, 3).Select(offset => ComputeCode(aliceEnroll.Secret, offset)));
        var bobCode = Enumerable.Range(-1, 3)
            .Select(offset => ComputeCode(bobEnroll.Secret, offset))
            .First(code => !aliceWindow.Contains(code));
        var crossed = await http.PostAsJsonAsync("/auth/2fa/verify", new
        {
            code = bobCode,
            totpChallengeToken = aliceEnroll.TotpChallengeToken,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, crossed.StatusCode);

        var bobVerified = await http.PostAsJsonAsync("/auth/2fa/verify", new
        {
            code = bobCode,
            totpChallengeToken = bobEnroll.TotpChallengeToken,
        });
        Assert.Equal(HttpStatusCode.OK, bobVerified.StatusCode);
    }

    [Fact]
    public async Task MandatoryEnrollmentChallenge_Expires()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Auth:Users:0:Require2FA"] = "true",
            ["Trading:Auth:Totp:ChallengeTokenTtl"] = "00:00:01",
        });
        var http = factory.CreateClient();
        var required = (await (await http.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" }))
            .Content.ReadFromJsonAsync<LoginEnrollmentRequiredDto>())!;
        var enroll = (await (await http.PostAsJsonAsync("/auth/2fa/enroll",
            new { enrollmentToken = required.EnrollmentToken }))
            .Content.ReadFromJsonAsync<EnrollResponseDto>())!;
        await Task.Delay(1500);
        var expired = await http.PostAsJsonAsync("/auth/2fa/verify", new
        {
            code = ComputeCode(enroll.Secret),
            totpChallengeToken = enroll.TotpChallengeToken,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task EnrolledUser_CanRenewSession_WithPasswordThenTotp()
    {
        await using var factory = new TestAppFactory();
        var authed = await factory.CreateAuthedClientAsync();
        var initialToken = authed.DefaultRequestHeaders.Authorization!.Parameter;
        var enroll = (await (await authed.PostAsJsonAsync("/auth/2fa/enroll", new { }))
            .Content.ReadFromJsonAsync<EnrollResponseDto>())!;
        await authed.PostAsJsonAsync("/auth/2fa/verify", new { code = ComputeCode(enroll.Secret) });

        var renewalClient = factory.CreateClient();
        var passwordResponse = await renewalClient.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" });
        var challenge = await passwordResponse.Content.ReadFromJsonAsync<LoginRequiresDto>();
        Assert.True(challenge!.Requires2fa);

        var renewedResponse = await renewalClient.PostAsJsonAsync("/auth/2fa/verify", new
        {
            code = ComputeCode(enroll.Secret, stepOffset: 1),
            totpChallengeToken = challenge.TotpChallengeToken,
        });
        Assert.Equal(HttpStatusCode.OK, renewedResponse.StatusCode);
        var renewed = await renewedResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrEmpty(renewed!.Token));
        Assert.NotEqual(initialToken, renewed.Token);
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
        // Use step offset = 1 to advance past the step consumed during
        // enrollment confirm (server seeds LastUsedTimeStep there).
        var freshCode = ComputeCode(enroll.Secret, stepOffset: 1);
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

        // Correct code: disables. Offset by one step so we don't trip
        // the same-window replay guard against the enrollment-confirm
        // step.
        var ok = await http.PostAsJsonAsync("/auth/2fa/disable", new { code = ComputeCode(enroll.Secret, stepOffset: 1) });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // Re-enroll works (no 409 because disabled cleared the prior state).
        var reenroll = await http.PostAsJsonAsync("/auth/2fa/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, reenroll.StatusCode);
    }

    [Fact]
    public async Task EnrollmentConfirm_InvalidCode_Rejected_ThenValidCodeStillEnrolls()
    {
        await using var factory = new TestAppFactory();
        var http = await factory.CreateAuthedClientAsync();

        var enroll = (await (await http.PostAsJsonAsync("/auth/2fa/enroll", new { }))
            .Content.ReadFromJsonAsync<EnrollResponseDto>())!;

        var invalid = await http.PostAsJsonAsync("/auth/2fa/verify", new { code = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);

        var valid = await http.PostAsJsonAsync("/auth/2fa/verify", new { code = ComputeCode(enroll.Secret) });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);

        var status = await http.GetFromJsonAsync<TotpStatusDto>("/auth/2fa/status");
        Assert.NotNull(status);
        Assert.True(status!.Enrolled);
    }

    [Fact]
    public async Task Disable_AcceptsRecoveryCode()
    {
        await using var factory = new TestAppFactory();
        var http = await factory.CreateAuthedClientAsync();

        var enroll = (await (await http.PostAsJsonAsync("/auth/2fa/enroll", new { }))
            .Content.ReadFromJsonAsync<EnrollResponseDto>())!;
        await http.PostAsJsonAsync("/auth/2fa/verify", new { code = ComputeCode(enroll.Secret) });

        var disable = await http.PostAsJsonAsync("/auth/2fa/disable", new { code = enroll.RecoveryCodes[0] });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        var status = await http.GetFromJsonAsync<TotpStatusDto>("/auth/2fa/status");
        Assert.NotNull(status);
        Assert.False(status!.Enrolled);
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
    public async Task Status_TracksEnrollAndDisableJourney()
    {
        await using var factory = new TestAppFactory();
        var http = await factory.CreateAuthedClientAsync();

        var initial = await http.GetFromJsonAsync<TotpStatusDto>("/auth/2fa/status");
        Assert.NotNull(initial);
        Assert.False(initial!.Enrolled);

        var enroll = (await (await http.PostAsJsonAsync("/auth/2fa/enroll", new { }))
            .Content.ReadFromJsonAsync<EnrollResponseDto>())!;
        await http.PostAsJsonAsync("/auth/2fa/verify", new { code = ComputeCode(enroll.Secret) });

        var enrolled = await http.GetFromJsonAsync<TotpStatusDto>("/auth/2fa/status");
        Assert.NotNull(enrolled);
        Assert.True(enrolled!.Enrolled);

        var disable = await http.PostAsJsonAsync("/auth/2fa/disable", new { code = ComputeCode(enroll.Secret, stepOffset: 1) });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        var disabled = await http.GetFromJsonAsync<TotpStatusDto>("/auth/2fa/status");
        Assert.NotNull(disabled);
        Assert.False(disabled!.Enrolled);
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
    public async Task TotpCode_ReusedWithinSameStep_RejectedAndIncrementsLockout()
    {
        // Same valid TOTP code presented twice through two distinct
        // challenge tokens must be accepted exactly once. Second use
        // looks like an invalid-code attempt (401 + lockout tick).
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
        // Offset by 1 so the test code is NOT the one consumed during
        // enrollment confirm (which already seeded LastUsedTimeStep).
        var code = ComputeCode(enroll.Secret, stepOffset: 1);

        // First login: code accepted, JWT issued.
        var login1 = (await (await plain.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" }))
            .Content.ReadFromJsonAsync<LoginRequiresDto>())!;
        var ok1 = await plain.PostAsJsonAsync("/auth/2fa/verify",
            new { code, totpChallengeToken = login1.TotpChallengeToken });
        Assert.Equal(HttpStatusCode.OK, ok1.StatusCode);
        Assert.False(string.IsNullOrEmpty(
            (await ok1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()));

        // Second login with SAME code via a fresh challenge: rejected
        // with the same shape as a wrong code (401 + {"error":"invalid code"}).
        var login2 = (await (await plain.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" }))
            .Content.ReadFromJsonAsync<LoginRequiresDto>())!;
        var bad = await plain.PostAsJsonAsync("/auth/2fa/verify",
            new { code, totpChallengeToken = login2.TotpChallengeToken });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        var badBody = await bad.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid code", badBody.GetProperty("error").GetString());

        // Lockout counter ticked: 4 more wrong attempts should trip 429.
        for (var i = 0; i < 4; i++)
        {
            var login = (await (await plain.PostAsJsonAsync("/auth/login",
                new { username = "alice", password = "wonderland" }))
                .Content.ReadFromJsonAsync<LoginRequiresDto>())!;
            await plain.PostAsJsonAsync("/auth/2fa/verify",
                new { code = "000000", totpChallengeToken = login.TotpChallengeToken });
        }
        var lockedLogin = (await (await plain.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" }))
            .Content.ReadFromJsonAsync<LoginRequiresDto>())!;
        var locked = await plain.PostAsJsonAsync("/auth/2fa/verify",
            new { code = "000000", totpChallengeToken = lockedLogin.TotpChallengeToken });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task RecoveryCode_ConcurrentVerifies_OnlyOneSucceeds()
    {
        // 10 racing verify requests presenting the SAME recovery code
        // must result in exactly one 200 — the store consumes
        // atomically. The 9 race-losers must NOT tick the TOTP lockout
        // counter (they presented a real code; only the race lost), so
        // even with the default MaxFailedAttempts=5 a 10-way race must
        // leave the account fully unlocked.
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
        var recovery = enroll.RecoveryCodes[0];

        const int N = 10;
        // Mint N distinct challenge tokens via N login calls so each
        // verify request is independently authorized.
        var challenges = new string[N];
        var clients = new HttpClient[N];
        for (var i = 0; i < N; i++)
        {
            clients[i] = factory.CreateClient();
            var login = (await (await clients[i].PostAsJsonAsync("/auth/login",
                new { username = "alice", password = "wonderland" }))
                .Content.ReadFromJsonAsync<LoginRequiresDto>())!;
            challenges[i] = login.TotpChallengeToken;
        }

        using var barrier = new Barrier(N);
        var responses = await Task.WhenAll(Enumerable.Range(0, N).Select(i => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await clients[i].PostAsJsonAsync("/auth/2fa/verify",
                new { code = recovery, totpChallengeToken = challenges[i] });
        })));

        var successes = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var failures = responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized);
        Assert.Equal(1, successes);
        Assert.Equal(N - 1, failures);

        // Lockout counter must still be 0: walk to MaxFailedAttempts
        // truly-wrong codes and observe the FIRST 5 return 401 then
        // the 6th trips 429 (the 5th call ticks the counter to 5 and
        // sets LockedUntil, but the IsLocked check runs at the TOP of
        // the next request). If race-losers had ticked the counter,
        // the very first wrong code below would already be 429.
        var plain = factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            var login = (await (await plain.PostAsJsonAsync("/auth/login",
                new { username = "alice", password = "wonderland" }))
                .Content.ReadFromJsonAsync<LoginRequiresDto>())!;
            var wrong = await plain.PostAsJsonAsync("/auth/2fa/verify",
                new { code = "wrong-recovery-code-xyz", totpChallengeToken = login.TotpChallengeToken });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        var sixthLogin = (await (await plain.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" }))
            .Content.ReadFromJsonAsync<LoginRequiresDto>())!;
        var locked = await plain.PostAsJsonAsync("/auth/2fa/verify",
            new { code = "wrong-recovery-code-xyz", totpChallengeToken = sixthLogin.TotpChallengeToken });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);

        foreach (var c in clients) c.Dispose();
        plain.Dispose();
    }

    [Fact]
    public async Task RecoveryCode_ReplayAfterSuccess_RejectedButDoesNotIncrementLockout()
    {
        // A client that successfully used a recovery code and then —
        // due to retry, page reload, or replay attempt — submits the
        // same code again must get 401 (same as wrong) but the
        // lockout counter must NOT tick. Otherwise a single replayed
        // success could chew up a sizeable chunk of the lockout
        // budget for no security gain.
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
        var recovery = enroll.RecoveryCodes[0];

        var plain = factory.CreateClient();
        var login1 = (await (await plain.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" }))
            .Content.ReadFromJsonAsync<LoginRequiresDto>())!;
        var ok = await plain.PostAsJsonAsync("/auth/2fa/verify",
            new { code = recovery, totpChallengeToken = login1.TotpChallengeToken });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // Replay the SAME recovery code (simulating the 5-minutes-later
        // retry the spec calls out — wall-clock here is irrelevant
        // because consumed status is permanent within the user record).
        for (var i = 0; i < 6; i++)
        {
            var loginR = (await (await plain.PostAsJsonAsync("/auth/login",
                new { username = "alice", password = "wonderland" }))
                .Content.ReadFromJsonAsync<LoginRequiresDto>())!;
            var replay = await plain.PostAsJsonAsync("/auth/2fa/verify",
                new { code = recovery, totpChallengeToken = loginR.TotpChallengeToken });
            // Always 401 (no info leak vs wrong code), never 429 —
            // proves lockout counter never moved off 0.
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        }

        plain.Dispose();
    }

    [Fact]
    public async Task RecoveryCode_TrulyWrongCodes_StillEngageLockout()
    {
        // Sanity check that the AlreadyConsumed silent-path didn't
        // accidentally disarm the wrong-code path: 5 genuinely wrong
        // codes (never enrolled for this user) MUST lock the account.
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
        // First 5 wrong → 401 (the 5th call ticks the counter to 5 and
        // engages the lock), 6th request hits the IsLocked check → 429.
        for (var i = 0; i < 5; i++)
        {
            var login = (await (await plain.PostAsJsonAsync("/auth/login",
                new { username = "alice", password = "wonderland" }))
                .Content.ReadFromJsonAsync<LoginRequiresDto>())!;
            var wrong = await plain.PostAsJsonAsync("/auth/2fa/verify",
                new { code = $"never-issued-{i}", totpChallengeToken = login.TotpChallengeToken });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        var lastLogin = (await (await plain.PostAsJsonAsync("/auth/login",
            new { username = "alice", password = "wonderland" }))
            .Content.ReadFromJsonAsync<LoginRequiresDto>())!;
        var locked = await plain.PostAsJsonAsync("/auth/2fa/verify",
            new { code = "never-issued-final", totpChallengeToken = lastLogin.TotpChallengeToken });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);

        plain.Dispose();
    }

    [Fact]
    public void PendingTotpEnrollmentStore_PutPurgesExpiredEntries()
    {
        // Opportunistic sweep: pre-seed an entry with an old
        // CreatedUtc, then Put a different user — the stale entry
        // must be dropped even though TryConsume was never called for
        // its username.
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var opts = new TestOptionsMonitor<TotpOptions>(new TotpOptions
        {
            PendingEnrollmentTtl = TimeSpan.FromMinutes(5),
        });
        var store = new InMemoryPendingTotpEnrollmentStore(opts, clock);

        store.Put("ghost", new PendingTotpEnrollment(
            Base32Secret: "JBSWY3DPEHPK3PXP",
            RecoveryCodes: Array.Empty<string>(),
            RecoveryCodeHashes: Array.Empty<string>(),
            CreatedAt: clock.GetUtcNow()));

        // Advance past TTL.
        clock.Advance(TimeSpan.FromMinutes(10));

        store.Put("alive", new PendingTotpEnrollment(
            Base32Secret: "JBSWY3DPEHPK3PXP",
            RecoveryCodes: Array.Empty<string>(),
            RecoveryCodeHashes: Array.Empty<string>(),
            CreatedAt: clock.GetUtcNow()));

        // "ghost" must have been purged.
        Assert.False(store.TryConsume("ghost", out _));
        // "alive" still consumable.
        Assert.True(store.TryConsume("alive", out var found));
        Assert.NotNull(found);
    }

    [Fact]
    public void TotpChallengeStore_IsExpiringOneTimeAndUserBound()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var opts = new TestOptionsMonitor<TotpOptions>(new TotpOptions
        {
            ChallengeTokenTtl = TimeSpan.FromMinutes(5),
        });
        var store = new InMemoryTotpChallengeStore(opts, clock);

        var token = store.Issue("alice", TotpChallengeKind.VerifyEnrollment);
        Assert.Equal("alice", store.Peek(token)!.Username);
        Assert.False(store.TryConsume(token, TotpChallengeKind.Verify, out _));
        Assert.True(store.TryConsume(token, TotpChallengeKind.VerifyEnrollment, out var consumed));
        Assert.Equal("alice", consumed!.Username);
        Assert.False(store.TryConsume(token, TotpChallengeKind.VerifyEnrollment, out _));

        var expired = store.Issue("bob", TotpChallengeKind.Verify);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(store.Peek(expired));
        Assert.False(store.TryConsume(expired, TotpChallengeKind.Verify, out _));
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) { _now = start; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) { _now += delta; }
    }

    private sealed class TestOptionsMonitor<T> : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
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
