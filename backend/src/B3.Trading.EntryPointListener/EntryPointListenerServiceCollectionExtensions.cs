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
            // Sub-issue #172 (F): outbound ER multiplexer wiring.
            services
                .AddOptions<BotErMultiplexerOptions>()
                .Bind(configuration.GetSection(BotErMultiplexerOptions.SectionName));

            // The mapping registry is the lookup key for routing ERs to
            // the originating bot. Host.Program.cs already registers it
            // for the production composition; TryAdd keeps that as the
            // winning binding while letting handshake-only test hosts
            // (which never persist mappings anyway) fall through to the
            // in-memory implementation.
            services.TryAddSingleton<InMemoryUserBotOrderMappingRegistry>();
            services.TryAddSingleton<IUserBotOrderMappingRegistry>(sp =>
                sp.GetRequiredService<InMemoryUserBotOrderMappingRegistry>());

            services.AddSingleton<BotSessionConnectionDirectory>();
            services.AddSingleton<IBotSessionConnectionDirectory>(sp =>
                sp.GetRequiredService<BotSessionConnectionDirectory>());

            services.AddSingleton<BotOutboundCoordinator>();

            services.AddSingleton<BotErMultiplexer>();
            services.AddSingleton<IBotErRouter>(sp => sp.GetRequiredService<BotErMultiplexer>());
            services.AddHostedService(sp => sp.GetRequiredService<BotErMultiplexer>());

            services.AddSingleton<BotSessionSeqCheckpointer>();
            services.AddHostedService(sp => sp.GetRequiredService<BotSessionSeqCheckpointer>());

            services.AddSingleton<Hosting.FixpListenerHostedService>();
            services.AddHostedService(sp =>
                sp.GetRequiredService<Hosting.FixpListenerHostedService>());
        }

        return services;
    }
}
