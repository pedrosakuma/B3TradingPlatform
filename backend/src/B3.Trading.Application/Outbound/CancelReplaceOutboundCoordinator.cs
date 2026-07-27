using B3.Trading.Application.Investor;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Routing;
using B3.Trading.Application.SubAccount;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Application.Outbound;

public sealed class CancelReplaceApprovalFactory
{
    private readonly IOutboundCommandProtector _protector;
    private readonly IOptionsMonitor<RiskOptions>? _riskOptions;
    private readonly ISubAccountWireIdMapper? _subAccounts;
    private readonly IVenueAccountResolver? _accounts;
    private readonly IInvestorIdResolver? _investors;
    private readonly IRoutingInstructionResolver? _routing;
    private readonly OutboundMutationLedger? _outboundLedger;

    public CancelReplaceApprovalFactory(
        IOutboundCommandProtector protector,
        IOptionsMonitor<RiskOptions>? riskOptions = null,
        ISubAccountWireIdMapper? subAccounts = null,
        IVenueAccountResolver? accounts = null,
        IInvestorIdResolver? investors = null,
        IRoutingInstructionResolver? routing = null,
        OutboundMutationLedger? outboundLedger = null)
    {
        _protector = protector;
        _riskOptions = riskOptions;
        _subAccounts = subAccounts;
        _accounts = accounts;
        _investors = investors;
        _routing = routing;
        _outboundLedger = outboundLedger;
    }

    public (string EndClientRef, OutboundApprovalSnapshot Approval) CreateCancel(
        OutboundMutationId mutationId,
        Order original,
        ulong cancelClOrdId,
        DateTimeOffset approvedAtUtc) =>
        Create(
            mutationId,
            original,
            new OutboundCanonicalCommand
            {
                ClOrdId = cancelClOrdId,
                OriginalClOrdId = original.ClOrdId,
                SecurityId = original.SecurityId,
                Symbol = original.Symbol,
                Side = original.Side.ToString(),
                OrderType = original.Type.ToString(),
                Quantity = original.Quantity,
                Price = original.Price,
                TimeInForce = original.TimeInForce.ToString(),
                StopPrice = original.StopPrice,
                GoodTillDate = original.GoodTillDate,
                MinQty = original.MinQty,
                MaxFloor = original.DisplayQty,
            },
            approvedAtUtc,
            marginReservationRef: null,
            marginAmount: null,
            marginBasis: null,
            venueOrderId: ResolveVenueOrderId(original));

    private ulong? ResolveVenueOrderId(Order original)
    {
        if (_outboundLedger?.TryGetByClOrdId(original.ClOrdId, out var mutation) != true ||
            mutation is null ||
            mutation.Kind is not (OutboundMutationKind.New or OutboundMutationKind.Replace) ||
            !string.Equals(mutation.FirmId, original.FirmId, StringComparison.Ordinal))
        {
            return null;
        }

        return mutation.Resolution?.VenueOrderId;
    }

    public (string EndClientRef, OutboundApprovalSnapshot Approval) CreateReplace(
        OutboundMutationId mutationId,
        Order original,
        ulong newClOrdId,
        long newQuantity,
        decimal? effectivePrice,
        TimeInForce effectiveTimeInForce,
        decimal? effectiveStopPrice,
        DateTimeOffset? effectiveGoodTillDate,
        decimal newRemainingNotional,
        DateTimeOffset approvedAtUtc) =>
        Create(
            mutationId,
            original,
            new OutboundCanonicalCommand
            {
                ClOrdId = newClOrdId,
                OriginalClOrdId = original.ClOrdId,
                SecurityId = original.SecurityId,
                Symbol = original.Symbol,
                Side = original.Side.ToString(),
                OrderType = original.Type.ToString(),
                Quantity = newQuantity,
                Price = effectivePrice,
                TimeInForce = effectiveTimeInForce.ToString(),
                StopPrice = effectiveStopPrice,
                GoodTillDate = effectiveGoodTillDate,
                MinQty = original.MinQty is { } minQty
                    ? Math.Min(minQty, newQuantity)
                    : null,
                MaxFloor = original.DisplayQty is { } displayQty
                    ? Math.Min(displayQty, newQuantity)
                    : null,
            },
            approvedAtUtc,
            marginReservationRef: $"replace:{mutationId}",
            marginAmount: newRemainingNotional,
            marginBasis: "worst-case-original-plus-replacement-delta",
            venueOrderId: null);

