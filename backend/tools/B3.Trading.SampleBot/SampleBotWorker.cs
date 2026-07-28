using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.SampleBot;

internal sealed class SampleBotWorker : BackgroundService, IPrivateFeedObserver
{
    private static readonly IReadOnlySet<string> SnapshotChannels = new HashSet<string>(StringComparer.Ordinal)
    {
        "orders.me",
        "executions.me",
        "positions.me",
    };

    private readonly TradingPlatformRestClient _restClient;
    private readonly TradingPlatformWebSocketClient _webSocketClient;
    private readonly SampleBotOptions _options;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<SampleBotWorker> _logger;
    private readonly object _snapshotGate = new();
    private HashSet<string> _seenSnapshots = new(StringComparer.Ordinal);
    private TaskCompletionSource<bool> _initialSnapshots = CreateSnapshotTcs();
    private string? _submittedClOrdId;

    public SampleBotWorker(
        TradingPlatformRestClient restClient,
        TradingPlatformWebSocketClient webSocketClient,
        Microsoft.Extensions.Options.IOptions<SampleBotOptions> options,
        IHostApplicationLifetime applicationLifetime,
        ILogger<SampleBotWorker> logger)
    {
        _restClient = restClient;
        _webSocketClient = webSocketClient;
        _options = options.Value;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ValidateOptionalSubAccountAsync(stoppingToken);

        var websocketTask = _webSocketClient.RunAsync(this, stoppingToken);
        await WaitForInitialSnapshotsAsync(stoppingToken);

        if (_options.DemoOrder.Enabled)
        {
            await RunDemoOrderWorkflowAsync(stoppingToken);
            if (_options.DemoOrder.ExitAfterWorkflow)
            {
                if (_options.DemoOrder.PostWorkflowWait > TimeSpan.Zero)
                    await Task.Delay(_options.DemoOrder.PostWorkflowWait, stoppingToken);
                _applicationLifetime.StopApplication();
            }
        }

        await websocketTask;
    }

    public async Task OnConnectedAsync(bool isReconnect, CancellationToken cancellationToken)
    {
        ResetSnapshots();
        if (!isReconnect)
            return;

        var orders = await _restClient.GetOrdersAsync(cancellationToken);
        _logger.LogInformation(
            "WebSocket reconnected; reconciled current working state via GET /api/orders ({OrderCount} order(s)).",
            orders.Count);
    }

    public Task OnDisconnectedAsync(Exception? error, CancellationToken cancellationToken)
    {
        if (error is null)
        {
            _logger.LogWarning("WebSocket disconnected; waiting to reconnect.");
        }
        else
        {
            _logger.LogWarning(error, "WebSocket disconnected; waiting to reconnect.");
        }

        return Task.CompletedTask;
    }

