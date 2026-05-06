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
            new SignupRequest(username, "wonderland-1"));
        first.EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest(username, "wonderland-2"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Signup_CollidesWithEnvSeeded_Returns409()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest("alice", "wonderland-1"));
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
            new SignupRequest("bad name", "wonderland-1"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Signup_SeedsDefaultPositions_ForNewUser()
    {
        using var client = _factory.CreateClient();
        var username = FreshUsername();
        var signup = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest(username, "wonderland-1"));
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

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage resp)
    {
        var doc = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return doc.TryGetProperty("error", out var err) ? err.GetString() : null;
    }

    [Fact]
    public async Task Signup_ShortPassword_Returns400_WithPolicyError()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest(FreshUsername(), "abc1"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var err = await ReadErrorAsync(resp);
        Assert.NotNull(err);
        Assert.Contains("policy", err!);
        Assert.Contains("length", err);
    }

    [Fact]
    public async Task Signup_NoDigit_Returns400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest(FreshUsername(), "wonderland"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var err = await ReadErrorAsync(resp);
        Assert.Contains("digit", err!);
    }

    [Fact]
    public async Task Signup_NoLetter_Returns400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest(FreshUsername(), "12345678"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var err = await ReadErrorAsync(resp);
        Assert.Contains("letter", err!);
    }

    [Fact]
    public async Task Signup_ReservedExactName_Root_Returns409()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest("root", "wonderland-1"));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var err = await ReadErrorAsync(resp);
        Assert.Equal("username is reserved", err);
    }

    [Fact]
    public async Task Signup_ReservedExactName_CaseInsensitive_Returns409()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest("SyStEm", "wonderland-1"));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var err = await ReadErrorAsync(resp);
        Assert.Equal("username is reserved", err);
    }

    [Fact]
    public async Task Signup_ReservedPrefix_BotDash_Returns409()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest("bot-rogueX", "wonderland-1"));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var err = await ReadErrorAsync(resp);
        Assert.Equal("username is reserved", err);
    }

    [Fact]
    public async Task Signup_NonPrefixSubstring_IsAllowed()
    {
        using var client = _factory.CreateClient();
        // "fooadmin" contains "admin" but is not an exact match nor a prefix
        // hit; should pass policy/reserved and create a fresh account.
        var resp = await client.PostAsJsonAsync("/auth/signup",
            new SignupRequest("fooadmin" + Guid.NewGuid().ToString("N")[..6], "wonderland-1"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task Login_AfterPolicyTightened_StillWorks_ForEnvSeededUser()
    {
        // Slice 1 design: policy is signup-only. Env-seeded "alice/wonderland"
        // would fail RequireDigit if policy were re-checked at login. This
        // test pins the decision so a future refactor sharing validation
        // between login/signup trips immediately.
        using var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/auth/login",
            new LoginRequest("alice", "wonderland"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }
}
