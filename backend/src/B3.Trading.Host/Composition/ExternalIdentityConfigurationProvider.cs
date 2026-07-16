using B3.Trading.Api.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace B3.Trading.Host.Composition;

internal sealed class ExternalIdentityConfigurationProvider : IExternalIdentityConfigurationProvider
{
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configurationManager;

    public ExternalIdentityConfigurationProvider(IOptions<AuthOptions> auth)
    {
        var external = auth.Value.ExternalIdentity;
        _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            external.EffectiveMetadataAddress,
            new OpenIdConnectConfigurationRetriever())
        {
            AutomaticRefreshInterval = TimeSpan.FromHours(12),
            RefreshInterval = TimeSpan.FromMinutes(5),
        };
    }

    public async Task<ExternalIdentityConfiguration> GetConfigurationAsync(CancellationToken ct = default)
    {
        var configuration = await _configurationManager.GetConfigurationAsync(ct);
        return new ExternalIdentityConfiguration(configuration.Issuer, configuration.SigningKeys.ToArray());
    }

    public void RequestRefresh() => _configurationManager.RequestRefresh();
}
