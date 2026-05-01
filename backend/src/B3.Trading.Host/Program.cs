using B3.Trading.Api;
using B3.Trading.Application;
using B3.Trading.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ExchangeOptions>(
    builder.Configuration.GetSection(ExchangeOptions.SectionName));

// Application-layer singletons: registries, books, processor, sink.
builder.Services.AddSingleton<EndClientRegistry>();
builder.Services.AddSingleton<ClOrdIdPrefixRegistry>();
builder.Services.AddSingleton<OrderOwnershipMap>();
builder.Services.AddSingleton<WorkingOrderBook>();
builder.Services.AddSingleton<PositionKeeper>();
builder.Services.AddSingleton<IExecutionEventSink, NoOpExecutionEventSink>();
builder.Services.AddSingleton<ExecutionReportProcessor>();

// Wire-side: pick the gateway based on config. When stub mode is on, the
// EntryPoint client + ER router are not wired at all — keeps the test
// surface minimal.
var exchangeSection = builder.Configuration.GetSection(ExchangeOptions.SectionName);
var useStub = exchangeSection.GetValue("UseStubGateway", defaultValue: false);

if (useStub)
{
    builder.Services.AddSingleton<IExchangeGateway, StubExchangeGateway>();
}
else
{
    builder.Services.AddSingleton<IEntryPointClient, MockEntryPointClient>();
    builder.Services.AddSingleton<IExchangeGateway>(sp =>
    {
        var client = sp.GetRequiredService<IEntryPointClient>();
        var firms = exchangeSection.GetSection(nameof(ExchangeOptions.Firms)).Get<List<FirmConfig>>() ?? new();
        var firmId = firms.FirstOrDefault()?.FirmId ?? "DEFAULT";
        return new EntryPointClientGateway(client, firmId);
    });
    builder.Services.AddSingleton<EntryPointExecutionReportRouter>();
    builder.Services.AddHostedService<EntryPointRouterStarter>();
}

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "B3TradingPlatform", status = "bootstrap" }));
app.MapGet("/health", () => Results.Ok("ok"));

app.MapOrders();
app.MapPositions();

app.Run();

/// <summary>
/// Forces construction of <see cref="EntryPointExecutionReportRouter"/> at
/// app start (DI is otherwise lazy). The router subscribes to ER events
/// in its constructor and unsubscribes on dispose.
/// </summary>
internal sealed class EntryPointRouterStarter : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly EntryPointExecutionReportRouter _router;
    public EntryPointRouterStarter(EntryPointExecutionReportRouter router) => _router = router;
    public Task StartAsync(CancellationToken cancellationToken) { _ = _router; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) { _router.Dispose(); return Task.CompletedTask; }
}

// Exposed so WebApplicationFactory<Program>-style tests can spin the host up.
public partial class Program;

