using System.Net;
using System.Net.Http.Json;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

public class BalanceEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public BalanceEndpointTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task UnauthenticatedGet_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/balance");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task FreshAccount_ReturnsZero()
    {
        // alice (test user) has not transacted in this isolated factory
        // and isn't seeded in the test config — balance starts at zero.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var client = await factory.CreateAuthedClientAsync();

        var resp = await client.GetAsync("/api/balance");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<BalanceDto>();

        Assert.NotNull(body);
        Assert.Equal(0m, body!.Available);
        Assert.False(body.SelfDepositEnabled);
    }

    [Fact]
    public async Task AfterFill_ReflectsLedger()
    {
        // Drive the ledger directly via the singleton CashLedger and
        // confirm GET /api/balance reads the same source of truth.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var client = await factory.CreateAuthedClientAsync();

        var ledger = factory.Services.GetRequiredService<CashLedger>();
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var owner = registry.Register(TestAppFactory.TestUser);
        ledger.ApplyFill(owner, OrderSide.Sell, 100, 50m); // +5000

        var body = await client.GetFromJsonAsync<BalanceDto>("/api/balance");
        Assert.Equal(5000m, body!.Available);
    }

    [Fact]
    public async Task SeededOpening_AppliedAtBoot()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Trading:Cash:Seeds:0:EndClientId"] = TestAppFactory.TestUser,
            ["Trading:Cash:Seeds:0:InitialAvailable"] = "12345.67",
        };
        await using var factory = TestAppFactory.WithOverrides(overrides);
        var client = await factory.CreateAuthedClientAsync();

        var body = await client.GetFromJsonAsync<BalanceDto>("/api/balance");
        Assert.Equal(12345.67m, body!.Available);
    }

    [Fact]
    public async Task SelfDepositFlag_ReflectsSandboxOption()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Trading:Sandbox:AllowSelfCashDeposit"] = "true",
        };
        await using var factory = TestAppFactory.WithOverrides(overrides);
        var client = await factory.CreateAuthedClientAsync();

        var body = await client.GetFromJsonAsync<BalanceDto>("/api/balance");

        Assert.NotNull(body);
        Assert.True(body!.SelfDepositEnabled);
    }
}
