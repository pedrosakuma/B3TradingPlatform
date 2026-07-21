using System.Net;
using System.Net.Http.Json;
using B3.Trading.Api.WebSockets;

namespace B3.Trading.Api.Tests;

/// <summary>
/// #679. Coverage for <c>POST /balance/deposit</c> — the self-service
/// cash deposit endpoint for sandbox/demo accounts. Gated by
/// <see cref="B3.Trading.Application.SandboxCashOptions.AllowSelfCashDeposit"/>;
/// most tests boot with it enabled, one confirms the route is entirely
/// absent (404) in the default (disabled) configuration.
/// </summary>
public class SelfDepositEndpointTests
{
    private static IDictionary<string, string?> Enabled() =>
        new Dictionary<string, string?>
        {
            ["Trading:Sandbox:AllowSelfCashDeposit"] = "true",
        };

    [Fact]
    public async Task Disabled_RouteIsAbsent()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());
        var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/balance/deposit", new { amount = 100m });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Enabled_UnauthenticatedPost_Returns401()
    {
        await using var factory = TestAppFactory.WithOverrides(Enabled());
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/balance/deposit", new { amount = 100m });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Deposit_IncreasesOwnBalance()
    {
        await using var factory = TestAppFactory.WithOverrides(Enabled());
        var client = await factory.CreateAuthedClientAsync();

        var before = await client.GetFromJsonAsync<BalanceDto>("/balance");

        var resp = await client.PostAsJsonAsync("/balance/deposit", new { amount = 500m });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var after = await client.GetFromJsonAsync<BalanceDto>("/balance");
        Assert.Equal(before!.Available + 500m, after!.Available);
    }

    [Fact]
    public async Task Deposit_NonPositiveAmount_Returns400()
    {
        await using var factory = TestAppFactory.WithOverrides(Enabled());
        var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/balance/deposit", new { amount = 0m });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Deposit_AboveMaxDepositAmount_Returns422()
    {
        var overrides = Enabled();
        overrides["Trading:Sandbox:MaxDepositAmount"] = "1000";
        await using var factory = TestAppFactory.WithOverrides(overrides);
        var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/balance/deposit", new { amount = 1001m });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Deposit_AboveMaxBalanceAfterDeposit_Returns422()
    {
        var overrides = Enabled();
        overrides["Trading:Sandbox:MaxBalanceAfterDeposit"] = "1000";
        await using var factory = TestAppFactory.WithOverrides(overrides);
        var client = await factory.CreateAuthedClientAsync();

        // First deposit brings the balance to 900 (within bounds)...
        var first = await client.PostAsJsonAsync("/balance/deposit", new { amount = 900m });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // ...second deposit would push it to 1100, over the 1000 cap.
        var second = await client.PostAsJsonAsync("/balance/deposit", new { amount = 200m });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
    }
}
