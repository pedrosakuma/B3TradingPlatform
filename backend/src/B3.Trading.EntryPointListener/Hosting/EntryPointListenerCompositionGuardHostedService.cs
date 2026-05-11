using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// Tiny <see cref="IHostedService"/> registered ahead of
/// <see cref="FixpListenerHostedService"/> that runs
/// <see cref="EntryPointListenerCompositionGuard.Validate"/> during
/// host startup. By failing in <see cref="StartAsync"/> we get a clear
/// boot-time exception (issue #185) before the listener binds its TCP
/// socket and starts accepting connections that would silently swallow
/// orders.
/// </summary>
internal sealed class EntryPointListenerCompositionGuardHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly EntryPointListenerOptions _opts;

    public EntryPointListenerCompositionGuardHostedService(
        IServiceProvider services,
        IOptions<EntryPointListenerOptions> opts)
    {
        _services = services;
        _opts = opts.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EntryPointListenerCompositionGuard.Validate(_services, _opts);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
