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
        Assert.Equal(0m, body!.TotalRealized);
        Assert.Equal(0m, body.TotalUnrealized);
        Assert.Empty(body.Realized);
        Assert.Empty(body.Unrealized);
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
        Assert.Equal(50m, body!.TotalRealized);
        var realizedRow = Assert.Single(body.Realized);
        Assert.Equal("PETR4", realizedRow.Symbol);
        Assert.Equal(50m, realizedRow.Value);

        // Unrealized appears only when the test ref-price source has
        // a value for PETR4; the test factory may or may not configure
        // one — accept both shapes.
        var refPrice = factory.Services.GetRequiredService<IReferencePrice>();
        if (refPrice.TryGet("PETR4", out var px))
        {
            var unr = Assert.Single(body.Unrealized);
            Assert.Equal("PETR4", unr.Symbol);
            Assert.Equal(50, unr.Position);
            Assert.Equal(30m, unr.AvgPrice);
            Assert.Equal(px, unr.RefPrice);
            Assert.Equal((px - 30m) * 50, unr.Value);
            Assert.Equal((px - 30m) * 50, body.TotalUnrealized);
        }
        else
        {
            Assert.Empty(body.Unrealized);
            Assert.Equal(0m, body.TotalUnrealized);
        }
    }
}
