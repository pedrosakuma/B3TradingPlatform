using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace B3.Trading.SampleBot;

internal sealed class SampleBotWorker : BackgroundService
{
    private readonly ITradingPlatformRestClient _restClient;
    private readonly TradingPlatformWebSocketClient _webSocketClient;
    private readonly ISampleBotMarketDataClient _marketDataClient;
    private readonly SampleBotOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SampleBotWorker> _logger;

    public SampleBotWorker(
        ITradingPlatformRestClient restClient,
        TradingPlatformWebSocketClient webSocketClient,
        ISampleBotMarketDataClient marketDataClient,
        Microsoft.Extensions.Options.IOptions<SampleBotOptions> options,
        TimeProvider timeProvider,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        ILogger<SampleBotWorker> logger)
    {
        _restClient = restClient;
        _webSocketClient = webSocketClient;
        _marketDataClient = marketDataClient;
        _options = options.Value;
        _timeProvider = timeProvider;
        _applicationLifetime = applicationLifetime;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ValidateOptionalSubAccountAsync(stoppingToken);

        var workflow = new SampleBotWorkflow(
            _restClient,
            Microsoft.Extensions.Options.Options.Create(_options),
            _timeProvider,
            _loggerFactory.CreateLogger<SampleBotWorkflow>());
        var websocketTask = _webSocketClient.RunAsync(workflow, workflow.SubscriptionChannels, stoppingToken);
        Task marketDataTask = Task.CompletedTask;
        if (_options.DemoOrder.Enabled)
        {
            marketDataTask = _marketDataClient.RunAsync(workflow, _options.DemoOrder.Symbol, stoppingToken);
        }

        var result = await workflow.RunAsync(stoppingToken);
        if (_options.DemoOrder.Enabled)
        {
            _logger.LogInformation(
                "Sample strategy finished outcome={Outcome} clOrdId={ClOrdId} detail={Detail}.",
                result.Outcome,
                result.ClOrdId ?? "<none>",
                result.Detail ?? "<none>");

            if (_options.DemoOrder.PostWorkflowWait > TimeSpan.Zero)
                await Task.Delay(_options.DemoOrder.PostWorkflowWait, stoppingToken);

            _applicationLifetime.StopApplication();
        }

        await websocketTask;
        await marketDataTask;
    }

    private async Task ValidateOptionalSubAccountAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.SubAccountId))
            return;

        var configuredSubAccount = _options.SubAccountId.Trim();
        var subAccounts = await _restClient.GetSubAccountsAsync(cancellationToken);
        var match = subAccounts.FirstOrDefault(sub => string.Equals(sub.Id, configuredSubAccount, StringComparison.Ordinal));
        if (match is null)
            throw new InvalidOperationException($"Configured sub-account '{configuredSubAccount}' was not returned by GET /api/sub-accounts.");
        if (!match.Active)
            throw new InvalidOperationException($"Configured sub-account '{configuredSubAccount}' is deactivated.");

        _logger.LogInformation(
            "Validated configured sub-account {SubAccountId} ({DisplayName}).",
            match.Id,
            match.DisplayName ?? "no display name");
    }
}
