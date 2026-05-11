using B3.Trading.Application.Lifecycle;
using Microsoft.Extensions.Options;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Default <see cref="IFirmDirectory"/> implementation. Always reads
/// per-firm wire configuration from <see cref="ExchangeOptions"/>; in Real
/// mode an injected <see cref="FirmGatewayRegistry"/> overlays live FIXP
/// session state on top. The endpoint shape is identical regardless of
/// mode so dashboards consume a single schema.
/// </summary>
public sealed class ConfigFirmDirectory : IFirmDirectory
{
    private readonly IOptions<ExchangeOptions> _options;
    private readonly FirmGatewayRegistry? _registry;

    public ConfigFirmDirectory(IOptions<ExchangeOptions> options, FirmGatewayRegistry? registry = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _registry = registry;
    }

    public FirmDirectorySnapshot Snapshot()
    {
        var opts = _options.Value;
        var mode = opts.ResolveMode();
        var firms = opts.Firms.Select(cfg =>
        {
            B3EntryPointClientGateway? live = null;
            if (_registry is not null && _registry.TryGet(cfg.FirmId, out var gw))
                live = gw;
            return new FirmDirectoryEntry(
                FirmId: cfg.FirmId,
                Endpoint: cfg.Endpoint,
                SessionId: cfg.SessionId,
                SessionState: live?.SessionStateTag,
                SessionVerId: live?.CurrentSessionVerId,
                Reconnecting: live?.IsReconnecting);
        }).ToArray();
        return new FirmDirectorySnapshot(mode.ToString(), firms);
    }
}
