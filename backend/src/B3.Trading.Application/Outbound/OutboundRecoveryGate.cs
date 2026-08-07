namespace B3.Trading.Application.Outbound;

public enum OutboundRecoveryPhase
{
    WaitingForFence,
    RestoringPersistence,
    ClassifyingAttempts,
    ReconciliationRequired,
    Complete,
    FenceUnavailable,
    Faulted,
}

public sealed record FirmOutboundRecoveryStatus(
    string FirmId,
    bool Required,
    bool BusinessIngressOpen,
    int BlockingMutations);

public interface IOutboundRecoveryGate
{
    OutboundRecoveryPhase Phase { get; }
    bool IsClassificationComplete { get; }
    bool IsReady { get; }
    string? FailureReason { get; }
    IReadOnlyList<FirmOutboundRecoveryStatus> Snapshot();
    bool IsBusinessIngressOpen(string firmId);

    /// <summary>
    /// Per-end-client business-ingress check (#781 layer 2): narrower than
    /// <see cref="IsBusinessIngressOpen(string)"/> — only the specific
    /// end-client with a pending mutation is blocked, not the whole firm.
    /// <paramref name="endClientRef"/> should be the stable, privacy-safe ref
    /// (see <c>IOutboundCommandProtector.CreateStableEndClientRef</c>), or
    /// null/empty if the caller cannot resolve one yet (falls back to the
    /// firm-level check).
    /// </summary>
    bool IsBusinessIngressOpen(string firmId, string? endClientRef);

    /// <summary>
    /// Candidate-aware counterpart of
    /// <see cref="IsBusinessIngressOpen(string, string)"/>: a stable-reference
    /// key rotation changes what <c>CreateStableEndClientRef</c> returns for
    /// the same (firm, end-client) pair going forward, but a mutation
    /// recorded before the rotation still carries the ref computed under the
    /// previous key. Callers should pass every ref still produced by a
    /// currently-loaded key (see
    /// <c>IOutboundCommandProtector.CreateStableEndClientRefCandidates</c>)
    /// so a pending blocker recorded under an older key is not silently
    /// bypassed right after a rotation. The end-client is blocked if ANY
    /// candidate has a pending blocker.
    /// </summary>
    bool IsBusinessIngressOpen(string firmId, IReadOnlyCollection<string>? endClientRefCandidates);

    ValueTask WaitUntilClassificationCompleteAsync(CancellationToken cancellationToken);
    ValueTask WaitUntilBusinessIngressOpenAsync(string firmId, CancellationToken cancellationToken);

    /// <summary>Per-end-client counterpart of <see cref="WaitUntilBusinessIngressOpenAsync(string, CancellationToken)"/>.</summary>
    ValueTask WaitUntilBusinessIngressOpenAsync(
        string firmId,
        string? endClientRef,
        CancellationToken cancellationToken);

    /// <summary>Candidate-aware counterpart, see <see cref="IsBusinessIngressOpen(string, IReadOnlyCollection{string})"/>.</summary>
    ValueTask WaitUntilBusinessIngressOpenAsync(
        string firmId,
        IReadOnlyCollection<string>? endClientRefCandidates,
        CancellationToken cancellationToken);

    ValueTask WaitUntilAllRequiredBusinessIngressOpenAsync(CancellationToken cancellationToken);
}

public sealed class ImmediateOutboundRecoveryGate : IOutboundRecoveryGate
{
    public static ImmediateOutboundRecoveryGate Instance { get; } = new();

    private ImmediateOutboundRecoveryGate()
    {
    }

    public OutboundRecoveryPhase Phase => OutboundRecoveryPhase.Complete;
    public bool IsClassificationComplete => true;
    public bool IsReady => true;
    public string? FailureReason => null;

    public IReadOnlyList<FirmOutboundRecoveryStatus> Snapshot() =>
        Array.Empty<FirmOutboundRecoveryStatus>();

    public bool IsBusinessIngressOpen(string firmId) => true;

    public bool IsBusinessIngressOpen(string firmId, string? endClientRef) => true;

    public bool IsBusinessIngressOpen(string firmId, IReadOnlyCollection<string>? endClientRefCandidates) => true;

