using B3.Trading.Application.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Application;

/// <summary>
/// Single-consumer hosted service that reacts to <see cref="AlgoSignal"/>s
/// queued by the API and the ER processor. RFC algo-orders-v0 §4.3:
/// "one IHostedService, bounded Channel, single consumer in v0, per-parent
/// serialisation via SemaphoreSlim".
///
/// <para>
/// In slice 5a the consumer body is intentionally a no-op — it logs and
/// counts signals so the wiring + back-pressure metric are observable end
/// to end without behavioural change. Slice 5b plugs in the Iceberg
/// state-machine reactor.
/// </para>
/// </summary>
public sealed class AlgoEngine : BackgroundService
{
    private readonly AlgoSignalQueue _queue;
    private readonly ILogger<AlgoEngine> _logger;

    public AlgoEngine(AlgoSignalQueue queue, ILogger<AlgoEngine> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AlgoEngine consumer task starting (slice 5a no-op reactor).");
        try
        {
            await foreach (var signal in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                MetricsRegistry.AlgoSignalsConsumed.Add(1,
                    new KeyValuePair<string, object?>("kind", SignalKind(signal)));
                _logger.LogDebug("AlgoEngine received signal {Kind} for algo {AlgoId}/{Firm}",
                    SignalKind(signal), AlgoIdOf(signal), signal.FirmId);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            _logger.LogInformation("AlgoEngine consumer task stopped.");
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Complete();
        return base.StopAsync(cancellationToken);
    }

    private static string SignalKind(AlgoSignal s) => s switch
    {
        AlgoCreatedSignal => "created",
        AlgoCancelRequestedSignal => "cancel_requested",
        ChildExecutionObservedSignal => "child_er",
        _ => "unknown",
    };

    private static ulong AlgoIdOf(AlgoSignal s) => s switch
    {
        AlgoCreatedSignal c => c.AlgoId,
        AlgoCancelRequestedSignal c => c.AlgoId,
        ChildExecutionObservedSignal c => c.AlgoId,
        _ => 0,
    };
}
