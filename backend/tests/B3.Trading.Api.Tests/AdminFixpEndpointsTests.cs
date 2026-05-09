using System.Net;

namespace B3.Trading.Api.Tests;

public class AdminFixpEndpointsTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public AdminFixpEndpointsTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_Sessions_WithoutAuth_ReturnsUnauthorizedOrNotFound()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/admin/fixp/sessions");
        // When the listener is not enabled the route is not mapped (404).
        // When enabled, an unauthenticated request should return 401.
        Assert.True(
            resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound,
            $"Unexpected status: {resp.StatusCode}");
    }

    [Fact]
    public async Task Get_Sessions_WithUserAuth_ReturnsForbiddenOrNotFound()
    {
        using var client = await _factory.CreateAuthedClientAsync("alice", "wonderland");
        var resp = await client.GetAsync("/admin/fixp/sessions");
        Assert.True(
            resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"Unexpected status: {resp.StatusCode}");
    }

    [Fact]
    public async Task Get_Sessions_WithAdminAuth_Returns200OrNotFound()
    {
        using var client = await _factory.CreateAuthedClientAsync("admin", "wonderland");
        var resp = await client.GetAsync("/admin/fixp/sessions");
        Assert.True(
            resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
            $"Unexpected status: {resp.StatusCode}");
    }
}
