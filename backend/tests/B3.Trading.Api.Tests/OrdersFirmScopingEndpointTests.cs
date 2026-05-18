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

    [Fact]
    public async Task PutModify_CrossFirm_SameOwner_ReturnsNotFound()
    {
        // PR #316 P1. A FIRM02 token must not be able to modify a
        // FIRM01 order even when the JWT sub is the same end-client
        // and the caller happens to know the FIRM01 ClOrdId.
        // Same NotFound posture as cross-owner / unknown id —
        // existence is not leaked across the firm boundary.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());

        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var book = factory.Services.GetRequiredService<WorkingOrderBook>();
        var owner = registry.Register(TestAppFactory.TestUser);

        book.TryAdd(new Order(201UL, owner, "PETR4", 9001UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: "FIRM01"));
        book.TryAdd(new Order(202UL, owner, "PETR4", 9002UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: "FIRM02"));

        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        var client = factory.CreateClient();

        // FIRM02 token attacking FIRM01's order id.
        var (firm2Token, _) = issuer.Issue(TestAppFactory.TestUser, "user", "FIRM02");
        var crossFirm = new HttpRequestMessage(HttpMethod.Put, "/orders/201")
        {
            Content = JsonContent.Create(new { Quantity = 200, Price = 30m }),
        };
        crossFirm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", firm2Token);
        var crossResp = await client.SendAsync(crossFirm);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, crossResp.StatusCode);

        // Sanity: a completely unknown id from the same FIRM02
        // session returns the same NotFound — proving cross-firm
        // is indistinguishable from non-existence.
        var unknown = new HttpRequestMessage(HttpMethod.Put, "/orders/9999999")
        {
            Content = JsonContent.Create(new { Quantity = 200, Price = 30m }),
        };
        unknown.Headers.Authorization = new AuthenticationHeaderValue("Bearer", firm2Token);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.SendAsync(unknown)).StatusCode);

        // FIRM01 happy-path against its own order is unaffected —
        // it can still reach the order through the modify pipeline
        // (the modify itself will go further, but here we just
        // assert we don't get a NotFound from the firm check).
        var (firm1Token, _) = issuer.Issue(TestAppFactory.TestUser, "user", "FIRM01");
        var sameFirm = new HttpRequestMessage(HttpMethod.Put, "/orders/201")
        {
            Content = JsonContent.Create(new { Quantity = 200, Price = 30m }),
        };
        sameFirm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", firm1Token);
        var sameResp = await client.SendAsync(sameFirm);
        Assert.NotEqual(System.Net.HttpStatusCode.NotFound, sameResp.StatusCode);
    }

    [Fact]
    public async Task DeleteCancel_CrossFirm_SameOwner_ReturnsNotFound()
    {
        // PR #316 P1. Mirror of the modify test for DELETE /orders.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());

        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var book = factory.Services.GetRequiredService<WorkingOrderBook>();
        var owner = registry.Register(TestAppFactory.TestUser);

        book.TryAdd(new Order(301UL, owner, "PETR4", 9001UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: "FIRM01"));
        book.TryAdd(new Order(302UL, owner, "PETR4", 9002UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId: "FIRM02"));

        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        var client = factory.CreateClient();

        var (firm2Token, _) = issuer.Issue(TestAppFactory.TestUser, "user", "FIRM02");

        // FIRM02 cancelling FIRM01's order → NotFound.
        var crossFirm = new HttpRequestMessage(HttpMethod.Delete, "/orders/301");
        crossFirm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", firm2Token);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.SendAsync(crossFirm)).StatusCode);

        // Same NotFound for a non-existent id — indistinguishable.
        var unknown = new HttpRequestMessage(HttpMethod.Delete, "/orders/9999999");
        unknown.Headers.Authorization = new AuthenticationHeaderValue("Bearer", firm2Token);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.SendAsync(unknown)).StatusCode);

        // FIRM01 path unaffected: the cancel proceeds through the
        // service (Accepted / NoContent) for its own order.
        var (firm1Token, _) = issuer.Issue(TestAppFactory.TestUser, "user", "FIRM01");
        var sameFirm = new HttpRequestMessage(HttpMethod.Delete, "/orders/301");
        sameFirm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", firm1Token);
        var sameResp = await client.SendAsync(sameFirm);
        Assert.NotEqual(System.Net.HttpStatusCode.NotFound, sameResp.StatusCode);
    }
}
