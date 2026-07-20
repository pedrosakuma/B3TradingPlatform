using B3.Trading.Infrastructure;
using B3.Trading.Application.Outbound;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Host.Hosted;

/// <summary>
/// Connects every per-firm <see cref="B3EntryPointClientGateway"/> at
/// startup and tears them down on shutdown. A failed cold-start connect
/// (e.g. <c>FixpRejectedException: Negotiate rejected</c> when matching
/// still considers a previous trading-host session valid) does NOT abort
/// host start — it falls back to a background retry loop with exponential
/// backoff so the firm recovers automatically once the peer reaps the
/// stale session (Bug A from #137). Subsequent peer-initiated terminations
/// are handled by the gateway's own hot-path reconnect loop, which fires
/// from the SDK's <c>Terminated</c> event.
/// </summary>
internal sealed class FirmGatewayConnector : IHostedService
{
    // Same shape as the gateway's hot-path reconnect: 1s base, ×2 to a
    // 30s cap, ±25% jitter, capped at 16 doublings so we don't overflow
    // for a long-degraded firm.
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

    private readonly FirmGatewayRegistry _registry;
    private readonly IOutboundRecoveryGate _recovery;
    private readonly ILogger<FirmGatewayConnector> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly List<Task> _backgroundLoops = new();

    public FirmGatewayConnector(
        FirmGatewayRegistry registry,
        IOutboundRecoveryGate recovery,
        ILogger<FirmGatewayConnector> logger)
    {
        _registry = registry;
        _recovery = recovery;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (firmId, gw) in _registry.Gateways)
            _backgroundLoops.Add(Task.Run(
                () => ConnectWhenRecoveredAsync(firmId, gw, _shutdownCts.Token)));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Background retry of <see cref="B3EntryPointClientGateway.ConnectAsync"/>
    /// with exponential backoff + jitter. Singleflight-safe per firm
    /// because the gateway's own connection fence prevents the
    /// hot-path Terminated-driven loop from racing with us if the SDK
    /// happens to fire Terminated mid-attempt.
    /// </summary>
    private async Task ConnectWhenRecoveredAsync(
        string firmId,
        B3EntryPointClientGateway gw,
        CancellationToken ct)
    {
        try
        {
            await _recovery.WaitUntilClassificationCompleteAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            try
            {
                await gw.ConnectAsync(ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "EntryPoint session connected for firm {Firm} after outbound recovery (attempt {N}).",
                    firmId, attempt);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "EntryPoint cold-start connect retry failed for firm {Firm} (attempt {N}); will retry after backoff.",
                    firmId, attempt);
            }
            var delay = ComputeBackoff(attempt);
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static TimeSpan ComputeBackoff(int attempt)
    {
        var basisMs = Math.Min(MaxReconnectDelay.TotalMilliseconds,
            InitialReconnectDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 16)));
        var jitterMs = Random.Shared.NextDouble() * 0.25 * basisMs;
        return TimeSpan.FromMilliseconds(basisMs + jitterMs);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _shutdownCts.Cancel(); } catch { /* swallow */ }
        if (_backgroundLoops.Count > 0)
        {
            try { await Task.WhenAll(_backgroundLoops).ConfigureAwait(false); }
            catch { /* loops swallow individually; defensive WhenAll */ }
        }
        _shutdownCts.Dispose();
        await _registry.DisposeAsync().ConfigureAwait(false);
    }
}
