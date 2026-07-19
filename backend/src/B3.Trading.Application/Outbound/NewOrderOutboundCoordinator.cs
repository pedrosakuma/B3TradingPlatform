using B3.Trading.Application.Investor;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Routing;
using B3.Trading.Application.Scheduling;
using B3.Trading.Application.SubAccount;
using B3.Trading.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Outbound;

public sealed class NewOrderApprovalFactory
{
    private readonly IOutboundCommandProtector _protector;
    private readonly IOptionsMonitor<RiskOptions>? _riskOptions;
    private readonly ISubAccountWireIdMapper? _subAccounts;
    private readonly IVenueAccountResolver? _accounts;
    private readonly IInvestorIdResolver? _investors;
    private readonly IRoutingInstructionResolver? _routing;

    public NewOrderApprovalFactory(
        IOutboundCommandProtector protector,
        IOptionsMonitor<RiskOptions>? riskOptions = null,
        ISubAccountWireIdMapper? subAccounts = null,
        IVenueAccountResolver? accounts = null,
        IInvestorIdResolver? investors = null,
        IRoutingInstructionResolver? routing = null)
    {
        _protector = protector;
        _riskOptions = riskOptions;
        _subAccounts = subAccounts;
        _accounts = accounts;
        _investors = investors;
        _routing = routing;
    }

    public (string EndClientRef, OutboundApprovalSnapshot Approval) Create(
        OutboundMutationId mutationId,
        Order order,
        DateTimeOffset approvedAtUtc)
    {
        var stp = _riskOptions is null
            ? SelfTradePreventionMode.None
            : RiskLimitsResolver.ResolveSelfTradePreventionMode(
                _riskOptions.CurrentValue,
                order.Owner.Value,
                order.FirmId,
                order.Symbol);
        var tradingSubAccount = _subAccounts?.TryMap(order.FirmId, order.SubAccountId);
        var account = _accounts?.TryResolve(order);
        var investor = _investors?.TryResolve(order);
        var routing = _routing?.TryResolve(order);
        var command = new OutboundCanonicalCommand
        {
            ClOrdId = order.ClOrdId,
            SecurityId = order.SecurityId,
            Symbol = order.Symbol,
            Side = order.Side.ToString(),
            OrderType = order.Type.ToString(),
            Quantity = order.Quantity,
            Price = order.Price,
            TimeInForce = order.TimeInForce.ToString(),
            StopPrice = order.StopPrice,
            GoodTillDate = order.GoodTillDate,
            MinQty = order.MinQty,
            MaxFloor = order.DisplayQty,
            SelfTradePreventionInstruction = stp.ToString(),
            RoutingInstruction = routing?.ToString(),
        };
        var sensitive = new SensitiveOutboundCommand
        {
            Account = account?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            InvestorIdPrefix = investor?.Prefix.ToString(System.Globalization.CultureInfo.InvariantCulture),
            InvestorIdDocument = investor?.Document.ToString(System.Globalization.CultureInfo.InvariantCulture),
            EndClientId = order.Owner.Value,
            TradingSubAccount = tradingSubAccount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var refs = new List<OutboundSensitiveFieldRef> { OutboundSensitiveFieldRef.EndClientId };
        if (account is not null) refs.Add(OutboundSensitiveFieldRef.Account);
        if (investor is not null) refs.Add(OutboundSensitiveFieldRef.InvestorId);
        if (tradingSubAccount is not null) refs.Add(OutboundSensitiveFieldRef.TradingSubAccount);

        return (
            _protector.CreateStableEndClientRef(order.FirmId, order.Owner.Value),
            OutboundApprovalFactory.Create(
                mutationId,
                order.FirmId,
                command,
                sensitive,
                refs,
                _protector,
                approvedAtUtc,
                marginReservationRef: $"new-order:{mutationId}"));
    }
}

public enum NewOrderDispatchOutcome
{
    TransportWriteCompleted,
    ProvenUnsent,
    ReconciliationRequired,
    DeferredForShutdown,
}

public sealed record NewOrderDispatchResult(
    NewOrderDispatchOutcome Outcome,
    Exception? Exception = null);

public sealed class NewOrderOutboundCoordinator : IHostedService
{
    private readonly OutboundMutationLedger _ledger;
    private readonly OutboundProcessEpoch _epoch;
    private readonly IOutboundCommandProtector _protector;
    private readonly IExchangeGateway _gateway;
    private readonly EventDispatcher _dispatcher;
    private readonly WorkingOrderBook _orders;
    private readonly IMarginProvider _margin;
    private readonly Lifecycle.IDrainController _drain;
    private readonly GtdExpirationScheduler? _gtd;
    private readonly IocFokWatchdog? _iocFok;
    private readonly TimeProvider _clock;
    private readonly ILogger<NewOrderOutboundCoordinator> _logger;
    private readonly IOutboundGatewayReadiness _gatewayReadiness;
    private readonly CancellationTokenSource _recoveryShutdown = new();
    private readonly object _recoveryGate = new();
    private readonly object _lifecycleGate = new();
    private readonly List<Task> _recoveryTasks = new();
    private int _recoveryStarted;
    private bool _stopping;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        OutboundMutationId,
        Lazy<Task<NewOrderDispatchResult>>> _executions = new();

