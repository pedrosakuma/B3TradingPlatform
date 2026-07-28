using B3.Trading.SampleBot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

builder.Services
    .AddOptions<SampleBotOptions>()
    .Bind(builder.Configuration.GetSection(SampleBotOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<SampleBotOptions>, SampleBotOptionsValidator>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddHttpClient<ISampleBotAuthProvider, SampleBotAuthProvider>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<SampleBotOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
});

builder.Services.AddSingleton<AuthenticatedSessionCache>();

builder.Services.AddHttpClient<TradingPlatformRestClient>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<SampleBotOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
});

builder.Services.AddSingleton<ISampleBotWebSocketConnectionFactory, ClientWebSocketConnectionFactory>();
builder.Services.AddSingleton<TradingPlatformWebSocketClient>();
builder.Services.AddHostedService<SampleBotWorker>();

await builder.Build().RunAsync();
