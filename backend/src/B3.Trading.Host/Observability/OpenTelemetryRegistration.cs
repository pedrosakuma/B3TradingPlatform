using B3.Trading.Application.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace B3.Trading.Host.Observability;

/// <summary>
/// Wires the OpenTelemetry SDK against the application's existing
/// <see cref="MetricsRegistry.Meter"/> and ASP.NET Core diagnostics.
///
/// <para>
/// Activation is gated on the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>
/// environment variable: when unset, this method is a no-op so dev loops,
/// unit tests, and "I just want to read /health" smoke runs pay zero
/// overhead and don't spam stderr with failed exporter retries. When set,
/// the OTLP exporter is honoured directly by the SDK (endpoint, protocol,
/// headers all read from the standard <c>OTEL_*</c> envs).
/// </para>
///
/// <para>
/// The Prometheus-friendly view is <b>not</b> wired here — the local docker
/// observability stack (PR 7-2c) collects via the OTLP receiver in
/// otel-collector and exposes <c>/metrics</c> from the collector side.
/// Keeps the host-side surface small and the protocol single.
/// </para>
/// </summary>
public static class OpenTelemetryRegistration
{
    public const string EndpointEnvVar = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// Logical service name reported in <c>service.name</c> resource
    /// attribute. Must match what the otel-collector / Grafana datasources
    /// filter on, so it is intentionally a const.
    /// </summary>
    public const string ServiceName = "b3-trading-host";

    public static IServiceCollection AddTradingObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var endpoint = Environment.GetEnvironmentVariable(EndpointEnvVar);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            // Honest no-op: log nothing, register nothing. The
            // /metrics-via-collector path stays dark until an operator
            // explicitly opts in via env. PR 7-2c will set this in the
            // observability compose profile.
            return services;
        }

        var environmentName = configuration["ASPNETCORE_ENVIRONMENT"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";

        var serviceVersion = typeof(OpenTelemetryRegistration).Assembly
            .GetName().Version?.ToString() ?? "0.0.0";

        services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName: ServiceName, serviceVersion: serviceVersion)
                .AddAttributes(new KeyValuePair<string, object>[]
                {
                    new("deployment.environment", environmentName),
                }))
            .WithMetrics(b => b
                // Application meter: every counter / histogram / gauge in
                // MetricsRegistry rides this one Meter, so a single
                // AddMeter is enough.
                .AddMeter(MetricsRegistry.Meter.Name)
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter())
            .WithTracing(b => b
                .AddAspNetCoreInstrumentation(o =>
                {
                    // Health probes flood the trace stream and never carry
                    // useful diagnostic signal. Drop them at source.
                    o.Filter = ctx => ctx.Request.Path != "/live"
                                   && ctx.Request.Path != "/ready"
                                   && ctx.Request.Path != "/health";
                })
                .AddOtlpExporter());

        return services;
    }
}
