using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            services.AddSingleton<Hosting.FixpListenerHostedService>();
            services.AddHostedService(sp =>
                sp.GetRequiredService<Hosting.FixpListenerHostedService>());
        }

        return services;
    }
}
