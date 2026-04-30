using B3.Trading.Api;
using B3.Trading.Application;
using B3.Trading.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Composition root. Bootstrap wiring; real auth, real EntryPoint gateway,
// SubscriptionManager and PreTradeRisk land in subsequent issues (see issue #1).
builder.Services.AddSingleton<EndClientRegistry>();
builder.Services.AddSingleton<WorkingOrderBook>();
builder.Services.AddSingleton<PositionKeeper>();
builder.Services.AddSingleton<IExchangeGateway, StubExchangeGateway>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "B3TradingPlatform", status = "bootstrap" }));
app.MapGet("/health", () => Results.Ok("ok"));

app.MapOrders();
app.MapPositions();

app.Run();

// Exposed so WebApplicationFactory<Program>-style tests can spin the host up.
public partial class Program;
