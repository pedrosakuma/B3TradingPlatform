using B3.Trading.Infrastructure;
using Microsoft.Extensions.Hosting;

namespace B3.Trading.Host.Hosted;

/// <summary>
/// Forces construction of <see cref="EntryPointExecutionReportRouter"/> at
/// app start (DI is otherwise lazy). The router subscribes to ER events
/// in its constructor and unsubscribes on dispose.
/// </summary>
internal sealed class EntryPointRouterStarter : IHostedService
{
    private readonly EntryPointExecutionReportRouter _router;
    public EntryPointRouterStarter(EntryPointExecutionReportRouter router) => _router = router;
    public Task StartAsync(CancellationToken cancellationToken) { _ = _router; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) { _router.Dispose(); return Task.CompletedTask; }
}
