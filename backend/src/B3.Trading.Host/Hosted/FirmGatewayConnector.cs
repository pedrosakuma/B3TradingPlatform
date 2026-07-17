using B3.Trading.Infrastructure;
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
    private readonly ILogger<FirmGatewayConnector> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly List<Task> _backgroundLoops = new();

    public FirmGatewayConnector(FirmGatewayRegistry registry, ILogger<FirmGatewayConnector> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // StartAsync's CT is the host's start-budget token, not the
        // shutdown lifetime. Each firm gets a single attempt up front;
        // on failure we hand off to a background retry that lives until
        // StopAsync. This keeps boot non-blocking while still recovering
        // from a transient peer-side reject without a manual restart.
        foreach (var (firmId, gw) in _registry.Gateways)
        {
            try
            {
                await gw.ConnectAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("EntryPoint session connected for firm {Firm}.", firmId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EntryPoint connect failed for firm {Firm}; scheduling background retry with backoff.", firmId);
                _backgroundLoops.Add(Task.Run(() => RetryConnectAsync(firmId, gw, _shutdownCts.Token)));
            }
        }
    }

    /// <summary>
    /// Background retry of <see cref="B3EntryPointClientGateway.ConnectAsync"/>
    /// with exponential backoff + jitter. Singleflight-safe per firm
    /// because the gateway's own connection fence prevents the
    /// hot-path Terminated-driven loop from racing with us if the SDK
    /// happens to fire Terminated mid-attempt.
    /// </summary>
    private async Task RetryConnectAsync(string firmId, B3EntryPointClientGateway gw, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            var delay = ComputeBackoff(attempt);
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            try
            {
                await gw.ConnectAsync(ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "EntryPoint session connected for firm {Firm} on cold-start retry attempt {N}.",
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