    public NewOrderOutboundCoordinator(
        OutboundMutationLedger ledger,
        OutboundProcessEpoch epoch,
        IOutboundCommandProtector protector,
        IExchangeGateway gateway,
        EventDispatcher dispatcher,
        WorkingOrderBook orders,
        IMarginProvider margin,
        Lifecycle.IDrainController drain,
        ILogger<NewOrderOutboundCoordinator> logger,
        GtdExpirationScheduler? gtd = null,
        IocFokWatchdog? iocFok = null,
        TimeProvider? clock = null,
        IOutboundGatewayReadiness? gatewayReadiness = null)
    {
        _ledger = ledger;
        _epoch = epoch;
        _protector = protector;
        _gateway = gateway;
        _dispatcher = dispatcher;
        _orders = orders;
        _margin = margin;
        _drain = drain;
        _logger = logger;
        _gtd = gtd;
        _iocFok = iocFok;
        _clock = clock ?? TimeProvider.System;
        _gatewayReadiness = gatewayReadiness
            ?? ImmediateOutboundGatewayReadiness.Instance;
    }

    public async Task<NewOrderDispatchResult> EnqueueAsync(
        OutboundMutationId mutationId,
        CancellationToken waitCancellationToken = default)
    {
        Lazy<Task<NewOrderDispatchResult>> execution;
        Task<NewOrderDispatchResult> task;
        lock (_lifecycleGate)
        {
            if (_stopping)
                return new(NewOrderDispatchOutcome.DeferredForShutdown);
            execution = GetOrAddExecution(mutationId);
            task = execution.Value;
        }
        try
        {
            return await task.WaitAsync(waitCancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted)
                RemoveExecution(mutationId, execution);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _recoveryStarted, 1) != 0)
            return Task.CompletedTask;
        foreach (var mutation in _ledger.GetMutations(
                     OutboundMutationKind.New,
                     OutboundMutationState.ApprovedToSend))
        {
            var task = RecoverWhenOperationalAsync(
                mutation.MutationId,
                mutation.FirmId,
                _recoveryShutdown.Token);
            lock (_recoveryGate)
                _recoveryTasks.Add(task);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] recoveryTasks;
        Task[] executionTasks;
        lock (_lifecycleGate)
        {
            _stopping = true;
            _recoveryShutdown.Cancel();
            lock (_recoveryGate)
                recoveryTasks = _recoveryTasks.ToArray();
            executionTasks = _executions.Values
                .Where(static execution => execution.IsValueCreated)
                .Select(static execution => execution.Value)
                .ToArray();
        }
        await Task.WhenAll(recoveryTasks.Concat(executionTasks))
            .ConfigureAwait(false);
    }

