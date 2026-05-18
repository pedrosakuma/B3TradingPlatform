using System.Net.Http.Headers;
using System.Net.Http.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// PR #316 P1. GET /orders must be firm-scoped so a JWT sub registered
/// under two firms does not leak the other firm's orders.
/// </summary>
public class OrdersFirmScopingEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public OrdersFirmScopingEndpointTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task GetOrders_ScopedByFirm_DoesNotLeakAcrossFirms()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());

        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var book = factory.Services.GetRequiredService<WorkingOrderBook>();
        var owner = registry.Register(TestAppFactory.TestUser);

        book.TryAdd(new Order(101UL, owner, "PETR4", 9001UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: "FIRM01"));
        book.TryAdd(new Order(102UL, owner, "PETR4", 9002UL, OrderSide.Buy, OrderType.Limit, 200, 31m, firmId: "FIRM02"));

        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        var client = factory.CreateClient();

        var (t1, _) = issuer.Issue(TestAppFactory.TestUser, "user", "FIRM01");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t1);
        var firm1 = await client.GetFromJsonAsync<List<OrderDto>>("/orders/");
        Assert.NotNull(firm1);
        Assert.Single(firm1!);
        Assert.Equal("101", firm1![0].ClOrdId);

        var (t2, _) = issuer.Issue(TestAppFactory.TestUser, "user", "FIRM02");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t2);
        var firm2 = await client.GetFromJsonAsync<List<OrderDto>>("/orders/");
        Assert.NotNull(firm2);
        Assert.Single(firm2!);
        Assert.Equal("102", firm2![0].ClOrdId);
    }
}
