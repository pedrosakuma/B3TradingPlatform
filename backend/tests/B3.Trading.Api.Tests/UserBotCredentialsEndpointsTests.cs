using System.Net;
using System.Net.Http.Json;
using B3.Trading.Application.UserBots;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// REST integration tests for <c>/api/user-bot-credentials</c>
/// (sub-issue C, RFC §4.5). Covers auth gating, the create-then-list
/// secret-shown-once contract, soft revoke, and cross-user isolation.
/// </summary>
public class UserBotCredentialsEndpointsTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public UserBotCredentialsEndpointsTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Unauthenticated_ReturnsUnauthorized_OnAllVerbs()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/user-bot-credentials")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/user-bot-credentials", new { label = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.DeleteAsync($"/api/user-bot-credentials/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task CreateListRevoke_HappyPath()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var client = await factory.CreateAuthedClientAsync();

        var createResp = await client.PostAsJsonAsync(
            "/api/user-bot-credentials", new CreateUserBotCredentialRequest("morning bot"));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<CreatedUserBotCredentialDto>();
        Assert.NotNull(created);
        Assert.StartsWith("b3t_", created!.PlainSecret);
        Assert.Equal("morning bot", created.Label);
        Assert.Contains(created.CredShortId, created.PlainSecret);

        var list = await client.GetFromJsonAsync<List<UserBotCredentialDto>>("/api/user-bot-credentials");
        Assert.NotNull(list);
        var entry = Assert.Single(list!);
        Assert.Equal(created.Id, entry.Id);
        Assert.Equal(created.CredShortId, entry.CredShortId);
        Assert.Null(entry.RevokedAt);

        var del = await client.DeleteAsync($"/api/user-bot-credentials/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var afterRevoke = await client.GetFromJsonAsync<List<UserBotCredentialDto>>("/api/user-bot-credentials");
        Assert.NotNull(afterRevoke!.Single().RevokedAt);

        // Re-revoke is 404 (registry returns false on the no-op).
        var redel = await client.DeleteAsync($"/api/user-bot-credentials/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, redel.StatusCode);
    }

    [Fact]
    public async Task ListResponse_NeverIncludesSecretFields()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var client = await factory.CreateAuthedClientAsync();

        var createResp = await client.PostAsJsonAsync(
            "/api/user-bot-credentials", new CreateUserBotCredentialRequest("scan bot"));
        var created = await createResp.Content.ReadFromJsonAsync<CreatedUserBotCredentialDto>();

        var raw = await client.GetStringAsync("/api/user-bot-credentials");
        Assert.DoesNotContain("plainSecret", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(created!.PlainSecret, raw);
    }

    [Fact]
    public async Task CrossUser_IsolationIsEnforced()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());

        var alice = await factory.CreateAuthedClientAsync(TestAppFactory.TestUser);
        var bob = await factory.CreateAuthedClientAsync("bob");

        var aliceCreate = await alice.PostAsJsonAsync(
            "/api/user-bot-credentials", new CreateUserBotCredentialRequest("alice's bot"));
        var aliceCred = await aliceCreate.Content.ReadFromJsonAsync<CreatedUserBotCredentialDto>();

        var bobList = await bob.GetFromJsonAsync<List<UserBotCredentialDto>>("/api/user-bot-credentials");
        Assert.Empty(bobList!);

        var bobRevokeAttempt = await bob.DeleteAsync($"/api/user-bot-credentials/{aliceCred!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, bobRevokeAttempt.StatusCode);

        var registry = factory.Services.GetRequiredService<IUserBotCredentialRegistry>();
        Assert.NotNull(await registry.TryAuthenticateAsync(aliceCred.PlainSecret, default));
    }

    [Fact]
    public async Task Create_RejectsBlankAndOversizedLabel()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var client = await factory.CreateAuthedClientAsync();

        var blank = await client.PostAsJsonAsync(
            "/api/user-bot-credentials", new CreateUserBotCredentialRequest("   "));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var oversized = new string('x', UserBotCredentialsEndpoints.MaxLabelLength + 1);
        var big = await client.PostAsJsonAsync(
            "/api/user-bot-credentials", new CreateUserBotCredentialRequest(oversized));
        Assert.Equal(HttpStatusCode.BadRequest, big.StatusCode);
    }

