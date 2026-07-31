using B3.Trading.MarketMakerBot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;

namespace B3.Trading.MarketMakerBot.Tests;

public class OpenTelemetryRegistrationTests
{
    [Fact]
    public void AddMarketMakerOpenTelemetry_NoEndpoint_DoesNotRegisterProvider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddMarketMakerOpenTelemetry(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<MeterProvider>());
    }

    [Fact]
    public void AddMarketMakerOpenTelemetry_EndpointConfigured_RegistersProvider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OpenTelemetryRegistration.EndpointEnvironmentVariable] = "http://localhost:4317",
            })
            .Build();

        services.AddMarketMakerOpenTelemetry(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<MeterProvider>());
    }
}
