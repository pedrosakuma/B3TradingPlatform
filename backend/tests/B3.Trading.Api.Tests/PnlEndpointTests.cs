using System.Net;
using System.Net.Http.Json;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q2.4 (#271). GET /pnl/today happy-path coverage. The endpoint is
/// AuthN-gated; the projection layer is shared with the WS pnl.me
/// channel snapshot — exercising it via HTTP is the lowest-risk surface
/// because route mounting + JWT plumbing get covered for free.
/// </summary>
public class PnlEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public PnlEndpointTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task UnauthenticatedGet_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/pnl/today");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task FreshAccount_ReturnsEmptySnapshot()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var client = await factory.CreateAuthedClientAsync();

        var body = await client.GetFromJsonAsync<PnlTodayDto>("/pnl/today");
        Assert.NotNull(body);
        Assert.Equal(0m, body!.RealizedTotal);
        Assert.Equal(0m, body.UnrealizedTotal);
        Assert.Empty(body.Symbols);
    }

    [Fact]
    public async Task AfterFillAndRealized_ProjectsRealizedAndUnrealized()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var client = await factory.CreateAuthedClientAsync();

        var pnl = factory.Services.GetRequiredService<PnlKeeper>();
        var positions = factory.Services.GetRequiredService<PositionKeeper>();
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var owner = registry.Register(TestAppFactory.TestUser);

        // Open 100 @ 30, then close 50 @ 31 → realized 50, residual 50 @ 30.
        positions.ApplyFill(owner, "PETR4", OrderSide.Buy, 100, 30m);
        positions.ApplyFill(owner, "PETR4", OrderSide.Sell, 50, 31m);
        pnl.ApplyFillToAvgCost(owner.Value, "PETR4", OrderSide.Buy, 100, 30m);
        pnl.ApplyFillToAvgCost(owner.Value, "PETR4", OrderSide.Sell, 50, 31m);
        var day = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        pnl.Apply(new Application.Persistence.RealizedPnlEvent
        {
            ClOrdId = 1,
            ExecutionId = "1:50",
            EndClientId = owner.Value,
            Symbol = "PETR4",
            DayKey = day,
            DeltaRealized = 50m,
            RunningTotal = 50m,
            TimestampUtc = DateTimeOffset.UtcNow,
        });

        var body = await client.GetFromJsonAsync<PnlTodayDto>("/pnl/today");
        Assert.NotNull(body);
        Assert.Equal(50m, body!.RealizedTotal);
        // No reference price configured for PETR4 in the test factory →
        // unrealized list omits the symbol but realized is still
        // surfaced.
        var refPrice = factory.Services.GetRequiredService<IReferencePrice>();
        var hasRef = refPrice.TryGet("PETR4", out var px);
        var row = Assert.Single(body.Symbols);
        Assert.Equal("PETR4", row.Symbol);
        Assert.Equal(50, row.NetQuantity);
        Assert.Equal(50m, row.Realized);
        if (hasRef)
        {
            Assert.NotNull(row.ReferencePrice);
            Assert.Equal(px, row.ReferencePrice);
            Assert.Equal((px - 30m) * 50, row.Unrealized);
        }
        else
        {
            Assert.Null(row.ReferencePrice);
            Assert.Null(row.Unrealized);
        }
    }
}
