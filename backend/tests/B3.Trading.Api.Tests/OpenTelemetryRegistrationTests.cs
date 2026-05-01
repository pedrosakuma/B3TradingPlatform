using B3.Trading.Host.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace B3.Trading.Api.Tests;

/// <summary>
/// The <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> gate is critical: leaving the
/// SDK on without an endpoint floods stderr with retry warnings every
/// few seconds and pulls in a periodic background pump for nothing. These
/// tests pin the contract: unset env -> nothing registered; set env ->
/// SDK registered.
/// </summary>
public class OpenTelemetryRegistrationTests
{
    private const string EnvVar = OpenTelemetryRegistration.EndpointEnvVar;

    [Fact]
    public void Without_Endpoint_Env_Skips_Registration()
    {
        var prior = Environment.GetEnvironmentVariable(EnvVar);
        Environment.SetEnvironmentVariable(EnvVar, null);
        try
        {
            var services = new ServiceCollection();
            services.AddTradingObservability(new ConfigurationBuilder().Build());
            using var sp = services.BuildServiceProvider();
            // No MeterProvider gets registered when AddOpenTelemetry isn't called.
            Assert.Null(sp.GetService<MeterProvider>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, prior);
        }
    }

    [Fact]
    public void With_Endpoint_Env_Registers_Sdk()
    {
        var prior = Environment.GetEnvironmentVariable(EnvVar);
        // localhost:4317 is the OTLP/grpc default; we never actually
        // connect — building the provider is enough to prove registration.
        Environment.SetEnvironmentVariable(EnvVar, "http://localhost:4317");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddTradingObservability(new ConfigurationBuilder().Build());
            using var sp = services.BuildServiceProvider();
            Assert.NotNull(sp.GetService<MeterProvider>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, prior);
        }
    }
}
