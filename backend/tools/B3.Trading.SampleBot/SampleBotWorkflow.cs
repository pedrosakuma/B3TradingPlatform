using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.SampleBot;

internal sealed class SampleBotWorkflow : IPrivateFeedObserver, ISampleBotMarketDataObserver
{
    private static readonly IReadOnlySet<string> SnapshotChannels = new HashSet<string>(StringComparer.Ordinal)
    {
        "orders.me",
        "executions.me",
        "positions.me",
    };

    private readonly ITradingPlatformRestClient _restClient;
    private readonly SampleBotOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SampleBotWorkflow> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _stateSignal = new(0, int.MaxValue);
    private readonly TaskCompletionSource<SampleBotWorkflowResult> _completion = CreateResultTcs();
    private readonly TaskCompletionSource<bool> _workingOrderReady = CreateBoolTcs();

    private HashSet<string> _seenSnapshots = new(StringComparer.Ordinal);
    private TaskCompletionSource<bool> _initialSnapshots = CreateBoolTcs();
    private TradingOrder? _knownWorkingOrder;
    private string? _submittedClOrdId;
    private MarketDataQuote? _latestQuote;
    private string? _phase;
    private bool _privateConnected;
    private bool _marketDataConnected;
    private bool _submitAttempted;
    private bool _cancelAttempted;
    private bool _feedLossHandled;
    private string? _blockedReason;

    public SampleBotWorkflow(
        ITradingPlatformRestClient restClient,
        IOptions<SampleBotOptions> options,
        TimeProvider timeProvider,
        ILogger<SampleBotWorkflow> logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _restClient = restClient;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _delayAsync = delayAsync ?? ((delay, ct) => Task.Delay(delay, timeProvider, ct));
    }

    public IReadOnlyList<string> SubscriptionChannels =>
        _options.DemoOrder.Enabled && _options.DemoOrder.RequireOpenPhase
            ? [.. PrivateFeedProtocol.PrivateChannels, $"phases.{_options.DemoOrder.Symbol}"]
            : [.. PrivateFeedProtocol.PrivateChannels];

    internal bool HasLiveOrder
    {
        get
        {
            lock (_gate)
            {
                return HasLiveOrderUnderLock();
            }
        }
    }

    internal Task WorkingOrderReady => _workingOrderReady.Task;

    public async Task<SampleBotWorkflowResult> RunAsync(CancellationToken cancellationToken)
    {
        if (!_options.DemoOrder.Enabled)
        {
            _logger.LogInformation("Trading workflow disabled; observing private feed only.");
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            return new SampleBotWorkflowResult("disabled", null, null);
        }

        CancellationTokenSource? timeoutLifetime = null;
        Task? timeoutTask = null;

        try
        {
            await WaitForInitialSnapshotsAsync(cancellationToken);

            TradingOrder? existing;
            lock (_gate)
            {
                existing = _knownWorkingOrder;
            }

            if (existing is not null)
            {
                throw new InvalidOperationException(
                    $"Refusing to submit because the private snapshot already reports a live order for {_options.DemoOrder.Symbol} (clOrdId={existing.ClOrdId}).");
            }

            var command = await WaitForSubmissionReadinessAsync(cancellationToken);
            var submitResult = await _restClient.SubmitLimitOrderAsync(command, BuildIdempotencyKey("submit"), cancellationToken);
            if (submitResult.Payload is null)
            {
                throw new InvalidOperationException(
                    $"POST /api/orders failed with HTTP {(int)submitResult.StatusCode}: {submitResult.ErrorMessage ?? submitResult.ErrorCode ?? "unknown error"}.");
            }

            if (string.Equals(submitResult.Payload.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Sample strategy order was rejected immediately. clOrdId={ClOrdId} reason={Reason}.",
                    submitResult.Payload.ClOrdId ?? "<none>",
                    submitResult.Payload.Reason ?? "<none>");
                return new SampleBotWorkflowResult("rejected", submitResult.Payload.ClOrdId, submitResult.Payload.Reason);
            }

            if (string.IsNullOrWhiteSpace(submitResult.Payload.ClOrdId))
                throw new InvalidOperationException("POST /api/orders succeeded without returning a clOrdId.");

            lock (_gate)
            {
                _submitAttempted = true;
                _submittedClOrdId = submitResult.Payload.ClOrdId;
                _knownWorkingOrder = new TradingOrder(
                    submitResult.Payload.ClOrdId,
                    command.Symbol,
                    command.SecurityId,
                    command.Side,
                    "Limit",
                    command.Quantity,
                    command.Quantity,
                    0,
                    command.Price,
                    submitResult.Payload.Status ?? "Pending",
                    SubAccountId: command.SubAccountId);
            }

            var reconcileResult = await ReconcileSubmittedOrderAsync(submitResult.Payload.ClOrdId, cancellationToken);
            if (reconcileResult is not null)
                return reconcileResult;

            _logger.LogInformation(
                "Submitted single-order strategy limit order. clOrdId={ClOrdId} symbol={Symbol} side={Side} qty={Quantity} price={Price} referenceSource={ReferenceSource}.",
                submitResult.Payload.ClOrdId,
                command.Symbol,
                command.Side,
                command.Quantity,
                command.Price,
                _latestQuote?.Source.ToString() ?? "unknown");

            _workingOrderReady.TrySetResult(true);

            timeoutLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutTask = MonitorOrderTimeoutAsync(submitResult.Payload.ClOrdId, timeoutLifetime.Token);

            var result = await _completion.Task.WaitAsync(cancellationToken);
            timeoutLifetime.Cancel();
            if (timeoutTask is not null)
            {
                try
                {
                    await timeoutTask;
                }
                catch (OperationCanceledException) when (timeoutLifetime.IsCancellationRequested)
                {
                }
            }

            return result;
        }
        finally
        {
            timeoutLifetime?.Dispose();
            await TryBestEffortCancelAsync("shutdown", CancellationToken.None);
        }
    }

