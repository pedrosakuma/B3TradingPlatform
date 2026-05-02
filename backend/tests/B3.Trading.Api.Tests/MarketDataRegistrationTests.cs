using B3.Trading.Application.MarketData;
using B3.Trading.Application.Risk;
using B3.Trading.Host.MarketData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace B3.Trading.Api.Tests;

/// <summary>
/// The <c>Trading:MarketData:WsUrl</c> gate is the contract operators
/// rely on: leaving it unset must keep the host on the static
/// <see cref="ConfigReferencePrice"/> exactly like before — no
/// background tasks, no SDK pull, no surprise dependencies. Setting it
/// must swap in <see cref="MarketDataReferencePrice"/> AND register it
/// as a hosted service so its event handlers attach before the
/// subscriber loop starts.
/// </summary>
public class MarketDataRegistrationTests
{
    [Fact]
    public void Without_WsUrl_resolves_ConfigReferencePrice()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trading:MarketData:WsUrl"] = "",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTradingMarketData(cfg);

        using var sp = services.BuildServiceProvider();

        var refPrice = sp.GetRequiredService<IReferencePrice>();
        Assert.IsType<ConfigReferencePrice>(refPrice);

        // No hosted service registered when feature is off.
        Assert.DoesNotContain(
            sp.GetServices<IHostedService>(),
            h => h is MarketDataReferencePrice);
    }

    [Fact]
    public async Task With_WsUrl_resolves_MarketDataReferencePrice_and_hosted_service()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Trading:MarketData:WsUrl"] = "ws://marketdata:8080/ws",
                ["Trading:MarketData:Symbols:0"] = "PETR4",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTradingMarketData(cfg);

        await using var sp = services.BuildServiceProvider();

        var refPrice = sp.GetRequiredService<IReferencePrice>();
        var hosted = sp.GetServices<IHostedService>()
            .OfType<MarketDataReferencePrice>()
            .Single();

        Assert.Same(refPrice, hosted); // same singleton instance on both seams
    }
}
