using System.Net;
using System.Net.Http.Json;
using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.Api.Tests;

public class OrderStaleAdminEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public OrderStaleAdminEndpointTests(TestAppFactory factory) => _factory = factory;

    private static Order SeedWorkingOrder(TestAppFactory factory, ulong clOrdId, string firmId = "TEST")
    {
        var book = (WorkingOrderBook)factory.Services.GetService(typeof(WorkingOrderBook))!;
        var ownership = (OrderOwnershipMap)factory.Services.GetService(typeof(OrderOwnershipMap))!;
        var registry = (EndClientRegistry)factory.Services.GetService(typeof(EndClientRegistry))!;
        var owner = registry.Register("alice");
        var o = new Order(clOrdId, owner, "PETR4", 1UL, OrderSide.Buy, OrderType.Limit, 100, 30m, firmId);
        o.MarkWorking();
        book.TryAdd(o);
        ownership.Register(clOrdId, owner);
        return o;
    }

    [Fact]
    public async Task MarkStale_RequiresAdminRole()
    {
        SeedWorkingOrder(_factory, 9000UL);
        using var user = await _factory.CreateAuthedClientAsync(); // alice
        var resp = await user.PostAsJsonAsync("/admin/firms/TEST/orders/9000/mark-stale", new { reason = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task MarkStale_HappyPath_ReturnsNoContent_AndSetsFlag()
    {
        var order = SeedWorkingOrder(_factory, 9001UL);
        using var admin = await _factory.CreateAuthedClientAsync("admin");

        var resp = await admin.PostAsJsonAsync(
            "/admin/firms/TEST/orders/9001/mark-stale",
            new { reason = "matching restart" });

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.True(order.IsStale);
        Assert.Equal("matching restart", order.StaleReason);
    }

    [Fact]
    public async Task MarkStale_Idempotent_ReturnsNoContent()
    {
        SeedWorkingOrder(_factory, 9002UL);
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        await admin.PostAsJsonAsync("/admin/firms/TEST/orders/9002/mark-stale", new { reason = "x" });

        var resp = await admin.PostAsJsonAsync("/admin/firms/TEST/orders/9002/mark-stale", new { reason = "y" });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task MarkStale_UnknownClOrdId_ReturnsNotFound()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsJsonAsync("/admin/firms/TEST/orders/99999/mark-stale", new { reason = "x" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task MarkStale_WrongFirm_ReturnsNotFound()
    {
        SeedWorkingOrder(_factory, 9003UL, firmId: "TEST");
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsJsonAsync("/admin/firms/OTHER/orders/9003/mark-stale", new { reason = "x" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ClearStale_RemovesFlag()
    {
        var order = SeedWorkingOrder(_factory, 9004UL);
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        await admin.PostAsJsonAsync("/admin/firms/TEST/orders/9004/mark-stale", new { reason = "x" });
        Assert.True(order.IsStale);

        var resp = await admin.PostAsync("/admin/firms/TEST/orders/9004/clear-stale", content: null);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.False(order.IsStale);
    }

    [Fact]
    public async Task DeleteOrder_OnStaleOrder_Returns409()
    {
        var order = SeedWorkingOrder(_factory, 9005UL);
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        await admin.PostAsJsonAsync("/admin/firms/TEST/orders/9005/mark-stale", new { reason = "x" });
        Assert.True(order.IsStale);

        // alice owns the order; she gets 409 trying to cancel.
        using var alice = await _factory.CreateAuthedClientAsync();
        var resp = await alice.DeleteAsync("/orders/9005");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task ModifyOrder_OnStaleOrder_Returns409()
    {
        var order = SeedWorkingOrder(_factory, 9006UL);
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        await admin.PostAsJsonAsync("/admin/firms/TEST/orders/9006/mark-stale", new { reason = "x" });

        using var alice = await _factory.CreateAuthedClientAsync();
        var resp = await alice.PutAsJsonAsync("/orders/9006", new { quantity = 200, price = 30m });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }
}
