using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using B3.Trading.Application;
using B3.Trading.Application.Outbound;
using B3.Trading.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace B3.Trading.Api.Tests.Lifecycle;

/// <summary>
/// End-to-end behavior of <c>Trading:Exchange:Mode = Unavailable</c>:
/// process boots, /health surfaces the mode, and POST /api/orders is rejected
/// with a durable proven-no-write rejection instead of being silently accepted
/// by a stub. Recovered approved mutations remain deferred until shutdown.
/// </summary>
public class UnavailableModeTests
{
    private static TestAppFactory MakeUnavailableFactory() => TestAppFactory.WithOverrides(new Dictionary<string, string?>
    {
        ["Trading:Exchange:Mode"] = nameof(ExchangeMode.Unavailable),
    });

    [Fact]
    public async Task Health_Surfaces_Mode_And_NotReadyForOrders()
    {
        using var factory = MakeUnavailableFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        var exchange = doc.RootElement.GetProperty("exchange");
        Assert.Equal(nameof(ExchangeMode.Unavailable), exchange.GetProperty("mode").GetString());
        Assert.False(exchange.GetProperty("readyForOrders").GetBoolean());
        Assert.Same(
            UnavailableOutboundGatewayReadiness.Instance,
            factory.Services.GetRequiredService<IOutboundGatewayReadiness>());
        Assert.Single(
            factory.Services.GetServices<IHostedService>()
                .OfType<NewOrderOutboundCoordinator>());
    }

    [Fact]
    public async Task Ready_Is503_While_Live_RemainsOk()
    {
        using var factory = MakeUnavailableFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);

        var live = await client.GetAsync("/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task Submit_ProvenUnavailable_TerminalisesAsDurableNoWriteRejection()
    {
        using var factory = MakeUnavailableFactory();
        using var client = await factory.CreateAuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/api/orders/", new
        {
            Symbol = "PETR4",
            SecurityId = 12345UL,
            Side = "Buy",
            Type = "Limit",
            Quantity = 100,
            Price = 30m,
        });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Rejected\"", body, StringComparison.Ordinal);
        Assert.Contains("gateway_proven_unsent", body, StringComparison.Ordinal);
    }
}
