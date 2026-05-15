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

    [Fact]
    public void PnlProjection_OmitsUnknownBasisPositions_FromUnrealizedArray()
    {
        // Pass-4 (#278) P1#2 — unknown-basis legacy positions carry
        // no usable AverageEntryPrice, so the projection must NOT
        // publish phantom unrealized for them on either /pnl/today
        // (REST) or pnl.me (WS — both share PnlProjection.Build).
        // Symbols WITH a real basis are still surfaced; the
        // unknown-basis symbol is simply absent from the unrealized
        // array, and its (zero) contribution is excluded from the
        // total.
        var owner = new EndClientId("alice");
        var positions = new PositionKeeper();
        // Real basis: open 100 @ 30.
        positions.ApplyFill(owner, "PETR4", OrderSide.Buy, 100, 30m);
        // Legacy zero-basis position: ApplyFill with 0 price seeds
        // a degenerate AverageEntryPrice, mimicking the pass-2
        // restore that produced the bug.
        positions.ApplyFill(owner, "VALE3", OrderSide.Buy, 50, 0m);

        var pnl = new PnlKeeper();
        pnl.ApplyFillToAvgCost(owner.Value, "PETR4", OrderSide.Buy, 100, 30m);
        // Mark VALE3 as unknown-basis via the legacy seed path.
        pnl.SeedAvgCostFromLegacyPositions(new[]
        {
            new Application.Persistence.PositionSnapshot(owner.Value, "VALE3", 50, 0m),
        });
        Assert.Equal(50, pnl.GetUnknownBasisQty(owner.Value, "VALE3"));

        var refPrice = new StubAllPrices(35m);

        var dto = PnlProjection.Build(owner, pnl, positions, refPrice);

        var unr = Assert.Single(dto.Unrealized);
        Assert.Equal("PETR4", unr.Symbol);
        Assert.Equal((35m - 30m) * 100, unr.Value);
        Assert.Equal(unr.Value, dto.TotalUnrealized);
        Assert.DoesNotContain(dto.Unrealized, e => e.Symbol == "VALE3");
    }

    private sealed class StubAllPrices : IReferencePrice
    {
        private readonly decimal _px;
        public StubAllPrices(decimal px) => _px = px;
        public bool TryGet(string symbol, out decimal price) { price = _px; return true; }
    }
}
