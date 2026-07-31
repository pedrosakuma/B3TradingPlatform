using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using B3.Trading.SampleBot;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace B3.Trading.SampleBot.Tests;

public sealed class SampleBotAuthProviderTests
{
    [Fact]
    public async Task LocalPasswordMode_ReturnsInternalJwt()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/auth/login", request.RequestUri!.AbsolutePath);
            return Task.FromResult(StubHttpMessageHandler.Json(
                HttpStatusCode.OK,
                """
                {"token":"internal-jwt","expiresAt":"2026-07-24T04:10:00Z"}
                """));
        });

        var provider = CreateProvider(
            new SampleBotOptions
            {
                BaseUrl = "https://trading.local",
                Auth = new SampleBotAuthOptions
                {
                    Mode = SampleBotAuthMode.LocalPassword,
                    Username = "alice",
                    Password = "wonderland",
                },
            },
            handler);

        var session = await provider.AuthenticateAsync(CancellationToken.None);

        Assert.Equal("internal-jwt", session.Token);
        Assert.Equal(DateTimeOffset.Parse("2026-07-24T04:10:00Z"), session.ExpiresAt);
    }

    [Fact]
    public async Task LocalPasswordMode_Interactive2Fa_ThrowsExplicitException()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(StubHttpMessageHandler.Json(
                HttpStatusCode.OK,
                """
                {"requires2fa":true,"challengeToken":"challenge-1","factors":["totp","webauthn"],"totpChallengeToken":"challenge-1"}
                """)));

        var provider = CreateProvider(
            new SampleBotOptions
            {
                BaseUrl = "https://trading.local",
                Auth = new SampleBotAuthOptions
                {
                    Mode = SampleBotAuthMode.LocalPassword,
                    Username = "alice",
                    Password = "wonderland",
                },
            },
            handler);

        var ex = await Assert.ThrowsAsync<SampleBotInteractiveAuthRequiredException>(() => provider.AuthenticateAsync(CancellationToken.None));
        Assert.Contains("interactive authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("totp", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExternalExchangeMode_UsesBearerHeader_AndReturnsInternalJwt()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/auth/exchange", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("external-token", request.Headers.Authorization?.Parameter);
            Assert.Null(request.Content);
            return Task.FromResult(StubHttpMessageHandler.Json(
                HttpStatusCode.OK,
                """
                {"token":"internal-jwt","expiresAt":"2026-07-24T04:10:00Z"}
                """));
        });

        var provider = CreateProvider(
            new SampleBotOptions
            {
                BaseUrl = "https://trading.local",
                Auth = new SampleBotAuthOptions
                {
                    Mode = SampleBotAuthMode.ExternalExchange,
                    ExternalAccessToken = "external-token",
                },
            },
            handler);

        var session = await provider.AuthenticateAsync(CancellationToken.None);

        Assert.Equal("internal-jwt", session.Token);
    }

    [Fact]
    public async Task InternalTokenMode_ReadsJwtExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var token = IssueJwt(now.AddMinutes(5));
        var provider = CreateProvider(
            new SampleBotOptions
            {
                BaseUrl = "https://trading.local",
                Auth = new SampleBotAuthOptions
                {
                    Mode = SampleBotAuthMode.InternalToken,
                    InternalTradingToken = token,
                },
            },
            new StubHttpMessageHandler((_, _) => throw new InvalidOperationException("HTTP should not be used.")));

        var session = await provider.AuthenticateAsync(CancellationToken.None);

        Assert.Equal(token, session.Token);
        Assert.NotNull(session.ExpiresAt);
        Assert.True(session.ExpiresAt > now);
    }

    private static SampleBotAuthProvider CreateProvider(SampleBotOptions options, HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl) };
        return new SampleBotAuthProvider(httpClient, Options.Create(options), NullLogger<SampleBotAuthProvider>.Instance);
    }

    private static string IssueJwt(DateTimeOffset expiresAt)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes("samplebot-test-signing-key-must-be-32b")),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            claims: [new Claim(JwtRegisteredClaimNames.Sub, "alice")],
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