    // ─── Cert↔credential binding (sub-issue #540) ────────────────────────────

    private const string Pin =
        "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public async Task Create_WithPin_EchoesThumbprint_AndListIncludesIt()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var client = await factory.CreateAuthedClientAsync();

        var createResp = await client.PostAsJsonAsync(
            "/api/user-bot-credentials", new CreateUserBotCredentialRequest("pinned bot", Pin));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<CreatedUserBotCredentialDto>();
        Assert.Equal(Pin, created!.BoundCertThumbprint);

        var list = await client.GetFromJsonAsync<List<UserBotCredentialDto>>("/api/user-bot-credentials");
        Assert.Equal(Pin, Assert.Single(list!).BoundCertThumbprint);
    }

    [Fact]
    public async Task Create_WithMalformedPin_ReturnsBadRequest()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync(
            "/api/user-bot-credentials", new CreateUserBotCredentialRequest("bad", "not-a-thumbprint"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SetCertBinding_SetsThenClears()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var client = await factory.CreateAuthedClientAsync();

        var createResp = await client.PostAsJsonAsync(
            "/api/user-bot-credentials", new CreateUserBotCredentialRequest("editable"));
        var created = await createResp.Content.ReadFromJsonAsync<CreatedUserBotCredentialDto>();

        var set = await client.PutAsJsonAsync(
            $"/api/user-bot-credentials/{created!.Id}/cert-binding", new SetCertBindingRequest(Pin));
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        var afterSet = await client.GetFromJsonAsync<List<UserBotCredentialDto>>("/api/user-bot-credentials");
        Assert.Equal(Pin, Assert.Single(afterSet!).BoundCertThumbprint);

        var clear = await client.PutAsJsonAsync(
            $"/api/user-bot-credentials/{created.Id}/cert-binding", new SetCertBindingRequest(null));
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);

        var afterClear = await client.GetFromJsonAsync<List<UserBotCredentialDto>>("/api/user-bot-credentials");
        Assert.Null(Assert.Single(afterClear!).BoundCertThumbprint);
    }

    [Fact]
    public async Task SetCertBinding_MalformedThumbprint_ReturnsBadRequest()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var client = await factory.CreateAuthedClientAsync();

        var createResp = await client.PostAsJsonAsync(
            "/api/user-bot-credentials", new CreateUserBotCredentialRequest("editable"));
        var created = await createResp.Content.ReadFromJsonAsync<CreatedUserBotCredentialDto>();

        var resp = await client.PutAsJsonAsync(
            $"/api/user-bot-credentials/{created!.Id}/cert-binding", new SetCertBindingRequest("zzzz"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SetCertBinding_CrossUser_ReturnsNotFound()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var alice = await factory.CreateAuthedClientAsync(TestAppFactory.TestUser);
        var bob = await factory.CreateAuthedClientAsync("bob");

        var aliceCreate = await alice.PostAsJsonAsync(
            "/api/user-bot-credentials", new CreateUserBotCredentialRequest("alice's bot"));
        var aliceCred = await aliceCreate.Content.ReadFromJsonAsync<CreatedUserBotCredentialDto>();

        var resp = await bob.PutAsJsonAsync(
            $"/api/user-bot-credentials/{aliceCred!.Id}/cert-binding", new SetCertBindingRequest(Pin));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task SetCertBinding_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();
        var resp = await client.PutAsJsonAsync(
            $"/api/user-bot-credentials/{Guid.NewGuid()}/cert-binding", new SetCertBindingRequest(Pin));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
