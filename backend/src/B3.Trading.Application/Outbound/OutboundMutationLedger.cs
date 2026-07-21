using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application.Outbound;

/// <summary>
/// Pure durable projection of outbound evidence. The transition matrix is:
/// Approved → IntentPrepared → FramePrepared → WriteCompleted; IntentPrepared
/// may become ProvenUnsent only while no frame exists; dead-epoch frame/write
/// attempts become Ambiguous; only venue evidence or an authorised operator
/// resolution terminalises ambiguity. Duplicate identical evidence is a no-op;
/// conflicting, missing or out-of-order evidence fails closed.
///
/// Invariants: one active attempt per mutation, unique attempt/ClOrdID/frame
/// correlation, no retry after ambiguity, no purge of unresolved state, and no
/// plaintext customer identity in ledger snapshots or diagnostics.
/// </summary>
public sealed class OutboundMutationLedger
{
    private const string UnknownFirm = "<unknown>";
    public const string UnknownFirmId = UnknownFirm;
    public const int MaxOutboundAttempts = 2;
    public const int MaxRetainedUnmatchedEvidence = 1024;
    public const int MaxEvidenceDiagnostics = 256;
    public static readonly TimeSpan DefaultTerminalCorrelationRetention = TimeSpan.FromDays(30);

    private readonly object _gate = new();
    private readonly Dictionary<OutboundMutationId, OutboundMutationSnapshot> _mutations = new();
    private readonly Dictionary<ulong, OutboundMutationId> _byClOrdId = new();
    private readonly Dictionary<OriginalOrderKey, OutboundMutationId> _activeByOriginal = new();
    private readonly Dictionary<FirmAlgoOriginKey, OutboundMutationId> _byAlgoOrigin = new();
    private readonly Dictionary<FrameKey, OutboundMutationId> _byFrame = new();
    private readonly Dictionary<ulong, OutboundCorrelationTombstone> _correlations = new();
    private readonly Dictionary<string, InboundVenueEvidenceSnapshot> _inboundEvidence =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _inboundEvidenceIdentity =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _unmatchedEvidenceOrder = new();
    private readonly IOutboundCommandProtector? _protector;
    private bool _legacyMigrationCompleted;

    public OutboundMutationLedger(IOutboundCommandProtector? protector = null)
    {
        _protector = protector;
    }

    public int Count
    {
        get { lock (_gate) return _mutations.Count; }
    }

    public int ReadinessBlockingCount
    {
        get
        {
            lock (_gate)
                return _mutations.Values.Count(IsReadinessBlocking)
                    + _inboundEvidence.Values.Count(e =>
                        e.Disposition != InboundVenueEvidenceDisposition.Matched
                        && (e.MatchedMutationIds.Count == 0
                            || !e.MatchedMutationIds.Any(_mutations.ContainsKey)));
        }
    }

