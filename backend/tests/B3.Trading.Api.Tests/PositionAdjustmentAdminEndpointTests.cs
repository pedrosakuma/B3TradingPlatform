using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace B3.Trading.Api.Tests;

/// <summary>
/// #671/#753 (RFC: admin account reset + runtime position adjustment,
/// PR 1). Coverage for <c>POST /api/admin/positions</c>: auth role
/// gate, validation surface (RFC #753 invariants), absolute overwrite
/// (never accumulate) semantics, and FirmId always derived from the
/// caller's JWT firm claim rather than the request body. Mirrors
/// <see cref="CashLedgerAdminEndpointTests"/>'s shape for the sibling
/// <c>/cash</c> endpoint.
/// </summary>
public class PositionAdjustmentAdminEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PositionAdjustmentAdminEndpointTests(TestAppFactory factory) => _factory = factory;

    private static string UniqueClient(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Fact]
    public async Task Post_RequiresAdminRole_TraderGets403()
    {
        using var trader = await _factory.CreateAuthedClientAsync(); // alice (user role)
        var resp = await trader.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient = "anyone",
            symbol = "PETR4",
            netQuantity = 100,
            averageEntryPrice = 30m,
        });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Post_SetsAbsolutePosition()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var endclient = UniqueClient("alice");

        var resp = await admin.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient,
            symbol = "PETR4",
            netQuantity = 500,
            averageEntryPrice = 28.5m,
            reference = "seed-1",
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<PositionAdjustmentResponse>(Json);
        Assert.Equal(500, body!.NetQuantity);
        Assert.Equal(28.5m, body.AverageEntryPrice);
        Assert.Equal("PETR4", body.Symbol);
    }

    [Fact]
    public async Task Post_Overwrites_DoesNotAccumulate()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var endclient = UniqueClient("bob");

        var first = await admin.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient,
            symbol = "VALE3",
            netQuantity = 200,
            averageEntryPrice = 60m,
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var resp = await admin.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient,
            symbol = "VALE3",
            netQuantity = -50,
            averageEntryPrice = 65m,
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<PositionAdjustmentResponse>(Json);

        // Absolute overwrite: must land on -50, NOT the delta-accumulated
        // 200 + (-50) = 150 a naive fill-style fold would have produced.
        Assert.Equal(-50, body!.NetQuantity);
        Assert.Equal(65m, body.AverageEntryPrice);
    }

    [Fact]
    public async Task Post_CanFlattenPosition_ZeroQuantityZeroPrice()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var endclient = UniqueClient("carol");

        await admin.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient,
            symbol = "ITUB4",
            netQuantity = 100,
            averageEntryPrice = 25m,
        });
        var resp = await admin.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient,
            symbol = "ITUB4",
            netQuantity = 0,
            averageEntryPrice = 0m,
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<PositionAdjustmentResponse>(Json);
        Assert.Equal(0, body!.NetQuantity);
        Assert.Equal(0m, body.AverageEntryPrice);
    }

    [Fact]
    public async Task ZeroQuantity_WithNonZeroPrice_Returns400()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient = UniqueClient("dave"),
            symbol = "PETR4",
            netQuantity = 0,
            averageEntryPrice = 10m,
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonZeroQuantity_WithNonPositivePrice_Returns400(decimal price)
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient = UniqueClient("eve"),
            symbol = "PETR4",
            netQuantity = 100,
            averageEntryPrice = price,
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task NegativeQuantity_WithPositivePrice_Succeeds()
    {
        // Short positions are a legitimate absolute state — only the
        // sign/price pairing invariant is enforced, not the sign itself.
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient = UniqueClient("frank"),
            symbol = "PETR4",
            netQuantity = -100,
            averageEntryPrice = 30m,
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<PositionAdjustmentResponse>(Json);
        Assert.Equal(-100, body!.NetQuantity);
    }

    [Fact]
    public async Task MissingEndclient_Returns400()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient = "",
            symbol = "PETR4",
            netQuantity = 100,
            averageEntryPrice = 10m,
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task MissingSymbol_Returns400()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient = UniqueClient("grace"),
            symbol = "",
            netQuantity = 100,
            averageEntryPrice = 10m,
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task MissingBody_Returns400()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsync(
            "/api/admin/positions",
            new StringContent("null", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // Code-review addendum (#671/#753 PR 1). NetQuantity/AverageEntryPrice
    // are nullable at JSON-binding level so an omitted field is
    // distinguishable from an explicit 0 (the intentional flatten case
    // covered by Post_CanFlattenPosition_ZeroQuantityZeroPrice above).
    [Fact]
    public async Task MissingNetQuantity_Returns400()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient = UniqueClient("ivan"),
            symbol = "PETR4",
            averageEntryPrice = 10m,
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task MissingAverageEntryPrice_Returns400()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient = UniqueClient("judy"),
            symbol = "PETR4",
            netQuantity = 100,
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task MissingBothQuantityAndPrice_Returns400()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient = UniqueClient("kevin"),
            symbol = "PETR4",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // Code-review addendum (#671/#753 PR 1). Fail closed rather than
    // defaulting to the DEFAULT tenant bucket when the caller's JWT firm
    // claim is missing or blank — this is a durable, tenant-scoped WRITE.
    [Fact]
    public async Task BlankFirmClaim_FailsClosed_Returns401()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        using var client = factory.CreateClient();

        // JwtIssuer.Issue always emits SOME firm claim value — mint one
        // with a blank (whitespace-only) value to exercise the "blank"
        // half of "missing or blank".
        var (token, _) = issuer.Issue("admin-op", "admin", firm: "   ");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient = UniqueClient("mallory"),
            symbol = "PETR4",
            netQuantity = 100,
            averageEntryPrice = 10m,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task MissingFirmClaim_FailsClosed_Returns401()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        using var client = factory.CreateClient();

        // Hand-craft a token (same signing key/issuer/audience as
        // JwtIssuer, replicated here) with NO firm claim at all — this
        // is what a malformed/forged token looks like; JwtIssuer.Issue
        // itself has no way to omit the claim.
        var authOptions = factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        var keyBytes = Encoding.UTF8.GetBytes(authOptions.SigningKey);
        var credentials = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "admin-op"),
            new(JwtIssuer.RoleClaim, "admin"),
            // Deliberately no JwtIssuer.FirmClaim.
        };
        var now = DateTime.UtcNow;
        var jwt = new JwtSecurityToken(
            issuer: authOptions.Issuer,
            audience: authOptions.Audience,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(5),
            signingCredentials: credentials);
        var token = new JwtSecurityTokenHandler().WriteToken(jwt);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient = UniqueClient("trent"),
            symbol = "PETR4",
            netQuantity = 100,
            averageEntryPrice = 10m,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task FirmId_IsDerivedFromAdminJwt_NotFromRequestBody()
    {
        // RFC #753 product decision: "admin operations are scoped to the
        // administrator's JWT firm. No explicit cross-firm firmId
        // parameter in v1." Verify (a) a firmId sent in the body is
        // ignored, and (b) the same admin sub minting tokens under two
        // different firms writes into two firm-isolated buckets.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var issuer = factory.Services.GetRequiredService<JwtIssuer>();
        var positions = factory.Services.GetRequiredService<PositionKeeper>();
        var endclient = UniqueClient("heidi");
        var owner = new EndClientId(endclient);

        using var client = factory.CreateClient();

        var (firm1Token, _) = issuer.Issue("admin-op", "admin", "FIRM01");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firm1Token);
        var resp1 = await client.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient,
            symbol = "PETR4",
            netQuantity = 300,
            averageEntryPrice = 20m,
            firmId = "FIRM02", // must be ignored — never trust the request body.
        });
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        var (firm2Token, _) = issuer.Issue("admin-op", "admin", "FIRM02");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firm2Token);
        var resp2 = await client.PostAsJsonAsync("/api/admin/positions", new
        {
            endclient,
            symbol = "PETR4",
            netQuantity = 700,
            averageEntryPrice = 22m,
        });
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        var firm1Positions = positions.ForEndClientAndFirm("FIRM01", owner);
        var firm2Positions = positions.ForEndClientAndFirm("FIRM02", owner);
        Assert.Single(firm1Positions);
        Assert.Equal(300, firm1Positions.Single().NetQuantity);
        Assert.Single(firm2Positions);
        Assert.Equal(700, firm2Positions.Single().NetQuantity);
    }

    private sealed record PositionAdjustmentResponse(
        string Endclient, string FirmId, string Symbol, long NetQuantity, decimal AverageEntryPrice);
}
