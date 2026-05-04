using B3.Trading.DemoDriver;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var options = DemoDriverOptions.FromEnvironment();
if (options.UserBots.Count == 0)
{
    Console.Error.WriteLine("[demo-driver] DEMO_USER_BOTS is empty; nothing to do.");
    return 64; // EX_USAGE
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

var services = builder.Services;
services.AddSingleton(options);
services.AddSingleton<DemoOrderRegistry>();
services.AddSingleton<DemoModeState>();

// One typed HttpClient per bot. Named clients so each gets its own JWT
// header and its own DefaultRequestHeaders without bleeding across bots.
foreach (var bot in options.UserBots)
{
    services.AddHttpClient($"bot:{bot.Username}", c => c.BaseAddress = new Uri(options.BackendUrl));
}
if (options.Admin is not null)
    services.AddHttpClient($"admin:{options.Admin.Username}", c => c.BaseAddress = new Uri(options.BackendUrl));
services.AddHttpClient("probe", c => c.BaseAddress = new Uri(options.BackendUrl));

services.AddHostedService<BootstrapHostedService>();

foreach (var bot in options.UserBots)
{
    var captured = bot;
    services.AddSingleton<IHostedService>(sp =>
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient($"bot:{captured.Username}");
        var client = new TradingClient(http, captured);
        return new BotSubmitterWorker(
            client,
            sp.GetRequiredService<DemoOrderRegistry>(),
            sp.GetRequiredService<DemoDriverOptions>(),
            sp.GetRequiredService<DemoModeState>(),
            sp.GetRequiredService<ILogger<BotSubmitterWorker>>());
    });
}

if (options.Admin is not null)
{
    var captured = options.Admin;
    services.AddSingleton<IHostedService>(sp =>
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient($"admin:{captured.Username}");
        var client = new TradingClient(http, captured);
        return new InjectorWorker(
            client,
            sp.GetRequiredService<DemoOrderRegistry>(),
            sp.GetRequiredService<DemoDriverOptions>(),
            sp.GetRequiredService<DemoModeState>(),
            sp.GetRequiredService<ILogger<InjectorWorker>>());
    });
}

await builder.Build().RunAsync();
return 0;

// Mode probe runs once before the workers wake up. Workers await
// DemoModeState.WaitReadyAsync().
internal sealed class BootstrapHostedService : IHostedService
{
    private readonly DemoModeState _mode;
    private readonly IHttpClientFactory _http;
    private readonly DemoDriverOptions _options;

    public BootstrapHostedService(DemoModeState mode, IHttpClientFactory http, DemoDriverOptions options)
    {
        _mode = mode;
        _http = http;
        _options = options;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        // Use the first user bot as the probe identity. /health is unauthenticated
        // so the credential never gets used in the probe path; we just need a
        // TradingClient instance.
        var probeBot = _options.UserBots[0];
        var probe = new TradingClient(_http.CreateClient("probe"), probeBot);
        await _mode.BootstrapAsync(probe, ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