    private (string EndClientRef, OutboundApprovalSnapshot Approval) Create(
        OutboundMutationId mutationId,
        Order original,
        OutboundCanonicalCommand command,
        DateTimeOffset approvedAtUtc,
        string? marginReservationRef,
        decimal? marginAmount,
        string? marginBasis,
        ulong? venueOrderId)
    {
        var stp = _riskOptions is null
            ? SelfTradePreventionMode.None
            : RiskLimitsResolver.ResolveSelfTradePreventionMode(
                _riskOptions.CurrentValue,
                original.Owner.Value,
                original.FirmId,
                original.Symbol);
        var tradingSubAccount = _subAccounts?.TryMap(original.FirmId, original.SubAccountId);
        var account = _accounts?.TryResolve(original);
        var investor = _investors?.TryResolve(original);
        var routing = _routing?.TryResolve(original);
        command = command with
        {
            SelfTradePreventionInstruction = stp.ToString(),
            RoutingInstruction = routing?.ToString(),
        };
        var sensitive = new SensitiveOutboundCommand
        {
            Account = account?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            InvestorIdPrefix = investor?.Prefix.ToString(System.Globalization.CultureInfo.InvariantCulture),
            InvestorIdDocument = investor?.Document.ToString(System.Globalization.CultureInfo.InvariantCulture),
            EndClientId = original.Owner.Value,
            TradingSubAccount = tradingSubAccount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var refs = new List<OutboundSensitiveFieldRef> { OutboundSensitiveFieldRef.EndClientId };
        if (account is not null) refs.Add(OutboundSensitiveFieldRef.Account);
        if (investor is not null) refs.Add(OutboundSensitiveFieldRef.InvestorId);
        if (tradingSubAccount is not null) refs.Add(OutboundSensitiveFieldRef.TradingSubAccount);
        var approval = OutboundApprovalFactory.Create(
            mutationId,
            original.FirmId,
            command,
            sensitive,
            refs,
            _protector,
            approvedAtUtc,
            marginReservationRef: marginReservationRef,
            marginAmount: marginAmount,
            marginBasis: marginBasis) with
        {
            VenueOrderId = venueOrderId,
        };
        return (
            _protector.CreateStableEndClientRef(original.FirmId, original.Owner.Value),
            approval);
    }
}

public enum CancelReplaceDispatchOutcome
{
    TransportWriteCompleted,
    ProvenUnsent,
    ReconciliationRequired,
    DeferredForShutdown,
    RetryNotAllowed,
    MarginRejected,
}

public sealed record CancelReplaceDispatchResult(
    CancelReplaceDispatchOutcome Outcome,
    ulong ClOrdId,
    Exception? Exception = null);

public sealed class CancelReplaceOutboundCoordinator : IHostedService
{
    private readonly OutboundMutationLedger _ledger;
    private readonly OutboundProcessEpoch _epoch;
    private readonly IOutboundCommandProtector _protector;
    private readonly IExchangeGateway _gateway;
    private readonly EventDispatcher _dispatcher;
    private readonly WorkingOrderBook _orders;
    private readonly ClOrdIdPrefixRegistry _clOrdIds;
    private readonly OrderOwnershipMap _ownership;
    private readonly PendingCancelRegistry _pendingCancels;
    private readonly PendingReplacementRegistry _replacements;
    private readonly IReplaceMarginCoordinator _replaceMargin;
    private readonly Lifecycle.IDrainController _drain;
    private readonly IUserBotOrderMappingRegistry? _botMappings;
    private readonly TimeProvider _clock;
    private readonly ILogger<CancelReplaceOutboundCoordinator> _logger;
    private readonly IOutboundGatewayReadiness _gatewayReadiness;
    private readonly IOutboundRecoveryGate _recovery;
    private readonly CancellationTokenSource _recoveryShutdown = new();
    private readonly object _recoveryGate = new();
    private readonly object _lifecycleGate = new();
    private readonly List<Task> _recoveryTasks = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        OutboundMutationId,
        Lazy<Task<CancelReplaceDispatchResult>>> _executions = new();
    private int _recoveryStarted;
    private bool _stopping;

