using Microsoft.Extensions.Logging;

namespace B3.Trading.DemoDriver;

/// <summary>
/// Hosted resolution of the trading-host's exchange mode + readiness, shared
/// between the submitter and injector workers. Polls /health until the host
/// is ready (or until cancellation), then exposes derived flags.
///
/// The mode determines behavior per the rubber-duck design review:
///   Simulator   → submits enabled, injects enabled (target mode).
///   Mock        → submits enabled, injects disabled (orders sit working).
///   Stub        → submits enabled but will get 502; we let the bots try
///                 anyway, so the operator sees the failure mode honestly.
///   Real        → submits enabled, injects disabled (cross requires real
///                 cross-firm setup; out of D1 scope).
///   Unavailable → submits disabled (loud warning); host explicitly refuses
///                 orders. Bots would just spam 502s.
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
    public bool SubmitsEnabled { get; private set; }
    public bool InjectsEnabled { get; private set; }

    public Task WaitReadyAsync(CancellationToken ct)
    {
        if (_ready.Task.IsCompleted) return Task.CompletedTask;
        return _ready.Task.WaitAsync(ct);
    }

    public async Task BootstrapAsync(TradingClient probeClient, CancellationToken ct)
    {
        // Wait for /health to return ready (or at least respond) — gives
        // trading-host time to come up if compose started us in parallel.
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
        }
        else
        {
            ExchangeMode = health.Exchange?.Mode ?? "unknown";
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

        _log.LogInformation("[mode] resolved exchange={Mode} submits={Submits} injects={Injects} (DEMO_MODE={Demo})",
            ExchangeMode, SubmitsEnabled, InjectsEnabled, explicitMode);
        _ready.TrySetResult();
    }

    private void ApplyAutoDetect()
    {
        switch (ExchangeMode)
        {
            case "Simulator":
                SubmitsEnabled = true;
                InjectsEnabled = true;
                break;
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
                _log.LogWarning("[mode] exchange=Unavailable — demo requires Mode=Simulator. Configure docker-compose.demo.yml overlay.");
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
