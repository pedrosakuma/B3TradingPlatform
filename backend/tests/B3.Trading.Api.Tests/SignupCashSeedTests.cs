using System.Net.Http.Json;
using B3.Trading.Api.Auth;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

/// <summary>
/// Slice 3 of #107 — POST /auth/signup pre-funds new accounts via
/// CashSeedOptions.SignupInitialBalance. Verifies the wiring round-trips
/// through GET /balance and that the CashLedger holds the expected
/// amount for the freshly-registered owner.
/// </summary>
public class SignupCashSeedTests
{
    private static string FreshUsername() => "u" + Guid.NewGuid().ToString("N")[..10];

    private static async Task<string> SignupAsync(HttpClient client, string user)
    {
        var resp = await client.PostAsJsonAsync("/auth/signup", new SignupRequest(user, "wonderland-1"));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        return body!.Token;
    }

    [Fact]
    public async Task SignupInitialBalance_Configured_PreFundsNewAccount()
    {
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Cash:SignupInitialBalance"] = "50000.00",
        });

        var client = factory.CreateClient();
        var username = FreshUsername();
        var token = await SignupAsync(client, username);

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var balance = await client.GetFromJsonAsync<BalanceDto>("/balance");
        Assert.Equal(50_000m, balance!.Available);

        // And the ledger snapshot now contains the new owner.
        var ledger = factory.Services.GetRequiredService<CashLedger>();
        Assert.Equal(50_000m, ledger.GetAvailable("FIRM01", new EndClientId(username)));
    }

    [Fact]
    public async Task SignupInitialBalance_Unset_NewAccountStartsAtZero()
    {
        // Without SignupInitialBalance configured, signup leaves the
        // ledger empty for the new owner — GET /balance returns 0.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>());

        var client = factory.CreateClient();
        var username = FreshUsername();
        var token = await SignupAsync(client, username);

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var balance = await client.GetFromJsonAsync<BalanceDto>("/balance");
        Assert.Equal(0m, balance!.Available);
    }

    [Fact]
    public async Task SignupInitialBalance_Zero_DoesNotSeed()
    {
        // A configured-zero is intentionally NOT seeded — the rationale
        // is that CashLedger.Snapshot prunes zero rows, so a seed of
        // zero would silently resurface as a fallback to
        // RiskOptions.Margin.Initial after a snapshot/restore cycle.
        // Slice 4 retires that fallback; until then, "zero seed" is a
        // foot-gun we explicitly avoid.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Cash:SignupInitialBalance"] = "0",
        });

        var client = factory.CreateClient();
        var username = FreshUsername();
        await SignupAsync(client, username);

        var ledger = factory.Services.GetRequiredService<CashLedger>();
        // No entry: Snapshot is empty for this owner.
        Assert.DoesNotContain(ledger.Snapshot(), s => s.EndClientId == username);
    }

    [Fact]
    public async Task SignupInitialBalance_FlowsToCashLedger()
    {
        // Slice 3's contract is: signup populates the CashLedger, which
        // slice 2 already proved is the source of truth for the margin
        // provider. We assert the ledger directly here rather than
        // round-tripping through POST /orders (orthogonal firm/security
        // wiring) — the slice-2 unit tests already cover the margin
        // path against a populated ledger.
        await using var factory = TestAppFactory.WithOverrides(new Dictionary<string, string?>
        {
            ["Trading:Cash:SignupInitialBalance"] = "1000",
            ["Trading:Risk:Margin:Enabled"] = "true",
        });

        var client = factory.CreateClient();
        var username = FreshUsername();
        await SignupAsync(client, username);

        var ledger = factory.Services.GetRequiredService<CashLedger>();
        Assert.Equal(1000m, ledger.GetAvailable("FIRM01", new EndClientId(username)));
    }
}
