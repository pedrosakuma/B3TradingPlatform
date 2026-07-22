using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.DemoDriver;

/// <summary>
/// Single instance. Logs in as the configured admin and periodically picks a
/// random working order from <see cref="DemoOrderRegistry"/> to inject either
/// a PartialFill or a Fill into via <c>POST /api/admin/simulator/er</c>.
///
/// Only runs in Simulator mode. In other modes the worker exits early (the
/// bots will still submit; orders just sit working in Mock or fail gracefully
/// in Stub/Unavailable).
/// </summary>
internal sealed class InjectorWorker : BackgroundService
{
    private readonly TradingClient _admin;
    private readonly DemoOrderRegistry _registry;
    private readonly DemoDriverOptions _options;
    private readonly DemoModeState _modeState;
    private readonly ILogger<InjectorWorker> _log;
    private readonly Random _rng = new();

    public InjectorWorker(
        TradingClient admin,
        DemoOrderRegistry registry,
        DemoDriverOptions options,
        DemoModeState modeState,
        ILogger<InjectorWorker> log)
    {
        _admin = admin;
        _registry = registry;
        _options = options;
        _modeState = modeState;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _modeState.WaitReadyAsync(stoppingToken);
        if (!_modeState.InjectsEnabled)
        {
            _log.LogInformation("[injector] disabled (exchange mode {Mode}); idling.", _modeState.ExchangeMode);
            return;
        }

        try
        {
            await _admin.EnsureAuthenticatedAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[injector] admin login failed; aborting.");
            return;
        }

        var period = TimeSpan.FromSeconds(1.0 / Math.Max(0.01, _options.InjectRateHz));
        _log.LogInformation("[injector] starting; period={Period} actor={Actor}", period, _admin.Username);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var order = _registry.PickRandomWorking(_rng);
                if (order is null)
                {
                    await Task.Delay(period, stoppingToken);
                    continue;
                }

                // Decide: full fill (30%) or partial (70%). Fill quantity is
                // bounded by remaining leaves so we never overfill.
                var leaves = order.LeavesQuantity;
                var lastQty = _rng.NextDouble() < 0.3
                    ? leaves
                    : Math.Max(1L, (long)Math.Round(leaves * (0.2 + _rng.NextDouble() * 0.5)));
                lastQty = Math.Min(lastQty, leaves);
                var lastPx = order.Price; // synthetic — at the resting price.
                var execType = lastQty >= leaves ? "Fill" : "PartialFill";

                var inject = await _admin.InjectErAsync(order.ClOrdId, execType, lastQty, lastPx, stoppingToken);
                if (inject.Success)
                {
                    _registry.OnInjected(order.ClOrdId, inject.LeavesQuantity, inject.CumulativeQuantity);
                    _log.LogDebug("[injector] {Type} {ClOrdId} owner={Owner} qty={Qty} leaves={Leaves}",
                        execType, order.ClOrdId, order.OwnerUsername, lastQty, inject.LeavesQuantity);
                }
                else
                {
                    // 404/400 = stale or overfill; evict and move on.
                    _registry.TryEvict(order.ClOrdId);
                    _log.LogDebug("[injector] evict stale {ClOrdId}: {Reason}", order.ClOrdId, inject.Reason);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "[injector] cycle error");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }

            await Task.Delay(period, stoppingToken);
        }
    }
}
