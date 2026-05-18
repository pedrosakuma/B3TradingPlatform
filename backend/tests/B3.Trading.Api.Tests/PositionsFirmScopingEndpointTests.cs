using System.Net.Http.Headers;
using System.Net.Http.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// PR #316 P1. GET /positions must be firm-scoped so a JWT sub registered
/// under two firms does not leak the other firm's positions.
/// </summary>
public class PositionsFirmScopingEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public PositionsFirmScopingEndpointTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task GetPositions_ScopedByFirm_DoesNotLeakAcrossFirms()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());

        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var positions = factory.Services.GetRequiredService<PositionKeeper>();
        var owner = registry.Register(TestAppFactory.TestUser);

        positions.ApplyFill("FIRM01", owner, "PETR4", OrderSide.Buy, 100, 30m);
        positions.ApplyFill("FIRM02", owner, "VALE3", OrderSide.Buy, 50, 60m);

        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        var client = factory.CreateClient();

        var (t1, _) = issuer.Issue(TestAppFactory.TestUser, "user", "FIRM01");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t1);
        var firm1 = await client.GetFromJsonAsync<List<PositionDto>>("/positions");
        Assert.NotNull(firm1);
        Assert.Single(firm1!);
        Assert.Equal("PETR4", firm1![0].Symbol);

        var (t2, _) = issuer.Issue(TestAppFactory.TestUser, "user", "FIRM02");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t2);
        var firm2 = await client.GetFromJsonAsync<List<PositionDto>>("/positions");
        Assert.NotNull(firm2);
        Assert.Single(firm2!);
        Assert.Equal("VALE3", firm2![0].Symbol);
    }
}
