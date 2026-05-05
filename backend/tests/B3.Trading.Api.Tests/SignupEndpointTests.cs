using System.Net;
using System.Net.Http.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

public class SignupEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public SignupEndpointTests(TestAppFactory factory) => _factory = factory;

    private static string FreshUsername() => "u" + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task Signup_HappyPath_Returns201_WithToken()
    {
        using var client = _factory.CreateClient();
        var username = FreshUsername();
        var resp = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest(username, "wonderland-1"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body!.Token));
        Assert.True(body.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Signup_ThenLogin_Succeeds()
    {
        using var client = _factory.CreateClient();
        var username = FreshUsername();
        var signup = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest(username, "secret-pass-9"));
        signup.EnsureSuccessStatusCode();

        var login = await client.PostAsJsonAsync("/auth/login",
            new LoginRequest(username, "secret-pass-9"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Signup_DuplicateUsername_Returns409()
    {
        using var client = _factory.CreateClient();
        var username = FreshUsername();
        var first = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest(username, "pw1"));
        first.EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest(username, "pw2"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Signup_CollidesWithEnvSeeded_Returns409()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest("alice", "anything"));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Signup_BlankCredentials_Returns400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest("", "pw"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Signup_InvalidUsernameChars_Returns400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest("bad name", "pw"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Signup_SeedsDefaultPositions_ForNewUser()
    {
        using var client = _factory.CreateClient();
        var username = FreshUsername();
        var signup = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest(username, "pw"));
        signup.EnsureSuccessStatusCode();

        var keeper = _factory.Services.GetRequiredService<PositionKeeper>();
        var owner = new EndClientId(username);
        var positions = keeper.ForEndClient(owner);
        var bySymbol = positions.ToDictionary(p => p.Symbol, p => p);
        Assert.True(bySymbol.ContainsKey("PETR4"), "PETR4 not seeded");
        Assert.True(bySymbol.ContainsKey("VALE3"), "VALE3 not seeded");
        Assert.Equal(2000, bySymbol["PETR4"].NetQuantity);
        Assert.Equal(2000, bySymbol["VALE3"].NetQuantity);
    }
}