    public IReadOnlyDictionary<string, int> GetReadinessBlockingCountsByFirm()
    {
        lock (_gate)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var mutation in _mutations.Values.Where(IsReadinessBlocking))
                Increment(counts, string.IsNullOrWhiteSpace(mutation.FirmId) ? UnknownFirm : mutation.FirmId);
            foreach (var evidence in _inboundEvidence.Values.Where(e =>
                         e.Disposition != InboundVenueEvidenceDisposition.Matched
                         && (e.MatchedMutationIds.Count == 0
                             || !e.MatchedMutationIds.Any(_mutations.ContainsKey))))
            {
                Increment(
                    counts,
                    string.IsNullOrWhiteSpace(evidence.FirmId) ? UnknownFirm : evidence.FirmId);
            }
            return counts;
        }

        static void Increment(Dictionary<string, int> counts, string firmId) =>
            counts[firmId] = counts.GetValueOrDefault(firmId) + 1;
    }

    public int InboundEvidenceCount
    {
        get { lock (_gate) return _inboundEvidence.Count; }
    }

    public bool LegacyMigrationCompleted
    {
        get { lock (_gate) return _legacyMigrationCompleted; }
    }

    public bool ShouldImportLegacy
    {
        get { lock (_gate) return !_legacyMigrationCompleted; }
    }

    public void CompleteLegacyMigration()
    {
        lock (_gate)
            _legacyMigrationCompleted = true;
    }

    public void Apply(OutboundApprovedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ValidateIdentity(evt.MutationId, evt.PrimaryClOrdId, evt.FirmId, evt.EndClientRef);
        ValidateOrigin(evt.Origin, evt.AlgoOriginIdentity);
        ValidateApproval(evt.Approval, evt.PrimaryClOrdId);
        lock (_gate)
        {
            if (_mutations.TryGetValue(evt.MutationId, out var existing))
            {
                if (IsApprovalReplaceableState(existing.State)
                    && existing.PrimaryClOrdId == evt.PrimaryClOrdId
                    && existing.Kind == evt.MutationKind)
                {
                    RemoveMutationIndexes(existing);
                    _mutations.Remove(evt.MutationId);
                }
                else if (ApprovalEquivalent(existing, evt))
                    return;
                else
                    throw TransitionError("Conflicting approval evidence.");
            }
            if (_byClOrdId.TryGetValue(evt.PrimaryClOrdId, out var existingMutation))
            {
                if (_mutations.TryGetValue(existingMutation, out var legacy)
                    && IsApprovalReplaceableState(legacy.State))
                {
                    RemoveMutationIndexes(legacy);
                    _mutations.Remove(existingMutation);
                }
                else
                {
                    throw TransitionError("The approval ClOrdID is already correlated.");
                }
            }
            if (evt.OriginalClOrdId is { } originalClOrdId)
            {
                var originalKey = new OriginalOrderKey(evt.FirmId, originalClOrdId);
                if (_activeByOriginal.TryGetValue(originalKey, out var activeMutation)
                    && activeMutation != evt.MutationId)
                    throw TransitionError("The original order already has an active outbound mutation.");
            }
            if (evt.AlgoOriginIdentity is { } algoOrigin
                && _byAlgoOrigin.TryGetValue(
                    new FirmAlgoOriginKey(evt.FirmId, algoOrigin),
                    out var existingAlgoMutation)
                && existingAlgoMutation != evt.MutationId)
            {
                throw TransitionError("The algo logical action already has an outbound mutation.");
            }

            var availability = CheckPayloadAvailability(evt);
            var requiresReconciliation = availability != OutboundSensitivePayloadAvailability.Available;
            var mutation = new OutboundMutationSnapshot
            {
                MutationId = evt.MutationId,
                Kind = evt.MutationKind,
                FirmId = evt.FirmId,
                EndClientRef = evt.EndClientRef,
                Origin = evt.Origin,
                AlgoOriginIdentity = evt.AlgoOriginIdentity,
                BotBusinessIdentity = evt.BotBusinessIdentity,
                PrimaryClOrdId = evt.PrimaryClOrdId,
                OriginalClOrdId = evt.OriginalClOrdId,
                RecordedAtUtc = evt.RecordedAtUtc,
                Approval = evt.Approval,
                State = OutboundMutationState.ApprovedToSend,
                StateChangedAtUtc = evt.TimestampUtc,
                SensitivePayloadAvailability = availability,
                RequiresReconciliation = requiresReconciliation,
            };
            _mutations.Add(evt.MutationId, mutation);
            AddClOrdCorrelation(mutation, evt.PrimaryClOrdId, terminal: false, evt.TimestampUtc);
            AddActiveOriginalIndex(mutation);
            AddAlgoOriginIndex(mutation);
        }
    }

    public void Apply(OutboundAttemptIntentPreparedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.AttemptId.Value == Guid.Empty || evt.ProcessEpochId.Value == Guid.Empty
            || evt.AttemptNo <= 0 || evt.ClOrdId == 0)
            throw TransitionError("Attempt identity is invalid.");
        lock (_gate)
        {
            var mutation = RequiredMutation(evt.MutationId);
            var duplicate = mutation.Attempts.FirstOrDefault(a => a.AttemptId == evt.AttemptId);
            if (duplicate is not null)
            {
                if (duplicate.AttemptNo == evt.AttemptNo
                    && duplicate.ClOrdId == evt.ClOrdId
                    && duplicate.ProcessEpochId == evt.ProcessEpochId
                    && duplicate.IntentPreparedAtUtc == evt.IntentPreparedAtUtc)
                    return;
                throw TransitionError("Conflicting attempt-intent evidence.");
            }
            if (mutation.State is not OutboundMutationState.ApprovedToSend
                and not OutboundMutationState.ProvenUnsent)
                throw TransitionError("Attempt intent is out of order.");
            if (mutation.State == OutboundMutationState.ProvenUnsent
                && mutation.OriginalClOrdId is { } retryOriginalClOrdId)
            {
                var originalKey = new OriginalOrderKey(mutation.FirmId, retryOriginalClOrdId);
                if (_activeByOriginal.TryGetValue(originalKey, out var activeMutation)
                    && activeMutation != mutation.MutationId)
                {
                    throw TransitionError(
                        "Another outbound mutation became active before the retry attempt.");
                }
            }
            if (mutation.Attempts.Count >= MaxOutboundAttempts
                || evt.AttemptNo != mutation.Attempts.Count + 1)
                throw TransitionError("Attempt number or cap is invalid.");
            if (mutation.Attempts.Any(a => a.ClOrdId == evt.ClOrdId)
                || (_byClOrdId.TryGetValue(evt.ClOrdId, out var owner)
                    && owner != mutation.MutationId))
                throw TransitionError("Attempt ClOrdID is already correlated.");
            if (evt.AttemptNo == 1 && evt.ClOrdId != mutation.PrimaryClOrdId)
                throw TransitionError("The initial attempt must use the approved ClOrdID.");

            var attempts = mutation.Attempts.ToList();
            attempts.Add(new OutboundAttemptSnapshot
            {
                AttemptId = evt.AttemptId,
                AttemptNo = evt.AttemptNo,
                ClOrdId = evt.ClOrdId,
                ProcessEpochId = evt.ProcessEpochId,
                IntentPreparedAtUtc = evt.IntentPreparedAtUtc,
            });
            mutation = mutation with
            {
                Attempts = attempts,
                State = OutboundMutationState.AttemptIntentPrepared,
                StateChangedAtUtc = evt.TimestampUtc,
                Resolution = null,
            };
            _mutations[evt.MutationId] = mutation;
            AddClOrdCorrelation(mutation, evt.ClOrdId, terminal: false, evt.TimestampUtc);
            AddActiveOriginalIndex(mutation);
        }
    }

    internal bool CanPrepareAttempt(
        OutboundMutationId mutationId,
        int attemptNo,
        ulong clOrdId)
    {
        lock (_gate)
        {
            if (!_mutations.TryGetValue(mutationId, out var mutation)
                || mutation.State is not (OutboundMutationState.ApprovedToSend
                    or OutboundMutationState.ProvenUnsent)
                || mutation.Attempts.Count >= MaxOutboundAttempts
                || attemptNo != mutation.Attempts.Count + 1
                || mutation.Attempts.Any(attempt => attempt.ClOrdId == clOrdId)
                || (_byClOrdId.TryGetValue(clOrdId, out var clOrdOwner)
                    && clOrdOwner != mutationId))
            {
                return false;
            }
            if (mutation.State != OutboundMutationState.ProvenUnsent
                || mutation.OriginalClOrdId is not { } originalClOrdId)
            {
                return true;
            }
            var key = new OriginalOrderKey(mutation.FirmId, originalClOrdId);
            return !_activeByOriginal.TryGetValue(key, out var activeMutation)
                || activeMutation == mutationId;
        }
    }

    public void Apply(OutboundFramePreparedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.SessionId == 0 || evt.SessionVerId == 0 || evt.OutboundSeqNum == 0
            || !IsLowerHex(evt.EncodedFrameSha256, 64))
            throw TransitionError("Frame correlation is invalid.");
        lock (_gate)
        {
            var mutation = RequiredMutation(evt.MutationId);
            var (attempt, index) = RequiredAttempt(mutation, evt.AttemptId);
            if (attempt.FramePrepared is { } existing)
            {
                if (existing.SessionId == evt.SessionId
                    && existing.SessionVerId == evt.SessionVerId
                    && existing.OutboundSeqNum == evt.OutboundSeqNum
                    && existing.EncodedFrameSha256 == evt.EncodedFrameSha256
                    && existing.PreparedAtUtc == evt.PreparedAtUtc)
                    return;
                throw TransitionError("Conflicting frame-prepared evidence.");
            }
            if (mutation.State != OutboundMutationState.AttemptIntentPrepared
                || index != mutation.Attempts.Count - 1)
                throw TransitionError("Frame preparation is out of order.");
            var key = new FrameKey(evt.FirmId, evt.SessionId, evt.SessionVerId, evt.OutboundSeqNum);
            if (!string.Equals(mutation.FirmId, evt.FirmId, StringComparison.Ordinal)
                || (_byFrame.TryGetValue(key, out var owner) && owner != mutation.MutationId))
                throw TransitionError("Frame correlation conflicts with existing evidence.");

            var updatedAttempt = attempt with
            {
                FramePrepared = new OutboundFramePreparedSnapshot
                {
                    SessionId = evt.SessionId,
                    SessionVerId = evt.SessionVerId,
                    OutboundSeqNum = evt.OutboundSeqNum,
                    EncodedFrameSha256 = evt.EncodedFrameSha256,
                    PreparedAtUtc = evt.PreparedAtUtc,
                },
            };
            _mutations[evt.MutationId] = ReplaceAttempt(
                mutation, index, updatedAttempt,
                OutboundMutationState.FramePrepared, evt.TimestampUtc);
            _byFrame[key] = mutation.MutationId;
        }
    }

    public void Apply(OutboundTransportWriteCompletedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        lock (_gate)
        {
            var mutation = RequiredMutation(evt.MutationId);
            var (attempt, index) = RequiredAttempt(mutation, evt.AttemptId);
            if (attempt.TransportWriteCompletedAtUtc is { } existing)
            {
                if (existing == evt.CompletedAtUtc
                    && attempt.GatewayReceiptVersion == evt.GatewayReceiptVersion)
                    return;
                throw TransitionError("Conflicting transport-write evidence.");
            }
            if (mutation.State != OutboundMutationState.FramePrepared
                || attempt.FramePrepared is null
                || index != mutation.Attempts.Count - 1)
                throw TransitionError("Transport-write completion is out of order.");
            var updatedAttempt = attempt with
            {
                TransportWriteCompletedAtUtc = evt.CompletedAtUtc,
                GatewayReceiptVersion = evt.GatewayReceiptVersion,
            };
            _mutations[evt.MutationId] = ReplaceAttempt(
                mutation, index, updatedAttempt,
                OutboundMutationState.TransportWriteCompleted, evt.TimestampUtc);
        }
    }

    public void Apply(OutboundProvenUnsentEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        lock (_gate)
        {
            var mutation = RequiredMutation(evt.MutationId);
            var (attempt, index) = RequiredAttempt(mutation, evt.AttemptId);
            if (attempt.ProvenUnsentEvidence is { } existing)
            {
                if (existing == evt.Evidence)
                    return;
                throw TransitionError("Conflicting proven-unsent evidence.");
            }
            if (mutation.State != OutboundMutationState.AttemptIntentPrepared
                || attempt.FramePrepared is not null
                || index != mutation.Attempts.Count - 1)
                throw TransitionError("Proven-unsent evidence is invalid after frame preparation.");
            var updatedAttempt = attempt with { ProvenUnsentEvidence = evt.Evidence };
            var updatedMutation = ReplaceAttempt(
                mutation, index, updatedAttempt,
                OutboundMutationState.ProvenUnsent, evt.TimestampUtc);
            _mutations[evt.MutationId] = updatedMutation;
            AddClOrdCorrelation(
                updatedMutation, attempt.ClOrdId, terminal: true, evt.TimestampUtc);
            RemoveActiveOriginalIndex(updatedMutation);
        }
    }

    public void Apply(OutboundAuthoritativeEvidenceRegisteredEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(evt.Evidence);
        var evidence = evt.Evidence;
        if (!IsOpaqueReference(evidence.EvidenceReference)
            || !IsLowerHex(evidence.EvidenceDigest, 64)
            || !IsOpaqueReference(evidence.AttestationReference)
            || !IsOpaqueReference(evidence.AttestedBy)
            || evidence.CoverageEndUtc < evidence.CoverageStartUtc
            || evidence.AttestedAtUtc > evidence.RegisteredAtUtc
            || !EvidenceReferenceMatchesSource(
                evidence.SourceType,
                evidence.EvidenceReference,
                evidence.EvidenceDigest))
            throw TransitionError("Authoritative evidence registration is incomplete.");
        lock (_gate)
        {
            var mutation = RequiredMutation(evt.MutationId);
            if (!string.Equals(mutation.FirmId, evidence.FirmId, StringComparison.Ordinal)
                || !evidence.CoveredMutationIds.Contains(evt.MutationId)
                || mutation.RecordedAtUtc < evidence.CoverageStartUtc
                || mutation.RecordedAtUtc > evidence.CoverageEndUtc)
                throw TransitionError("Authoritative evidence does not cover the mutation.");
            var duplicate = mutation.AuthoritativeEvidence.FirstOrDefault(
                candidate => candidate.EvidenceReference == evidence.EvidenceReference);
            if (duplicate is not null)
            {
                if (AuthoritativeEvidenceEquals(duplicate, evidence))
                    return;
                throw TransitionError("Conflicting authoritative evidence registration.");
            }
            var registrations = mutation.AuthoritativeEvidence.ToList();
            registrations.Add(CloneAuthoritativeEvidence(evidence));
            _mutations[evt.MutationId] = mutation with
            {
                AuthoritativeEvidence = registrations,
            };
        }
    }

    public void Apply(OutboundOperatorResolutionProposedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.ProposalId.Value == Guid.Empty
            || !IsLowerHex(evt.EvidenceDigest, 64)
            || !IsOpaqueReference(evt.EvidenceReference)
            || !IsOpaqueReference(evt.ReasonCode)
            || !IsOpaqueReference(evt.MakerRef))
            throw TransitionError("Operator resolution proposal is incomplete.");
        ValidateOperatorEvidencePair(evt.Decision, evt.EvidenceType, releaseCapacity: true);
        lock (_gate)
        {
            var mutation = RequiredMutation(evt.MutationId);
            var duplicate = mutation.ResolutionProposals.FirstOrDefault(
                proposal => proposal.ProposalId == evt.ProposalId);
            if (duplicate is not null)
            {
                if (duplicate.Decision == evt.Decision
                    && duplicate.EvidenceType == evt.EvidenceType
                    && duplicate.EvidenceReference == evt.EvidenceReference
                    && duplicate.EvidenceDigest == evt.EvidenceDigest
                    && duplicate.ReasonCode == evt.ReasonCode
                    && duplicate.MakerRef == evt.MakerRef
                    && duplicate.ProposedAtUtc == evt.ProposedAtUtc)
                    return;
                throw TransitionError("Conflicting operator resolution proposal.");
            }
            if (!CanOperatorResolve(mutation))
                throw TransitionError("Operator resolution is not valid in the current state.");
            if (mutation.ResolutionProposals.Any(proposal => proposal.ApprovedAtUtc is null))
                throw TransitionError("A maker/checker proposal is already pending.");
            var proposals = mutation.ResolutionProposals.ToList();
            proposals.Add(new OutboundOperatorResolutionProposalSnapshot
            {
                ProposalId = evt.ProposalId,
                Decision = evt.Decision,
                EvidenceType = evt.EvidenceType,
                EvidenceReference = evt.EvidenceReference,
                EvidenceDigest = evt.EvidenceDigest,
                ReasonCode = evt.ReasonCode,
                MakerRef = evt.MakerRef,
                ProposedAtUtc = evt.ProposedAtUtc,
            });
            _mutations[evt.MutationId] = mutation with
            {
                ResolutionProposals = proposals,
                RequiresReconciliation = true,
            };
        }
    }

    public void Apply(OutboundReconciliationRequiredEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        lock (_gate)
        {
            var mutation = RequiredMutation(evt.MutationId);
            if (mutation.ExplicitlyRequiresReconciliation) return;
            _mutations[mutation.MutationId] = mutation with
            {
                RequiresReconciliation = true,
                ExplicitlyRequiresReconciliation = true,
                StateChangedAtUtc = evt.TimestampUtc,
            };
        }
    }

    public void Apply(OutboundOperatorResolvedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ValidateOperatorEvidencePair(evt.Decision, evt.EvidenceType, evt.ReleaseCapacity);
        if (!IsLowerHex(evt.EvidenceDigest, 64)
            || !IsOpaqueReference(evt.OperatorRef)
            || (evt.EvidenceReference is not null && !IsOpaqueReference(evt.EvidenceReference))
            || (evt.ReasonCode is not null && !IsOpaqueReference(evt.ReasonCode))
            || (evt.MakerRef is not null && !IsOpaqueReference(evt.MakerRef))
            || (evt.CheckerRef is not null && !IsOpaqueReference(evt.CheckerRef)))
            throw TransitionError("Operator resolution evidence is incomplete.");
        lock (_gate)
        {
            var mutation = RequiredMutation(evt.MutationId);
            var duplicate = mutation.OperatorEvidence.FirstOrDefault(e =>
                e.EvidenceDigest == evt.EvidenceDigest);
            if (duplicate is not null)
            {
                if (duplicate.Decision == evt.Decision
                    && duplicate.EvidenceType == evt.EvidenceType
                    && duplicate.OperatorRef == evt.OperatorRef
                    && duplicate.ProposalId == evt.ProposalId
                    && duplicate.CapacityReleased == evt.ReleaseCapacity)
                    return;
                throw TransitionError("Conflicting operator resolution.");
            }
            if (evt.EvidenceType == OutboundOperatorEvidenceType.TerminalExecutionReport)
            {
                if (evt.EvidenceReference is not { } terminalEvidenceReference
                    || !HasAuthoritativeTerminalExecutionReportUnsafe(
                        mutation,
                        terminalEvidenceReference,
                        out var terminalEvidence))
                    throw TransitionError(
                        "Terminal execution report evidence is not currently authoritative.");
                if (evt.Decision == OutboundOperatorDecision.VenueAbsent
                    && IsVenueAcknowledgmentOnlyExecutionReportKind(
                        terminalEvidence.MessageKind))
                    throw TransitionError(
                        "Fill or Replaced execution reports cannot prove venue absence.");
            }
            if (mutation.OperatorEvidence.Any(
                    evidence => evidence.Decision != OutboundOperatorDecision.LeaveAmbiguous)
                && mutation.State is OutboundMutationState.OperatorResolved
                    or OutboundMutationState.VenueAcknowledged)
                throw TransitionError("Outbound mutation already has a terminal operator resolution.");
            if (!CanOperatorResolve(mutation))
                throw TransitionError("Operator resolution is not valid in the current state.");
            var proposals = mutation.ResolutionProposals.ToList();
            if (evt.ProposalId is { } proposalId)
            {
                var proposalIndex = proposals.FindIndex(
                    proposal => proposal.ProposalId == proposalId);
                if (proposalIndex < 0)
                    throw TransitionError("Maker/checker proposal is unknown.");
                var proposal = proposals[proposalIndex];
                if (proposal.ApprovedAtUtc is not null)
                    throw TransitionError("Maker/checker proposal was already approved.");
                if (proposal.Decision != evt.Decision
                    || proposal.EvidenceType != evt.EvidenceType
                    || proposal.EvidenceDigest != evt.EvidenceDigest
                    || proposal.EvidenceReference != evt.EvidenceReference
                    || proposal.ReasonCode != evt.ReasonCode
                    || proposal.MakerRef != evt.MakerRef
                    || string.Equals(proposal.MakerRef, evt.CheckerRef, StringComparison.Ordinal))
                    throw TransitionError("Maker/checker approval does not match the proposal.");
                proposals[proposalIndex] = proposal with
                {
                    CheckerRef = evt.CheckerRef,
                    ApprovedAtUtc = evt.ResolvedAtUtc,
                };
            }
            var evidence = mutation.OperatorEvidence.ToList();
            evidence.Add(new OutboundOperatorEvidenceSnapshot
            {
                Decision = evt.Decision,
                EvidenceType = evt.EvidenceType,
                EvidenceDigest = evt.EvidenceDigest,
                EvidenceReference = evt.EvidenceReference,
                ReasonCode = evt.ReasonCode,
                OperatorRef = evt.OperatorRef,
                MakerRef = evt.MakerRef,
                CheckerRef = evt.CheckerRef,
                ProposalId = evt.ProposalId,
                CapacityReleased = evt.ReleaseCapacity,
                RecordedAtUtc = evt.ResolvedAtUtc,
            });
            mutation = mutation with
            {
                OperatorEvidence = evidence,
                ResolutionProposals = proposals,
            };
            if (evt.Decision == OutboundOperatorDecision.LeaveAmbiguous)
            {
                _mutations[evt.MutationId] = mutation with
                {
                    State = OutboundMutationState.Ambiguous,
                    StateChangedAtUtc = evt.ResolvedAtUtc,
                    RequiresReconciliation = true,
                };
                return;
            }
            var terminalState = evt.Decision == OutboundOperatorDecision.VenueAcknowledged
                ? OutboundMutationState.VenueAcknowledged
                : OutboundMutationState.OperatorResolved;
            Terminalise(
                mutation, terminalState, evt.ResolvedAtUtc,
                evt.EvidenceType.ToString(), evt.EvidenceDigest, venueOrderId: null);
        }
    }

    public InboundVenueEvidenceApplyResult ApplyVenueAcknowledgement(
        ExecutionReportReceivedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var evidenceId = ExecutionReportEvidenceId(evt);
        var evidenceIdentity = ExecutionReportEvidenceIdentity(evt, evidenceId);
        lock (_gate)
        {
            if (_inboundEvidence.ContainsKey(evidenceId))
            {
                if (evt.PossibleResend)
                    PromotePossibleResendUnsafe(evidenceId);
                return new(InboundVenueEvidenceApplyStatus.Duplicate);
            }
            var identityConflict = TryGetExistingEvidenceUnsafe(
                evidenceIdentity,
                evidenceId,
                out var duplicate);
            if (duplicate)
            {
                if (evt.PossibleResend)
                    PromotePossibleResendUnsafe(evidenceId);
                return new(InboundVenueEvidenceApplyStatus.Duplicate);
            }

            var direct = default(OutboundMutationId);
            var hasDirect = evt.ClOrdId != 0
                && _byClOrdId.TryGetValue(evt.ClOrdId, out direct);
            var original = default(OutboundMutationId);
            var hasOriginal = evt.OrigClOrdId != 0
                && _byClOrdId.TryGetValue(evt.OrigClOrdId, out original);
            if (!hasDirect
                && hasOriginal
                && evt.ClOrdId != evt.OrigClOrdId
                && _mutations.TryGetValue(original, out var originalMutation)
                && originalMutation.Kind == OutboundMutationKind.New)
            {
                // #643 owns cancel/replace coordinator correlation. Until then,
                // a new-order ledger row must not claim an ER whose business
                // ClOrdID belongs to an uncoordinated cancel/replace mutation.
                hasOriginal = false;
            }
            var id = hasDirect ? direct : hasOriginal ? original : default;
            if (identityConflict)
            {
                var matchedIds = MarkEvidenceIdentityConflictUnsafe(
                    evidenceIdentity,
                    id.Value == Guid.Empty ? null : id,
                    evt.ClOrdId,
                    evt.TimestampUtc);
                AddInboundEvidenceUnsafe(
                    CreateExecutionReportEvidence(
                        evt,
                        evidenceId,
                        InboundVenueEvidenceDisposition.Conflicting,
                        matchedIds),
                    evidenceIdentity);
                return new(InboundVenueEvidenceApplyStatus.RecordedConflicting);
            }
            if (id.Value == Guid.Empty || !_mutations.TryGetValue(id, out var mutation))
            {
                AddInboundEvidenceUnsafe(
                    CreateExecutionReportEvidence(
                        evt, evidenceId, InboundVenueEvidenceDisposition.Unmatched, []),
                    evidenceIdentity);
                return new(InboundVenueEvidenceApplyStatus.RecordedUnmatched);
            }
            if (evt.Synthetic)
            {
                if (hasDirect
                    && mutation.State == OutboundMutationState.RecordedPendingApproval
                    && evt.ClOrdId == mutation.PrimaryClOrdId)
                {
                    Terminalise(
                        mutation,
                        OutboundMutationState.OperatorResolved,
                        evt.TimestampUtc,
                        "OutboundProvenNoWrite",
                        DigestEvidence(
                            $"{mutation.MutationId}|{evt.ClOrdId}|{evt.RejectReason}"),
                        venueOrderId: null);
                    return new(InboundVenueEvidenceApplyStatus.RecordedMatched);
                }
                if (hasDirect
                    && IsLegacyState(mutation.State)
                    && evt.ClOrdId == mutation.PrimaryClOrdId)
                {
                    Terminalise(
                        mutation,
                        OutboundMutationState.OperatorResolved,
                        evt.TimestampUtc,
                        "LegacySyntheticTerminal",
                        DigestEvidence($"{evt.ClOrdId}|{evt.ExecKind}|{evt.RejectReason}"),
                        venueOrderId: null);
                    return new(InboundVenueEvidenceApplyStatus.RecordedUnmatched);
                }
                if (evt.OutboundProvenNoWrite
                    && evt.OutboundMutationId == mutation.MutationId
                    && hasDirect
                    && mutation.Kind == OutboundMutationKind.New
                    && evt.ClOrdId == mutation.PrimaryClOrdId
                    && ((mutation.State == OutboundMutationState.ApprovedToSend
                            && mutation.Attempts.Count == 0)
                        || (mutation.State == OutboundMutationState.ProvenUnsent
                            && mutation.Attempts.LastOrDefault()?.ProvenUnsentEvidence is not null)))
                {
                    Terminalise(
                        mutation,
                        OutboundMutationState.OperatorResolved,
                        evt.TimestampUtc,
                        "OutboundProvenNoWrite",
                        DigestEvidence(
                            $"{evt.OutboundMutationId}|{evt.ClOrdId}|{evt.RejectReason}"),
                        venueOrderId: null);
                    return new(InboundVenueEvidenceApplyStatus.RecordedMatched);
                }
                if (evt.OutboundProvenNoWrite)
                {
                    MarkConflictingVenueEvidence(mutation, evt.ClOrdId, evt.TimestampUtc);
                    return new(InboundVenueEvidenceApplyStatus.RecordedConflicting);
                }
                return new(InboundVenueEvidenceApplyStatus.RecordedUnmatched);
            }

            if (mutation.Approval is null)
            {
                if (hasDirect
                    && !string.IsNullOrWhiteSpace(mutation.FirmId)
                    && !string.IsNullOrWhiteSpace(evt.FirmId)
                    && !string.Equals(evt.FirmId, mutation.FirmId, StringComparison.Ordinal))
                {
                    MarkConflictingVenueEvidence(mutation, evt.ClOrdId, evt.TimestampUtc);
                    AddInboundEvidenceUnsafe(
                        CreateExecutionReportEvidence(
                            evt, evidenceId, InboundVenueEvidenceDisposition.Conflicting, [id]),
                        evidenceIdentity);
                    return new(InboundVenueEvidenceApplyStatus.RecordedConflicting);
                }
                AddInboundEvidenceUnsafe(
                    CreateExecutionReportEvidence(
                        evt, evidenceId, InboundVenueEvidenceDisposition.Unmatched, [id]),
                    evidenceIdentity);
                return new(InboundVenueEvidenceApplyStatus.RecordedUnmatched);
            }

            if (!hasDirect
                || (!string.IsNullOrWhiteSpace(evt.FirmId)
                    && !string.Equals(evt.FirmId, mutation.FirmId, StringComparison.Ordinal)))
            {
                MarkConflictingVenueEvidence(mutation, evt.ClOrdId, evt.TimestampUtc);
                AddInboundEvidenceUnsafe(
                    CreateExecutionReportEvidence(
                        evt, evidenceId, InboundVenueEvidenceDisposition.Conflicting, [id]),
                    evidenceIdentity);
                return new(InboundVenueEvidenceApplyStatus.RecordedConflicting);
            }

            if (mutation.Attempts.Count == 0)
            {
                if (HasCompleteExecutionReportIdentity(evt))
                {
                    MarkConflictingVenueEvidence(mutation, evt.ClOrdId, evt.TimestampUtc);
                    AddInboundEvidenceUnsafe(
                        CreateExecutionReportEvidence(
                            evt, evidenceId, InboundVenueEvidenceDisposition.Conflicting, [id]),
                        evidenceIdentity);
                    return new(InboundVenueEvidenceApplyStatus.RecordedConflicting);
                }

                MarkUnmatchedVenueEvidence(
                    mutation,
                    evt.ClOrdId,
                    evt.TimestampUtc,
                    OutboundAmbiguityReason.IncompleteVenueEvidence);
                AddInboundEvidenceUnsafe(
                    CreateExecutionReportEvidence(
                        evt, evidenceId, InboundVenueEvidenceDisposition.Unmatched, [id]),
                    evidenceIdentity);
                return new(InboundVenueEvidenceApplyStatus.RecordedUnmatched);
            }
            var activeAttemptIndex = mutation.Attempts.Count - 1;
            var activeAttempt = mutation.Attempts[activeAttemptIndex];
            var originalMismatch = mutation.OriginalClOrdId is { } expectedOriginal
                ? evt.OrigClOrdId != 0 && evt.OrigClOrdId != expectedOriginal
                : evt.OrigClOrdId != 0;
            var frame = activeAttempt.FramePrepared;
            var positiveIdentityMismatch =
                evt.ClOrdId != activeAttempt.ClOrdId
                || originalMismatch
                || (evt.SessionId is not null and not 0
                    && frame is not null
                    && evt.SessionId != frame.SessionId);
            if (positiveIdentityMismatch)
            {
                var reopenedReconciliation = IsTerminal(mutation.State);
                if (reopenedReconciliation)
                {
                    MarkTerminalEvidenceConflict(
                        mutation,
                        evt.TimestampUtc,
                        reopenReconciliation: true);
                }
                else
                {
                    MarkConflictingVenueEvidence(mutation, evt.ClOrdId, evt.TimestampUtc);
                }
                AddInboundEvidenceUnsafe(
                    CreateExecutionReportEvidence(
                        evt, evidenceId, InboundVenueEvidenceDisposition.Conflicting, [id]),
                    evidenceIdentity);
                return new(
                    InboundVenueEvidenceApplyStatus.RecordedConflicting,
                    ReopenedReconciliation: reopenedReconciliation);
            }

            if (mutation.State == OutboundMutationState.OperatorResolved
                && mutation.OperatorEvidence.LastOrDefault()?.Decision
                    == OutboundOperatorDecision.VenueAbsent
                && HasCompleteExecutionReportIdentity(evt))
            {
                MarkTerminalEvidenceConflict(
                    mutation,
                    evt.TimestampUtc,
                    reopenReconciliation: true);
                AddInboundEvidenceUnsafe(
                    CreateExecutionReportEvidence(
                        evt,
                        evidenceId,
                        InboundVenueEvidenceDisposition.Conflicting,
                        [id],
                        authoritativeTerminalContradiction: true),
                    evidenceIdentity);
                return new(
                    InboundVenueEvidenceApplyStatus.RecordedConflicting,
                    ReopenedReconciliation: true,
                    ApplyDomainDespiteConflict: true);
            }

            if (activeAttempt.ProvenUnsentEvidence is not null
                || mutation.Attempts.Any(a =>
                    a.AmbiguityReason
                    is OutboundAmbiguityReason.ConflictingVenueEvidence
                        or OutboundAmbiguityReason.NotAppliedEvidence))
            {
                MarkConflictingVenueEvidence(mutation, evt.ClOrdId, evt.TimestampUtc);
                AddInboundEvidenceUnsafe(
                    CreateExecutionReportEvidence(
                        evt, evidenceId, InboundVenueEvidenceDisposition.Conflicting, [id]),
                    evidenceIdentity);
                return new(InboundVenueEvidenceApplyStatus.RecordedConflicting);
            }

            if (!HasCompleteExecutionReportIdentity(evt))
            {
                if (!IsTerminal(mutation.State))
                    MarkUnmatchedVenueEvidence(
                        mutation,
                        evt.ClOrdId,
                        evt.TimestampUtc,
                        OutboundAmbiguityReason.IncompleteVenueEvidence);
                AddInboundEvidenceUnsafe(
                    CreateExecutionReportEvidence(
                        evt, evidenceId, InboundVenueEvidenceDisposition.Unmatched, [id]),
                    evidenceIdentity);
                return new(InboundVenueEvidenceApplyStatus.RecordedUnmatched);
            }

            if (evt.SessionVerId is not null and not 0
                && frame is not null
                && evt.SessionVerId != frame.SessionVerId)
            {
                // A post-roll ER cannot resolve the old outbound attempt, but
                // it can still be valid order-lifecycle evidence.
                if (!IsTerminal(mutation.State))
                    MarkUnmatchedVenueEvidence(
                        mutation,
                        evt.ClOrdId,
                        evt.TimestampUtc,
                        OutboundAmbiguityReason.SessionVersionMismatchEvidence);
                AddInboundEvidenceUnsafe(
                    CreateExecutionReportEvidence(
                        evt, evidenceId, InboundVenueEvidenceDisposition.Unmatched, [id]),
                    evidenceIdentity);
                return new(InboundVenueEvidenceApplyStatus.RecordedUnmatched);
            }

            if (IsTerminal(mutation.State))
            {
                if (string.Equals(
                        mutation.Resolution?.EvidenceKind,
                        "BusinessReject",
                        StringComparison.Ordinal))
                {
                    MarkTerminalEvidenceConflict(mutation, evt.TimestampUtc);
                    AddInboundEvidenceUnsafe(
                        CreateExecutionReportEvidence(
                            evt,
                            evidenceId,
                            InboundVenueEvidenceDisposition.Conflicting,
                            [id]),
                        evidenceIdentity);
                    return new(InboundVenueEvidenceApplyStatus.RecordedConflicting);
                }

                var terminalMatches = evt.ClOrdId == activeAttempt.ClOrdId
                    && frame is not null
                    && evt.SessionId == frame.SessionId
                    && evt.SessionVerId == frame.SessionVerId
                    && evt.InboundSeqNum is not null and not 0;
                AddInboundEvidenceUnsafe(
                    CreateExecutionReportEvidence(
                        evt,
                        evidenceId,
                        terminalMatches
                            ? InboundVenueEvidenceDisposition.Matched
                            : InboundVenueEvidenceDisposition.Conflicting,
                        [id]),
                    evidenceIdentity);
                if (!terminalMatches)
                    MarkTerminalEvidenceConflict(mutation, evt.TimestampUtc);
                return new(terminalMatches
                    ? InboundVenueEvidenceApplyStatus.RecordedMatched
                    : InboundVenueEvidenceApplyStatus.RecordedConflicting);
            }
            if (mutation.State is not OutboundMutationState.FramePrepared
                    and not OutboundMutationState.TransportWriteCompleted
                    and not OutboundMutationState.Ambiguous
                || frame is null
                || evt.SessionId != frame.SessionId
                || evt.SessionVerId != frame.SessionVerId
                || evt.InboundSeqNum is null or 0)
            {
                MarkConflictingVenueEvidence(mutation, evt.ClOrdId, evt.TimestampUtc);
                AddInboundEvidenceUnsafe(
                    CreateExecutionReportEvidence(
                        evt, evidenceId, InboundVenueEvidenceDisposition.Conflicting, [id]),
                    evidenceIdentity);
                return new(InboundVenueEvidenceApplyStatus.RecordedConflicting);
            }

            AddInboundEvidenceUnsafe(
                CreateExecutionReportEvidence(
                    evt, evidenceId, InboundVenueEvidenceDisposition.Matched, [id]),
                evidenceIdentity);
            var evidenceDigest = DigestEvidence(
                $"{evt.FirmId}|{evt.SessionId}|{evt.SessionVerId}|{evt.InboundSeqNum}|{evt.ClOrdId}|{evt.OrigClOrdId}|{evt.ExecKind}");
            Terminalise(
                mutation, OutboundMutationState.VenueAcknowledged,
                evt.TimestampUtc, "ExecutionReport", evidenceDigest, evt.VenueOrderId);
            return new(InboundVenueEvidenceApplyStatus.RecordedMatched);
        }
    }

    public InboundVenueEvidenceApplyResult ApplyBusinessReject(
        BusinessRejectReceivedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var evidenceId = BusinessRejectEvidenceId(evt);
        var evidenceIdentity = BusinessRejectEvidenceIdentity(evt, evidenceId);
        lock (_gate)
        {
            if (_inboundEvidence.ContainsKey(evidenceId))
            {
                if (evt.PossibleResend)
                    PromotePossibleResendUnsafe(evidenceId);
                return new(InboundVenueEvidenceApplyStatus.Duplicate);
            }
            var identityConflict = TryGetExistingEvidenceUnsafe(
                evidenceIdentity,
                evidenceId,
                out var duplicate);
            if (duplicate)
            {
                if (evt.PossibleResend)
                    PromotePossibleResendUnsafe(evidenceId);
                return new(InboundVenueEvidenceApplyStatus.Duplicate);
            }
            if (evt.SessionId is null or 0 || evt.SessionVerId is null or 0
                || string.IsNullOrWhiteSpace(evt.FirmId) || evt.RefSeqNum == 0)
            {
                AddInboundEvidenceUnsafe(
                    CreateBusinessRejectEvidence(
                        evt, evidenceId, InboundVenueEvidenceDisposition.Unmatched, []),
                    evidenceIdentity);
                return new(InboundVenueEvidenceApplyStatus.RecordedUnmatched);
            }
            var key = new FrameKey(evt.FirmId, evt.SessionId.Value, evt.SessionVerId.Value, evt.RefSeqNum);
            var id = default(OutboundMutationId);
            OutboundMutationSnapshot? mutation = null;
            var hasMutation = _byFrame.TryGetValue(key, out id)
                && _mutations.TryGetValue(id, out mutation);
            if (identityConflict)
            {
                AddInboundEvidenceUnsafe(
                    CreateBusinessRejectEvidence(
                        evt,
                        evidenceId,
                        InboundVenueEvidenceDisposition.Conflicting,
                        MarkEvidenceIdentityConflictUnsafe(
                            evidenceIdentity,
                            hasMutation ? id : null,
                            clOrdId: 0,
                            evt.TimestampUtc)),
                    evidenceIdentity);
                return new(InboundVenueEvidenceApplyStatus.RecordedConflicting);
            }
            if (!hasMutation || mutation is null)
            {
                AddInboundEvidenceUnsafe(
                    CreateBusinessRejectEvidence(
                        evt, evidenceId, InboundVenueEvidenceDisposition.Unmatched, []),
                    evidenceIdentity);
                return new(InboundVenueEvidenceApplyStatus.RecordedUnmatched);
            }
            var activeAttempt = mutation.Attempts.LastOrDefault();
            if (IsTerminal(mutation.State))
            {
                if (string.Equals(
                        mutation.Resolution?.EvidenceKind,
                        "ExecutionReport",
                        StringComparison.Ordinal))
                {
                    MarkTerminalEvidenceConflict(mutation, evt.TimestampUtc);
                    AddInboundEvidenceUnsafe(
                        CreateBusinessRejectEvidence(
                            evt,
                            evidenceId,
                            InboundVenueEvidenceDisposition.Conflicting,
                            [id]),
                        evidenceIdentity);
                    return new(InboundVenueEvidenceApplyStatus.RecordedConflicting);
                }

                var terminalMatches = activeAttempt?.FramePrepared is { } terminalFrame
                    && terminalFrame.SessionId == evt.SessionId.Value
                    && terminalFrame.SessionVerId == evt.SessionVerId.Value
                    && terminalFrame.OutboundSeqNum == evt.RefSeqNum;
                AddInboundEvidenceUnsafe(
                    CreateBusinessRejectEvidence(
                        evt,
                        evidenceId,
                        terminalMatches
                            ? InboundVenueEvidenceDisposition.Matched
                            : InboundVenueEvidenceDisposition.Conflicting,
                        [id]),
                    evidenceIdentity);
                if (!terminalMatches)
                    MarkTerminalEvidenceConflict(mutation, evt.TimestampUtc);
                return new(terminalMatches
                    ? InboundVenueEvidenceApplyStatus.RecordedMatched
                    : InboundVenueEvidenceApplyStatus.RecordedConflicting);
            }
            if (activeAttempt is null
                || activeAttempt.FramePrepared is not { } frame
                || frame.SessionId != evt.SessionId.Value
                || frame.SessionVerId != evt.SessionVerId.Value
                || frame.OutboundSeqNum != evt.RefSeqNum
                || mutation.Attempts.Any(a =>
                    a.AmbiguityReason
                    is OutboundAmbiguityReason.ConflictingVenueEvidence
                        or OutboundAmbiguityReason.NotAppliedEvidence)
                || mutation.State is not OutboundMutationState.FramePrepared
                    and not OutboundMutationState.TransportWriteCompleted
                    and not OutboundMutationState.Ambiguous)
            {
                MarkConflictingVenueEvidence(mutation, clOrdId: 0, evt.TimestampUtc);
                AddInboundEvidenceUnsafe(
                    CreateBusinessRejectEvidence(
                        evt, evidenceId, InboundVenueEvidenceDisposition.Conflicting, [id]),
                    evidenceIdentity);
                return new(InboundVenueEvidenceApplyStatus.RecordedConflicting);
            }
            AddInboundEvidenceUnsafe(
                CreateBusinessRejectEvidence(
                    evt, evidenceId, InboundVenueEvidenceDisposition.Matched, [id]),
                evidenceIdentity);
            var evidenceDigest = DigestEvidence(
                $"{evt.FirmId}|{evt.SessionId}|{evt.SessionVerId}|{evt.RefSeqNum}|{evt.SeqNum}|{evt.RejectReason}");
            Terminalise(
                mutation, OutboundMutationState.VenueAcknowledged,
                evt.TimestampUtc, "BusinessReject", evidenceDigest, venueOrderId: null);
            return new(InboundVenueEvidenceApplyStatus.RecordedMatched);
        }
    }

    public InboundVenueEvidenceApplyResult ApplyNotApplied(NotAppliedReceivedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var evidenceId = NotAppliedEvidenceId(evt);
        var evidenceIdentity = NotAppliedEvidenceIdentity(evt);
        lock (_gate)
        {
            if (_inboundEvidence.ContainsKey(evidenceId))
                return new(InboundVenueEvidenceApplyStatus.Duplicate);
            _ = TryGetExistingEvidenceUnsafe(
                evidenceIdentity,
                evidenceId,
                out var duplicate);
            if (duplicate)
                return new(InboundVenueEvidenceApplyStatus.Duplicate);

            var matched = _byFrame
                .Where(pair =>
                    string.Equals(pair.Key.FirmId, evt.FirmId, StringComparison.Ordinal)
                    && pair.Key.SessionId == evt.SessionId
                    && pair.Key.SessionVerId == evt.SessionVerId
                    && SequenceRangeContains(evt.FromSeqNo, evt.Count, pair.Key.OutboundSeqNum))
                .Select(pair => pair.Value)
                .Distinct()
                .OrderBy(id => id.Value)
                .ToArray();

            if (matched.Length == 0)
            {
                AddInboundEvidenceUnsafe(
                    CreateNotAppliedEvidence(
                        evt, evidenceId, InboundVenueEvidenceDisposition.Unmatched, []),
                    evidenceIdentity);
                return new(InboundVenueEvidenceApplyStatus.RecordedUnmatched);
            }

            var disposition = InboundVenueEvidenceDisposition.Matched;
            foreach (var id in matched)
            {
                if (!_mutations.TryGetValue(id, out var mutation))
                    continue;
                if (IsTerminal(mutation.State))
                {
                    MarkTerminalEvidenceConflict(mutation, evt.TimestampUtc);
                    disposition = InboundVenueEvidenceDisposition.Conflicting;
                    continue;
                }
                var attempt = mutation.Attempts.LastOrDefault();
                if (attempt?.FramePrepared is not { } frame
                    || !SequenceRangeContains(evt.FromSeqNo, evt.Count, frame.OutboundSeqNum)
                    || mutation.State is not OutboundMutationState.FramePrepared
                        and not OutboundMutationState.TransportWriteCompleted
                        and not OutboundMutationState.Ambiguous)
                {
                    MarkConflictingVenueEvidence(mutation, attempt?.ClOrdId ?? 0, evt.TimestampUtc);
                    disposition = InboundVenueEvidenceDisposition.Conflicting;
                    continue;
                }
                var index = mutation.Attempts.Count - 1;
                var updated = attempt with
                {
                    AmbiguityReason = OutboundAmbiguityReason.NotAppliedEvidence,
                };
                _mutations[id] = ReplaceAttempt(
                    mutation,
                    index,
                    updated,
                    OutboundMutationState.Ambiguous,
                    evt.TimestampUtc,
                    requiresReconciliation: true);
            }

            AddInboundEvidenceUnsafe(
                CreateNotAppliedEvidence(evt, evidenceId, disposition, matched),
                evidenceIdentity);
            return new(disposition == InboundVenueEvidenceDisposition.Conflicting
                ? InboundVenueEvidenceApplyStatus.RecordedConflicting
                : InboundVenueEvidenceApplyStatus.RecordedMatched);
        }
    }

    public void ImportLegacyNew(OrderSubmittedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var durableMutationId = evt.MutationId is { Value: var value } mutationId
            && value != Guid.Empty
                ? mutationId
                : (OutboundMutationId?)null;
        ImportLegacy(
            OutboundMutationKind.New, evt.FirmId, evt.ClOrdId, null,
            evt.TimestampUtc,
            durableMutationId is not null
                ? OutboundMutationState.RecordedPendingApproval
                : OutboundMutationState.LegacyUnknown,
            durableMutationId);
    }

    public void ImportLegacyCancel(
        OrderCancelRequestedEvent evt,
        string? authoritativeFirmId = null)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var firmId = ResolveLegacyFirm(
            evt.OriginalClOrdId, authoritativeFirmId);
        ImportLegacy(
            OutboundMutationKind.Cancel, firmId, evt.CancelClOrdId,
            evt.OriginalClOrdId, evt.TimestampUtc, OutboundMutationState.LegacyUnknownCancel,
            evt.MutationId);
    }

    public void ImportLegacyReplace(OrderReplaceRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ImportLegacy(
            OutboundMutationKind.Replace, evt.FirmId, evt.NewClOrdId,
            evt.OriginalClOrdId, evt.TimestampUtc, OutboundMutationState.LegacyUnknownReplace,
            evt.MutationId);
    }

    public void ImportLegacyProvenUnsent(
        ulong mutationClOrdId,
        OutboundMutationKind kind,
        ulong originalClOrdId,
        DateTimeOffset atUtc,
        OutboundProvenUnsentEvidence evidence)
    {
        lock (_gate)
        {
            var mutation = GetOrCreateLegacy(kind, string.Empty, mutationClOrdId, originalClOrdId, atUtc);
            mutation = AppendLegacyEvidence(
                mutation,
                $"ProvenUnsent:{evidence}",
                atUtc,
                $"{kind}|{mutationClOrdId}|{originalClOrdId}|{evidence}");
            if (IsTerminal(mutation.State))
            {
                _mutations[mutation.MutationId] = mutation;
                return;
            }
            if (mutation.State == OutboundMutationState.Ambiguous)
            {
                _mutations[mutation.MutationId] = mutation with
                {
                    RequiresReconciliation = true,
                };
                MarkCorrelations(mutation, terminal: false, atUtc);
                return;
            }
            if (mutation.State == OutboundMutationState.ProvenUnsent)
            {
                _mutations[mutation.MutationId] = mutation;
                RemoveActiveOriginalIndex(mutation);
                return;
            }
            mutation = mutation with
            {
                State = OutboundMutationState.ProvenUnsent,
                StateChangedAtUtc = atUtc,
                Resolution = new OutboundResolutionSnapshot
                {
                    State = OutboundMutationState.ProvenUnsent,
                    ResolvedAtUtc = atUtc,
                    EvidenceKind = evidence.ToString(),
                    EvidenceDigest = DigestEvidence($"{kind}|{mutationClOrdId}|{evidence}"),
                },
                RequiresReconciliation = false,
            };
            _mutations[mutation.MutationId] = mutation;
            MarkCorrelations(mutation, terminal: true, atUtc);
            RemoveActiveOriginalIndex(mutation);
        }
    }

    public void ImportLegacyAmbiguous(
        ulong mutationClOrdId,
        ulong originalClOrdId,
        DateTimeOffset atUtc)
    {
        lock (_gate)
        {
            var mutation = GetOrCreateLegacy(
                OutboundMutationKind.Replace, string.Empty,
                mutationClOrdId, originalClOrdId, atUtc);
            mutation = AppendLegacyEvidence(
                mutation,
                "ReplaceAmbiguous",
                atUtc,
                $"{OutboundMutationKind.Replace}|{mutationClOrdId}|{originalClOrdId}|ambiguous");
            if (IsTerminal(mutation.State))
            {
                _mutations[mutation.MutationId] = mutation;
                return;
            }
            if (mutation.State == OutboundMutationState.Ambiguous)
            {
                _mutations[mutation.MutationId] = mutation with
                {
                    RequiresReconciliation = true,
                };
                return;
            }
            mutation = mutation with
            {
                State = OutboundMutationState.Ambiguous,
                StateChangedAtUtc = atUtc,
                Resolution = null,
                RequiresReconciliation = true,
            };
            _mutations[mutation.MutationId] = mutation;
            MarkCorrelations(mutation, terminal: false, atUtc);
        }
    }

    public void ImportReconciliationMarker(ReconciliationMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        switch (marker.Kind)
        {
            case ReconciliationMarkerKind.CancelPreSend:
                ImportLegacyProvenUnsent(
                    marker.MutationClOrdId, OutboundMutationKind.Cancel,
                    marker.OriginalClOrdId, marker.AmbiguousAtUtc ?? DateTimeOffset.UnixEpoch,
                    OutboundProvenUnsentEvidence.LegacyWave1CancelPreSend);
                break;
            case ReconciliationMarkerKind.ReplacePreSend:
                ImportLegacyProvenUnsent(
                    marker.MutationClOrdId, OutboundMutationKind.Replace,
                    marker.OriginalClOrdId, marker.AmbiguousAtUtc ?? DateTimeOffset.UnixEpoch,
                    OutboundProvenUnsentEvidence.LegacyWave1ReplacePreSend);
                break;
            case ReconciliationMarkerKind.ReplaceAmbiguous:
                ImportLegacyAmbiguous(
                    marker.MutationClOrdId, marker.OriginalClOrdId,
                    marker.AmbiguousAtUtc ?? DateTimeOffset.UnixEpoch);
                break;
        }
    }

    public int ClassifyRecoveredAttempts(ProcessEpochId activeEpoch, DateTimeOffset atUtc)
    {
        var classifications = PlanRecoveredAttempts(activeEpoch);
        if (activeEpoch.Value == Guid.Empty)
            throw new ArgumentException("Process epoch is required.", nameof(activeEpoch));
        lock (_gate)
        {
            var changed = 0;
            foreach (var classification in classifications)
            {
                if (!_mutations.TryGetValue(classification.MutationId, out var mutation)
                    || mutation.Attempts.Count == 0
                    || IsTerminal(mutation.State))
                    continue;
                var attempt = mutation.Attempts[^1];
                if (attempt.AttemptId != classification.AttemptId)
                    continue;
                if (classification.Disposition == RecoveredOutboundAttemptDisposition.ProvenUnsent)
                {
                    var updated = attempt with
                    {
                        ProvenUnsentEvidence = classification.ProvenUnsentEvidence,
                    };
                    var updatedMutation = ReplaceAttempt(
                        mutation, mutation.Attempts.Count - 1, updated,
                        OutboundMutationState.ProvenUnsent, atUtc);
                    _mutations[classification.MutationId] = updatedMutation;
                    AddClOrdCorrelation(
                        updatedMutation, attempt.ClOrdId, terminal: true, atUtc);
                    RemoveActiveOriginalIndex(updatedMutation);
                }
                else
                {
                    var updated = attempt with
                    {
                        AmbiguityReason = classification.AmbiguityReason,
                    };
                    _mutations[classification.MutationId] = ReplaceAttempt(
                        mutation, mutation.Attempts.Count - 1, updated,
                        OutboundMutationState.Ambiguous, atUtc,
                        requiresReconciliation: true);
                }
                changed++;
            }
            return changed;
        }
    }

    public IReadOnlyList<RecoveredOutboundAttemptClassification> PlanRecoveredAttempts(
        ProcessEpochId activeEpoch)
    {
        if (activeEpoch.Value == Guid.Empty)
            throw new ArgumentException("Process epoch is required.", nameof(activeEpoch));
        lock (_gate)
        {
            var result = new List<RecoveredOutboundAttemptClassification>();
            foreach (var mutation in _mutations.Values)
            {
                if (mutation.Attempts.Count == 0 || IsTerminal(mutation.State))
                    continue;
                var attempt = mutation.Attempts[^1];
                if (attempt.ProcessEpochId == activeEpoch
                    || attempt.ProvenUnsentEvidence is not null
                    || attempt.AmbiguityReason is not null)
                {
                    continue;
                }
                if (attempt.FramePrepared is null)
                {
                    result.Add(new RecoveredOutboundAttemptClassification(
                        mutation.MutationId,
                        attempt.AttemptId,
                        mutation.FirmId,
                        RecoveredOutboundAttemptDisposition.ProvenUnsent,
                        OutboundProvenUnsentEvidence.DeadEpochIntentWithoutFrame,
                        null));
                    continue;
                }
                result.Add(new RecoveredOutboundAttemptClassification(
                    mutation.MutationId,
                    attempt.AttemptId,
                    mutation.FirmId,
                    RecoveredOutboundAttemptDisposition.Ambiguous,
                    null,
                    attempt.TransportWriteCompletedAtUtc is null
                        ? OutboundAmbiguityReason.DeadEpochFramePrepared
                        : OutboundAmbiguityReason.DeadEpochTransportWriteCompleted));
            }
            return result;
        }
    }

    public void MarkAmbiguous(
        OutboundMutationId mutationId,
        OutboundAttemptId attemptId,
        OutboundAmbiguityReason reason,
        DateTimeOffset atUtc)
    {
        lock (_gate)
        {
            var mutation = RequiredMutation(mutationId);
            var (attempt, index) = RequiredAttempt(mutation, attemptId);
            if (attempt.FramePrepared is null)
                throw TransitionError("Ambiguity requires committed frame evidence.");
            if (attempt.AmbiguityReason is { } existing)
            {
                if (existing == reason)
                    return;
                throw TransitionError("Conflicting ambiguity classification.");
            }
            _mutations[mutationId] = ReplaceAttempt(
                mutation,
                index,
                attempt with { AmbiguityReason = reason },
                OutboundMutationState.Ambiguous,
                atUtc,
                requiresReconciliation: true);
        }
    }

    public IReadOnlyList<OutboundMutationSnapshot> GetMutations(
        OutboundMutationKind kind,
        OutboundMutationState state)
    {
        lock (_gate)
            return _mutations.Values
                .Where(m => m.Kind == kind && m.State == state)
                .OrderBy(m => m.RecordedAtUtc)
                .ToArray();
    }

    public int ReconcileLegacyPendingState(
        IEnumerable<ulong> pendingNewClOrdIds,
        IEnumerable<ulong> pendingCancelClOrdIds,
        IEnumerable<ulong> pendingReplaceClOrdIds,
        DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(pendingNewClOrdIds);
        ArgumentNullException.ThrowIfNull(pendingCancelClOrdIds);
        ArgumentNullException.ThrowIfNull(pendingReplaceClOrdIds);
        var pendingNew = pendingNewClOrdIds.ToHashSet();
        var pendingCancel = pendingCancelClOrdIds.ToHashSet();
        var pendingReplace = pendingReplaceClOrdIds.ToHashSet();
        lock (_gate)
        {
            var changed = 0;
            foreach (var pair in _mutations.ToArray())
            {
                var mutation = pair.Value;
                var remainsPending = mutation.State switch
                {
                    OutboundMutationState.LegacyUnknown =>
                        pendingNew.Contains(mutation.PrimaryClOrdId),
                    OutboundMutationState.LegacyUnknownCancel =>
                        pendingCancel.Contains(mutation.PrimaryClOrdId),
                    OutboundMutationState.LegacyUnknownReplace =>
                        pendingReplace.Contains(mutation.PrimaryClOrdId),
                    _ => true,
                };
                if (remainsPending || !IsLegacyState(mutation.State))
                    continue;
                Terminalise(
                    mutation,
                    OutboundMutationState.LegacyTerminal,
                    atUtc,
                    "LegacyDomainTerminal",
                    DigestEvidence(
                        $"{mutation.Kind}|{mutation.PrimaryClOrdId}|legacy-domain-terminal"),
                    venueOrderId: null);
                changed++;
            }
            return changed;
        }
    }

    public int PurgeTerminalCorrelations(
        DateTimeOffset now,
        TimeSpan? retention = null)
    {
        var keep = retention ?? DefaultTerminalCorrelationRetention;
        if (keep < DefaultTerminalCorrelationRetention)
            throw new ArgumentOutOfRangeException(nameof(retention));
        var cutoff = now - keep;
        lock (_gate)
        {
            var purgeIds = _mutations.Values
                .Where(m => IsTerminal(m.State)
                    && !m.RequiresReconciliation
                    && m.OperatorEvidence.Count == 0
                    && m.ResolutionProposals.Count == 0
                    && m.AuthoritativeEvidence.Count == 0
                    && m.Resolution is { ResolvedAtUtc: var resolved }
                    && resolved <= cutoff)
                .Select(m => m.MutationId)
                .ToArray();
            foreach (var id in purgeIds)
            {
                var mutation = _mutations[id];
                RemoveMutationIndexes(mutation);
                _mutations.Remove(id);
            }
            if (purgeIds.Length > 0)
            {
                var purged = purgeIds.ToHashSet();
                foreach (var pair in _inboundEvidence.ToArray())
                {
                    var remaining = pair.Value.MatchedMutationIds
                        .Where(id => !purged.Contains(id))
                        .ToArray();
                    if (remaining.Length == 0
                        && pair.Value.MatchedMutationIds.Count > 0)
                    {
                        _inboundEvidence.Remove(pair.Key);
                        var identity = EvidenceIdentity(pair.Value);
                        if (_inboundEvidenceIdentity.TryGetValue(identity, out var indexed)
                            && indexed == pair.Key)
                        {
                            _inboundEvidenceIdentity.Remove(identity);
                        }
                    }
                    else if (remaining.Length != pair.Value.MatchedMutationIds.Count)
                    {
                        _inboundEvidence[pair.Key] = pair.Value with
                        {
                            MatchedMutationIds = remaining,
                        };
                    }
                }
            }
            foreach (var correlation in _correlations.Values
                         .Where(c => c.Terminal
                             && c.RetainFromUtc <= cutoff
                             && !_mutations.ContainsKey(c.MutationId))
                         .Select(c => c.ClOrdId)
                         .ToArray())
                _correlations.Remove(correlation);
            return purgeIds.Length;
        }
    }

    public IReadOnlyList<OutboundMutationSnapshot> SnapshotMutations()
    {
        lock (_gate)
            return _mutations.Values
                .OrderBy(m => m.MutationId.Value)
                .Select(Clone)
                .ToArray();
    }

    public IReadOnlyList<OutboundCorrelationTombstone> SnapshotCorrelations()
    {
        lock (_gate)
            return _correlations.Values
                .OrderBy(c => c.ClOrdId)
                .ToArray();
    }

    public (
        IReadOnlyList<OutboundMutationSnapshot> Mutations,
        IReadOnlyList<OutboundCorrelationTombstone> Correlations,
        IReadOnlyList<InboundVenueEvidenceSnapshot> InboundEvidence) CaptureSnapshot()
    {
        lock (_gate)
        {
            return (
                _mutations.Values
                    .OrderBy(m => m.MutationId.Value)
                    .Select(Clone)
                    .ToArray(),
                _correlations.Values
                    .OrderBy(c => c.ClOrdId)
                    .ToArray(),
                _inboundEvidence.Values
                    .OrderBy(e => e.ObservedAtUtc)
                    .ThenBy(e => e.EvidenceId, StringComparer.Ordinal)
                    .Select(CloneEvidence)
                    .ToArray());
        }
    }

    public IReadOnlyList<OutboundMutationDiagnostic> GetDiagnostics()
    {
        lock (_gate)
            return _mutations.Values
                .OrderBy(m => m.MutationId.Value)
                .Select(m => new OutboundMutationDiagnostic(
                    m.MutationId,
                    m.FirmId,
                    m.Kind,
                    m.State,
                    m.StateChangedAtUtc,
                    m.Attempts.Count,
                    m.RequiresReconciliation,
                    m.Approval?.SensitiveCommandEnvelope.KeyId,
                    m.Approval?.SensitiveCommandEnvelope.KeyVersion,
                    m.BotBusinessIdentity?.CredentialId,
                    m.BotBusinessIdentity?.ExternalClOrdId))
                .ToArray();
    }

    public IReadOnlyList<OutboundMutationMetricDimensions> GetMetricDimensions()
    {
        lock (_gate)
            return _mutations.Values
                .Select(m => new OutboundMutationMetricDimensions(
                    m.FirmId, m.Kind, m.State, m.Origin))
                .Distinct()
                .OrderBy(m => m.FirmId, StringComparer.Ordinal)
                .ThenBy(m => m.Kind)
                .ThenBy(m => m.State)
                .ThenBy(m => m.Origin)
                .ToArray();
    }

    public IReadOnlyList<InboundVenueEvidenceDiagnostic> GetInboundEvidenceDiagnostics(
        int limit = 100)
    {
        if (limit is < 1 or > MaxEvidenceDiagnostics)
            throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate)
        {
            return _inboundEvidence.Values
                .OrderByDescending(e => e.ObservedAtUtc)
                .ThenBy(e => e.EvidenceId, StringComparer.Ordinal)
                .Take(limit)
                .Select(e => new InboundVenueEvidenceDiagnostic(
                    e.EvidenceId,
                    e.Kind,
                    e.Disposition,
                    e.FirmId,
                    e.SessionId,
                    e.SessionVerId,
                    e.InboundSeqNum,
                    e.BusinessRejectRefSeqNum,
                    e.NotAppliedFromSeqNo,
                    e.NotAppliedCount,
                    e.ObservedAtUtc,
                    e.MatchedMutationIds.Count))
                .ToArray();
        }
    }

    public IReadOnlyList<InboundVenueEvidenceSnapshot> GetInboundEvidenceForMutation(
        OutboundMutationId mutationId)
    {
        lock (_gate)
            return _inboundEvidence.Values
                .Where(evidence => evidence.MatchedMutationIds.Contains(mutationId))
                .OrderBy(evidence => evidence.ObservedAtUtc)
                .ThenBy(evidence => evidence.EvidenceId, StringComparer.Ordinal)
                .Select(CloneEvidence)
                .ToArray();
    }

    public bool HasAuthoritativeEvidence(
        OutboundMutationId mutationId,
        OutboundOperatorEvidenceType evidenceType,
        string evidenceReference)
    {
        lock (_gate)
        {
            if (!_mutations.TryGetValue(mutationId, out var mutation))
                return false;
            return evidenceType switch
            {
                OutboundOperatorEvidenceType.TerminalExecutionReport =>
                    HasAuthoritativeTerminalExecutionReportUnsafe(
                        mutation,
                        evidenceReference,
                        out _),
                OutboundOperatorEvidenceType.ContractedNotApplied =>
                    _inboundEvidence.TryGetValue(evidenceReference, out var notApplied)
                    && notApplied.Kind == InboundVenueEvidenceKind.NotApplied
                    && notApplied.Disposition == InboundVenueEvidenceDisposition.Matched
                    && notApplied.MatchedMutationIds.Contains(mutationId),
                OutboundOperatorEvidenceType.VenueMassAction =>
                    HasRegisteredAuthoritativeEvidence(
                        mutation,
                        OutboundAuthoritativeEvidenceSourceType.VenueMassAction,
                        evidenceReference),
                OutboundOperatorEvidenceType.OfficialExtract =>
                    HasRegisteredAuthoritativeEvidence(
                        mutation,
                        OutboundAuthoritativeEvidenceSourceType.OfficialExtract,
                        evidenceReference),
                OutboundOperatorEvidenceType.ManualAnnotation => true,
                _ => false,
            };
        }
    }

    public bool IsTerminalExecutionReportDecisionCompatible(
        OutboundMutationId mutationId,
        string evidenceReference,
        OutboundOperatorDecision decision)
    {
        lock (_gate)
        {
            if (!_mutations.TryGetValue(mutationId, out var mutation)
                || !HasAuthoritativeTerminalExecutionReportUnsafe(
                    mutation,
                    evidenceReference,
                    out var evidence))
                return false;
            return decision != OutboundOperatorDecision.VenueAbsent
                || !IsVenueAcknowledgmentOnlyExecutionReportKind(evidence.MessageKind);
        }
    }

    private bool HasAuthoritativeTerminalExecutionReportUnsafe(
        OutboundMutationSnapshot mutation,
        string evidenceReference,
        out InboundVenueEvidenceSnapshot evidence)
    {
        if (_inboundEvidence.TryGetValue(evidenceReference, out var found)
            && found.Kind == InboundVenueEvidenceKind.ExecutionReport
            && (found.Disposition == InboundVenueEvidenceDisposition.Matched
                || (found.Disposition == InboundVenueEvidenceDisposition.Conflicting
                    && found.AuthoritativeTerminalContradiction))
            && found.MatchedMutationIds.Contains(mutation.MutationId)
            && string.Equals(found.FirmId, mutation.FirmId, StringComparison.Ordinal)
            && found.SessionId is > 0
            && found.SessionVerId is > 0
            && found.InboundSeqNum is > 0
            && found.SendingTime is not null
            && IsTerminalExecutionReportKind(found.MessageKind))
        {
            evidence = found;
            return true;
        }
        evidence = null!;
        return false;
    }

    public IReadOnlyList<OutboundReconciliationMetricSnapshot> GetReconciliationMetrics(
        DateTimeOffset now)
    {
        lock (_gate)
        {
            return _mutations.Values
                .Where(mutation => mutation.RequiresReconciliation)
                .GroupBy(mutation =>
                {
                    var reason = ResolveAmbiguityReason(mutation);
                    var age = Math.Max(0d, (now - mutation.StateChangedAtUtc).TotalSeconds);
                    return new
                    {
                        mutation.FirmId,
                        mutation.Kind,
                        mutation.State,
                        Reason = reason,
                        AgeBucket = AgeBucket(age),
                    };
                })
                .Select(group => new OutboundReconciliationMetricSnapshot(
                    group.Key.FirmId,
                    group.Key.Kind,
                    group.Key.State,
                    group.Key.Reason,
                    group.Key.AgeBucket,
                    group.LongCount(),
                    group.Max(mutation =>
                        Math.Max(0d, (now - mutation.StateChangedAtUtc).TotalSeconds))))
                .OrderBy(snapshot => snapshot.FirmId, StringComparer.Ordinal)
                .ThenBy(snapshot => snapshot.Kind)
                .ThenBy(snapshot => snapshot.State)
                .ThenBy(snapshot => snapshot.AmbiguityReason, StringComparer.Ordinal)
                .ThenBy(snapshot => snapshot.AgeBucket, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public OutboundReconciliationHealthSnapshot GetReconciliationHealth(DateTimeOffset now)
    {
        lock (_gate)
        {
            var unresolved = _mutations.Values
                .Where(mutation => mutation.RequiresReconciliation)
                .ToArray();
            return new OutboundReconciliationHealthSnapshot(
                unresolved.Length,
                unresolved.Select(mutation => mutation.FirmId)
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                OldestAge(unresolved.Where(mutation =>
                    mutation.State == OutboundMutationState.Ambiguous), now),
                OldestAge(unresolved.Where(mutation =>
                    IsLegacyState(mutation.State)), now));
        }
    }

    public void Restore(
        IEnumerable<OutboundMutationSnapshot> mutations,
        IEnumerable<OutboundCorrelationTombstone> correlations,
        bool legacyMigrationCompleted = false)
        => Restore(
            mutations,
            correlations,
            Array.Empty<InboundVenueEvidenceSnapshot>(),
            legacyMigrationCompleted);

    public void Restore(
        IEnumerable<OutboundMutationSnapshot> mutations,
        IEnumerable<OutboundCorrelationTombstone> correlations,
        IEnumerable<InboundVenueEvidenceSnapshot> inboundEvidence,
        bool legacyMigrationCompleted = false)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(correlations);
        ArgumentNullException.ThrowIfNull(inboundEvidence);
        lock (_gate)
        {
            _mutations.Clear();
            _byClOrdId.Clear();
            _activeByOriginal.Clear();
            _byAlgoOrigin.Clear();
            _byFrame.Clear();
            _correlations.Clear();
            _inboundEvidence.Clear();
            _inboundEvidenceIdentity.Clear();
            _unmatchedEvidenceOrder.Clear();
            _legacyMigrationCompleted = legacyMigrationCompleted;
            foreach (var source in mutations.OrderBy(m => m.MutationId.Value))
            {
                var mutation = Clone(source);
                if (mutation.MutationId.Value == Guid.Empty || mutation.PrimaryClOrdId == 0)
                    throw new OutboundLedgerRecoveryException("Outbound ledger snapshot identity is invalid.");
                if (mutation.Approval is { } approval)
                {
                    if (!AeadOutboundCommandProtector.IntegrityMatches(approval))
                        throw new OutboundLedgerRecoveryException("Outbound ledger command integrity validation failed.");
                    var snapshotDerivedReconciliation =
                        mutation.SensitivePayloadAvailability
                            != OutboundSensitivePayloadAvailability.Available
                        || StateRequiresReconciliation(mutation.State)
                        || mutation.Attempts.Any(a =>
                            a.AmbiguityReason
                            == OutboundAmbiguityReason.ConflictingVenueEvidence);
                    var explicitlyRequiresReconciliation =
                        mutation.ExplicitlyRequiresReconciliation
                        || (mutation.RequiresReconciliation
                            && !snapshotDerivedReconciliation);
                    var availability = CheckPayloadAvailability(mutation.MutationId, mutation.FirmId, approval);
                    mutation = mutation with
                    {
                        SensitivePayloadAvailability = availability,
                        RequiresReconciliation =
                            explicitlyRequiresReconciliation
                            || availability != OutboundSensitivePayloadAvailability.Available
                            || StateRequiresReconciliation(mutation.State)
                            || mutation.Attempts.Any(a =>
                                a.AmbiguityReason
                                == OutboundAmbiguityReason.ConflictingVenueEvidence)
                            || mutation.ResolutionProposals.Any(
                                proposal => proposal.ApprovedAtUtc is null),
                        ExplicitlyRequiresReconciliation =
                            explicitlyRequiresReconciliation,
                    };
                }
                if (!_mutations.TryAdd(mutation.MutationId, mutation))
                    throw new OutboundLedgerRecoveryException("Duplicate outbound mutation identity.");
                AddMutationIndexes(mutation);
            }
            foreach (var correlation in correlations)
            {
                if (_correlations.TryGetValue(correlation.ClOrdId, out var existing)
                    && (existing.MutationId != correlation.MutationId
                        || existing.Kind != correlation.Kind))
                    throw new OutboundLedgerRecoveryException("Conflicting outbound correlation tombstone.");
                _correlations[correlation.ClOrdId] = correlation;
            }
            foreach (var evidence in inboundEvidence
                         .OrderBy(e => e.ObservedAtUtc)
                         .ThenBy(e => e.EvidenceId, StringComparer.Ordinal))
            {
                if (!IsLowerHex(evidence.EvidenceId, 64)
                    || string.IsNullOrWhiteSpace(evidence.FirmId))
                {
                    throw new OutboundLedgerRecoveryException(
                        "Inbound venue evidence identity is invalid.");
                }
                AddInboundEvidenceUnsafe(
                    CloneEvidence(evidence),
                    EvidenceIdentity(evidence));
            }
        }
    }

    public bool TryGet(OutboundMutationId mutationId, out OutboundMutationSnapshot? mutation)
    {
        lock (_gate)
        {
            if (_mutations.TryGetValue(mutationId, out var found))
            {
                mutation = Clone(found);
                return true;
            }
            mutation = null;
            return false;
        }
    }

    public bool TryGetByClOrdId(ulong clOrdId, out OutboundMutationSnapshot? mutation)
    {
        if (clOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(clOrdId));
        lock (_gate)
        {
            if (_byClOrdId.TryGetValue(clOrdId, out var mutationId)
                && _mutations.TryGetValue(mutationId, out var found))
            {
                mutation = Clone(found);
                return true;
            }
            mutation = null;
            return false;
        }
    }

    public bool TryGetByAlgoOrigin(
        string firmId,
        AlgoOutboundOriginIdentity origin,
        out OutboundMutationSnapshot? mutation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        ArgumentNullException.ThrowIfNull(origin);
        lock (_gate)
        {
            if (_byAlgoOrigin.TryGetValue(
                    new FirmAlgoOriginKey(firmId, origin),
                    out var mutationId)
                && _mutations.TryGetValue(mutationId, out var found))
            {
                mutation = Clone(found);
                return true;
            }
            mutation = null;
            return false;
        }
    }

    public IReadOnlyList<OutboundMutationSnapshot> GetAlgoMutations(
        string firmId,
        ulong parentAlgoId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        if (parentAlgoId == 0)
            throw new ArgumentOutOfRangeException(nameof(parentAlgoId));
        lock (_gate)
            return _mutations.Values
                .Where(m =>
                    string.Equals(m.FirmId, firmId, StringComparison.Ordinal)
                    && m.AlgoOriginIdentity?.ParentAlgoId == parentAlgoId)
                .OrderBy(m => m.RecordedAtUtc)
                .ThenBy(m => m.MutationId.Value)
                .Select(Clone)
                .ToArray();
    }

    public bool HasBlockingAlgoMutation(string firmId, ulong parentAlgoId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        if (parentAlgoId == 0)
            throw new ArgumentOutOfRangeException(nameof(parentAlgoId));
        lock (_gate)
            return _mutations.Values.Any(m =>
                string.Equals(m.FirmId, firmId, StringComparison.Ordinal)
                && m.AlgoOriginIdentity?.ParentAlgoId == parentAlgoId
                && (m.RequiresReconciliation || IsAlgoActionBlocking(m.State)));
    }

    public bool HasBlockingAlgoMutationExcept(
        string firmId,
        ulong parentAlgoId,
        OutboundMutationId excludedMutationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        if (parentAlgoId == 0)
            throw new ArgumentOutOfRangeException(nameof(parentAlgoId));
        lock (_gate)
            return _mutations.Values.Any(m =>
                m.MutationId != excludedMutationId
                && string.Equals(m.FirmId, firmId, StringComparison.Ordinal)
                && m.AlgoOriginIdentity?.ParentAlgoId == parentAlgoId
                && (m.RequiresReconciliation || IsAlgoActionBlocking(m.State)));
    }

    public bool TryGetActiveForOriginal(
        string firmId,
        ulong originalClOrdId,
        out OutboundMutationSnapshot? mutation)
    {
        if (string.IsNullOrWhiteSpace(firmId))
            throw new ArgumentException("Firm id is required.", nameof(firmId));
        if (originalClOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(originalClOrdId));
        lock (_gate)
        {
            if (_activeByOriginal.TryGetValue(
                    new OriginalOrderKey(firmId, originalClOrdId),
                    out var mutationId)
                && _mutations.TryGetValue(mutationId, out var found))
            {
                mutation = Clone(found);
                return true;
            }
            mutation = null;
            return false;
        }
    }

    public bool TryResolveWatermarkOwner(
        OutboundMutationId mutationId,
        out EndClientId? owner)
    {
        lock (_gate)
        {
            owner = null;
            if (_protector is null
                || !_mutations.TryGetValue(mutationId, out var mutation)
                || mutation.Approval is not { } approval)
                return false;
            try
            {
                var sensitive = _protector.Decrypt(
                    mutation.MutationId,
                    mutation.FirmId,
                    approval.CanonicalCommandNonSensitive,
                    approval.SensitiveFieldRefs,
                    approval.SensitiveCommandEnvelope);
                owner = new EndClientId(sensitive.EndClientId);
                return true;
            }
            catch (OutboundCommandEnvelopeException)
            {
                return false;
            }
        }
    }

    private void ImportLegacy(
        OutboundMutationKind kind,
        string firmId,
        ulong mutationClOrdId,
        ulong? originalClOrdId,
        DateTimeOffset atUtc,
        OutboundMutationState state,
        OutboundMutationId? preferredMutationId = null)
    {
        if (mutationClOrdId == 0)
            return;
        lock (_gate)
        {
            if (_byClOrdId.ContainsKey(mutationClOrdId))
                return;
            var mutation = GetOrCreateLegacy(
                kind, firmId, mutationClOrdId, originalClOrdId, atUtc, preferredMutationId);
            _mutations[mutation.MutationId] = mutation with
            {
                State = state,
                StateChangedAtUtc = atUtc,
                RequiresReconciliation = true,
            };
        }
    }

    private OutboundMutationSnapshot GetOrCreateLegacy(
        OutboundMutationKind kind,
        string firmId,
        ulong mutationClOrdId,
        ulong? originalClOrdId,
        DateTimeOffset atUtc,
        OutboundMutationId? preferredMutationId = null)
    {
        if (_byClOrdId.TryGetValue(mutationClOrdId, out var existingId))
            return _mutations[existingId];
        var id = preferredMutationId is { } preferred
            && preferred.Value != Guid.Empty
                ? preferred
                : DeterministicLegacyId(kind, firmId, mutationClOrdId);
        if (_mutations.TryGetValue(id, out var existing))
        {
            AddActiveOriginalIndex(existing);
            return existing;
        }
        var state = kind switch
        {
            OutboundMutationKind.New => OutboundMutationState.LegacyUnknown,
            OutboundMutationKind.Cancel => OutboundMutationState.LegacyUnknownCancel,
            _ => OutboundMutationState.LegacyUnknownReplace,
        };
        var mutation = new OutboundMutationSnapshot
        {
            MutationId = id,
            Kind = kind,
            FirmId = firmId,
            EndClientRef = $"legacy-clordid-{mutationClOrdId}",
            Origin = OutboundMutationOrigin.Legacy,
            PrimaryClOrdId = mutationClOrdId,
            OriginalClOrdId = originalClOrdId,
            RecordedAtUtc = atUtc,
            State = state,
            StateChangedAtUtc = atUtc,
            RequiresReconciliation = true,
        };
        _mutations[id] = mutation;
        AddClOrdCorrelation(mutation, mutationClOrdId, terminal: false, atUtc);
        AddActiveOriginalIndex(mutation);
        return mutation;
    }

    private string ResolveLegacyFirm(
        ulong originalClOrdId,
        string? authoritativeFirmId)
    {
        if (!string.IsNullOrWhiteSpace(authoritativeFirmId))
            return authoritativeFirmId;
        lock (_gate)
        {
            if (_byClOrdId.TryGetValue(originalClOrdId, out var mutationId)
                && _mutations.TryGetValue(mutationId, out var original)
                && !string.IsNullOrWhiteSpace(original.FirmId))
                return original.FirmId;
        }
        return string.Empty;
    }

    private OutboundSensitivePayloadAvailability CheckPayloadAvailability(
        OutboundApprovedEvent evt) =>
        CheckPayloadAvailability(evt.MutationId, evt.FirmId, evt.Approval);

    private OutboundSensitivePayloadAvailability CheckPayloadAvailability(
        OutboundMutationId mutationId,
        string firmId,
        OutboundApprovalSnapshot approval)
    {
        if (approval.CanonicalCommandNonSensitive.Version != OutboundCanonicalCommand.CurrentVersion)
            return OutboundSensitivePayloadAvailability.UnsupportedVersion;
        if (_protector is null)
            return OutboundSensitivePayloadAvailability.MissingHistoricalKey;
        try
        {
            _ = _protector.Decrypt(
                mutationId,
                firmId,
                approval.CanonicalCommandNonSensitive,
                approval.SensitiveFieldRefs,
                approval.SensitiveCommandEnvelope);
            return OutboundSensitivePayloadAvailability.Available;
        }
        catch (OutboundCommandEnvelopeException ex)
        {
            return ex.Availability;
        }
    }

    private bool TryGetExistingEvidenceUnsafe(
        string identity,
        string evidenceId,
        out bool duplicate)
    {
        duplicate = false;
        if (!_inboundEvidenceIdentity.TryGetValue(identity, out var existingId))
            return false;
        duplicate = string.Equals(existingId, evidenceId, StringComparison.Ordinal);
        return !duplicate;
    }

    private OutboundMutationId[] MarkEvidenceIdentityConflictUnsafe(
        string identity,
        OutboundMutationId? newlyReferencedMutationId,
        ulong clOrdId,
        DateTimeOffset atUtc)
    {
        if (!_inboundEvidenceIdentity.TryGetValue(identity, out var existingEvidenceId)
            || !_inboundEvidence.TryGetValue(existingEvidenceId, out var existingEvidence))
        {
            throw TransitionError("Inbound evidence identity index is inconsistent.");
        }

        var matchedIds = existingEvidence.MatchedMutationIds
            .Append(newlyReferencedMutationId ?? default)
            .Where(id => id.Value != Guid.Empty)
            .Distinct()
            .OrderBy(id => id.Value)
            .ToArray();
        foreach (var matchedId in matchedIds)
        {
            if (!_mutations.TryGetValue(matchedId, out var mutation))
                continue;
            if (IsTerminal(mutation.State))
            {
                MarkTerminalEvidenceConflict(mutation, atUtc);
                continue;
            }

            var mutationClOrdId = newlyReferencedMutationId is { } newId
                && matchedId == newId
                && clOrdId != 0
                    ? clOrdId
                    : mutation.Attempts.LastOrDefault()?.ClOrdId
                        ?? mutation.PrimaryClOrdId;
            MarkConflictingVenueEvidence(mutation, mutationClOrdId, atUtc);
        }

        _inboundEvidence[existingEvidenceId] = existingEvidence with
        {
            Disposition = InboundVenueEvidenceDisposition.Conflicting,
            MatchedMutationIds = matchedIds,
            AuthoritativeTerminalContradiction = false,
        };
        return matchedIds;
    }

    private void PromotePossibleResendUnsafe(string evidenceId)
    {
        if (_inboundEvidence.TryGetValue(evidenceId, out var evidence)
            && !evidence.PossibleResend)
        {
            _inboundEvidence[evidenceId] = evidence with
            {
                PossibleResend = true,
            };
        }
    }

    private void AddInboundEvidenceUnsafe(
        InboundVenueEvidenceSnapshot evidence,
        string identity)
    {
        if (_inboundEvidence.TryGetValue(evidence.EvidenceId, out var existing))
        {
            if (existing != evidence)
                throw TransitionError("Conflicting inbound venue evidence identity.");
            return;
        }

        _inboundEvidence.Add(evidence.EvidenceId, evidence);
        _inboundEvidenceIdentity.TryAdd(identity, evidence.EvidenceId);
        if (evidence.Disposition == InboundVenueEvidenceDisposition.Unmatched
            && evidence.MatchedMutationIds.Count == 0)
        {
            _unmatchedEvidenceOrder.Enqueue(evidence.EvidenceId);
            while (_unmatchedEvidenceOrder.Count > MaxRetainedUnmatchedEvidence)
            {
                var expired = _unmatchedEvidenceOrder.Dequeue();
                if (_inboundEvidence.TryGetValue(expired, out var candidate)
                    && candidate.Disposition == InboundVenueEvidenceDisposition.Unmatched
                    && candidate.MatchedMutationIds.Count == 0)
                {
                    _inboundEvidence.Remove(expired);
                    var candidateIdentity = EvidenceIdentity(candidate);
                    if (_inboundEvidenceIdentity.TryGetValue(
                            candidateIdentity,
                            out var indexed)
                        && indexed == expired)
                    {
                        _inboundEvidenceIdentity.Remove(candidateIdentity);
                    }
                }
            }
        }
    }

    private static InboundVenueEvidenceSnapshot CreateExecutionReportEvidence(
        ExecutionReportReceivedEvent evt,
        string evidenceId,
        InboundVenueEvidenceDisposition disposition,
        IReadOnlyList<OutboundMutationId> matchedMutationIds,
        bool authoritativeTerminalContradiction = false) =>
        new()
        {
            EvidenceId = evidenceId,
            Kind = InboundVenueEvidenceKind.ExecutionReport,
            Disposition = disposition,
            FirmId = NormalizeFirm(evt.FirmId),
            SessionId = evt.SessionId,
            SessionVerId = evt.SessionVerId,
            InboundSeqNum = evt.InboundSeqNum,
            SendingTime = evt.VenueSendingTime,
            PossibleResend = evt.PossibleResend,
            AuthoritativeTerminalContradiction = authoritativeTerminalContradiction,
            MessageKind = evt.ExecKind,
            ClOrdId = evt.ClOrdId,
            OrigClOrdId = evt.OrigClOrdId == 0 ? null : evt.OrigClOrdId,
            VenueOrderId = evt.VenueOrderId,
            ObservedAtUtc = evt.TimestampUtc,
            MatchedMutationIds = matchedMutationIds.ToArray(),
        };

    private static InboundVenueEvidenceSnapshot CreateBusinessRejectEvidence(
        BusinessRejectReceivedEvent evt,
        string evidenceId,
        InboundVenueEvidenceDisposition disposition,
        IReadOnlyList<OutboundMutationId> matchedMutationIds) =>
        new()
        {
            EvidenceId = evidenceId,
            Kind = InboundVenueEvidenceKind.BusinessReject,
            Disposition = disposition,
            FirmId = NormalizeFirm(evt.FirmId),
            SessionId = evt.SessionId,
            SessionVerId = evt.SessionVerId,
            InboundSeqNum = evt.SeqNum,
            SendingTime = evt.SendingTime,
            PossibleResend = evt.PossibleResend,
            BusinessRejectRefSeqNum = evt.RefSeqNum,
            ObservedAtUtc = evt.TimestampUtc,
            MatchedMutationIds = matchedMutationIds.ToArray(),
        };

    private static InboundVenueEvidenceSnapshot CreateNotAppliedEvidence(
        NotAppliedReceivedEvent evt,
        string evidenceId,
        InboundVenueEvidenceDisposition disposition,
        IReadOnlyList<OutboundMutationId> matchedMutationIds) =>
        new()
        {
            EvidenceId = evidenceId,
            Kind = InboundVenueEvidenceKind.NotApplied,
            Disposition = disposition,
            FirmId = NormalizeFirm(evt.FirmId),
            SessionId = evt.SessionId,
            SessionVerId = evt.SessionVerId,
            NotAppliedFromSeqNo = evt.FromSeqNo,
            NotAppliedCount = evt.Count,
            ObservedAtUtc = evt.ObservedAtUtc,
            MatchedMutationIds = matchedMutationIds.ToArray(),
        };

    private static string ExecutionReportEvidenceId(ExecutionReportReceivedEvent evt) =>
        DigestEvidence(Canonical(
            $"er|{NormalizeFirm(evt.FirmId)}|{evt.SessionId}|{evt.SessionVerId}|{evt.InboundSeqNum}|{evt.ExecKind}|{evt.ClOrdId}|{evt.OrigClOrdId}|{evt.VenueOrderId}|{evt.LeavesQuantity}|{evt.CumulativeQuantity}|{evt.LastQuantity}|{evt.LastPrice}|{CanonicalOptionalText(evt.RejectReason)}|{evt.Synthetic}"));

    private static string BusinessRejectEvidenceId(BusinessRejectReceivedEvent evt) =>
        DigestEvidence(Canonical(
            $"br|{NormalizeFirm(evt.FirmId)}|{evt.SessionId}|{evt.SessionVerId}|{evt.SeqNum}|{evt.RefSeqNum}|{evt.RejectReason}"));

    private static string NotAppliedEvidenceId(NotAppliedReceivedEvent evt) =>
        DigestEvidence(Canonical(
            $"na|{NormalizeFirm(evt.FirmId)}|{evt.SessionId}|{evt.SessionVerId}|{evt.FromSeqNo}|{evt.Count}"));

    private static string ExecutionReportEvidenceIdentity(
        ExecutionReportReceivedEvent evt,
        string evidenceId) =>
        HasCompleteExecutionReportIdentity(evt)
            ? Canonical(
                $"er|{NormalizeFirm(evt.FirmId)}|{evt.SessionId}|{evt.SessionVerId}|{evt.InboundSeqNum}")
            : $"legacy|{evidenceId}";

    private static bool HasCompleteExecutionReportIdentity(
        ExecutionReportReceivedEvent evt) =>
        !string.IsNullOrWhiteSpace(evt.FirmId)
        && evt.SessionId is not null and not 0
        && evt.SessionVerId is not null and not 0
        && evt.InboundSeqNum is not null and not 0;

    private static string BusinessRejectEvidenceIdentity(
        BusinessRejectReceivedEvent evt,
        string evidenceId) =>
        !string.IsNullOrWhiteSpace(evt.FirmId)
        && evt.SessionId is not null and not 0
        && evt.SessionVerId is not null and not 0
        && evt.SeqNum != 0
            ? Canonical(
                $"br|{NormalizeFirm(evt.FirmId)}|{evt.SessionId}|{evt.SessionVerId}|{evt.SeqNum}")
            : $"legacy|{evidenceId}";

    private static string NotAppliedEvidenceIdentity(NotAppliedReceivedEvent evt) =>
        Canonical(
            $"na|{NormalizeFirm(evt.FirmId)}|{evt.SessionId}|{evt.SessionVerId}|{evt.FromSeqNo}|{evt.Count}");

    private static string EvidenceIdentity(InboundVenueEvidenceSnapshot evidence) =>
        evidence.Kind switch
        {
            InboundVenueEvidenceKind.ExecutionReport
                when IsKnownEvidenceFirm(evidence.FirmId)
                     && evidence.SessionId is not null and not 0
                     && evidence.SessionVerId is not null and not 0
                     && evidence.InboundSeqNum is not null and not 0 =>
                Canonical(
                    $"er|{NormalizeFirm(evidence.FirmId)}|{evidence.SessionId}|{evidence.SessionVerId}|{evidence.InboundSeqNum}"),
            InboundVenueEvidenceKind.BusinessReject
                when IsKnownEvidenceFirm(evidence.FirmId)
                     && evidence.SessionId is not null and not 0
                     && evidence.SessionVerId is not null and not 0
                     && evidence.InboundSeqNum is not null and not 0 =>
                Canonical(
                    $"br|{NormalizeFirm(evidence.FirmId)}|{evidence.SessionId}|{evidence.SessionVerId}|{evidence.InboundSeqNum}"),
            InboundVenueEvidenceKind.NotApplied =>
                Canonical(
                    $"na|{NormalizeFirm(evidence.FirmId)}|{evidence.SessionId}|{evidence.SessionVerId}|{evidence.NotAppliedFromSeqNo}|{evidence.NotAppliedCount}"),
            _ => $"legacy|{evidence.EvidenceId}",
        };

    private static string Canonical(FormattableString value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string CanonicalOptionalText(string? value)
    {
        if (value is null)
            return "null";
        var bytes = Encoding.UTF8.GetBytes(value);
        return Canonical(
            $"text:{bytes.Length}:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}");
    }

    private static bool SequenceRangeContains(
        ulong fromSeqNo,
        uint count,
        ulong sequenceNumber) =>
        count != 0
        && sequenceNumber >= fromSeqNo
        && sequenceNumber - fromSeqNo < count;

    private static string NormalizeFirm(string? firmId) =>
        string.IsNullOrWhiteSpace(firmId) ? UnknownFirm : firmId;

    private static bool IsKnownEvidenceFirm(string firmId) =>
        !string.IsNullOrWhiteSpace(firmId)
        && !string.Equals(firmId, UnknownFirm, StringComparison.Ordinal);

    private static InboundVenueEvidenceSnapshot CloneEvidence(
        InboundVenueEvidenceSnapshot evidence) =>
        evidence with
        {
            MatchedMutationIds = evidence.MatchedMutationIds.ToArray(),
        };

    private static OutboundAuthoritativeEvidenceSnapshot CloneAuthoritativeEvidence(
        OutboundAuthoritativeEvidenceSnapshot evidence) =>
        evidence with
        {
            CoveredMutationIds = evidence.CoveredMutationIds.ToArray(),
        };

    private static bool AuthoritativeEvidenceEquals(
        OutboundAuthoritativeEvidenceSnapshot left,
        OutboundAuthoritativeEvidenceSnapshot right) =>
        left.EvidenceReference == right.EvidenceReference
        && left.EvidenceDigest == right.EvidenceDigest
        && left.FirmId == right.FirmId
        && left.SourceType == right.SourceType
        && left.CoverageStartUtc == right.CoverageStartUtc
        && left.CoverageEndUtc == right.CoverageEndUtc
        && left.CoveredMutationIds.SequenceEqual(right.CoveredMutationIds)
        && left.AttestationReference == right.AttestationReference
        && left.AttestedBy == right.AttestedBy
        && left.AttestedAtUtc == right.AttestedAtUtc
        && left.RegisteredAtUtc == right.RegisteredAtUtc;

    private void Terminalise(
        OutboundMutationSnapshot mutation,
        OutboundMutationState state,
        DateTimeOffset atUtc,
        string evidenceKind,
        string evidenceDigest,
        ulong? venueOrderId)
    {
        var attempts = mutation.Attempts.ToArray();
        if (attempts.Length > 0
            && attempts[^1].AmbiguityReason
                == OutboundAmbiguityReason.ConflictingVenueEvidence
            && mutation.OperatorEvidence.Any(evidence =>
                evidence.Decision != OutboundOperatorDecision.LeaveAmbiguous
                && evidence.EvidenceDigest == evidenceDigest))
        {
            attempts[^1] = attempts[^1] with
            {
                AmbiguityReason = null,
            };
        }
        mutation = mutation with
        {
            Attempts = attempts,
            State = state,
            StateChangedAtUtc = atUtc,
            Resolution = new OutboundResolutionSnapshot
            {
                State = state,
                ResolvedAtUtc = atUtc,
                EvidenceKind = evidenceKind,
                EvidenceDigest = evidenceDigest,
                VenueOrderId = venueOrderId,
            },
            RequiresReconciliation =
                mutation.SensitivePayloadAvailability
                != OutboundSensitivePayloadAvailability.Available,
            ExplicitlyRequiresReconciliation = false,
        };
        _mutations[mutation.MutationId] = mutation;
        MarkCorrelations(mutation, terminal: true, atUtc);
        RemoveActiveOriginalIndex(mutation);
    }

    private void MarkConflictingVenueEvidence(
        OutboundMutationSnapshot mutation,
        ulong clOrdId,
        DateTimeOffset atUtc)
    {
        if (IsTerminal(mutation.State))
            return;
        var attempts = mutation.Attempts.ToArray();
        var index = Array.FindIndex(
            attempts,
            attempt => attempt.ClOrdId == clOrdId);
        if (index < 0 && attempts.Length > 0)
            index = attempts.Length - 1;
        if (index >= 0)
        {
            attempts[index] = attempts[index] with
            {
                AmbiguityReason = OutboundAmbiguityReason.ConflictingVenueEvidence,
            };
        }
        mutation = mutation with
        {
            Attempts = attempts,
            State = OutboundMutationState.Ambiguous,
            StateChangedAtUtc = atUtc,
            Resolution = null,
            RequiresReconciliation = true,
        };
        _mutations[mutation.MutationId] = mutation;
        MarkCorrelations(mutation, terminal: false, atUtc);
    }

    private void MarkUnmatchedVenueEvidence(
        OutboundMutationSnapshot mutation,
        ulong clOrdId,
        DateTimeOffset atUtc,
        OutboundAmbiguityReason reason)
    {
        if (IsTerminal(mutation.State))
            return;
        var attempts = mutation.Attempts.ToArray();
        var index = Array.FindIndex(
            attempts,
            attempt => attempt.ClOrdId == clOrdId);
        if (index < 0 && attempts.Length > 0)
            index = attempts.Length - 1;
        if (index >= 0)
        {
            attempts[index] = attempts[index] with
            {
                AmbiguityReason = attempts[index].AmbiguityReason
                    ?? reason,
            };
        }
        mutation = mutation with
        {
            Attempts = attempts,
            State = OutboundMutationState.Ambiguous,
            StateChangedAtUtc = atUtc,
            Resolution = null,
            RequiresReconciliation = true,
        };
        _mutations[mutation.MutationId] = mutation;
        MarkCorrelations(mutation, terminal: false, atUtc);
    }

    private void MarkTerminalEvidenceConflict(
        OutboundMutationSnapshot mutation,
        DateTimeOffset atUtc,
        bool reopenReconciliation = false)
    {
        var attempts = mutation.Attempts.ToArray();
        if (attempts.Length > 0)
        {
            attempts[^1] = attempts[^1] with
            {
                AmbiguityReason = OutboundAmbiguityReason.ConflictingVenueEvidence,
            };
        }
        var updated = mutation with
        {
            Attempts = attempts,
            State = reopenReconciliation
                ? OutboundMutationState.Ambiguous
                : mutation.State,
            StateChangedAtUtc = reopenReconciliation
                ? atUtc
                : mutation.StateChangedAtUtc,
            RequiresReconciliation = true,
        };
        _mutations[mutation.MutationId] = updated;
        if (reopenReconciliation)
        {
            MarkCorrelations(updated, terminal: false, atUtc);
            RestoreActiveOriginalGuard(updated);
        }
    }

    private void RestoreActiveOriginalGuard(OutboundMutationSnapshot mutation)
    {
        if (mutation.OriginalClOrdId is not { } originalClOrdId)
            return;
        var key = new OriginalOrderKey(mutation.FirmId, originalClOrdId);
        if (!_activeByOriginal.TryGetValue(key, out var existing)
            || existing == mutation.MutationId
            || !_mutations.TryGetValue(existing, out var existingMutation)
            || IsTerminal(existingMutation.State))
        {
            _activeByOriginal[key] = mutation.MutationId;
        }
    }

    private static OutboundMutationSnapshot AppendLegacyEvidence(
        OutboundMutationSnapshot mutation,
        string evidenceKind,
        DateTimeOffset observedAtUtc,
        string canonicalEvidence)
    {
        var digest = DigestEvidence(canonicalEvidence);
        if (mutation.LegacyEvidence.Any(e =>
                e.EvidenceKind == evidenceKind
                && e.EvidenceDigest == digest))
            return mutation;
        var evidence = mutation.LegacyEvidence.ToList();
        evidence.Add(new OutboundLegacyEvidenceSnapshot
        {
            EvidenceKind = evidenceKind,
            EvidenceDigest = digest,
            ObservedAtUtc = observedAtUtc,
        });
        return mutation with { LegacyEvidence = evidence };
    }

    private void AddClOrdCorrelation(
        OutboundMutationSnapshot mutation,
        ulong clOrdId,
        bool terminal,
        DateTimeOffset retainFromUtc)
    {
        if (_byClOrdId.TryGetValue(clOrdId, out var existing)
            && existing != mutation.MutationId)
            throw TransitionError("ClOrdID correlation is not unique.");
        _byClOrdId[clOrdId] = mutation.MutationId;
        _correlations[clOrdId] = new OutboundCorrelationTombstone
        {
            ClOrdId = clOrdId,
            MutationId = mutation.MutationId,
            Kind = mutation.Kind,
            Terminal = terminal,
            RetainFromUtc = retainFromUtc,
        };
    }

    private void MarkCorrelations(
        OutboundMutationSnapshot mutation,
        bool terminal,
        DateTimeOffset atUtc)
    {
        AddClOrdCorrelation(mutation, mutation.PrimaryClOrdId, terminal, atUtc);
        foreach (var attempt in mutation.Attempts)
            AddClOrdCorrelation(mutation, attempt.ClOrdId, terminal, atUtc);
    }

    private void AddMutationIndexes(OutboundMutationSnapshot mutation)
    {
        AddClOrdCorrelation(
            mutation, mutation.PrimaryClOrdId,
            IsTerminal(mutation.State),
            mutation.Resolution?.ResolvedAtUtc ?? mutation.RecordedAtUtc);
        foreach (var attempt in mutation.Attempts)
        {
            AddClOrdCorrelation(
                mutation, attempt.ClOrdId,
                IsTerminal(mutation.State),
                mutation.Resolution?.ResolvedAtUtc ?? attempt.IntentPreparedAtUtc);
            if (attempt.FramePrepared is { } frame)
            {
                var key = new FrameKey(
                    mutation.FirmId, frame.SessionId, frame.SessionVerId, frame.OutboundSeqNum);
                if (_byFrame.TryGetValue(key, out var existing)
                    && existing != mutation.MutationId)
                    throw new OutboundLedgerRecoveryException("Duplicate outbound frame correlation.");
                _byFrame[key] = mutation.MutationId;
            }
        }
        AddActiveOriginalIndex(mutation);
        AddAlgoOriginIndex(mutation);
    }

    private void RemoveMutationIndexes(OutboundMutationSnapshot mutation)
    {
        foreach (var clOrdId in mutation.Attempts.Select(a => a.ClOrdId)
                     .Append(mutation.PrimaryClOrdId)
                     .Distinct())
            _byClOrdId.Remove(clOrdId);
        foreach (var attempt in mutation.Attempts)
        {
            if (attempt.FramePrepared is { } frame)
                _byFrame.Remove(new FrameKey(
                    mutation.FirmId, frame.SessionId, frame.SessionVerId, frame.OutboundSeqNum));
        }
        RemoveActiveOriginalIndex(mutation);
        if (mutation.AlgoOriginIdentity is { } algoOrigin
            && _byAlgoOrigin.TryGetValue(
                new FirmAlgoOriginKey(mutation.FirmId, algoOrigin),
                out var existing)
            && existing == mutation.MutationId)
        {
            _byAlgoOrigin.Remove(new FirmAlgoOriginKey(mutation.FirmId, algoOrigin));
        }
    }

    private void AddAlgoOriginIndex(OutboundMutationSnapshot mutation)
    {
        if (mutation.AlgoOriginIdentity is not { } origin)
            return;
        var key = new FirmAlgoOriginKey(mutation.FirmId, origin);
        if (_byAlgoOrigin.TryGetValue(key, out var existing)
            && existing != mutation.MutationId)
        {
            throw new OutboundLedgerRecoveryException(
                "Multiple outbound mutations share the same algo logical action.");
        }
        _byAlgoOrigin[key] = mutation.MutationId;
    }

    private void AddActiveOriginalIndex(OutboundMutationSnapshot mutation)
    {
        if (mutation.OriginalClOrdId is not { } originalClOrdId
            || mutation.State == OutboundMutationState.ProvenUnsent
            || IsTerminal(mutation.State))
            return;
        var key = new OriginalOrderKey(mutation.FirmId, originalClOrdId);
        if (_activeByOriginal.TryGetValue(key, out var existing)
            && existing != mutation.MutationId)
        {
            if (_mutations.TryGetValue(existing, out var existingMutation)
                && existingMutation.Origin == OutboundMutationOrigin.Legacy
                && mutation.Origin == OutboundMutationOrigin.Legacy)
                return;
            throw new OutboundLedgerRecoveryException(
                "Multiple active outbound mutations target the same original order.");
        }
        _activeByOriginal[key] = mutation.MutationId;
    }

    private void RemoveActiveOriginalIndex(OutboundMutationSnapshot mutation)
    {
        if (mutation.OriginalClOrdId is not { } originalClOrdId)
            return;
        var key = new OriginalOrderKey(mutation.FirmId, originalClOrdId);
        if (_activeByOriginal.TryGetValue(key, out var existing)
            && existing == mutation.MutationId)
        {
            _activeByOriginal.Remove(key);
            var replacement = _mutations.Values
                .Where(candidate =>
                    candidate.MutationId != mutation.MutationId
                    && candidate.OriginalClOrdId == originalClOrdId
                    && string.Equals(candidate.FirmId, mutation.FirmId, StringComparison.Ordinal)
                    && candidate.State != OutboundMutationState.ProvenUnsent
                    && !IsTerminal(candidate.State))
                .OrderBy(candidate => candidate.RecordedAtUtc)
                .ThenBy(candidate => candidate.MutationId.Value)
                .FirstOrDefault();
            if (replacement is not null)
                _activeByOriginal[key] = replacement.MutationId;
        }
    }

    private OutboundMutationSnapshot RequiredMutation(OutboundMutationId id)
    {
        if (!_mutations.TryGetValue(id, out var mutation))
            throw TransitionError("Outbound mutation is unknown.");
        return mutation;
    }

    private static (OutboundAttemptSnapshot Attempt, int Index) RequiredAttempt(
        OutboundMutationSnapshot mutation,
        OutboundAttemptId id)
    {
        for (var i = 0; i < mutation.Attempts.Count; i++)
        {
            if (mutation.Attempts[i].AttemptId == id)
                return (mutation.Attempts[i], i);
        }
        throw TransitionError("Outbound attempt is unknown.");
    }

    private static OutboundMutationSnapshot ReplaceAttempt(
        OutboundMutationSnapshot mutation,
        int index,
        OutboundAttemptSnapshot attempt,
        OutboundMutationState state,
        DateTimeOffset atUtc,
        bool? requiresReconciliation = null)
    {
        var attempts = mutation.Attempts.ToArray();
        attempts[index] = attempt;
        return mutation with
        {
            Attempts = attempts,
            State = state,
            StateChangedAtUtc = atUtc,
            RequiresReconciliation = requiresReconciliation ?? mutation.RequiresReconciliation,
        };
    }

    private static bool ApprovalEquivalent(
        OutboundMutationSnapshot existing,
        OutboundApprovedEvent evt) =>
        existing.Kind == evt.MutationKind
        && existing.FirmId == evt.FirmId
        && existing.EndClientRef == evt.EndClientRef
        && existing.Origin == evt.Origin
        && existing.AlgoOriginIdentity == evt.AlgoOriginIdentity
        && existing.BotBusinessIdentity == evt.BotBusinessIdentity
        && existing.PrimaryClOrdId == evt.PrimaryClOrdId
        && existing.OriginalClOrdId == evt.OriginalClOrdId
        && existing.Approval?.StoredCommandIntegritySha256
            == evt.Approval.StoredCommandIntegritySha256;

    private static void ValidateIdentity(
        OutboundMutationId mutationId,
        ulong primaryClOrdId,
        string firmId,
        string endClientRef)
    {
        if (mutationId.Value == Guid.Empty || primaryClOrdId == 0
            || string.IsNullOrWhiteSpace(firmId)
            || !IsLowerHex(endClientRef, 32))
            throw TransitionError("Outbound mutation identity is invalid.");
    }

    private static void ValidateApproval(
        OutboundApprovalSnapshot approval,
        ulong primaryClOrdId)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (approval.ApprovalVersion != 1
            || approval.CanonicalCommandNonSensitive.ClOrdId != primaryClOrdId)
            throw TransitionError("Outbound approval version or ClOrdID is invalid.");
        if (!AeadOutboundCommandProtector.IntegrityMatches(approval))
            throw TransitionError("Outbound approval integrity validation failed.");
        if (!approval.SensitiveFieldRefs.Contains(OutboundSensitiveFieldRef.EndClientId))
            throw TransitionError("The encrypted command envelope must carry end-client identity.");
    }

    private static void ValidateOrigin(
        OutboundMutationOrigin origin,
        AlgoOutboundOriginIdentity? algoOrigin)
    {
        if (origin == OutboundMutationOrigin.Algo)
        {
            if (algoOrigin is null
                || algoOrigin.ParentAlgoId == 0
                || algoOrigin.Sequence < 0)
            {
                throw TransitionError("Algo outbound origin identity is invalid.");
            }
            return;
        }
        if (algoOrigin is not null)
            throw TransitionError("Algo outbound origin identity requires Algo origin.");
    }

    private static bool IsReadinessBlocking(OutboundMutationSnapshot mutation) =>
        mutation.RequiresReconciliation
        || (mutation.Kind == OutboundMutationKind.New
            && mutation.State == OutboundMutationState.ProvenUnsent)
        || StateRequiresReconciliation(mutation.State);

    private static bool StateRequiresReconciliation(OutboundMutationState state) =>
        state is OutboundMutationState.Ambiguous
            or OutboundMutationState.RecordedPendingApproval
            or OutboundMutationState.LegacyUnknown
            or OutboundMutationState.LegacyUnknownCancel
            or OutboundMutationState.LegacyUnknownReplace;

    private static bool IsAlgoActionBlocking(OutboundMutationState state) =>
        state is OutboundMutationState.RecordedPendingApproval
            or OutboundMutationState.ApprovedToSend
            or OutboundMutationState.AttemptIntentPrepared
            or OutboundMutationState.FramePrepared
            or OutboundMutationState.ProvenUnsent
            or OutboundMutationState.Ambiguous
            or OutboundMutationState.LegacyUnknown
            or OutboundMutationState.LegacyUnknownCancel
            or OutboundMutationState.LegacyUnknownReplace;

    private static bool IsLegacyState(OutboundMutationState state) =>
        state is OutboundMutationState.LegacyUnknown
            or OutboundMutationState.LegacyUnknownCancel
            or OutboundMutationState.LegacyUnknownReplace;

    private static bool IsApprovalReplaceableState(OutboundMutationState state) =>
        state == OutboundMutationState.RecordedPendingApproval
        || IsLegacyState(state);

    private static bool IsTerminal(OutboundMutationState state) =>
        state is OutboundMutationState.VenueAcknowledged
            or OutboundMutationState.OperatorResolved
            or OutboundMutationState.LegacyTerminal;

    private static OutboundMutationId DeterministicLegacyId(
        OutboundMutationKind kind,
        string firmId,
        ulong clOrdId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"b3-legacy-outbound-v1|{kind}|{firmId}|{clOrdId}"));
        return new OutboundMutationId(new Guid(bytes.AsSpan(0, 16)));
    }

    private static string DigestEvidence(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();

    private static OutboundMutationSnapshot Clone(OutboundMutationSnapshot mutation) =>
        mutation with
        {
            Attempts = mutation.Attempts.Select(a => a with
            {
                FramePrepared = a.FramePrepared is null ? null : a.FramePrepared with { },
            }).ToArray(),
            Approval = mutation.Approval is null
                ? null
                : mutation.Approval with
                {
                    CanonicalCommandNonSensitive =
                        mutation.Approval.CanonicalCommandNonSensitive with { },
                    SensitiveFieldRefs = mutation.Approval.SensitiveFieldRefs.ToArray(),
                    SensitiveCommandEnvelope = new EncryptedOutboundCommandEnvelope
                    {
                        KeyId = mutation.Approval.SensitiveCommandEnvelope.KeyId,
                        KeyVersion = mutation.Approval.SensitiveCommandEnvelope.KeyVersion,
                        AlgorithmVersion = mutation.Approval.SensitiveCommandEnvelope.AlgorithmVersion,
                        NonceBase64 = mutation.Approval.SensitiveCommandEnvelope.NonceBase64,
                        CiphertextBase64 = mutation.Approval.SensitiveCommandEnvelope.CiphertextBase64,
                        AuthenticationTagBase64 = mutation.Approval.SensitiveCommandEnvelope.AuthenticationTagBase64,
                    },
                },
            BotBusinessIdentity = mutation.BotBusinessIdentity is null
                ? null
                : mutation.BotBusinessIdentity with { },
            Resolution = mutation.Resolution is null ? null : mutation.Resolution with { },
            OperatorEvidence = mutation.OperatorEvidence
                .Select(e => e with { })
                .ToArray(),
            ResolutionProposals = mutation.ResolutionProposals
                .Select(proposal => proposal with { })
                .ToArray(),
            AuthoritativeEvidence = mutation.AuthoritativeEvidence
                .Select(CloneAuthoritativeEvidence)
                .ToArray(),
            LegacyEvidence = mutation.LegacyEvidence
                .Select(e => e with { })
                .ToArray(),
        };

    private static bool CanOperatorResolve(OutboundMutationSnapshot mutation) =>
        mutation.RequiresReconciliation
        || mutation.State is OutboundMutationState.Ambiguous
            or OutboundMutationState.ProvenUnsent
            or OutboundMutationState.VenueAcknowledged
            or OutboundMutationState.LegacyUnknown
            or OutboundMutationState.LegacyUnknownCancel
            or OutboundMutationState.LegacyUnknownReplace;

    private static bool HasRegisteredAuthoritativeEvidence(
        OutboundMutationSnapshot mutation,
        OutboundAuthoritativeEvidenceSourceType sourceType,
        string evidenceReference) =>
        mutation.AuthoritativeEvidence.Any(evidence =>
            evidence.SourceType == sourceType
            && evidence.EvidenceReference == evidenceReference
            && string.Equals(evidence.FirmId, mutation.FirmId, StringComparison.Ordinal)
            && evidence.CoveredMutationIds.Contains(mutation.MutationId)
            && mutation.RecordedAtUtc >= evidence.CoverageStartUtc
            && mutation.RecordedAtUtc <= evidence.CoverageEndUtc
            && EvidenceReferenceMatchesSource(
                evidence.SourceType,
                evidence.EvidenceReference,
                evidence.EvidenceDigest)
            && IsOpaqueReference(evidence.AttestationReference)
            && IsOpaqueReference(evidence.AttestedBy)
            && evidence.AttestedAtUtc <= evidence.RegisteredAtUtc);

    private static bool EvidenceReferenceMatchesSource(
        OutboundAuthoritativeEvidenceSourceType sourceType,
        string evidenceReference,
        string evidenceDigest)
    {
        var prefix = sourceType switch
        {
            OutboundAuthoritativeEvidenceSourceType.VenueMassAction => "venue-report:",
            OutboundAuthoritativeEvidenceSourceType.OfficialExtract => "official-extract:",
            _ => string.Empty,
        };
        return prefix.Length > 0
            && evidenceReference == $"{prefix}{evidenceDigest}"
            && IsLowerHex(evidenceDigest, 64);
    }

    private static void ValidateOperatorEvidencePair(
        OutboundOperatorDecision decision,
        OutboundOperatorEvidenceType evidenceType,
        bool releaseCapacity)
    {
        if (evidenceType == OutboundOperatorEvidenceType.ManualAnnotation)
        {
            if (decision != OutboundOperatorDecision.LeaveAmbiguous || releaseCapacity)
                throw TransitionError("Manual annotation can only leave a mutation ambiguous.");
            return;
        }
        if (decision == OutboundOperatorDecision.LeaveAmbiguous)
            throw TransitionError("Leave-ambiguous requires manual annotation evidence.");
        if (releaseCapacity
            && evidenceType is not (
                OutboundOperatorEvidenceType.TerminalExecutionReport
                or OutboundOperatorEvidenceType.ContractedNotApplied
                or OutboundOperatorEvidenceType.VenueMassAction
                or OutboundOperatorEvidenceType.OfficialExtract))
            throw TransitionError("Capacity release requires authoritative venue evidence.");
        if (decision == OutboundOperatorDecision.VenueAcknowledged
            && evidenceType != OutboundOperatorEvidenceType.TerminalExecutionReport)
            throw TransitionError(
                "Venue acknowledgment requires terminal execution report evidence.");
    }

    private static bool IsTerminalExecutionReportKind(string? messageKind) =>
        messageKind is not null
        && (messageKind.Equals("Rejected", StringComparison.OrdinalIgnoreCase)
            || messageKind.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
            || messageKind.Equals("Fill", StringComparison.OrdinalIgnoreCase)
            || messageKind.Equals("Replaced", StringComparison.OrdinalIgnoreCase)
            || messageKind.Equals("Expired", StringComparison.OrdinalIgnoreCase));

    private static bool IsVenueAcknowledgmentOnlyExecutionReportKind(string? messageKind) =>
        messageKind is not null
        && (messageKind.Equals("Fill", StringComparison.OrdinalIgnoreCase)
            || messageKind.Equals("Replaced", StringComparison.OrdinalIgnoreCase));

    private static string ResolveAmbiguityReason(OutboundMutationSnapshot mutation) =>
        mutation.Attempts.LastOrDefault()?.AmbiguityReason?.ToString()
        ?? (IsLegacyState(mutation.State) ? "LegacyUnknown" : "Unclassified");

    private static string AgeBucket(double seconds) => seconds switch
    {
        < 60d => "lt_1m",
        < 300d => "1m_5m",
        < 900d => "5m_15m",
        < 3600d => "15m_1h",
        < 21600d => "1h_6h",
        < 86400d => "6h_24h",
        _ => "gte_24h",
    };

    private static double OldestAge(
        IEnumerable<OutboundMutationSnapshot> mutations,
        DateTimeOffset now)
    {
        var oldest = mutations.Select(mutation =>
                Math.Max(0d, (now - mutation.StateChangedAtUtc).TotalSeconds))
            .DefaultIfEmpty(0d);
        return oldest.Max();
    }

    private static InvalidOperationException TransitionError(string message) =>
        new($"Outbound mutation ledger rejected evidence: {message}");

    private static bool IsLowerHex(string? value, int length) =>
        value is { Length: var actual }
        && actual == length
        && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsOpaqueReference(string? value) =>
        value is { Length: > 0 and <= 128 }
        && value.All(static c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    private readonly record struct FrameKey(
        string FirmId,
        ulong SessionId,
        uint SessionVerId,
        ulong OutboundSeqNum);

    private readonly record struct FirmAlgoOriginKey(
        string FirmId,
        AlgoOutboundOriginIdentity Origin);

    private readonly record struct OriginalOrderKey(string FirmId, ulong OriginalClOrdId);
}
