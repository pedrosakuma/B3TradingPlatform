using System.Net;
using System.Net.Http.Json;
using B3.Trading.Api.WebSockets;

namespace B3.Trading.Api.Tests;

/// <summary>
/// #679. Coverage for <c>POST /api/balance/deposit</c> — the self-service
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

        var resp = await client.PostAsJsonAsync("/api/balance/deposit", new { amount = 100m });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Enabled_UnauthenticatedPost_Returns401()
    {
        await using var factory = TestAppFactory.WithOverrides(Enabled());
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/balance/deposit", new { amount = 100m });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Deposit_IncreasesOwnBalance()
    {
        await using var factory = TestAppFactory.WithOverrides(Enabled());
        var client = await factory.CreateAuthedClientAsync();

        var before = await client.GetFromJsonAsync<BalanceDto>("/api/balance");
        Assert.True(before!.SelfDepositEnabled);

        var resp = await client.PostAsJsonAsync("/api/balance/deposit", new { amount = 500m });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var after = await client.GetFromJsonAsync<BalanceDto>("/api/balance");
        Assert.Equal(before!.Available + 500m, after!.Available);
        Assert.True(after.SelfDepositEnabled);
    }

    [Fact]
    public async Task Deposit_NonPositiveAmount_Returns400()
    {
        await using var factory = TestAppFactory.WithOverrides(Enabled());
        var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/api/balance/deposit", new { amount = 0m });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Deposit_AboveMaxDepositAmount_Returns422()
    {
        var overrides = Enabled();
        overrides["Trading:Sandbox:MaxDepositAmount"] = "1000";
        await using var factory = TestAppFactory.WithOverrides(overrides);
        var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/api/balance/deposit", new { amount = 1001m });

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
        var first = await client.PostAsJsonAsync("/api/balance/deposit", new { amount = 900m });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // ...second deposit would push it to 1100, over the 1000 cap.
        var second = await client.PostAsJsonAsync("/api/balance/deposit", new { amount = 200m });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
    }

    [Fact]
    public async Task ConcurrentDeposits_NeverExceedMaxBalanceAfterDeposit()
    {
        // Review fix (#679): the cap check must be atomic w.r.t. the
        // dispatcher's snapshot lock (DispatchWithPreApply), otherwise
        // two concurrent requests could both observe a pre-cap balance
        // and both commit, exceeding MaxBalanceAfterDeposit.
        var overrides = Enabled();
        overrides["Trading:Sandbox:MaxBalanceAfterDeposit"] = "1000";
        await using var factory = TestAppFactory.WithOverrides(overrides);
        var client = await factory.CreateAuthedClientAsync();

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => client.PostAsJsonAsync("/api/balance/deposit", new { amount = 200m }))
            .ToArray();
        var responses = await Task.WhenAll(tasks);

        var okCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var rejectedCount = responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
        Assert.Equal(10, okCount + rejectedCount);
        // At most 5 deposits of 200 fit under the 1000 cap.
        Assert.True(okCount <= 5, $"Expected at most 5 accepted deposits, got {okCount}");

        var final = await client.GetFromJsonAsync<BalanceDto>("/api/balance");
        Assert.True(final!.Available <= 1000m, $"Final balance {final.Available} exceeded the cap");
    }
}
