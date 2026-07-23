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
    .Validate(o => o.MarketData.WsUrl is null || Uri.TryCreate(o.MarketData.WsUrl, UriKind.Absolute, out _),
        "MarketMaker:MarketData:WsUrl, if set, must be an absolute URI.")
    .ValidateOnStart();

builder.Services.AddSingleton<OrderTracker>();
builder.Services.AddSingleton<MarketPriceTracker>();
builder.Services.AddSingleton<MarketDataFeed>(sp => new MarketDataFeed(
    sp.GetRequiredService<MarketPriceTracker>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("MarketDataFeed")));
builder.Services.AddHostedService<MarketMakerWorker>();

await builder.Build().RunAsync();
