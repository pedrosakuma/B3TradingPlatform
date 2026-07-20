using B3.Trading.EntryPointListener;
using B3.Trading.Api.RateLimit;
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
builder.Services.AddTradingIdentityDirectory(builder.Configuration);
builder.Services.AddTradingApplicationCore(builder.Configuration);
builder.Services.AddTradingPersistence(builder.Configuration);
builder.Services.AddTradingRisk(builder.Configuration);
builder.Services.AddTradingExchangeGateway(builder.Configuration);
builder.Services.AddTradingDataProtection(builder.Configuration);
builder.Services.AddTradingObservability(builder.Configuration);
builder.Services.AddTradingRateLimit(builder.Configuration);

var app = builder.Build();

TradingHostStartup.RegisterMetricsSources(app);
TradingHostStartup.ValidateBootGuards(app);
await TradingHostStartup.RunIdentityDirectoryStartupAsync(app);

if (corsOrigins.Length > 0)
    app.UseCors(CorsPolicy);

// Rate limiter must run after CORS (so preflight OPTIONS still gets the
// CORS headers before potential 429s) and before auth so abusive
// callers cannot tie up the password hashing pipeline with floods.
app.UseRateLimiter();

app.UseWebSockets();
app.UseAuthentication();
// Q4.4 (#304). Per-user × endpoint token-bucket runs AFTER auth so the
// JWT sub-claim is the partition key for authenticated traffic, and
// BEFORE authorization so the 429 short-circuits the handler pipeline
// before any per-firm scoping or claims check runs.
app.UseTradingRateLimit();
app.UseAuthorization();

app.MapTradingEndpoints();

app.Run();

// Exposed so WebApplicationFactory<Program>-style tests can spin the host up.
public partial class Program;
