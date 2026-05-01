using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.Extensions.Options;
using B3.Trading.Api;
using B3.Trading.Api.Auth;
using B3.Trading.Api.WebSockets;
using B3.Trading.Application;
using B3.Trading.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ExchangeOptions>(
    builder.Configuration.GetSection(ExchangeOptions.SectionName));
builder.Services.Configure<AuthOptions>(
    builder.Configuration.GetSection(AuthOptions.SectionName));

// Application-layer singletons: registries, books, processor, sink.
builder.Services.AddSingleton<EndClientRegistry>();
builder.Services.AddSingleton<ClOrdIdPrefixRegistry>();
builder.Services.AddSingleton<OrderOwnershipMap>();
builder.Services.AddSingleton<WorkingOrderBook>();
builder.Services.AddSingleton<PositionKeeper>();
builder.Services.AddSingleton<SubscriptionManager>();
builder.Services.AddSingleton<IExecutionEventSink, WebSocketExecutionEventSink>();
builder.Services.AddSingleton<ExecutionReportProcessor>();
builder.Services.AddSingleton<JwtIssuer>();

// Auth: JWT bearer with explicit claim mapping. We disable the legacy
// inbound mapping so 'sub' stays 'sub' (not ClaimTypes.NameIdentifier),
// matching what JwtIssuer emits.
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
// Configure JwtBearerOptions through the options pipeline so test-time
// AuthOptions overrides (in-memory config) propagate end-to-end.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<AuthOptions>>((options, authHolder) =>
    {
        var authOpts = authHolder.Value;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = authOpts.Issuer,
            ValidAudience = authOpts.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOpts.SigningKey)),
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = JwtIssuer.RoleClaim,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        // Browsers can't easily set Authorization on a WS handshake;
        // accept ?access_token= for /ws only.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (string.IsNullOrEmpty(ctx.Token) &&
                    ctx.Request.Path.StartsWithSegments("/ws") &&
                    ctx.Request.Query.TryGetValue("access_token", out var accessToken))
                {
                    ctx.Token = accessToken;
                }
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

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

app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { service = "B3TradingPlatform", status = "bootstrap" }));
app.MapGet("/health", () => Results.Ok("ok"));

app.MapAuth();
app.MapOrders();
app.MapPositions();
app.MapWebSocketHub();

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

