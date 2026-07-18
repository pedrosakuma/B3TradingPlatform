using System.Security.Cryptography;
using System.Text;
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
    public const int MaxOutboundAttempts = 2;
    public static readonly TimeSpan DefaultTerminalCorrelationRetention = TimeSpan.FromDays(30);

    private readonly object _gate = new();
    private readonly Dictionary<OutboundMutationId, OutboundMutationSnapshot> _mutations = new();
    private readonly Dictionary<ulong, OutboundMutationId> _byClOrdId = new();
    private readonly Dictionary<FrameKey, OutboundMutationId> _byFrame = new();
    private readonly Dictionary<ulong, OutboundCorrelationTombstone> _correlations = new();
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
                return _mutations.Values.Count(IsReadinessBlocking);
        }
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
        ValidateApproval(evt.Approval, evt.PrimaryClOrdId);
        lock (_gate)
        {
            if (_mutations.TryGetValue(evt.MutationId, out var existing))
            {
                if (ApprovalEquivalent(existing, evt))
                    return;
                throw TransitionError("Conflicting approval evidence.");
            }
            if (_byClOrdId.TryGetValue(evt.PrimaryClOrdId, out var existingMutation))
            {
                if (_mutations.TryGetValue(existingMutation, out var legacy)
                    && IsLegacyState(legacy.State))
                {
                    RemoveMutationIndexes(legacy);
                    _mutations.Remove(existingMutation);
                }
                else
                {
                    throw TransitionError("The approval ClOrdID is already correlated.");
                }
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
        }
    }

    public void Apply(OutboundOperatorResolvedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.EvidenceType == OutboundOperatorEvidenceType.ManualAnnotation
            && evt.Decision != OutboundOperatorDecision.LeaveAmbiguous)
            throw TransitionError("Manual annotation cannot terminalise a mutation.");
        if (!IsLowerHex(evt.EvidenceDigest, 64)
            || !IsOpaqueReference(evt.OperatorRef))
            throw TransitionError("Operator resolution evidence is incomplete.");
        lock (_gate)
        {
            var mutation = RequiredMutation(evt.MutationId);
            var duplicate = mutation.OperatorEvidence.FirstOrDefault(e =>
                e.EvidenceDigest == evt.EvidenceDigest
                && e.RecordedAtUtc == evt.ResolvedAtUtc);
            if (duplicate is not null)
            {
                if (duplicate.Decision == evt.Decision
                    && duplicate.EvidenceType == evt.EvidenceType
                    && duplicate.OperatorRef == evt.OperatorRef)
                    return;
                throw TransitionError("Conflicting operator resolution.");
            }
            if (mutation.Resolution is not null)
                throw TransitionError("Conflicting operator resolution.");
            if (mutation.State is not OutboundMutationState.Ambiguous
                and not OutboundMutationState.ProvenUnsent
                and not OutboundMutationState.LegacyUnknown
                and not OutboundMutationState.LegacyUnknownCancel
                and not OutboundMutationState.LegacyUnknownReplace)
                throw TransitionError("Operator resolution is not valid in the current state.");
            var evidence = mutation.OperatorEvidence.ToList();
            evidence.Add(new OutboundOperatorEvidenceSnapshot
            {
                Decision = evt.Decision,
                EvidenceType = evt.EvidenceType,
                EvidenceDigest = evt.EvidenceDigest,
                OperatorRef = evt.OperatorRef,
                RecordedAtUtc = evt.ResolvedAtUtc,
            });
            mutation = mutation with { OperatorEvidence = evidence };
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

    public void ApplyVenueAcknowledgement(ExecutionReportReceivedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        lock (_gate)
        {
            var direct = default(OutboundMutationId);
            var hasDirect = evt.ClOrdId != 0
                && _byClOrdId.TryGetValue(evt.ClOrdId, out direct);
            var original = default(OutboundMutationId);
            var hasOriginal = evt.OrigClOrdId != 0
                && _byClOrdId.TryGetValue(evt.OrigClOrdId, out original);
            var id = hasDirect ? direct : hasOriginal ? original : default;
            if (id.Value == Guid.Empty || !_mutations.TryGetValue(id, out var mutation))
                return;
            if (IsTerminal(mutation.State))
                return;
            if (evt.Synthetic)
            {
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
                }
                return;
            }

            if (!hasDirect
                || string.IsNullOrWhiteSpace(evt.FirmId)
                || !string.Equals(evt.FirmId, mutation.FirmId, StringComparison.Ordinal))
            {
                MarkConflictingVenueEvidence(mutation, evt.ClOrdId, evt.TimestampUtc);
                return;
            }

            if (mutation.Approval is null)
            {
                var legacyOriginalMatches = mutation.OriginalClOrdId is { } legacyExpectedOriginal
                    ? evt.OrigClOrdId == legacyExpectedOriginal
                    : evt.OrigClOrdId == 0;
                if (!IsLegacyState(mutation.State)
                    || evt.ClOrdId != mutation.PrimaryClOrdId
                    || !legacyOriginalMatches)
                {
                    MarkConflictingVenueEvidence(mutation, evt.ClOrdId, evt.TimestampUtc);
                    return;
                }
                var legacyEvidenceDigest = DigestEvidence(
                    $"{evt.FirmId}|{evt.ClOrdId}|{evt.OrigClOrdId}|{evt.ExecKind}");
                Terminalise(
                    mutation, OutboundMutationState.VenueAcknowledged,
                    evt.TimestampUtc, "LegacyExecutionReport",
                    legacyEvidenceDigest, evt.VenueOrderId);
                return;
            }

            if (mutation.Attempts.Count == 0)
            {
                MarkConflictingVenueEvidence(mutation, evt.ClOrdId, evt.TimestampUtc);
                return;
            }
            var activeAttemptIndex = mutation.Attempts.Count - 1;
            var activeAttempt = mutation.Attempts[activeAttemptIndex];
            var originalMatches = mutation.OriginalClOrdId is { } expectedOriginal
                ? evt.OrigClOrdId == expectedOriginal
                : evt.OrigClOrdId == 0;
            var frame = activeAttempt.FramePrepared;
            if (evt.ClOrdId != activeAttempt.ClOrdId
                || activeAttempt.ProvenUnsentEvidence is not null
                || mutation.Attempts.Any(a =>
                    a.AmbiguityReason
                    == OutboundAmbiguityReason.ConflictingVenueEvidence)
                || mutation.State is not OutboundMutationState.FramePrepared
                    and not OutboundMutationState.TransportWriteCompleted
                    and not OutboundMutationState.Ambiguous
                || frame is null
                || evt.SessionId != frame.SessionId
                || evt.SessionVerId != frame.SessionVerId
                || evt.InboundSeqNum is null or 0
                || !originalMatches)
            {
                MarkConflictingVenueEvidence(mutation, evt.ClOrdId, evt.TimestampUtc);
                return;
            }

            var evidenceDigest = DigestEvidence(
                $"{evt.FirmId}|{evt.SessionId}|{evt.SessionVerId}|{evt.InboundSeqNum}|{evt.ClOrdId}|{evt.OrigClOrdId}|{evt.ExecKind}");
            Terminalise(
                mutation, OutboundMutationState.VenueAcknowledged,
                evt.TimestampUtc, "ExecutionReport", evidenceDigest, evt.VenueOrderId);
        }
    }

    public void ApplyBusinessReject(BusinessRejectReceivedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.SessionId is null || evt.SessionVerId is null)
            return;
        lock (_gate)
        {
            var key = new FrameKey(evt.FirmId, evt.SessionId.Value, evt.SessionVerId.Value, evt.RefSeqNum);
            if (!_byFrame.TryGetValue(key, out var id)
                || !_mutations.TryGetValue(id, out var mutation)
                || IsTerminal(mutation.State))
                return;
            var activeAttempt = mutation.Attempts.LastOrDefault();
            if (activeAttempt is null
                || activeAttempt.FramePrepared is not { } frame
                || frame.SessionId != evt.SessionId.Value
                || frame.SessionVerId != evt.SessionVerId.Value
                || frame.OutboundSeqNum != evt.RefSeqNum
                || mutation.Attempts.Any(a =>
                    a.AmbiguityReason
                    == OutboundAmbiguityReason.ConflictingVenueEvidence)
                || mutation.State is not OutboundMutationState.FramePrepared
                    and not OutboundMutationState.TransportWriteCompleted
                    and not OutboundMutationState.Ambiguous)
            {
                MarkConflictingVenueEvidence(mutation, clOrdId: 0, evt.TimestampUtc);
                return;
            }
            var evidenceDigest = DigestEvidence(
                $"{evt.FirmId}|{evt.SessionId}|{evt.SessionVerId}|{evt.RefSeqNum}|{evt.SeqNum}|{evt.RejectReason}");
            Terminalise(
                mutation, OutboundMutationState.VenueAcknowledged,
                evt.TimestampUtc, "BusinessReject", evidenceDigest, venueOrderId: null);
        }
    }

    public void ImportLegacyNew(OrderSubmittedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ImportLegacy(
            OutboundMutationKind.New, evt.FirmId, evt.ClOrdId, null,
            evt.TimestampUtc, OutboundMutationState.LegacyUnknown);
    }

    public void ImportLegacyCancel(OrderCancelRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ImportLegacy(
            OutboundMutationKind.Cancel, string.Empty, evt.CancelClOrdId,
            evt.OriginalClOrdId, evt.TimestampUtc, OutboundMutationState.LegacyUnknownCancel);
    }

    public void ImportLegacyReplace(OrderReplaceRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ImportLegacy(
            OutboundMutationKind.Replace, evt.FirmId, evt.NewClOrdId,
            evt.OriginalClOrdId, evt.TimestampUtc, OutboundMutationState.LegacyUnknownReplace);
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
        if (activeEpoch.Value == Guid.Empty)
            throw new ArgumentException("Process epoch is required.", nameof(activeEpoch));
        lock (_gate)
        {
            var changed = 0;
            foreach (var pair in _mutations.ToArray())
            {
                var mutation = pair.Value;
                if (mutation.Attempts.Count == 0 || IsTerminal(mutation.State))
                    continue;
                var attempt = mutation.Attempts[^1];
                if (attempt.ProcessEpochId == activeEpoch
                    || attempt.ProvenUnsentEvidence is not null
                    || attempt.AmbiguityReason is not null)
                    continue;
                if (attempt.FramePrepared is null)
                {
                    var updated = attempt with
                    {
                        ProvenUnsentEvidence = OutboundProvenUnsentEvidence.DeadEpochIntentWithoutFrame,
                    };
                    var updatedMutation = ReplaceAttempt(
                        mutation, mutation.Attempts.Count - 1, updated,
                        OutboundMutationState.ProvenUnsent, atUtc);
                    _mutations[pair.Key] = updatedMutation;
                    AddClOrdCorrelation(
                        updatedMutation, attempt.ClOrdId, terminal: true, atUtc);
                }

                else
                {
                    var reason = attempt.TransportWriteCompletedAtUtc is null
                        ? OutboundAmbiguityReason.DeadEpochFramePrepared
                        : OutboundAmbiguityReason.DeadEpochTransportWriteCompleted;
                    var updated = attempt with { AmbiguityReason = reason };
                    _mutations[pair.Key] = ReplaceAttempt(
                        mutation, mutation.Attempts.Count - 1, updated,
                        OutboundMutationState.Ambiguous, atUtc,
                        requiresReconciliation: true);
                }
                changed++;
            }
            return changed;
        }
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
        IReadOnlyList<OutboundCorrelationTombstone> Correlations) CaptureSnapshot()
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
                    m.Approval?.SensitiveCommandEnvelope.KeyVersion))
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

    public void Restore(
        IEnumerable<OutboundMutationSnapshot> mutations,
        IEnumerable<OutboundCorrelationTombstone> correlations,
        bool legacyMigrationCompleted = false)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(correlations);
        lock (_gate)
        {
            _mutations.Clear();
            _byClOrdId.Clear();
            _byFrame.Clear();
            _correlations.Clear();
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
                    var availability = CheckPayloadAvailability(mutation.MutationId, mutation.FirmId, approval);
                    mutation = mutation with
                    {
                        SensitivePayloadAvailability = availability,
                        RequiresReconciliation = mutation.RequiresReconciliation
                            || availability != OutboundSensitivePayloadAvailability.Available,
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
        OutboundMutationState state)
    {
        if (mutationClOrdId == 0)
            return;
        lock (_gate)
        {
            if (_byClOrdId.ContainsKey(mutationClOrdId))
                return;
            var mutation = GetOrCreateLegacy(
                kind, firmId, mutationClOrdId, originalClOrdId, atUtc);
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
        DateTimeOffset atUtc)
    {
        if (_byClOrdId.TryGetValue(mutationClOrdId, out var existingId))
            return _mutations[existingId];
        var id = DeterministicLegacyId(kind, firmId, mutationClOrdId);
        if (_mutations.TryGetValue(id, out var existing))
            return existing;
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
        return mutation;
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

    private void Terminalise(
        OutboundMutationSnapshot mutation,
        OutboundMutationState state,
        DateTimeOffset atUtc,
        string evidenceKind,
        string evidenceDigest,
        ulong? venueOrderId)
    {
        mutation = mutation with
        {
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
            RequiresReconciliation = false,
        };
        _mutations[mutation.MutationId] = mutation;
        MarkCorrelations(mutation, terminal: true, atUtc);
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

    private static bool IsReadinessBlocking(OutboundMutationSnapshot mutation) =>
        mutation.RequiresReconciliation
        || mutation.State is OutboundMutationState.Ambiguous
            or OutboundMutationState.LegacyUnknown
            or OutboundMutationState.LegacyUnknownCancel
            or OutboundMutationState.LegacyUnknownReplace;

    private static bool IsLegacyState(OutboundMutationState state) =>
        state is OutboundMutationState.LegacyUnknown
            or OutboundMutationState.LegacyUnknownCancel
            or OutboundMutationState.LegacyUnknownReplace;

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
            Resolution = mutation.Resolution is null ? null : mutation.Resolution with { },
            OperatorEvidence = mutation.OperatorEvidence
                .Select(e => e with { })
                .ToArray(),
            LegacyEvidence = mutation.LegacyEvidence
                .Select(e => e with { })
                .ToArray(),
        };

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
}
