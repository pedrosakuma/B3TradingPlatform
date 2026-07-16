using B3.Trading.Application.Identity;
using B3.Trading.Infrastructure.Identity;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace B3.Trading.Host.Composition;

public static class TradingIdentityDirectoryServiceCollectionExtensions
{
    public static IServiceCollection AddTradingIdentityDirectory(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<IdentityDirectoryOptions>(
            configuration.GetSection(IdentityDirectoryOptions.SectionName));
        services.PostConfigure<IdentityDirectoryOptions>(opts =>
        {
            if (!string.IsNullOrWhiteSpace(opts.Path))
                return;

            var persistDir = configuration
                .GetSection(PersistenceOptions.SectionName)
                .Get<PersistenceOptions>()?.DataDirectory
                ?? "data";
            opts.Path = Path.Combine(persistDir, "identity", "users.db");
        });

        services.AddSingleton<ITradingUserDirectory>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<IdentityDirectoryOptions>>().Value;
            if (opts.ExpectedWriterCount != 1)
                throw new InvalidOperationException($"{IdentityDirectoryOptions.SectionName}:ExpectedWriterCount must be 1.");

            return opts.Provider switch
            {
                var p when string.Equals(p, IdentityDirectoryProviders.InMemory, StringComparison.OrdinalIgnoreCase)
                    => ActivatorUtilities.CreateInstance<InMemoryTradingUserDirectory>(sp),
                var p when string.Equals(p, IdentityDirectoryProviders.Sqlite, StringComparison.OrdinalIgnoreCase)
                    => ActivatorUtilities.CreateInstance<SqliteTradingUserDirectory>(sp),
                _ => throw new InvalidOperationException(
                    $"{IdentityDirectoryOptions.SectionName}:Provider must be '{IdentityDirectoryProviders.InMemory}' or '{IdentityDirectoryProviders.Sqlite}'."),
            };
        });

        return services;
    }
}
