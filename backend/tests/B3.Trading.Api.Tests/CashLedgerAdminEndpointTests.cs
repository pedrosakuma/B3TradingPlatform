using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Q2.2 (#269). Coverage for the admin cash-ledger endpoint:
/// auth role gate, validation surface, deposit/withdrawal flow,
/// over-withdraw 422.
/// </summary>
public class CashLedgerAdminEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public CashLedgerAdminEndpointTests(TestAppFactory factory) => _factory = factory;

    private static string UniqueClient(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Fact]
    public async Task Post_RequiresAdminRole_TraderGets403()
    {
        using var trader = await _factory.CreateAuthedClientAsync(); // alice (user role)
        var resp = await trader.PostAsJsonAsync("/admin/cash", new
        {
            endclient = "anyone",
            kind = "Deposit",
            amount = 100m,
            currency = "BRL",
        });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Deposit_IncreasesBalance()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var endclient = UniqueClient("alice");

        var resp = await admin.PostAsJsonAsync("/admin/cash", new
        {
            endclient,
            kind = "Deposit",
            amount = 1_000m,
            currency = "BRL",
            reference = "ticket-1",
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CashResponse>(Json);
        Assert.Equal(1_000m, body!.Available);

        // Second deposit accumulates.
        var resp2 = await admin.PostAsJsonAsync("/admin/cash", new
        {
            endclient,
            kind = "Deposit",
            amount = 250m,
            currency = "BRL",
        });
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<CashResponse>(Json);
        Assert.Equal(1_250m, body2!.Available);
    }

    [Fact]
    public async Task Withdrawal_DecreasesBalance()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var endclient = UniqueClient("bob");

        await admin.PostAsJsonAsync("/admin/cash", new
        {
            endclient, kind = "Deposit", amount = 1_000m, currency = "BRL",
        });
        var resp = await admin.PostAsJsonAsync("/admin/cash", new
        {
            endclient, kind = "Withdrawal", amount = 400m, currency = "BRL",
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CashResponse>(Json);
        Assert.Equal(600m, body!.Available);
    }

    [Fact]
    public async Task OverWithdraw_Returns422_WithDetail()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var endclient = UniqueClient("carol");

        await admin.PostAsJsonAsync("/admin/cash", new
        {
            endclient, kind = "Deposit", amount = 100m, currency = "BRL",
        });
        var resp = await admin.PostAsJsonAsync("/admin/cash", new
        {
            endclient, kind = "Withdrawal", amount = 250m, currency = "BRL",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("insufficient_funds", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(100m, doc.RootElement.GetProperty("available").GetDecimal());
        Assert.Equal(250m, doc.RootElement.GetProperty("requested").GetDecimal());

        // Balance unchanged after the rejected withdrawal.
        var probe = await admin.PostAsJsonAsync("/admin/cash", new
        {
            endclient, kind = "Deposit", amount = 1m, currency = "BRL",
        });
        var probeBody = await probe.Content.ReadFromJsonAsync<CashResponse>(Json);
        Assert.Equal(101m, probeBody!.Available);
    }

    [Fact]
    public async Task UnknownCurrency_Returns400()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsJsonAsync("/admin/cash", new
        {
            endclient = UniqueClient("dave"),
            kind = "Deposit",
            amount = 100m,
            currency = "USD",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveAmount_Returns400(decimal amount)
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsJsonAsync("/admin/cash", new
        {
            endclient = UniqueClient("eve"),
            kind = "Deposit",
            amount,
            currency = "BRL",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task UnknownKind_Returns400()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsJsonAsync("/admin/cash", new
        {
            endclient = UniqueClient("frank"),
            kind = "Transfer",
            amount = 10m,
            currency = "BRL",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task MissingEndclient_Returns400()
    {
        using var admin = await _factory.CreateAuthedClientAsync("admin");
        var resp = await admin.PostAsJsonAsync("/admin/cash", new
        {
            endclient = "",
            kind = "Deposit",
            amount = 10m,
            currency = "BRL",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private sealed record CashResponse(string Endclient, string Kind, decimal Amount, string Currency, decimal Available);
}