    public CancelReplaceOutboundCoordinator(
        OutboundMutationLedger ledger,
        OutboundProcessEpoch epoch,
        IOutboundCommandProtector protector,
        IExchangeGateway gateway,
        EventDispatcher dispatcher,
        WorkingOrderBook orders,
        ClOrdIdPrefixRegistry clOrdIds,
        OrderOwnershipMap ownership,
        PendingCancelRegistry pendingCancels,
        PendingReplacementRegistry replacements,
        IReplaceMarginCoordinator replaceMargin,
        Lifecycle.IDrainController drain,
        ILogger<CancelReplaceOutboundCoordinator> logger,
        IUserBotOrderMappingRegistry? botMappings = null,
        TimeProvider? clock = null,
        IOutboundGatewayReadiness? gatewayReadiness = null,
        IOutboundRecoveryGate? recovery = null)
    {
        _ledger = ledger;
        _epoch = epoch;
        _protector = protector;
        _gateway = gateway;
        _dispatcher = dispatcher;
        _orders = orders;
        _clOrdIds = clOrdIds;
        _ownership = ownership;
        _pendingCancels = pendingCancels;
        _replacements = replacements;
        _replaceMargin = replaceMargin;
        _drain = drain;
        _logger = logger;
        _botMappings = botMappings;
        _clock = clock ?? TimeProvider.System;
        _gatewayReadiness = gatewayReadiness
            ?? ImmediateOutboundGatewayReadiness.Instance;
        _recovery = recovery ?? ImmediateOutboundRecoveryGate.Instance;
    }

    public Task<CancelReplaceDispatchResult> EnqueueAsync(
        OutboundMutationId mutationId,
        CancellationToken cancellationToken = default) =>
        EnqueueCoreAsync(mutationId, allowRetry: false, cancellationToken);

