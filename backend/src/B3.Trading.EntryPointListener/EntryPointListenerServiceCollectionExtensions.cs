using B3.Trading.Application;
using B3.Trading.Application.UserBots;
using B3.Trading.EntryPointListener.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace B3.Trading.EntryPointListener;

/// <summary>
/// IServiceCollection extension for wiring the FIXP listener into the host.
/// </summary>
public static class EntryPointListenerServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="EntryPointListenerOptions"/> and — when
    /// <c>Trading:EntryPointListener:Enabled=true</c> — registers
    /// <see cref="Hosting.FixpListenerHostedService"/> as a hosted service.
    /// </summary>
    public static IServiceCollection AddEntryPointListener(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<EntryPointListenerOptions>()
            .Bind(configuration.GetSection(EntryPointListenerOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<EntryPointListenerOptions>,
            EntryPointListenerOptionsValidator>();

        var enabled = configuration
            .GetSection(EntryPointListenerOptions.SectionName)
            .GetValue<bool>("Enabled");

        if (enabled)
        {
            // Issue #185: composition guard runs first so a missing
            // order-path dependency aborts host startup with a clear,
            // actionable message before FixpListenerHostedService binds
            // its socket and starts silently swallowing inbound orders.
            // IHostedService.StartAsync is invoked in registration order,
            // so registering the guard ahead of the listener guarantees
            // it fires first.
            services.AddHostedService<Hosting.EntryPointListenerCompositionGuardHostedService>();

            // Sub-issue #172 (F): outbound ER multiplexer wiring.
            services
                .AddOptions<BotErMultiplexerOptions>()
                .Bind(configuration.GetSection(BotErMultiplexerOptions.SectionName));

            // The mapping registry is the lookup key for routing ERs to
            // the originating bot.
            services.TryAddSingleton<InMemoryUserBotOrderMappingRegistry>();
            services.TryAddSingleton<IUserBotOrderMappingRegistry>(sp =>
                sp.GetRequiredService<InMemoryUserBotOrderMappingRegistry>());

            services.AddSingleton<BotSessionConnectionDirectory>();
            services.AddSingleton<IBotSessionConnectionDirectory>(sp =>
                sp.GetRequiredService<BotSessionConnectionDirectory>());

            services.AddSingleton<BotOutboundCoordinator>();

            services.AddSingleton<BotErMultiplexer>();
            services.AddSingleton<IBotErRouter>(sp => sp.GetRequiredService<BotErMultiplexer>());
            // RFC §5.2 (F2). Register the multiplexer as a fan-out sink
            // so the EventDispatcher TryWrites onto its (unbounded)
            // internal channel UNDER the dispatcher lock — preserving
            // per-bot ordering = WAL append order. Unbounded is required:
            // dropping an ER pre-credential-resolve would leave the bot
            // unable to emit any per-bot recovery signal (RFC §5.4),
            // and memory is bounded transitively by the per-credential
            // BotOutboundBuffer.MaxMessages caps (§5.2 / §6.3).
            services.AddSingleton<B3.Trading.Application.Persistence.IExecutionFanOutSink>(
                sp => sp.GetRequiredService<BotErMultiplexer>());
            services.AddHostedService(sp => sp.GetRequiredService<BotErMultiplexer>());

            services.AddSingleton<BotSessionSeqCheckpointer>();
            services.AddHostedService(sp => sp.GetRequiredService<BotSessionSeqCheckpointer>());

            // Sub-issue #174 (H): rate limiter + per-user session counter.
            services.AddSingleton(sp =>
            {
                var opts = sp.GetRequiredService<IOptions<EntryPointListenerOptions>>().Value;
                return new RateLimiterRegistry(opts);
            });
            services.AddSingleton<UserSessionCounter>();

            // Issue #185: build the order adapter through a DI factory.
            // The composition guard above produces the friendly error
            // message before we get here when any of the four order-path
            // dependencies are missing in production wiring; this
            // factory is the structural backstop. We resolve via
            // GetService (not GetRequiredService) so test harnesses can
            // register null-returning factories purely to satisfy
            // IServiceProviderIsService — see
            // OrderPathStubRegistrations in the test project.
            services.AddSingleton<Hosting.FixpOrderAdapter>(sp =>
                new Hosting.FixpOrderAdapter(
                    sp.GetService<SymbolDirectory>()!,
                    sp.GetService<OrderSubmissionService>()!,
                    sp.GetService<OrderCancelService>()!,
                    sp.GetService<IUserBotOrderMappingRegistry>()!,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Hosting.FixpListenerHostedService>>()));

            // Use an explicit factory so DI binds to the internal
            // overload that accepts FixpOrderAdapter (issue #185).
            services.AddSingleton<Hosting.FixpListenerHostedService>(sp =>
                new Hosting.FixpListenerHostedService(
                    sp.GetRequiredService<IOptions<EntryPointListenerOptions>>(),
                    sp.GetRequiredService<IUserBotCredentialRegistry>(),
                    sp.GetRequiredService<IUserBotSessionRegistry>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Hosting.FixpListenerHostedService>>(),
                    sp.GetRequiredService<Hosting.FixpOrderAdapter>(),
                    sp.GetRequiredService<IBotSessionConnectionDirectory>(),
                    sp.GetRequiredService<BotOutboundCoordinator>(),
                    sp.GetRequiredService<RateLimiterRegistry>(),
                    sp.GetRequiredService<UserSessionCounter>(),
                    sp.GetService<TimeProvider>()));
            services.AddHostedService(sp =>
                sp.GetRequiredService<Hosting.FixpListenerHostedService>());
        }

        return services;
    }
}
