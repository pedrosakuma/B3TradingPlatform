using B3.Trading.MarketMakerBot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
// Host.CreateApplicationBuilder already adds environment variables with
// no prefix to Configuration, so MarketMaker__* env vars (set in
// docker/docker-compose.market-maker.yml) bind to MarketMakerBotOptions
// without any extra wiring.

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

builder.Services
    .AddOptions<MarketMakerBotOptions>()
    .Bind(builder.Configuration.GetSection(MarketMakerBotOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o => o.Instruments.Count > 0, "MarketMaker:Instruments must be non-empty.")
    .Validate(o => o.ReconcileInterval > TimeSpan.Zero, "MarketMaker:ReconcileInterval must be positive.")
    .Validate(o => o.MaxOrderAge > TimeSpan.Zero, "MarketMaker:MaxOrderAge must be positive.")
    .Validate(o => o.MinRequoteInterval > TimeSpan.Zero, "MarketMaker:MinRequoteInterval must be positive.")
    .Validate(o => o.CancelAckTimeout > TimeSpan.Zero, "MarketMaker:CancelAckTimeout must be positive.")
    .Validate(o => o.Telemetry.SnapshotInterval > TimeSpan.Zero,
        "MarketMaker:Telemetry:SnapshotInterval must be positive.")
    .Validate(o => o.Telemetry.MarkMaxAge > TimeSpan.Zero,
        "MarketMaker:Telemetry:MarkMaxAge must be positive.")
    .Validate(o => string.IsNullOrWhiteSpace(o.MarketData.WsUrl) ||
        MarketDataOptionsValidation.TryGetWebSocketUri(o.MarketData.WsUrl, out _),
        "MarketMaker:MarketData:WsUrl, if set, must be an absolute ws:// or wss:// URI.")
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<MarketMakerBotOptions>, MarketMakerBotOptionsValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<OrderTracker>();
builder.Services.AddSingleton<MarketPriceTracker>();
builder.Services.AddSingleton<VolatilitySpreadEstimator>();
builder.Services.AddSingleton<MarketMakerPnlLedger>();
builder.Services.AddSingleton<MarketMakerMetrics>();
builder.Services.AddSingleton<MarketDataFeed>(sp => new MarketDataFeed(
    sp.GetRequiredService<MarketPriceTracker>(),
    sp.GetRequiredService<VolatilitySpreadEstimator>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("MarketDataFeed"),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService<MarketMakerWorker>();
builder.Services.AddHostedService<MarketMakerPnlReporter>();
builder.Services.AddMarketMakerOpenTelemetry(builder.Configuration);

await builder.Build().RunAsync();