    async Task IPrivateFeedObserver.OnConnectedAsync(bool isReconnect, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _privateConnected = true;
            if (isReconnect)
                ResetSnapshotsUnderLock();
        }

        if (isReconnect)
            _logger.LogInformation("Trading Platform websocket reconnected; waiting for fresh snapshots before any new action.");

        SignalStateChanged();
        await Task.CompletedTask;
    }

    async Task IPrivateFeedObserver.OnDisconnectedAsync(Exception? error, CancellationToken cancellationToken)
    {
        var shouldHandleFeedLoss = false;
        lock (_gate)
        {
            _privateConnected = false;
            shouldHandleFeedLoss = HasLiveOrderUnderLock() && !_feedLossHandled;
        }

        if (error is null)
            _logger.LogWarning("Trading Platform websocket disconnected.");
        else
            _logger.LogWarning(error, "Trading Platform websocket disconnected.");

        SignalStateChanged();
        if (shouldHandleFeedLoss)
            await HandleFeedLossAsync("trading_websocket_disconnected", cancellationToken);
    }

    Task IPrivateFeedObserver.OnFrameAsync(PrivateFeedFrame frame, CancellationToken cancellationToken)
    {
        switch (frame)
        {
            case OrdersSnapshotFrame orders:
                HandleOrdersSnapshot(orders);
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
                HandleOrderDelta(order.Order);
                break;
            case ExecutionDeltaFrame execution:
                HandleExecutionDelta(execution.Execution);
                break;
            case PhaseSnapshotFrame phase:
                HandlePhase(phase.Symbol, phase.Phase);
                break;
            case PhaseDeltaFrame phase:
                HandlePhase(phase.Symbol, phase.Phase);
                break;
            case ProtocolErrorFrame protocolError:
                _logger.LogWarning("Trading Platform websocket protocol error code={Code} message={Message}.", protocolError.Code, protocolError.Message);
                break;
        }

        SignalStateChanged();
        return Task.CompletedTask;
    }

    async Task ISampleBotMarketDataObserver.OnConnectedAsync(bool isReconnect, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _marketDataConnected = true;
            _latestQuote = null;
        }

        _logger.LogInformation(
            isReconnect
                ? "Market-data websocket connected again; waiting for a fresh quote before any new submission."
                : "Market-data websocket connected.");
        SignalStateChanged();
        await Task.CompletedTask;
    }

    async Task ISampleBotMarketDataObserver.OnDisconnectedAsync(Exception? error, CancellationToken cancellationToken)
    {
        var shouldHandleFeedLoss = false;
        lock (_gate)
        {
            _marketDataConnected = false;
            _latestQuote = null;
            shouldHandleFeedLoss = HasLiveOrderUnderLock() && !_feedLossHandled;
        }

        if (error is null)
            _logger.LogWarning("Market-data websocket disconnected; new submissions are paused.");
        else
            _logger.LogWarning(error, "Market-data websocket disconnected; new submissions are paused.");

        SignalStateChanged();
        if (shouldHandleFeedLoss)
            await HandleFeedLossAsync("market_data_disconnected", cancellationToken);
    }

    Task ISampleBotMarketDataObserver.OnQuoteAsync(MarketDataQuote quote, CancellationToken cancellationToken)
    {
        if (!string.Equals(quote.Symbol, _options.DemoOrder.Symbol, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        lock (_gate)
        {
            _latestQuote = quote;
        }

        _logger.LogInformation(
            "Observed market-data quote symbol={Symbol} securityId={SecurityId} source={Source} price={Price} receivedAt={ReceivedAtUtc:O}.",
            quote.Symbol,
            quote.SecurityId,
            quote.Source,
            quote.Price,
            quote.ReceivedAtUtc);
        SignalStateChanged();
        return Task.CompletedTask;
    }

    private void HandleOrdersSnapshot(OrdersSnapshotFrame orders)
    {
        MarkSnapshot("orders.me");
        lock (_gate)
        {
            if (!_submitAttempted)
                _knownWorkingOrder = orders.Orders.FirstOrDefault(IsStrategyWorkingOrder);
        }

        _logger.LogInformation("orders.me snapshot seq={Seq} count={Count}.", orders.Seq, orders.Orders.Count);
    }

    private void HandleOrderDelta(TradingOrder order)
    {
        SampleBotWorkflowResult? terminal = null;

        lock (_gate)
        {
            if (!IsTrackedMutationUnderLock(order.ClOrdId, order.Symbol))
                return;

            _knownWorkingOrder = order;
            terminal = TranslateTerminalOrderState(order.Status, order.ClOrdId, detail: null);
        }

        _logger.LogInformation(
            "orders.me delta clOrdId={ClOrdId} symbol={Symbol} status={Status} leaves={LeavesQuantity}.",
            order.ClOrdId,
            order.Symbol,
            order.Status,
            order.LeavesQuantity);

        if (terminal is not null)
            CompleteTerminal(terminal);
    }

    private void HandleExecutionDelta(TradingExecution execution)
    {
        SampleBotWorkflowResult? terminal = null;

        lock (_gate)
        {
            if (!IsTrackedMutationUnderLock(execution.ClOrdId, execution.Symbol))
                return;

            terminal = TranslateTerminalExecutionState(execution);
            if (terminal is not null && _knownWorkingOrder is not null)
            {
                _knownWorkingOrder = _knownWorkingOrder with
                {
                    Status = terminal.Outcome switch
                    {
                        "filled" => "Filled",
                        "cancelled" => "Cancelled",
                        "rejected" => "Rejected",
                        _ => _knownWorkingOrder.Status,
                    },
                };
            }
        }

        _logger.LogInformation(
            "executions.me delta clOrdId={ClOrdId} kind={Kind} symbol={Symbol} status={Status} lastQty={LastQuantity} lastPrice={LastPrice}.",
            execution.ClOrdId,
            execution.Kind,
            execution.Symbol,
            execution.Status,
            execution.LastQuantity,
            execution.LastPrice);

        if (terminal is not null)
            CompleteTerminal(terminal);
    }

    private void HandlePhase(string symbol, PhaseSnapshot phase)
    {
        if (!string.Equals(symbol, _options.DemoOrder.Symbol, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_gate)
        {
            _phase = phase.Phase;
        }

        _logger.LogInformation(
            "Observed trading phase symbol={Symbol} phase={Phase} at={At}.",
            symbol,
            phase.Phase,
            phase.At?.ToString("O") ?? "<none>");
    }

    private async Task WaitForInitialSnapshotsAsync(CancellationToken cancellationToken)
    {
        Task snapshotTask;
        lock (_gate)
        {
            snapshotTask = _initialSnapshots.Task;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.InitialSnapshotTimeout);
        await snapshotTask.WaitAsync(timeout.Token);
    }

    private async Task<SubmitOrderCommand> WaitForSubmissionReadinessAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryBuildReadyOrder(out var command, out var blockedReason))
            {
                lock (_gate)
                {
                    _blockedReason = null;
                }

                return command!;
            }

            lock (_gate)
            {
                if (!string.Equals(_blockedReason, blockedReason, StringComparison.Ordinal))
                {
                    _blockedReason = blockedReason;
                    _logger.LogInformation("Sample strategy waiting: {Reason}.", blockedReason);
                }
            }

            await _stateSignal.WaitAsync(cancellationToken);
        }
    }

    private async Task<SampleBotWorkflowResult?> ReconcileSubmittedOrderAsync(
        string clOrdId,
        CancellationToken cancellationToken)
    {
        var orders = await _restClient.GetOrdersAsync(cancellationToken);
        var liveOrder = orders.FirstOrDefault(order => string.Equals(order.ClOrdId, clOrdId, StringComparison.Ordinal));
        if (liveOrder is null)
        {
            _logger.LogWarning(
                "Submitted clOrdId={ClOrdId} was not present in the immediate GET /api/orders reconciliation snapshot; stopping conservatively.",
                clOrdId);
            return new SampleBotWorkflowResult(
                "submitted_order_not_working",
                clOrdId,
                "GET /api/orders did not report a live order immediately after submit.");
        }

        lock (_gate)
        {
            _knownWorkingOrder = liveOrder;
        }

        return TranslateTerminalOrderState(liveOrder.Status, clOrdId, null);
    }

    private bool TryBuildReadyOrder(out SubmitOrderCommand? command, out string blockedReason)
    {
        lock (_gate)
        {
            command = null;

            if (!_privateConnected)
            {
                blockedReason = "private websocket is not connected";
                return false;
            }

            if (!_marketDataConnected)
            {
                blockedReason = "market-data websocket is not connected";
                return false;
            }

            if (!_initialSnapshots.Task.IsCompleted)
            {
                blockedReason = "waiting for current private snapshots";
                return false;
            }

            if (HasLiveOrderUnderLock())
            {
                blockedReason = $"a live order already exists (clOrdId={_knownWorkingOrder!.ClOrdId})";
                return false;
            }

            if (_latestQuote is null)
            {
                blockedReason = "no market-data quote has been observed yet";
                return false;
            }

            var age = _timeProvider.GetUtcNow() - _latestQuote.ReceivedAtUtc;
            if (age > _options.MarketData.MaxAge)
            {
                blockedReason = $"market-data quote is stale ({age.TotalMilliseconds:F0} ms old)";
                return false;
            }

            if (_options.DemoOrder.RequireOpenPhase
                && !string.Equals(_phase, "Open", StringComparison.OrdinalIgnoreCase))
            {
                blockedReason = $"trading phase is '{_phase ?? "Unknown"}', not Open";
                return false;
            }

            var price = ComputePassiveLimitPrice(
                _latestQuote.Price,
                _options.DemoOrder.Side,
                _options.DemoOrder.PriceOffsetTicks,
                _options.DemoOrder.TickSize);
            var notional = price * _options.DemoOrder.Quantity;
            if (notional > _options.DemoOrder.MaxNotional)
            {
                throw new InvalidOperationException(
                    $"Refusing to submit {notional} notional because it exceeds configured max {_options.DemoOrder.MaxNotional}.");
            }

            command = new SubmitOrderCommand(
                _options.DemoOrder.Symbol,
                _latestQuote.SecurityId,
                NormalizeSide(_options.DemoOrder.Side),
                _options.DemoOrder.Quantity,
                price,
                string.IsNullOrWhiteSpace(_options.SubAccountId) ? null : _options.SubAccountId.Trim());
            blockedReason = string.Empty;
            return true;
        }
    }

    private async Task MonitorOrderTimeoutAsync(string clOrdId, CancellationToken cancellationToken)
    {
        await _delayAsync(_options.DemoOrder.OrderTimeout, cancellationToken);
        await TriggerOrderTimeoutAsync(clOrdId, cancellationToken);
    }

    internal Task TriggerOrderTimeoutAsync(CancellationToken cancellationToken)
    {
        var clOrdId = CurrentClOrdId();
        return clOrdId is null ? Task.CompletedTask : TriggerOrderTimeoutAsync(clOrdId, cancellationToken);
    }

    private async Task TriggerOrderTimeoutAsync(string clOrdId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!HasLiveOrderUnderLock() || !string.Equals(_knownWorkingOrder!.ClOrdId, clOrdId, StringComparison.Ordinal))
                return;
        }

        _logger.LogInformation(
            "Sample strategy order timeout elapsed for clOrdId={ClOrdId}; attempting cancel.",
            clOrdId);

        var immediateTerminal = await TryBestEffortCancelAsync("order_timeout", cancellationToken, failureOutcome: "cancel_error");
        if (immediateTerminal is not null)
            CompleteTerminal(immediateTerminal);
    }

    private async Task HandleFeedLossAsync(string reason, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_feedLossHandled)
                return;
            _feedLossHandled = true;
        }

        _logger.LogWarning("Sample strategy entering terminal feed-loss path because {Reason}.", reason);
        var terminal = await TryBestEffortCancelAsync(reason, cancellationToken);
        CompleteTerminal(terminal ?? new SampleBotWorkflowResult("feed_lost", CurrentClOrdId(), reason));
    }

    private async Task<SampleBotWorkflowResult?> TryBestEffortCancelAsync(
        string reason,
        CancellationToken cancellationToken,
        string? failureOutcome = null)
    {
        string? clOrdId;
        lock (_gate)
        {
            if (_cancelAttempted || !HasLiveOrderUnderLock())
                return null;

            _cancelAttempted = true;
            clOrdId = _knownWorkingOrder!.ClOrdId;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.DemoOrder.CancellationAttemptTimeout);

        try
        {
            var result = await _restClient.CancelOrderAsync(clOrdId, BuildIdempotencyKey($"cancel-{reason}"), timeout.Token);
            if (result.Payload is null)
            {
                var detail = result.ErrorMessage ?? result.ErrorCode ?? "unknown error";
                _logger.LogWarning(
                    "Best-effort cancel for clOrdId={ClOrdId} failed with HTTP {StatusCode}: {Error}.",
                    clOrdId,
                    (int)result.StatusCode,
                    detail);
                return failureOutcome is null ? null : new SampleBotWorkflowResult(failureOutcome, clOrdId, detail);
            }

            _logger.LogInformation(
                "Best-effort cancel requested for clOrdId={ClOrdId} because {Reason}. mutationId={MutationId}.",
                clOrdId,
                reason,
                result.Payload.MutationId ?? "<none>");

            return TranslateTerminalOrderState(result.Payload.Status, clOrdId, result.Payload.Reason);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Best-effort cancel for clOrdId={ClOrdId} timed out after {Timeout}.",
                clOrdId,
                _options.DemoOrder.CancellationAttemptTimeout);
            return failureOutcome is null
                ? null
                : new SampleBotWorkflowResult(failureOutcome, clOrdId, "cancel request timed out");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                _cancelAttempted = false;
            }

            _logger.LogWarning(ex, "Best-effort cancel for clOrdId={ClOrdId} threw before a response was observed.", clOrdId);
            return failureOutcome is null ? null : new SampleBotWorkflowResult(failureOutcome, clOrdId, ex.Message);
        }
    }

    private void CompleteTerminal(SampleBotWorkflowResult result)
    {
        lock (_gate)
        {
            _submittedClOrdId = null;
            _knownWorkingOrder = null;
        }

        _completion.TrySetResult(result);
        SignalStateChanged();
    }

    private bool IsStrategyWorkingOrder(TradingOrder order) =>
        string.Equals(order.Symbol, _options.DemoOrder.Symbol, StringComparison.OrdinalIgnoreCase)
        && !IsTerminalStatus(order.Status)
        && string.Equals(order.Side, NormalizeSide(_options.DemoOrder.Side), StringComparison.OrdinalIgnoreCase)
        && string.Equals(order.SubAccountId ?? string.Empty, NormalizedSubAccount(), StringComparison.Ordinal);

    private bool IsTrackedMutationUnderLock(string clOrdId, string symbol)
    {
        if (_knownWorkingOrder is not null && string.Equals(_knownWorkingOrder.ClOrdId, clOrdId, StringComparison.Ordinal))
            return true;

        return !string.IsNullOrWhiteSpace(_submittedClOrdId)
            && string.Equals(_submittedClOrdId, clOrdId, StringComparison.Ordinal)
            && string.Equals(symbol, _options.DemoOrder.Symbol, StringComparison.OrdinalIgnoreCase);
    }

    private bool HasLiveOrderUnderLock() => _knownWorkingOrder is not null && !IsTerminalStatus(_knownWorkingOrder.Status);

    private string? CurrentClOrdId()
    {
        lock (_gate)
        {
            return _knownWorkingOrder?.ClOrdId;
        }
    }

    private string NormalizedSubAccount() =>
        string.IsNullOrWhiteSpace(_options.SubAccountId) ? string.Empty : _options.SubAccountId.Trim();

    private void ResetSnapshotsUnderLock()
    {
        _seenSnapshots = new HashSet<string>(StringComparer.Ordinal);
        _initialSnapshots = CreateBoolTcs();
    }

    private void MarkSnapshot(string channel)
    {
        lock (_gate)
        {
            _seenSnapshots.Add(channel);
            if (SnapshotChannels.All(_seenSnapshots.Contains))
                _initialSnapshots.TrySetResult(true);
        }
    }

    private void SignalStateChanged()
    {
        try
        {
            _stateSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private string BuildIdempotencyKey(string action) =>
        $"{_options.DemoOrder.IdempotencyKeyPrefix.Trim()}-{action}-{Guid.NewGuid():N}";

    internal static decimal ComputePassiveLimitPrice(decimal referencePrice, string side, int offsetTicks, decimal tickSize)
    {
        var offset = offsetTicks * tickSize;
        var price = string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase)
            ? referencePrice - offset
            : referencePrice + offset;
        if (price <= 0m)
            throw new InvalidOperationException($"Computed passive limit price {price} is not positive.");
        return decimal.Round(price, 4, MidpointRounding.AwayFromZero);
    }

    private static SampleBotWorkflowResult? TranslateTerminalOrderState(string? status, string? clOrdId, string? detail)
    {
        if (string.Equals(status, "Filled", StringComparison.OrdinalIgnoreCase))
            return new SampleBotWorkflowResult("filled", clOrdId, detail);
        if (string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase))
            return new SampleBotWorkflowResult("cancelled", clOrdId, detail);
        if (string.Equals(status, "Rejected", StringComparison.OrdinalIgnoreCase))
            return new SampleBotWorkflowResult("rejected", clOrdId, detail);
        return null;
    }

    private static SampleBotWorkflowResult? TranslateTerminalExecutionState(TradingExecution execution)
    {
        if (string.Equals(execution.Status, "Filled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(execution.Kind, "Fill", StringComparison.OrdinalIgnoreCase))
        {
            return execution.LeavesQuantity == 0
                ? new SampleBotWorkflowResult("filled", execution.ClOrdId, null)
                : null;
        }

        if (string.Equals(execution.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(execution.Status, "Canceled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(execution.Kind, "Cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(execution.Kind, "Canceled", StringComparison.OrdinalIgnoreCase))
        {
            return new SampleBotWorkflowResult("cancelled", execution.ClOrdId, null);
        }

        if (string.Equals(execution.Status, "Rejected", StringComparison.OrdinalIgnoreCase)
            || string.Equals(execution.Kind, "Rejected", StringComparison.OrdinalIgnoreCase)
            || string.Equals(execution.Kind, "Reject", StringComparison.OrdinalIgnoreCase))
        {
            return new SampleBotWorkflowResult("rejected", execution.ClOrdId, execution.RejectReason);
        }

        return null;
    }

    private static bool IsTerminalStatus(string status) =>
        string.Equals(status, "Filled", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Rejected", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "FeedLost", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSide(string side) =>
        string.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase) ? "Sell" : "Buy";

    private static TaskCompletionSource<bool> CreateBoolTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<SampleBotWorkflowResult> CreateResultTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed record SampleBotWorkflowResult(string Outcome, string? ClOrdId, string? Detail);
