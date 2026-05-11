using B3.Trading.EntryPointListener;
using B3.Trading.Host.Composition;
using B3.Trading.Host.Observability;

var builder = WebApplication.CreateBuilder(args);

// CORS: opt-in allowlist for the dev/prod frontend origins. Empty list
// disables CORS entirely (server-only deploys, integration tests).
const string CorsPolicy = "trading-frontend";
var corsOrigins = builder.Configuration.GetSection("Trading:Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));
}

builder.Services.AddEntryPointListener(builder.Configuration);
builder.Services.AddTradingAuth(builder.Configuration);
builder.Services.AddTradingApplicationCore(builder.Configuration);
builder.Services.AddTradingPersistence(builder.Configuration);
builder.Services.AddTradingRisk(builder.Configuration);
builder.Services.AddTradingExchangeGateway(builder.Configuration);
builder.Services.AddTradingDataProtection(builder.Configuration);
builder.Services.AddTradingObservability(builder.Configuration);

var app = builder.Build();

TradingHostStartup.RegisterMetricsSources(app);
TradingHostStartup.ValidateBootGuards(app);
await TradingHostStartup.RunRecoveryAndSeedingAsync(app);

if (corsOrigins.Length > 0)
    app.UseCors(CorsPolicy);

// Rate limiter must run after CORS (so preflight OPTIONS still gets the
// CORS headers before potential 429s) and before auth so abusive
// callers cannot tie up the password hashing pipeline with floods.
app.UseRateLimiter();

app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

app.MapTradingEndpoints();

app.Run();

// Exposed so WebApplicationFactory<Program>-style tests can spin the host up.
public partial class Program;
