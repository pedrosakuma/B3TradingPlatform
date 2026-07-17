using B3.Trading.Application;
using B3.Trading.Host.Composition;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace B3.Trading.Api.Tests;

public sealed class RealMultiFirmWiringTests
{
    [Fact]
    public async Task RealMode_RegistersOneGatewayPerConfiguredFirm()
    {
        var dataDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-artifacts",
            "issue-627",
            Guid.NewGuid().ToString("N"));
        try
        {
            var values = new Dictionary<string, string?>
            {
                ["Trading:Exchange:Mode"] = "Real",
                ["Trading:Exchange:Firms:0:FirmId"] = "BROKER-A",
                ["Trading:Exchange:Firms:0:Endpoint"] = "localhost:19001",
                ["Trading:Exchange:Firms:0:SessionId"] = "101",
                ["Trading:Exchange:Firms:0:SessionVerId"] = "1",
                ["Trading:Exchange:Firms:0:EnteringFirm"] = "1001",
                ["Trading:Exchange:Firms:0:AccessKey"] = "0123456789ABCDEF0123456789ABCDEF",
                ["Trading:Exchange:Firms:0:SenderLocation"] = "BR-SP",
                ["Trading:Exchange:Firms:0:EnteringTrader"] = "A1",
                ["Trading:Exchange:Firms:1:FirmId"] = "BROKER-B",
                ["Trading:Exchange:Firms:1:Endpoint"] = "localhost:19002",
                ["Trading:Exchange:Firms:1:SessionId"] = "102",
                ["Trading:Exchange:Firms:1:SessionVerId"] = "1",
                ["Trading:Exchange:Firms:1:EnteringFirm"] = "1002",
                ["Trading:Exchange:Firms:1:AccessKey"] = "FEDCBA9876543210FEDCBA9876543210",
                ["Trading:Exchange:Firms:1:SenderLocation"] = "BR-RJ",
                ["Trading:Exchange:Firms:1:EnteringTrader"] = "B1",
            };
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.Configure<PersistenceOptions>(o => o.DataDirectory = dataDirectory);
            services.AddTradingExchangeGateway(configuration);

            await using var provider = services.BuildServiceProvider();
            var gateway = provider.GetRequiredService<IExchangeGateway>();
            var registry = provider.GetRequiredService<FirmGatewayRegistry>();

            Assert.IsType<MultiFirmExchangeGateway>(gateway);
            Assert.Equal(
                ["BROKER-A", "BROKER-B"],
                registry.Gateways.Keys.OrderBy(x => x).ToArray());
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
