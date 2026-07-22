using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Application.MarketData;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q4.14 (#314). Regression coverage: a <c>compliance</c> principal
/// must be able to read <c>GET /api/fills/{id}/touch</c> within its own
/// firm (the endpoint is gated by plain
/// <see cref="Microsoft.AspNetCore.Builder.AuthorizationEndpointConventionBuilderExtensions.RequireAuthorization{TBuilder}(TBuilder)"/>
/// — any authenticated principal passes — and the firm scope is
/// derived from the JWT firm claim). A compliance caller hitting a
/// fill in a different firm gets 404 (no existence leak), matching
/// the user-role contract.
/// </summary>
public class FillTouchComplianceAccessTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string Firm01 = "FIRM01";
    private const string Firm02 = "FIRM02";

    private static (FillRecord Record, string Id) SeedFill(
        TestAppFactory factory,
        string user,
        string firmId,
        ulong clOrdId,
        long cumQty)
    {
        var registry = factory.Services.GetRequiredService<EndClientRegistry>();
        var fills = factory.Services.GetRequiredService<FillProjection>();
        var book = factory.Services.GetRequiredService<WorkingOrderBook>();
        var owner = registry.Register(user);
        book.TryAdd(new Order(clOrdId, owner, "PETR4", 9000UL, OrderSide.Buy, OrderType.Limit, cumQty, 30m, firmId: firmId));
        var touch = new BookTouchSnapshot
        {
            BestBid = 29.95m,
            BestAsk = 30.05m,
            MidPrice = 30.00m,
            LastTradePrice = 30.00m,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Stale = false,
        };
        var record = fills.Record(
            clOrdId, cumQty, owner, firmId, "PETR4", OrderSide.Buy, cumQty, 30m,
            DateTimeOffset.UtcNow, touch);
        return (record, FillProjection.BuildId(clOrdId, cumQty));
    }

    private static HttpClient ComplianceClient(TestAppFactory factory, string firm)
    {
        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        var (token, _) = issuer.Issue("dave", Roles.Compliance, firm);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task ComplianceCanReadOwnFirmFill()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var (_, id) = SeedFill(factory, "alice", Firm01, 1001UL, 100);

        using var client = ComplianceClient(factory, Firm01);
        var resp = await client.GetAsync($"/api/fills/{id}/touch");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<BookTouchDto>(JsonOptions);
        Assert.NotNull(dto);
        Assert.Equal(29.95m, dto!.BestBid);
        Assert.Equal(30.05m, dto.BestAsk);
        Assert.False(dto.Stale);
    }

    [Fact]
    public async Task ComplianceCrossFirmReturns404()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var (_, id) = SeedFill(factory, "alice", Firm01, 1002UL, 50);

        using var client = ComplianceClient(factory, Firm02);
        var resp = await client.GetAsync($"/api/fills/{id}/touch");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ComplianceFirmIdOverrideIsForbidden()
    {
        // Only admin may pass ?firmId= on this endpoint — compliance
        // is firm-pinned to its JWT firm claim. The 403 keeps the
        // firm boundary explicit instead of silently dropping the
        // override.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var (_, id) = SeedFill(factory, "alice", Firm01, 1003UL, 75);

        using var client = ComplianceClient(factory, Firm01);
        var resp = await client.GetAsync($"/api/fills/{id}/touch?firmId={Firm02}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
