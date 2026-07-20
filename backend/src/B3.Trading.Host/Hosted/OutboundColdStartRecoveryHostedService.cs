using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Host.Composition;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Host.Hosted;

internal sealed class OutboundColdStartRecoveryHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ActiveHostFence _fence;
    private readonly IEventStoreHealth _wal;
    private readonly OutboundRecoveryState _state;
    private readonly OutboundColdStartRecoveryCoordinator _coordinator;
    private readonly SnapshotService _snapshots;
    private readonly PersistenceOptions _persistence;
    private readonly IDrainController _drain;
    private readonly ILogger<OutboundColdStartRecoveryHostedService> _logger;

    public OutboundColdStartRecoveryHostedService(
        IServiceProvider services,
        ActiveHostFence fence,
        IEventStoreHealth wal,
        OutboundRecoveryState state,
        OutboundColdStartRecoveryCoordinator coordinator,
        SnapshotService snapshots,
        IOptions<PersistenceOptions> persistence,
        IDrainController drain,
        ILogger<OutboundColdStartRecoveryHostedService> logger)
    {
        _services = services;
        _fence = fence;
        _wal = wal;
        _state = state;
        _coordinator = coordinator;
        _snapshots = snapshots;
        _persistence = persistence.Value;
        _drain = drain;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken) =>
        _persistence.Enabled
            ? base.StartAsync(cancellationToken)
            : ExecuteAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_persistence.Enabled)
            await Task.Yield();
        if (!_fence.IsHeld && !_fence.TryAcquire())
        {
            _state.FailFence(_fence.Failure?.GetType().Name ?? "active_host_fence_unavailable");
            return;
        }
        if (!_wal.IsHealthy)
        {
            _state.Fail(_wal.TerminalFault ?? new InvalidOperationException("WAL is unhealthy."));
            return;
        }

        try
        {
            _state.MarkRestoring();
            await TradingHostStartup.RunRecoveryAndSeedingAsync(
                _services,
                stoppingToken).ConfigureAwait(false);
            _state.MarkClassifying();
            await _coordinator.RunAsync(stoppingToken).ConfigureAwait(false);
            if (_persistence.Enabled
                && !await _snapshots.TryTakeSnapshotAsync(stoppingToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Outbound recovery classification checkpoint could not be published.");
            }
            _state.Complete();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _state.Fail(ex);
            _drain.BeginDrain("outbound_cold_start_recovery_failed");
            _logger.LogCritical(
                ex,
                "Outbound cold-start recovery failed; venue connection, business ingress and readiness remain closed.");
        }
    }
}