    public Task<CancelReplaceDispatchResult> RetryProvenUnsentAsync(
        OutboundMutationId mutationId,
        CancellationToken cancellationToken = default) =>
        EnqueueCoreAsync(mutationId, allowRetry: true, cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _recoveryStarted, 1) != 0)
            return Task.CompletedTask;
        var task = RecoverAllAsync(_recoveryShutdown.Token);
        lock (_recoveryGate)
            _recoveryTasks.Add(task);
        return Task.CompletedTask;
    }

    private async Task RecoverAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _recovery.WaitUntilClassificationCompleteAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var mutation in _ledger.SnapshotMutations()
                         .Where(static mutation =>
                             mutation.Kind is OutboundMutationKind.Cancel or OutboundMutationKind.Replace))
            {
                var task = RecoverWhenOperationalAsync(
                    mutation.MutationId,
                    mutation.FirmId,
                    cancellationToken);
                lock (_recoveryGate)
                    _recoveryTasks.Add(task);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
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
        await Task.WhenAll(recoveryTasks.Concat(executionTasks)).ConfigureAwait(false);
    }

    private async Task<CancelReplaceDispatchResult> EnqueueCoreAsync(
        OutboundMutationId mutationId,
        bool allowRetry,
        CancellationToken cancellationToken)
    {
        Lazy<Task<CancelReplaceDispatchResult>> execution;
        Task<CancelReplaceDispatchResult> task;
        lock (_lifecycleGate)
        {
            if (_stopping)
                return new(CancelReplaceDispatchOutcome.DeferredForShutdown, 0);
            execution = _executions.GetOrAdd(
                mutationId,
                id => new Lazy<Task<CancelReplaceDispatchResult>>(
                    () => ExecuteAsync(id, allowRetry),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            task = execution.Value;
        }
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted)
                RemoveExecution(mutationId, execution);
        }
    }

    private async Task RecoverWhenOperationalAsync(
        OutboundMutationId mutationId,
        string firmId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_ledger.TryGet(mutationId, out var mutation) || mutation is null)
                return;
            await RestoreProjectionAsync(mutation, cancellationToken).ConfigureAwait(false);
            if (mutation.State != OutboundMutationState.ApprovedToSend)
                return;
            await _recovery.WaitUntilBusinessIngressOpenAsync(
                firmId,
                cancellationToken).ConfigureAwait(false);
            await _gatewayReadiness.WaitUntilOperationalAsync(
                firmId,
                cancellationToken).ConfigureAwait(false);
            await EnqueueCoreAsync(mutationId, allowRetry: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReconciliationRequired("recovered cancel/replace mutation could not be restored", 0, ex);
        }
    }

    private async Task<CancelReplaceDispatchResult> ExecuteAsync(
        OutboundMutationId mutationId,
        bool allowRetry)
    {
        if (!_ledger.TryGet(mutationId, out var mutation) || mutation is null)
            return ReconciliationRequired("outbound mutation disappeared before dispatch", 0);
        if (mutation.Kind is not (OutboundMutationKind.Cancel or OutboundMutationKind.Replace)
            || mutation.Approval is null
            || mutation.OriginalClOrdId is null)
            return ReconciliationRequired("outbound mutation is not a cancel/replace approval", 0);
        if (mutation.State == OutboundMutationState.TransportWriteCompleted)
            return new(CancelReplaceDispatchOutcome.TransportWriteCompleted, ActiveClOrdId(mutation));
        if (mutation.State == OutboundMutationState.ProvenUnsent && !allowRetry)
            return new(CancelReplaceDispatchOutcome.ProvenUnsent, ActiveClOrdId(mutation));
        if (mutation.State is not (OutboundMutationState.ApprovedToSend
            or OutboundMutationState.ProvenUnsent))
            return ReconciliationRequired("outbound mutation is not dispatchable", ActiveClOrdId(mutation));
        if (mutation.State == OutboundMutationState.ProvenUnsent
            && mutation.Attempts.Count >= OutboundMutationLedger.MaxOutboundAttempts)
            return new(CancelReplaceDispatchOutcome.RetryNotAllowed, ActiveClOrdId(mutation));
        if (!_orders.TryGet(mutation.OriginalClOrdId.Value, out var original) || original is null)
            return ReconciliationRequired("approved mutation has no original order", ActiveClOrdId(mutation));

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
            return ReconciliationRequired("approved outbound command cannot be decrypted", 0, ex);
        }

        var attemptNo = mutation.Attempts.Count + 1;
        var attemptClOrdId = attemptNo == 1
            ? mutation.PrimaryClOrdId
            : _clOrdIds.Generate(new EndClientId(sensitive.EndClientId));
        var canonical = mutation.Approval.CanonicalCommandNonSensitive with
        {
            ClOrdId = attemptClOrdId,
        };

        var attemptId = OutboundAttemptId.New();
        var intentAt = _clock.GetUtcNow();
        try
        {
            var intent = new OutboundAttemptIntentPreparedEvent
            {
                MutationId = mutation.MutationId,
                AttemptId = attemptId,
                AttemptNo = attemptNo,
                ClOrdId = attemptClOrdId,
                ProcessEpochId = _epoch.Id,
                IntentPreparedAtUtc = intentAt,
                TimestampUtc = intentAt,
            };
            var dispatched = _dispatcher.DispatchCommittedIf(
                intent,
                () => _ledger.CanPrepareAttempt(
                    mutation.MutationId,
                    attemptNo,
                    attemptClOrdId),
                () => _ledger.Apply(intent),
                CancellationToken.None);
            if (!dispatched.Applied)
            {
                return attemptNo > 1
                    ? new(CancelReplaceDispatchOutcome.RetryNotAllowed, attemptClOrdId)
                    : ReconciliationRequired(
                        "initial attempt intent was no longer eligible",
                        attemptClOrdId);
            }
        }
        catch (Exception ex) when (ex is WalBackpressureException or WalFaultedException)
        {
            return ReconciliationRequired("attempt intent could not be committed", attemptClOrdId, ex);
        }

        if (attemptNo > 1)
        {
            bool projection;
            try
            {
                projection = await PrepareRetryProjectionAsync(
                    mutation,
                    original,
                    sensitive,
                    canonical,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return MarkRetryProjectionUnsent(
                    mutation,
                    attemptId,
                    attemptClOrdId,
                    "retry projection failed before gateway entry",
                    ex);
            }
            if (!projection)
            {
                return MarkRetryProjectionUnsent(
                    mutation,
                    attemptId,
                    attemptClOrdId,
                    "retry projection was rejected before gateway entry");
            }
        }

        ExchangeGatewayFrameIdentity? committedFrame = null;
        try
        {
            var receipt = mutation.Kind == OutboundMutationKind.Cancel
                ? await _gateway.CancelWithReceiptAsync(
                    new OutboundCancelCommand(
                        mutation.MutationId,
                        mutation.FirmId,
                        canonical,
                        sensitive,
                        mutation.Approval.VenueOrderId),
                    OnFramePrepared,
                    CancellationToken.None).ConfigureAwait(false)
                : await _gateway.CancelReplaceWithReceiptAsync(
                    new OutboundReplaceCommand(
                        mutation.MutationId,
                        mutation.FirmId,
                        canonical,
                        sensitive),
                    OnFramePrepared,
                    CancellationToken.None).ConfigureAwait(false);

            if (committedFrame is null || receipt.Frame != committedFrame)
                return MarkAmbiguous(
                    mutation,
                    attemptId,
                    attemptClOrdId,
                    "gateway receipt did not match committed frame");
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
            return new(CancelReplaceDispatchOutcome.TransportWriteCompleted, attemptClOrdId);
        }
        catch (ExchangeGatewayAttemptException ex)
            when (ex.NoTransportWritePossible && committedFrame is null)
        {
            var unsent = new OutboundProvenUnsentEvent
            {
                MutationId = mutation.MutationId,
                AttemptId = attemptId,
                Evidence = OutboundProvenUnsentEvidence.TypedPreFrameFailure,
                TimestampUtc = _clock.GetUtcNow(),
            };
            try
            {
                _dispatcher.DispatchCommitted(
                    unsent,
                    () => _ledger.Apply(unsent),
                    CancellationToken.None);
                RemoveProjection(mutation.Kind, attemptClOrdId);
                return new(CancelReplaceDispatchOutcome.ProvenUnsent, attemptClOrdId, ex);
            }
            catch (Exception walEx) when (walEx is WalBackpressureException or WalFaultedException)
            {
                return ReconciliationRequired(
                    "proven-unsent evidence could not be committed",
                    attemptClOrdId,
                    walEx);
            }
        }
        catch (Exception ex)
        {
            if (committedFrame is not null)
            {
                return MarkAmbiguous(
                    mutation,
                    attemptId,
                    attemptClOrdId,
                    "gateway outcome is unknown after frame preparation",
                    ex);
            }
            return ReconciliationRequired(
                "gateway failed without typed pre-frame evidence",
                attemptClOrdId,
                ex);
        }

        ValueTask OnFramePrepared(
            ExchangeGatewayFrameIdentity frame,
            CancellationToken cancellationToken)
        {
            ValidateFrame(mutation, attemptClOrdId, frame);
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
        }
    }

    private async Task RestoreProjectionAsync(
        OutboundMutationSnapshot mutation,
        CancellationToken cancellationToken)
    {
        if (mutation.OriginalClOrdId is not { } originalClOrdId
            || mutation.Approval is null
            || !_orders.TryGet(originalClOrdId, out var original)
            || original is null)
            return;
        if (mutation.State == OutboundMutationState.ProvenUnsent)
        {
            RemoveProjection(mutation.Kind, ActiveClOrdId(mutation));
            return;
        }
        if (mutation.State == OutboundMutationState.OperatorResolved)
        {
            if (mutation.OperatorEvidence.LastOrDefault()?.Decision
                == OutboundOperatorDecision.VenueAbsent)
            {
                RemoveProjection(mutation.Kind, ActiveClOrdId(mutation));
            }
            return;
        }
        if (mutation.State is OutboundMutationState.VenueAcknowledged
            or OutboundMutationState.LegacyTerminal)
            return;
        var sensitive = _protector.Decrypt(
            mutation.MutationId,
            mutation.FirmId,
            mutation.Approval.CanonicalCommandNonSensitive,
            mutation.Approval.SensitiveFieldRefs,
            mutation.Approval.SensitiveCommandEnvelope);
        var canonical = mutation.Approval.CanonicalCommandNonSensitive with
        {
            ClOrdId = ActiveClOrdId(mutation),
        };
        await EnsureProjectionAsync(
            mutation,
            original,
            new EndClientId(sensitive.EndClientId),
            canonical,
            cancellationToken).ConfigureAwait(false);
        if (mutation.Kind == OutboundMutationKind.Replace
            && mutation.State == OutboundMutationState.Ambiguous)
        {
            _replacements.MarkAmbiguousMarginHeld(
                canonical.ClOrdId,
                mutation.StateChangedAtUtc,
                mutation.Approval.MarginAmount ?? 0m);
        }
    }

    private async Task<bool> PrepareRetryProjectionAsync(
        OutboundMutationSnapshot mutation,
        Order original,
        SensitiveOutboundCommand sensitive,
        OutboundCanonicalCommand canonical,
        CancellationToken cancellationToken) =>
        await EnsureProjectionAsync(
            mutation,
            original,
            new EndClientId(sensitive.EndClientId),
            canonical,
            cancellationToken).ConfigureAwait(false);

    private async Task<bool> EnsureProjectionAsync(
        OutboundMutationSnapshot mutation,
        Order original,
        EndClientId owner,
        OutboundCanonicalCommand canonical,
        CancellationToken cancellationToken)
    {
        if (mutation.Kind == OutboundMutationKind.Cancel)
        {
            if (!_pendingCancels.TryGetByCancel(canonical.ClOrdId, out _))
                _pendingCancels.TryAdd(original.ClOrdId, canonical.ClOrdId);
            _ownership.RegisterCancelLink(canonical.ClOrdId, original.ClOrdId);
            return true;
        }

        var remaining = Math.Max(0, canonical.Quantity - original.CumulativeQuantity);
        var notional = original.Side == OrderSide.Buy
            && original.Type.IsMarginBearing()
            && canonical.Price is { } price
                ? price * remaining
                : 0m;
        var margin = await _replaceMargin.PrepareReplaceAsync(
            original.ClOrdId,
            canonical.ClOrdId,
            owner,
            mutation.FirmId,
            notional,
            cancellationToken).ConfigureAwait(false);
        if (!margin.Approved)
            return false;
        var intent = CreateReplacementIntent(original, canonical, mutation.AlgoOriginIdentity);
        if (!_replacements.TryGet(canonical.ClOrdId, out _)
            && !_replacements.TryAdd(intent))
        {
            _replaceMargin.AbortReplace(canonical.ClOrdId);
            return false;
        }
        _ownership.RegisterReplaceLink(original.ClOrdId, canonical.ClOrdId);
        return true;
    }

    private CancelReplaceDispatchResult MarkAmbiguous(
        OutboundMutationSnapshot mutation,
        OutboundAttemptId attemptId,
        ulong clOrdId,
        string reason,
        Exception? exception = null)
    {
        _ledger.MarkAmbiguous(
            mutation.MutationId,
            attemptId,
            OutboundAmbiguityReason.GatewayOutcomeUnknown,
            _clock.GetUtcNow());
        if (mutation.Kind == OutboundMutationKind.Replace)
        {
            _replacements.MarkAmbiguousMarginHeld(
                clOrdId,
                _clock.GetUtcNow(),
                mutation.Approval?.MarginAmount ?? 0m);
        }
        return ReconciliationRequired(reason, clOrdId, exception);
    }

    private CancelReplaceDispatchResult MarkRetryProjectionUnsent(
        OutboundMutationSnapshot mutation,
        OutboundAttemptId attemptId,
        ulong clOrdId,
        string reason,
        Exception? exception = null)
    {
        var unsent = new OutboundProvenUnsentEvent
        {
            MutationId = mutation.MutationId,
            AttemptId = attemptId,
            Evidence = OutboundProvenUnsentEvidence.RetryProjectionNotPrepared,
            TimestampUtc = _clock.GetUtcNow(),
        };
        try
        {
            _dispatcher.DispatchCommitted(
                unsent,
                () => _ledger.Apply(unsent),
                CancellationToken.None);
            return new(CancelReplaceDispatchOutcome.MarginRejected, clOrdId, exception);
        }
        catch (Exception walException) when (
            walException is WalBackpressureException or WalFaultedException)
        {
            return ReconciliationRequired(reason, clOrdId, walException);
        }
    }

    private void RemoveProjection(OutboundMutationKind kind, ulong clOrdId)
    {
        if (kind == OutboundMutationKind.Cancel)
        {
            _pendingCancels.TryConsumeByCancel(clOrdId, out _);
            _ownership.RemoveCancelLink(clOrdId);
            _botMappings?.ReapCancel(clOrdId);
            return;
        }
        _replacements.TryConsume(clOrdId, out _);
        _ownership.RemoveCancelLink(clOrdId);
        _replaceMargin.AbortReplace(clOrdId);
    }

    private CancelReplaceDispatchResult ReconciliationRequired(
        string reason,
        ulong clOrdId,
        Exception? exception = null)
    {
        _drain.BeginDrain("outbound_cancel_replace_reconciliation_required");
        _logger.LogCritical(
            exception,
            "Cancel/replace outbound coordinator requires reconciliation: {Reason}.",
            reason);
        return new(CancelReplaceDispatchOutcome.ReconciliationRequired, clOrdId, exception);
    }

    private void RemoveExecution(
        OutboundMutationId mutationId,
        Lazy<Task<CancelReplaceDispatchResult>> execution) =>
        ((ICollection<KeyValuePair<
            OutboundMutationId,
            Lazy<Task<CancelReplaceDispatchResult>>>>)_executions)
        .Remove(new KeyValuePair<
            OutboundMutationId,
            Lazy<Task<CancelReplaceDispatchResult>>>(mutationId, execution));

    private static ulong ActiveClOrdId(OutboundMutationSnapshot mutation) =>
        mutation.Attempts.LastOrDefault()?.ClOrdId ?? mutation.PrimaryClOrdId;

    private static OrderReplacementIntent CreateReplacementIntent(
        Order original,
        OutboundCanonicalCommand canonical,
        AlgoOutboundOriginIdentity? algoOriginIdentity) =>
        new(
            original.ClOrdId,
            canonical.ClOrdId,
            original.Owner,
            canonical.Symbol,
            canonical.SecurityId,
            Enum.Parse<OrderSide>(canonical.Side, ignoreCase: true),
            Enum.Parse<OrderType>(canonical.OrderType, ignoreCase: true),
            canonical.Quantity,
            canonical.Price,
            original.FirmId,
            original.ParentAlgoId,
            original.AlgoSliceSeq,
            Enum.Parse<TimeInForce>(canonical.TimeInForce, ignoreCase: true),
            canonical.StopPrice,
            canonical.GoodTillDate,
            algoOriginIdentity is
            {
                ParentAlgoId: var parentAlgoId,
                ActionKind: AlgoOutboundActionKind.Repeg,
            } && parentAlgoId == original.ParentAlgoId);

    private static void ValidateFrame(
        OutboundMutationSnapshot mutation,
        ulong clOrdId,
        ExchangeGatewayFrameIdentity frame)
    {
        var operation = mutation.Kind == OutboundMutationKind.Cancel
            ? ExchangeGatewayOperation.Cancel
            : ExchangeGatewayOperation.Replace;
        if (frame.Operation != operation
            || frame.ClOrdId != clOrdId
            || !string.Equals(frame.FirmId, mutation.FirmId, StringComparison.Ordinal))
            throw new InvalidOperationException("Gateway frame identity does not match approved mutation.");
    }
}