    private async Task RecoverWhenOperationalAsync(
        OutboundMutationId mutationId,
        string firmId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _gatewayReadiness.WaitUntilOperationalAsync(
                firmId,
                cancellationToken).ConfigureAwait(false);
            if (!_ledger.TryGet(mutationId, out var mutation)
                || mutation?.State != OutboundMutationState.ApprovedToSend)
                return;
            Lazy<Task<NewOrderDispatchResult>> execution;
            Task<NewOrderDispatchResult> task;
            lock (_lifecycleGate)
            {
                if (_stopping || cancellationToken.IsCancellationRequested)
                    return;
                execution = GetOrAddExecution(mutationId);
                task = execution.Value;
            }
            try
            {
                await task.ConfigureAwait(false);
            }
            finally
            {
                if (task.IsCompleted)
                    RemoveExecution(mutationId, execution);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _drain.BeginDrain("outbound_new_order_reconciliation_required");
            _logger.LogCritical(
                ex,
                "Recovered new-order mutation {MutationId} for firm {FirmId} could not wait for or enter its operational gateway.",
                mutationId,
                firmId);
        }
    }

    private Lazy<Task<NewOrderDispatchResult>> GetOrAddExecution(
        OutboundMutationId mutationId) =>
        _executions.GetOrAdd(
            mutationId,
            id => new Lazy<Task<NewOrderDispatchResult>>(
                () => ExecuteAsync(id),
                LazyThreadSafetyMode.ExecutionAndPublication));

    private void RemoveExecution(
        OutboundMutationId mutationId,
        Lazy<Task<NewOrderDispatchResult>> execution) =>
        ((ICollection<KeyValuePair<
            OutboundMutationId,
            Lazy<Task<NewOrderDispatchResult>>>>)_executions)
        .Remove(new KeyValuePair<
            OutboundMutationId,
            Lazy<Task<NewOrderDispatchResult>>>(mutationId, execution));

