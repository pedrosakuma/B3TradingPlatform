using B3.Trading.SimulatorBot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
// Host.CreateApplicationBuilder already adds environment variables with
// no prefix to Configuration, so Bot__* env vars (set in
// docker/docker-compose.simulator-bot.yml) bind to SimulatorBotOptions
// without any extra wiring.

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

builder.Services
    .AddOptions<SimulatorBotOptions>()
    .Bind(builder.Configuration.GetSection(SimulatorBotOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o => o.Instruments.Count > 0, "Bot:Instruments must be non-empty.")
    .Validate(o => o.TickInterval > TimeSpan.Zero, "Bot:TickInterval must be positive.")
    .Validate(o => o.MaxInFlightPerSymbol > 0, "Bot:MaxInFlightPerSymbol must be > 0.")
    .Validate(o => o.CrossProbability is >= 0 and <= 1, "Bot:CrossProbability must be in [0, 1].")
    .ValidateOnStart();

builder.Services.AddSingleton<OrderTracker>();
builder.Services.AddHostedService<SimulatorBotWorker>();

await builder.Build().RunAsync();
