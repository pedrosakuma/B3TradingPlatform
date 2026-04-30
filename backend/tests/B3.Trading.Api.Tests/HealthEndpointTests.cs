using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace B3.Trading.Api.Tests;

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_Returns_Ok()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Root_Returns_ServiceMetadata()
    {
        using var client = _factory.CreateClient();
        var body = await client.GetStringAsync("/");
        Assert.Contains("B3TradingPlatform", body);
    }
}
