using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.DemoDriver;

/// <summary>
/// One per user bot. Logs in, then submits random buy/sell limit orders
/// around each symbol's reference price at the configured rate. Caps the
/// number of in-flight (working) orders per bot to keep the WorkingOrderBook
/// bounded during long demos.
/// </summary>
internal sealed class BotSubmitterWorker : BackgroundService
{
    private readonly TradingClient _client;
    private readonly DemoOrderRegistry _registry;
    private readonly DemoDriverOptions _options;
    private readonly DemoModeState _modeState;
    private readonly ILogger<BotSubmitterWorker> _log;
    private readonly Random _rng;

    public BotSubmitterWorker(
        TradingClient client,
        DemoOrderRegistry registry,
        DemoDriverOptions options,
        DemoModeState modeState,
        ILogger<BotSubmitterWorker> log)
    {
        _client = client;
        _registry = registry;
        _options = options;
        _modeState = modeState;
        _log = log;
        _rng = new Random(HashCode.Combine(client.Username, Environment.TickCount));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _modeState.WaitReadyAsync(stoppingToken);
        if (!_modeState.SubmitsEnabled)
        {
            _log.LogWarning("[bot {User}] submit disabled (exchange mode {Mode}); idling.",
                _client.Username, _modeState.ExchangeMode);
            return;
        }

        var period = TimeSpan.FromSeconds(1.0 / Math.Max(0.01, _options.SubmitRateHz));
        _log.LogInformation("[bot {User}] starting; period={Period}", _client.Username, period);

        try
        {
            await _client.EnsureAuthenticatedAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[bot {User}] login failed; aborting.", _client.Username);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_registry.CountFor(_client.Username) >= _options.MaxOpenOrdersPerBot)
                {
                    // Backpressure: pause submission until injector drains some.
                    await Task.Delay(period, stoppingToken);
                    continue;
                }

                if (_options.Symbols.Count == 0)
                {
                    _log.LogWarning("[bot {User}] no symbols configured; sleeping 5s.", _client.Username);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var sym = _options.Symbols[_rng.Next(_options.Symbols.Count)];
                var side = _rng.Next(2) == 0 ? "Buy" : "Sell";
                long qty = 100L * (1L + _rng.Next(10)); // 100..1000
                // ±0.5% around ref, rounded to 0.01.
                var spread = (decimal)((_rng.NextDouble() - 0.5) * 0.01);
                var raw = sym.ReferencePrice * (1m + spread);
                var price = Math.Round(raw, 2, MidpointRounding.AwayFromZero);

                var result = await _client.SubmitOrderAsync(sym.Symbol, side, qty, price, stoppingToken);
                switch (result.Kind)
                {
                    case SubmitResultKind.Accepted:
                        _registry.Register(new BotOrder(
                            ClOrdId: result.ClOrdId,
                            OwnerUsername: _client.Username,
                            Symbol: sym.Symbol,
                            Side: side,
                            Price: price,
                            Quantity: qty,
                            LeavesQuantity: qty,
                            CumulativeQuantity: 0));
                        break;
                    case SubmitResultKind.Rejected:
                        _log.LogDebug("[bot {User}] rejected: {Reason}", _client.Username, result.Reason);
                        break;
                    case SubmitResultKind.Failed:
                        _log.LogWarning("[bot {User}] submit failed: {Reason}", _client.Username, result.Reason);
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                        break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "[bot {User}] submit cycle error", _client.Username);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }

            await Task.Delay(period, stoppingToken);
        }
    }
}