    private async Task<NewOrderDispatchResult> ExecuteAsync(OutboundMutationId mutationId)
    {
        if (!_ledger.TryGet(mutationId, out var mutation) || mutation is null)
            return ReconciliationRequired("outbound mutation disappeared before dispatch");
        if (mutation.State == OutboundMutationState.TransportWriteCompleted)
            return new(NewOrderDispatchOutcome.TransportWriteCompleted);
        if (mutation.State != OutboundMutationState.ApprovedToSend
            || mutation.Approval is null
            || mutation.Kind != OutboundMutationKind.New)
            return ReconciliationRequired("outbound mutation is not dispatchable");
        if (!_orders.TryGet(mutation.PrimaryClOrdId, out var order) || order is null)
            return ReconciliationRequired("approved outbound mutation has no pending order");

        SensitiveOutboundCommand sensitive;
        try
        {
            sensitive = _protector.Decrypt(
                mutation.MutationId,
                mutation.FirmId,
                mutation.Approval.CanonicalCommandNonSensitive,
                mutation.Approval.SensitiveFieldRefs,
                mutation.Approval.SensitiveCommandEnvelope);
        }
        catch (OutboundCommandEnvelopeException ex)
        {
            return ReconciliationRequired("approved outbound command cannot be decrypted", ex);
        }

        var attemptId = OutboundAttemptId.New();
        var intentAt = _clock.GetUtcNow();
        try
        {
            var intent = new OutboundAttemptIntentPreparedEvent
            {
                MutationId = mutation.MutationId,
                AttemptId = attemptId,
                AttemptNo = 1,
                ClOrdId = mutation.PrimaryClOrdId,
                ProcessEpochId = _epoch.Id,
                IntentPreparedAtUtc = intentAt,
                TimestampUtc = intentAt,
            };
            _dispatcher.DispatchCommitted(
                intent,
                () => _ledger.Apply(intent),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is WalBackpressureException or WalFaultedException)
        {
            return ReconciliationRequired("attempt intent could not be committed", ex);
        }

        var command = new OutboundNewOrderCommand(
            mutation.MutationId,
            mutation.FirmId,
            mutation.Approval.CanonicalCommandNonSensitive,
            sensitive);
        ExchangeGatewayFrameIdentity? committedFrame = null;
        try
        {
            var receipt = await _gateway.SubmitWithReceiptAsync(
                command,
                (frame, _) =>
                {
                    ValidateFrame(mutation, frame);
                    var preparedAt = _clock.GetUtcNow();
                    var evt = new OutboundFramePreparedEvent
                    {
                        MutationId = mutation.MutationId,
                        AttemptId = attemptId,
                        FirmId = mutation.FirmId,
                        SessionId = frame.SessionId,
                        SessionVerId = frame.SessionVerId,
                        OutboundSeqNum = frame.OutboundSeqNum,
                        EncodedFrameSha256 = frame.EncodedFrameSha256,
                        PreparedAtUtc = preparedAt,
                        TimestampUtc = preparedAt,
                    };
                    _dispatcher.DispatchCommitted(
                        evt,
                        () => _ledger.Apply(evt),
                        CancellationToken.None);
                    committedFrame = frame;
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None).ConfigureAwait(false);

            if (committedFrame is null || receipt.Frame != committedFrame)
                return MarkAmbiguous(mutation, attemptId, "gateway receipt did not match committed frame");

            var completedAt = _clock.GetUtcNow();
            var completed = new OutboundTransportWriteCompletedEvent
            {
                MutationId = mutation.MutationId,
                AttemptId = attemptId,
                CompletedAtUtc = completedAt,
                GatewayReceiptVersion = receipt.Version,
                TimestampUtc = completedAt,
            };
            _dispatcher.DispatchCommitted(
                completed,
                () => _ledger.Apply(completed),
                CancellationToken.None);
            _gtd?.OnOrderTracked(order);
            _iocFok?.Register(order);
            return new(NewOrderDispatchOutcome.TransportWriteCompleted);
        }
        catch (ExchangeGatewayAttemptException ex)
            when (ex.NoTransportWritePossible && committedFrame is null)
        {
            var at = _clock.GetUtcNow();
            var unsent = new OutboundProvenUnsentEvent
            {
                MutationId = mutation.MutationId,
                AttemptId = attemptId,
                Evidence = OutboundProvenUnsentEvidence.TypedPreFrameFailure,
                TimestampUtc = at,
            };
            try
            {
                _dispatcher.DispatchCommitted(
                    unsent,
                    () => _ledger.Apply(unsent),
                    CancellationToken.None);
                return new(NewOrderDispatchOutcome.ProvenUnsent, ex);
            }
            catch (Exception walEx) when (walEx is WalBackpressureException or WalFaultedException)
            {
                return ReconciliationRequired("proven-unsent evidence could not be committed", walEx);
            }
        }
        catch (Exception ex)
        {
            if (committedFrame is not null)
                return MarkAmbiguous(mutation, attemptId, "gateway outcome is unknown after frame preparation", ex);
            return ReconciliationRequired("gateway failed without typed pre-frame evidence", ex);
        }
    }

    private NewOrderDispatchResult MarkAmbiguous(
        OutboundMutationSnapshot mutation,
        OutboundAttemptId attemptId,
        string reason,
        Exception? exception = null)
    {
        _ledger.MarkAmbiguous(
            mutation.MutationId,
            attemptId,
            OutboundAmbiguityReason.GatewayOutcomeUnknown,
            _clock.GetUtcNow());
        return ReconciliationRequired(reason, exception);
    }

    private NewOrderDispatchResult ReconciliationRequired(string reason, Exception? exception = null)
    {
        _drain.BeginDrain("outbound_new_order_reconciliation_required");
        _logger.LogCritical(
            exception,
            "New-order outbound coordinator requires reconciliation: {Reason}.",
            reason);
        return new(NewOrderDispatchOutcome.ReconciliationRequired, exception);
    }

    private static void ValidateFrame(
        OutboundMutationSnapshot mutation,
        ExchangeGatewayFrameIdentity frame)
    {
        if (frame.Operation != ExchangeGatewayOperation.NewOrder
            || frame.ClOrdId != mutation.PrimaryClOrdId
            || !string.Equals(frame.FirmId, mutation.FirmId, StringComparison.Ordinal))
            throw new InvalidOperationException("Gateway frame identity does not match approved mutation.");
    }
}
