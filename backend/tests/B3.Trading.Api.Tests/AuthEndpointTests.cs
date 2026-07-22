using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using B3.Trading.Api.Auth;

namespace B3.Trading.Api.Tests;

public class AuthEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public AuthEndpointTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsToken()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(TestAppFactory.TestUser, TestAppFactory.TestPassword));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body!.Token));
    }

    [Fact]
    public async Task Login_WithBadPassword_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(TestAppFactory.TestUser, "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Orders_WithoutToken_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/orders");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Orders_WithBearer_ReturnsEmpty()
    {
        using var client = await _factory.CreateAuthedClientAsync();
        var resp = await client.GetAsync("/api/orders");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task SubmitOrder_AndCancel_AsOwner_Succeeds()
    {
        using var client = await _factory.CreateAuthedClientAsync();
        var submit = await client.PostAsJsonAsync("/api/orders",
            new SubmitOrderRequest("PETR4", 4321UL, "Buy", "Limit", 100, 30m));
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);
        var body = await submit.Content.ReadFromJsonAsync<SubmittedOrderResponse>();

        var cancel = await client.DeleteAsync($"/api/orders/{body!.ClOrdId}");
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
    }

    [Fact]
    public async Task Cancel_AcrossTenants_Returns404()
    {
        // Alice submits; Bob attempts to cancel.
        using var aliceClient = await _factory.CreateAuthedClientAsync();
        var submit = await aliceClient.PostAsJsonAsync("/api/orders",
            new SubmitOrderRequest("PETR4", 4321UL, "Sell", "Limit", 50, 31m));
        var body = await submit.Content.ReadFromJsonAsync<SubmittedOrderResponse>();

        using var bobClient = await _factory.CreateAuthedClientAsync("bob", TestAppFactory.TestPassword);
        var cancel = await bobClient.DeleteAsync($"/api/orders/{body!.ClOrdId}");
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
    }

    private sealed record SubmittedOrderResponse(string ClOrdId);
}
