using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace B3.Trading.MarketMakerBot;

public static class OpenTelemetryRegistration
{
    public const string EndpointEnvironmentVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string ServiceName = "b3-market-maker-bot";

    public static IServiceCollection AddMarketMakerOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration[EndpointEnvironmentVariable]))
            return services;

        var environmentName = configuration["DOTNET_ENVIRONMENT"] ?? "Production";
        var serviceVersion = typeof(OpenTelemetryRegistration).Assembly
            .GetName().Version?.ToString() ?? "0.0.0";

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(ServiceName, serviceVersion: serviceVersion)
                .AddAttributes([
                    new KeyValuePair<string, object>("deployment.environment", environmentName),
                ]))
            .WithMetrics(metrics => metrics
                .AddMeter(MarketMakerMetrics.MeterName)
                .AddOtlpExporter());

        return services;
    }
}