    public ValueTask WaitUntilClassificationCompleteAsync(
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask WaitUntilBusinessIngressOpenAsync(
        string firmId,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask WaitUntilBusinessIngressOpenAsync(
        string firmId,
        string? endClientRef,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask WaitUntilBusinessIngressOpenAsync(
        string firmId,
        IReadOnlyCollection<string>? endClientRefCandidates,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask WaitUntilAllRequiredBusinessIngressOpenAsync(
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed class OutboundRecoveryState : IOutboundRecoveryGate
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    private readonly object _gate = new();
    private readonly OutboundMutationLedger _ledger;
    private readonly TaskCompletionSource _classificationCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HashSet<string> _requiredFirms = new(StringComparer.Ordinal);
    private HashSet<string> _recoveryBlockingFirms = new(StringComparer.Ordinal);
    private bool _recoveryHadUnknownBlockers;
    private OutboundRecoveryPhase _phase = OutboundRecoveryPhase.WaitingForFence;
    private string? _failureReason;

    public OutboundRecoveryState(OutboundMutationLedger ledger)
    {
        _ledger = ledger;
    }

    public OutboundRecoveryPhase Phase
    {
        get
        {
            lock (_gate)
                return EffectivePhaseUnsafe();
        }
    }

    public bool IsClassificationComplete =>
        Phase is OutboundRecoveryPhase.Complete
            or OutboundRecoveryPhase.ReconciliationRequired;

    public bool IsReady =>
        IsClassificationComplete
        && Snapshot().Where(static status => status.Required)
            .All(static status => status.BusinessIngressOpen);

    public string? FailureReason
    {
        get
        {
            lock (_gate)
                return EffectivePhaseUnsafe() == OutboundRecoveryPhase.Complete
                    ? null
                    : _failureReason;
        }
    }

    public void ConfigureRequiredFirms(IEnumerable<string> firmIds)
    {
        ArgumentNullException.ThrowIfNull(firmIds);
        var firms = firmIds
            .Where(static firm => !string.IsNullOrWhiteSpace(firm))
            .ToHashSet(StringComparer.Ordinal);
        if (firms.Count == 0)
            throw new ArgumentException("At least one required firm is required.", nameof(firmIds));

        lock (_gate)
        {
            if (_requiredFirms.Count > 0 && !_requiredFirms.SetEquals(firms))
                throw new InvalidOperationException("Required recovery firms were already configured.");
            _requiredFirms = firms;
        }
    }

    public void MarkRestoring()
    {
        lock (_gate)
            SetPhaseUnsafe(OutboundRecoveryPhase.RestoringPersistence, null);
    }

    public void MarkClassifying()
    {
        lock (_gate)
            SetPhaseUnsafe(OutboundRecoveryPhase.ClassifyingAttempts, null);
    }

    public void Complete()
    {
        lock (_gate)
        {
            var blockers = BlockingCountsUnsafe();
            var unknown = blockers.GetValueOrDefault(OutboundMutationLedger.UnknownFirmId);
            _recoveryHadUnknownBlockers = unknown > 0;
            _recoveryBlockingFirms = blockers
                .Where(static pair =>
                    pair.Key != OutboundMutationLedger.UnknownFirmId
                    && pair.Value > 0)
                .Select(static pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
            var requiredBlocking = unknown > 0
                || _requiredFirms.Any(firm => blockers.GetValueOrDefault(firm) > 0);
            SetPhaseUnsafe(
                !requiredBlocking
                    ? OutboundRecoveryPhase.Complete
                    : OutboundRecoveryPhase.ReconciliationRequired,
                !requiredBlocking
                    ? null
                    : "required outbound mutations need reconciliation");
            _classificationCompleted.TrySetResult();
        }
    }

    public void FailFence(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
            SetPhaseUnsafe(OutboundRecoveryPhase.FenceUnavailable, reason);
    }

    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
            SetPhaseUnsafe(OutboundRecoveryPhase.Faulted, exception.GetType().Name);
    }

    public IReadOnlyList<FirmOutboundRecoveryStatus> Snapshot()
    {
        lock (_gate)
        {
            var blockers = BlockingCountsUnsafe();
            var firms = _requiredFirms
                .Concat(_recoveryBlockingFirms)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static firm => firm, StringComparer.Ordinal)
                .ToArray();
            var statuses = firms.Select(firm =>
            {
                var count = _recoveryBlockingFirms.Contains(firm)
                    ? blockers.GetValueOrDefault(firm)
                    : 0;
                return new FirmOutboundRecoveryStatus(
                    firm,
                    _requiredFirms.Contains(firm),
                    IsClassificationComplete && count == 0,
                    count);
            });

            // #781 layer 3: unattributed (unknown-firm) evidence is surfaced
            // as its own line item for operator visibility, but must never
            // be smeared into every firm's blocking count — that would
            // reopen the cross-firm broadcast this fix removes from
            // IsBusinessIngressOpen below.
            var unknownCount = _recoveryHadUnknownBlockers
                ? blockers.GetValueOrDefault(OutboundMutationLedger.UnknownFirmId)
                : 0;
            if (unknownCount > 0)
            {
                statuses = statuses.Append(new FirmOutboundRecoveryStatus(
                    OutboundMutationLedger.UnknownFirmId,
                    Required: false,
                    BusinessIngressOpen: false,
                    unknownCount));
            }

            return statuses.ToArray();
        }
    }

    public bool IsBusinessIngressOpen(string firmId)
    {
        if (string.IsNullOrWhiteSpace(firmId))
            return false;
        lock (_gate)
        {
            if (_phase is not (OutboundRecoveryPhase.Complete
                    or OutboundRecoveryPhase.ReconciliationRequired))
            {
                return false;
            }

            // #781 layer 3: unattributed evidence for a *different* firm
            // (unknown FirmId) no longer blocks this firm's ingress — it is
            // surfaced separately via Snapshot() for operator investigation
            // instead of broadcasting to every unrelated firm.
            var blockers = BlockingCountsUnsafe();
            return !_recoveryBlockingFirms.Contains(firmId)
                || blockers.GetValueOrDefault(firmId) == 0;
        }
    }

    public bool IsBusinessIngressOpen(string firmId, string? endClientRef) =>
        IsBusinessIngressOpen(
            firmId,
            string.IsNullOrWhiteSpace(endClientRef) ? null : new[] { endClientRef });

    public bool IsBusinessIngressOpen(
        string firmId, IReadOnlyCollection<string>? endClientRefCandidates)
    {
        if (endClientRefCandidates is null || endClientRefCandidates.Count == 0)
            return IsBusinessIngressOpen(firmId);
        if (string.IsNullOrWhiteSpace(firmId))
            return false;
        lock (_gate)
        {
            if (_phase is not (OutboundRecoveryPhase.Complete
                    or OutboundRecoveryPhase.ReconciliationRequired))
            {
                return false;
            }
            if (!_recoveryBlockingFirms.Contains(firmId))
                return true;

            var byEndClient = _ledger.GetReadinessBlockingCountsByEndClient(firmId);
            // Unmatched inbound evidence for this firm cannot be attributed
            // to a specific end-client, so it is treated as blocking every
            // end-client of the firm — we cannot safely rule any of them out.
            if (byEndClient.GetValueOrDefault(OutboundMutationLedger.UnknownEndClientRef) > 0)
                return false;

            // A stable-reference key rotation changes what
            // CreateStableEndClientRef returns for the same end-client going
            // forward, but a mutation recorded under a previous key still
            // carries that key's ref. Checking every currently-supported
            // candidate (not just the active one) keeps a pending blocker
            // from a pre-rotation mutation from being silently bypassed.
            foreach (var candidate in endClientRefCandidates)
            {
                if (byEndClient.GetValueOrDefault(candidate) > 0)
                    return false;
            }
            return true;
        }
    }

    public async ValueTask WaitUntilClassificationCompleteAsync(
        CancellationToken cancellationToken) =>
        await _classificationCompleted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask WaitUntilBusinessIngressOpenAsync(
        string firmId,
        CancellationToken cancellationToken)
    {
        await WaitUntilClassificationCompleteAsync(cancellationToken).ConfigureAwait(false);
        while (!IsBusinessIngressOpen(firmId))
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WaitUntilBusinessIngressOpenAsync(
        string firmId,
        string? endClientRef,
        CancellationToken cancellationToken)
    {
        await WaitUntilClassificationCompleteAsync(cancellationToken).ConfigureAwait(false);
        while (!IsBusinessIngressOpen(firmId, endClientRef))
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WaitUntilBusinessIngressOpenAsync(
        string firmId,
        IReadOnlyCollection<string>? endClientRefCandidates,
        CancellationToken cancellationToken)
    {
        await WaitUntilClassificationCompleteAsync(cancellationToken).ConfigureAwait(false);
        while (!IsBusinessIngressOpen(firmId, endClientRefCandidates))
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WaitUntilAllRequiredBusinessIngressOpenAsync(
        CancellationToken cancellationToken)
    {
        await WaitUntilClassificationCompleteAsync(cancellationToken).ConfigureAwait(false);
        while (!IsReady)
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
    }

    private Dictionary<string, int> BlockingCountsUnsafe() =>
        _ledger.GetReadinessBlockingCountsByFirm()
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

    private OutboundRecoveryPhase EffectivePhaseUnsafe()
    {
        if (_phase != OutboundRecoveryPhase.ReconciliationRequired)
            return _phase;
        var blockers = BlockingCountsUnsafe();
        if (_recoveryHadUnknownBlockers
            && blockers.GetValueOrDefault(OutboundMutationLedger.UnknownFirmId) > 0)
        {
            return _phase;
        }
        return _requiredFirms.Any(firm =>
                _recoveryBlockingFirms.Contains(firm)
                && blockers.GetValueOrDefault(firm) > 0)
            ? _phase
            : OutboundRecoveryPhase.Complete;
    }

    private void SetPhaseUnsafe(OutboundRecoveryPhase phase, string? failureReason)
    {
        _phase = phase;
        _failureReason = failureReason;
    }
}
