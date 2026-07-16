using Microsoft.Extensions.Logging;

namespace B3.Trading.DemoDriver;

/// <summary>
/// Hosted resolution of the trading-host's exchange mode + readiness, shared
/// between the submitter and injector workers. Polls /health for capability
/// discovery, then /ready before enabling any submitting mode.
///
/// Auto-detect uses <c>health.exchange.erInjectionEnabled</c> (Mock +
/// AllowErInjection=true, after #163) to enable injects, NOT the legacy
/// <c>mode == "Simulator"</c> string check that no longer exists. Mode
/// alone is the fallback for submits-vs-no-submits decisions.
///   ER injection enabled (Mock+flag) → submits + injects (target mode).
///   Mock                             → submits only (orders sit working).
///   Stub                             → submits attempted (will get 502).
///   Real                             → submits only (cross requires real
///                                       cross-firm setup; out of D1 scope).
///   Unavailable                      → submits disabled (loud warning);
///                                       host explicitly refuses orders.
/// </summary>
internal sealed class DemoModeState
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DemoDriverOptions _options;
    private readonly ILogger<DemoModeState> _log;

    public DemoModeState(DemoDriverOptions options, ILogger<DemoModeState> log)
    {
        _options = options;
        _log = log;
    }

    public string ExchangeMode { get; private set; } = "unknown";
    public bool ErInjectionEnabled { get; private set; }
    public bool SubmitsEnabled { get; private set; }
    public bool InjectsEnabled { get; private set; }

    public Task WaitReadyAsync(CancellationToken ct)
    {
        if (_ready.Task.IsCompleted) return Task.CompletedTask;
        return _ready.Task.WaitAsync(ct);
    }

    public async Task BootstrapAsync(TradingClient probeClient, CancellationToken ct)
    {
        // Wait for /health to respond so capabilities can be discovered.
        // Order submission is gated separately on /ready below.
        var deadline = DateTime.UtcNow.AddMinutes(2);
        HealthResponse? health = null;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                health = await probeClient.GetHealthAsync(ct);
                if (health is not null) break;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[mode] /health probe failed; retrying");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        if (health is null)
        {
            _log.LogError("[mode] /health never became reachable; assuming Unavailable.");
            ExchangeMode = "Unavailable";
            ErInjectionEnabled = false;
        }
        else
        {
            ExchangeMode = health.Exchange?.Mode ?? "unknown";
            ErInjectionEnabled = health.Exchange?.ErInjectionEnabled ?? false;
        }

        var explicitMode = _options.Mode?.ToLowerInvariant() ?? "auto-detect";
        switch (explicitMode)
        {
            case "submit-only":
                SubmitsEnabled = true;
                InjectsEnabled = false;
                break;
            case "simulator-inject":
                SubmitsEnabled = true;
                InjectsEnabled = true;
                break;
            default: // auto-detect
                ApplyAutoDetect();
                break;
        }

        if (SubmitsEnabled && !await WaitForOrderIngressReadyAsync(probeClient, deadline, ct))
        {
            _log.LogError(
                "[mode] /ready never allowed order ingress for exchange={Mode}; disabling submits and injects.",
                ExchangeMode);
            SubmitsEnabled = false;
            InjectsEnabled = false;
        }

        _log.LogInformation("[mode] resolved exchange={Mode} submits={Submits} injects={Injects} (DEMO_MODE={Demo})",
            ExchangeMode, SubmitsEnabled, InjectsEnabled, explicitMode);
        _ready.TrySetResult();
    }

    private async Task<bool> WaitForOrderIngressReadyAsync(
        TradingClient probeClient,
        DateTime deadline,
        CancellationToken ct)
    {
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                if (await probeClient.IsReadyAsync(ct)) return true;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[mode] /ready probe failed; retrying");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return false;
    }

    private void ApplyAutoDetect()
    {
        // #163: capability check replaces "mode == Simulator" string check.
        // erInjectionEnabled is true iff Mock + AllowErInjection=true.
        if (ErInjectionEnabled)
        {
            SubmitsEnabled = true;
            InjectsEnabled = true;
            return;
        }

        switch (ExchangeMode)
        {
            case "Mock":
            case "Real":
                SubmitsEnabled = true;
                InjectsEnabled = false;
                break;
            case "Stub":
                _log.LogWarning("[mode] exchange=Stub — orders will return 502; bots will keep trying.");
                SubmitsEnabled = true;
                InjectsEnabled = false;
                break;
            case "Unavailable":
                _log.LogWarning("[mode] exchange=Unavailable — demo requires Mock+AllowErInjection. Configure docker-compose.demo.yml overlay.");
                SubmitsEnabled = false;
                InjectsEnabled = false;
                break;
            default:
                _log.LogWarning("[mode] unknown exchange mode '{Mode}'; defaulting to submits-only.", ExchangeMode);
                SubmitsEnabled = true;
                InjectsEnabled = false;
                break;
        }
    }
}