    public Task OnFrameAsync(PrivateFeedFrame frame, CancellationToken cancellationToken)
    {
        switch (frame)
        {
            case OrdersSnapshotFrame orders:
                MarkSnapshot("orders.me");
                _logger.LogInformation("orders.me snapshot seq={Seq} count={Count}.", orders.Seq, orders.Orders.Count);
                break;
            case ExecutionsSnapshotFrame executions:
                MarkSnapshot("executions.me");
                _logger.LogInformation("executions.me snapshot seq={Seq} count={Count}.", executions.Seq, executions.Executions.Count);
                break;
            case PositionsSnapshotFrame positions:
                MarkSnapshot("positions.me");
                _logger.LogInformation("positions.me snapshot seq={Seq} count={Count}.", positions.Seq, positions.Positions.Count);
                break;
            case OrderDeltaFrame order:
                _logger.LogInformation(
                    "orders.me delta seq={Seq} clOrdId={ClOrdId} symbol={Symbol} status={Status} leaves={LeavesQuantity}.",
                    order.Seq,
                    order.Order.ClOrdId,
                    order.Order.Symbol,
                    order.Order.Status,
                    order.Order.LeavesQuantity);
                LogCorrelation(order.Order.ClOrdId, "order state");
                break;
            case ExecutionDeltaFrame execution:
                _logger.LogInformation(
                    "executions.me delta seq={Seq} clOrdId={ClOrdId} kind={Kind} symbol={Symbol} lastQty={LastQuantity} lastPrice={LastPrice}.",
                    execution.Seq,
                    execution.Execution.ClOrdId,
                    execution.Execution.Kind,
                    execution.Execution.Symbol,
                    execution.Execution.LastQuantity,
                    execution.Execution.LastPrice);
                LogCorrelation(execution.Execution.ClOrdId, "execution");
                break;
            case PositionDeltaFrame position:
                _logger.LogInformation(
                    "positions.me delta seq={Seq} symbol={Symbol} netQty={NetQuantity} subAccount={SubAccountId}.",
                    position.Seq,
                    position.Position.Symbol,
                    position.Position.NetQuantity,
                    position.Position.SubAccountId ?? "<master>");
                break;
            case ProtocolErrorFrame protocolError:
                _logger.LogWarning("WebSocket protocol error code={Code} message={Message}", protocolError.Code, protocolError.Message);
                break;
        }

        return Task.CompletedTask;
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

    private async Task WaitForInitialSnapshotsAsync(CancellationToken cancellationToken)
    {
        Task snapshotTask;
        lock (_snapshotGate)
        {
            snapshotTask = _initialSnapshots.Task;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.InitialSnapshotTimeout);
        await snapshotTask.WaitAsync(timeout.Token);
    }

    private async Task RunDemoOrderWorkflowAsync(CancellationToken cancellationToken)
    {
        var idempotencyPrefix = _options.DemoOrder.IdempotencyKeyPrefix.Trim();
        var submitKey = $"{idempotencyPrefix}-submit-{Guid.NewGuid():N}";
        var submitResult = await _restClient.SubmitLimitOrderAsync(
            new SubmitOrderCommand(
                _options.DemoOrder.Symbol,
                _options.DemoOrder.SecurityId,
                _options.DemoOrder.Side,
                _options.DemoOrder.Quantity,
                _options.DemoOrder.Price,
                _options.SubAccountId),
            submitKey,
            cancellationToken);

        if (submitResult.Payload is null)
        {
            throw new InvalidOperationException(
                $"POST /api/orders failed with HTTP {(int)submitResult.StatusCode}: {submitResult.ErrorMessage ?? submitResult.ErrorCode ?? "unknown error"}.");
        }

        _submittedClOrdId = submitResult.Payload.ClOrdId;
        _logger.LogInformation(
            "POST /api/orders -> HTTP {StatusCode} mutationId={MutationId} clOrdId={ClOrdId} state={State} replayed={Replayed} status={OrderStatus} reason={Reason}.",
            (int)submitResult.StatusCode,
            submitResult.Payload.MutationId ?? "<none>",
            submitResult.Payload.ClOrdId ?? "<none>",
            submitResult.Payload.State,
            submitResult.Payload.Replayed,
            submitResult.Payload.Status ?? "<none>",
            submitResult.Payload.Reason ?? "<none>");

        if (string.Equals(submitResult.Payload.Status, "Rejected", StringComparison.OrdinalIgnoreCase)
            || !_options.DemoOrder.AutoCancel
            || string.IsNullOrWhiteSpace(submitResult.Payload.ClOrdId))
        {
            return;
        }

        if (_options.DemoOrder.CancelDelay > TimeSpan.Zero)
            await Task.Delay(_options.DemoOrder.CancelDelay, cancellationToken);

        var cancelKey = $"{idempotencyPrefix}-cancel-{Guid.NewGuid():N}";
        var cancelResult = await _restClient.CancelOrderAsync(submitResult.Payload.ClOrdId, cancelKey, cancellationToken);
        if (cancelResult.Payload is null)
        {
            throw new InvalidOperationException(
                $"DELETE /api/orders/{submitResult.Payload.ClOrdId} failed with HTTP {(int)cancelResult.StatusCode}: {cancelResult.ErrorMessage ?? cancelResult.ErrorCode ?? "unknown error"}.");
        }

        _logger.LogInformation(
            "DELETE /api/orders/{ClOrdId} -> HTTP {StatusCode} mutationId={MutationId} state={State} replayed={Replayed}.",
            submitResult.Payload.ClOrdId,
            (int)cancelResult.StatusCode,
            cancelResult.Payload.MutationId ?? "<none>",
            cancelResult.Payload.State,
            cancelResult.Payload.Replayed);
    }

    private void LogCorrelation(string clOrdId, string source)
    {
        if (!string.Equals(_submittedClOrdId, clOrdId, StringComparison.Ordinal))
            return;

        _logger.LogInformation("Observed correlated {Source} event for submitted clOrdId={ClOrdId}.", source, clOrdId);
    }

    private void ResetSnapshots()
    {
        lock (_snapshotGate)
        {
            _seenSnapshots = new HashSet<string>(StringComparer.Ordinal);
            _initialSnapshots = CreateSnapshotTcs();
        }
    }

    private void MarkSnapshot(string channel)
    {
        lock (_snapshotGate)
        {
            _seenSnapshots.Add(channel);
            if (SnapshotChannels.All(_seenSnapshots.Contains))
                _initialSnapshots.TrySetResult(true);
        }
    }

    private static TaskCompletionSource<bool> CreateSnapshotTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
