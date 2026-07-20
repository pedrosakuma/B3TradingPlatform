using System.Security.Cryptography;
using System.Text;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;

namespace B3.Trading.Application.Outbound;

public sealed record OutboundOperatorResolutionRequest(
    OutboundOperatorDecision Decision,
    OutboundOperatorEvidenceType EvidenceType,
    string EvidenceReference,
    string ReasonCode);

public sealed record OutboundAuthoritativeEvidenceRegistrationRequest(
    OutboundAuthoritativeEvidenceSourceType SourceType,
    string EvidenceReference,
    DateTimeOffset CoverageStartUtc,
    DateTimeOffset CoverageEndUtc,
    string AttestationReference);

public enum OutboundOperatorResolutionStatus
{
    PendingApproval,
    Annotated,
    Resolved,
}

public sealed record OutboundOperatorResolutionResult(
    OutboundMutationId MutationId,
    OutboundOperatorResolutionStatus Status,
    OutboundResolutionProposalId? ProposalId,
    bool CapacityReleased,
    bool RequiresReconciliation);

public sealed class OutboundReconciliationValidationException : InvalidOperationException
{
    public OutboundReconciliationValidationException(string message) : base(message) { }
}

public sealed class OutboundReconciliationNotFoundException : InvalidOperationException
{
    public OutboundReconciliationNotFoundException() : base("Outbound mutation was not found.") { }
}

public sealed class OutboundReconciliationForbiddenException : InvalidOperationException
{
    public OutboundReconciliationForbiddenException() : base("Outbound mutation is outside the caller firm scope.") { }
}

public sealed class OutboundReconciliationConflictException : InvalidOperationException
{
    public OutboundReconciliationConflictException(string message) : base(message) { }
}

public sealed class OutboundReconciliationUnavailableException : InvalidOperationException
{
    public OutboundReconciliationUnavailableException(Exception innerException)
        : base("Outbound reconciliation durability is unavailable.", innerException)
    {
    }
}

public sealed class OutboundReconciliationService
{
    private static readonly HashSet<string> AllowedReasonCodes =
    [
        "terminal_er_verified",
        "contracted_not_applied_verified",
        "venue_mass_action_verified",
        "official_extract_attested",
        "manual_comparison_recorded",
        "late_contradiction_reconciled",
    ];

    private readonly OutboundMutationLedger _ledger;
    private readonly EventDispatcher _dispatcher;
    private readonly IAuditLogger _audit;
    private readonly IMarginProvider _margin;
    private readonly IReplaceMarginCoordinator _replaceMargin;
    private readonly PendingReplacementRegistry _replacements;
    private readonly TimeProvider _clock;

    public OutboundReconciliationService(
        OutboundMutationLedger ledger,
        EventDispatcher dispatcher,
        IAuditLogger audit,
        IMarginProvider margin,
        IReplaceMarginCoordinator replaceMargin,
        PendingReplacementRegistry replacements,
        TimeProvider? clock = null)
    {
        _ledger = ledger;
        _dispatcher = dispatcher;
        _audit = audit;
        _margin = margin;
        _replaceMargin = replaceMargin;
        _replacements = replacements;
        _clock = clock ?? TimeProvider.System;
    }

    public OutboundOperatorResolutionResult Resolve(
        OutboundMutationId mutationId,
        string callerFirmId,
        string operatorRef,
        OutboundOperatorResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        operatorRef = CanonicalizeOperatorRef(operatorRef);
        var mutation = GetScopedMutation(mutationId, callerFirmId);
        var evidenceDigest = DigestResolution(mutationId, request);
        var existing = mutation.OperatorEvidence.FirstOrDefault(
            evidence => evidence.EvidenceDigest == evidenceDigest
                && evidence.Decision == request.Decision
                && evidence.EvidenceType == request.EvidenceType
                && evidence.EvidenceReference == request.EvidenceReference
                && evidence.ReasonCode == request.ReasonCode);
        if (existing is not null)
            return ResultFromEvidence(mutation, existing);
        ValidateRequest(mutation, request);

        var releaseCapacity = ReleasesCapacity(mutation, request.Decision);
        if (releaseCapacity)
        {
            var pending = mutation.ResolutionProposals.FirstOrDefault(proposal =>
                proposal.ApprovedAtUtc is null
                && proposal.EvidenceDigest == evidenceDigest
                && proposal.MakerRef == operatorRef);
            if (pending is not null)
            {
                return new OutboundOperatorResolutionResult(
                    mutationId,
                    OutboundOperatorResolutionStatus.PendingApproval,
                    pending.ProposalId,
                    CapacityReleased: false,
                    RequiresReconciliation: true);
            }
            return Propose(
                mutation,
                operatorRef,
                request,
                evidenceDigest,
                cancellationToken);
        }

        var atUtc = _clock.GetUtcNow();
        var resolved = new OutboundOperatorResolvedEvent
        {
            MutationId = mutationId,
            Decision = request.Decision,
            EvidenceType = request.EvidenceType,
            EvidenceReference = request.EvidenceReference,
            EvidenceDigest = evidenceDigest,
            ReasonCode = request.ReasonCode,
            OperatorRef = operatorRef,
            ReleaseCapacity = false,
            ResolvedAtUtc = atUtc,
            TimestampUtc = atUtc,
        };
        AuditFirst(
            mutation,
            operatorRef,
            "outbound_resolution_commit",
            request,
            evidenceDigest,
            proposalId: null,
            cancellationToken);
        DispatchResolution(
            resolved,
            mutation.FirmId,
            releaseCapacity: false,
            cancellationToken);
        var status = request.Decision == OutboundOperatorDecision.LeaveAmbiguous
            ? OutboundOperatorResolutionStatus.Annotated
            : OutboundOperatorResolutionStatus.Resolved;
        return new OutboundOperatorResolutionResult(
            mutationId,
            status,
            null,
            CapacityReleased: false,
            RequiresReconciliation: status == OutboundOperatorResolutionStatus.Annotated);
    }

    public OutboundAuthoritativeEvidenceSnapshot RegisterAuthoritativeEvidence(
        OutboundMutationId mutationId,
        string callerFirmId,
        string operatorRef,
        OutboundAuthoritativeEvidenceRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        operatorRef = CanonicalizeOperatorRef(operatorRef);
        var mutation = GetScopedMutation(mutationId, callerFirmId);
        ValidateEvidenceRegistration(mutation, request);
        var existing = mutation.AuthoritativeEvidence.FirstOrDefault(evidence =>
            evidence.EvidenceReference == request.EvidenceReference);
        if (existing is not null)
        {
            if (existing.SourceType == request.SourceType
                && existing.CoverageStartUtc == request.CoverageStartUtc
                && existing.CoverageEndUtc == request.CoverageEndUtc
                && existing.AttestationReference == request.AttestationReference)
                return existing;
            throw new OutboundReconciliationConflictException(
                "Authoritative evidence reference is already registered.");
        }
        var atUtc = _clock.GetUtcNow();
        var prefixLength = request.SourceType
            == OutboundAuthoritativeEvidenceSourceType.VenueMassAction
            ? "venue-report:".Length
            : "official-extract:".Length;
        var evidence = new OutboundAuthoritativeEvidenceSnapshot
        {
            EvidenceReference = request.EvidenceReference,
            EvidenceDigest = request.EvidenceReference[prefixLength..],
            FirmId = mutation.FirmId,
            SourceType = request.SourceType,
            CoverageStartUtc = request.CoverageStartUtc,
            CoverageEndUtc = request.CoverageEndUtc,
            CoveredMutationIds = [mutationId],
            AttestationReference = request.AttestationReference,
            AttestedBy = operatorRef,
            AttestedAtUtc = atUtc,
            RegisteredAtUtc = atUtc,
        };
        var registered = new OutboundAuthoritativeEvidenceRegisteredEvent
        {
            MutationId = mutationId,
            Evidence = evidence,
            TimestampUtc = atUtc,
        };
        AuditEvidenceRegistration(
            mutation,
            operatorRef,
            evidence,
            cancellationToken);
        try
        {
            var outcome = _dispatcher.DispatchCommittedIf(
                registered,
                () => IsEvidenceRegistrationStillEligible(registered),
                () => _ledger.Apply(registered),
                cancellationToken);
            if (!outcome.Applied)
                throw new OutboundReconciliationConflictException(
                    "Outbound mutation changed before evidence registration committed.");
        }
        catch (OutboundReconciliationConflictException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OutboundReconciliationUnavailableException(ex);
        }
        return evidence;
    }

    public OutboundOperatorResolutionResult Approve(
        OutboundMutationId mutationId,
        OutboundResolutionProposalId proposalId,
        string callerFirmId,
        string checkerRef,
        CancellationToken cancellationToken = default)
    {
        checkerRef = CanonicalizeOperatorRef(checkerRef);
        var mutation = GetScopedMutation(mutationId, callerFirmId);
        var proposal = mutation.ResolutionProposals.FirstOrDefault(
            candidate => candidate.ProposalId == proposalId)
            ?? throw new OutboundReconciliationNotFoundException();
        if (proposal.ApprovedAtUtc is not null)
        {
            var committed = mutation.OperatorEvidence.FirstOrDefault(
                evidence => evidence.ProposalId == proposalId);
            if (committed is null)
                throw new OutboundReconciliationConflictException(
                    "Approved proposal has no committed resolution.");
            return ResultFromEvidence(mutation, committed);
        }
        if (string.Equals(proposal.MakerRef, checkerRef, StringComparison.Ordinal))
            throw new OutboundReconciliationValidationException(
                "Maker and checker must be different operators.");
        var request = new OutboundOperatorResolutionRequest(
            proposal.Decision,
            proposal.EvidenceType,
            proposal.EvidenceReference,
            proposal.ReasonCode);
        ValidateRequest(mutation, request);
        var atUtc = _clock.GetUtcNow();
        var resolved = new OutboundOperatorResolvedEvent
        {
            MutationId = mutationId,
            Decision = proposal.Decision,
            EvidenceType = proposal.EvidenceType,
            EvidenceReference = proposal.EvidenceReference,
            EvidenceDigest = proposal.EvidenceDigest,
            ReasonCode = proposal.ReasonCode,
            OperatorRef = checkerRef,
            MakerRef = proposal.MakerRef,
            CheckerRef = checkerRef,
            ProposalId = proposalId,
            ReleaseCapacity = true,
            ResolvedAtUtc = atUtc,
            TimestampUtc = atUtc,
        };
        AuditFirst(
            mutation,
            checkerRef,
            "outbound_resolution_approve",
            request,
            proposal.EvidenceDigest,
            proposalId,
            cancellationToken);
        DispatchResolution(
            resolved,
            mutation.FirmId,
            releaseCapacity: true,
            cancellationToken);
        return new OutboundOperatorResolutionResult(
            mutationId,
            OutboundOperatorResolutionStatus.Resolved,
            proposalId,
            CapacityReleased: true,
            RequiresReconciliation: false);
    }

    private OutboundOperatorResolutionResult Propose(
        OutboundMutationSnapshot mutation,
        string makerRef,
        OutboundOperatorResolutionRequest request,
        string evidenceDigest,
        CancellationToken cancellationToken)
    {
        var atUtc = _clock.GetUtcNow();
        var proposalId = OutboundResolutionProposalId.New();
        var proposed = new OutboundOperatorResolutionProposedEvent
        {
            MutationId = mutation.MutationId,
            ProposalId = proposalId,
            Decision = request.Decision,
            EvidenceType = request.EvidenceType,
            EvidenceReference = request.EvidenceReference,
            EvidenceDigest = evidenceDigest,
            ReasonCode = request.ReasonCode,
            MakerRef = makerRef,
            ProposedAtUtc = atUtc,
            TimestampUtc = atUtc,
        };
        AuditFirst(
            mutation,
            makerRef,
            "outbound_resolution_propose",
            request,
            evidenceDigest,
            proposalId,
            cancellationToken);
        try
        {
            var outcome = _dispatcher.DispatchCommittedIf(
                proposed,
                () => IsProposalStillEligible(proposed, mutation.FirmId),
                () => _ledger.Apply(proposed),
                cancellationToken);
            if (!outcome.Applied)
                throw new OutboundReconciliationConflictException(
                    "Outbound mutation changed before the proposal committed.");
        }
        catch (OutboundReconciliationConflictException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OutboundReconciliationUnavailableException(ex);
        }
        MetricsRegistry.OutboundOperatorResolutions.Add(
            1,
            new("firm", mutation.FirmId),
            new("decision", ToMetricValue(request.Decision)),
            new("evidence_type", ToMetricValue(request.EvidenceType)),
            new("result", "pending_approval"));
        return new OutboundOperatorResolutionResult(
            mutation.MutationId,
            OutboundOperatorResolutionStatus.PendingApproval,
            proposalId,
            CapacityReleased: false,
            RequiresReconciliation: true);
    }

    private void DispatchResolution(
        OutboundOperatorResolvedEvent resolved,
        string firmId,
        bool releaseCapacity,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = _dispatcher.DispatchCommittedIf(
                resolved,
                () => IsResolutionStillEligible(resolved),
                () =>
                {
                    _ledger.Apply(resolved);
                    if (releaseCapacity)
                        ReleaseCapacity(resolved.MutationId);
                },
                cancellationToken);
            if (!outcome.Applied)
                throw new OutboundReconciliationConflictException(
                    "Outbound mutation changed before the resolution committed.");
        }
        catch (OutboundReconciliationConflictException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OutboundReconciliationUnavailableException(ex);
        }
        MetricsRegistry.OutboundOperatorResolutions.Add(
            1,
            new("firm", firmId),
            new("decision", ToMetricValue(resolved.Decision)),
            new("evidence_type", ToMetricValue(resolved.EvidenceType)),
            new("result", resolved.Decision == OutboundOperatorDecision.LeaveAmbiguous
                ? "annotated"
                : "resolved"));
    }

    private void ReleaseCapacity(OutboundMutationId mutationId)
    {
        if (!_ledger.TryGet(mutationId, out var mutation) || mutation is null)
            throw new InvalidOperationException("Committed outbound mutation disappeared.");
        switch (mutation.Kind)
        {
            case OutboundMutationKind.New:
                _margin.ReleaseReservation(mutation.PrimaryClOrdId);
                break;
            case OutboundMutationKind.Replace:
                _replacements.ReleaseForVenueAbsent(mutation.PrimaryClOrdId);
                _replaceMargin.AbortReplace(mutation.PrimaryClOrdId);
                break;
        }
    }

    private void AuditFirst(
        OutboundMutationSnapshot mutation,
        string operatorRef,
        string action,
        OutboundOperatorResolutionRequest request,
        string evidenceDigest,
        OutboundResolutionProposalId? proposalId,
        CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["target_firm"] = mutation.FirmId,
            ["mutation_id"] = mutation.MutationId.ToString(),
            ["action"] = action,
            ["decision"] = ToMetricValue(request.Decision),
            ["evidence_type"] = ToMetricValue(request.EvidenceType),
            ["evidence_reference"] = request.EvidenceReference,
            ["evidence_digest"] = evidenceDigest,
            ["reason_code"] = request.ReasonCode,
        };
        if (proposalId is { } id)
            details["proposal_id"] = id.ToString();
        var evt = new AuditLogEvent
        {
            EventType = AuditEventTypes.AdminOutboundResolution,
            Outcome = AuditOutcomes.Success,
            ActorUserId = operatorRef,
            ActorUsername = operatorRef,
            ActorFirm = mutation.FirmId,
            ActorRole = "admin",
            ResourcePath = $"/admin/outbound-mutations/{mutation.MutationId}/resolve",
            ReasonCode = request.ReasonCode,
            Details = details,
            TimestampUtc = _clock.GetUtcNow(),
        };
        try
        {
            _audit.LogCommittedOrFail(evt, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new OutboundReconciliationUnavailableException(ex);
        }
    }

    private void AuditEvidenceRegistration(
        OutboundMutationSnapshot mutation,
        string operatorRef,
        OutboundAuthoritativeEvidenceSnapshot evidence,
        CancellationToken cancellationToken)
    {
        var evt = new AuditLogEvent
        {
            EventType = AuditEventTypes.AdminOutboundResolution,
            Outcome = AuditOutcomes.Success,
            ActorUserId = operatorRef,
            ActorUsername = operatorRef,
            ActorFirm = mutation.FirmId,
            ActorRole = "admin",
            ResourcePath =
                $"/admin/outbound-mutations/{mutation.MutationId}/evidence",
            ReasonCode = "authoritative_evidence_registered",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["target_firm"] = mutation.FirmId,
                ["mutation_id"] = mutation.MutationId.ToString(),
                ["action"] = "outbound_evidence_register",
                ["source_type"] = ToMetricValue(evidence.SourceType),
                ["evidence_digest"] = evidence.EvidenceDigest,
                ["attestation_reference"] = evidence.AttestationReference,
                ["coverage_start_utc"] = evidence.CoverageStartUtc.ToString("O"),
                ["coverage_end_utc"] = evidence.CoverageEndUtc.ToString("O"),
            },
            TimestampUtc = _clock.GetUtcNow(),
        };
        try
        {
            _audit.LogCommittedOrFail(evt, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new OutboundReconciliationUnavailableException(ex);
        }
    }

    private OutboundMutationSnapshot GetScopedMutation(
        OutboundMutationId mutationId,
        string callerFirmId)
    {
        if (!_ledger.TryGet(mutationId, out var mutation) || mutation is null)
            throw new OutboundReconciliationNotFoundException();
        if (!string.Equals(mutation.FirmId, callerFirmId, StringComparison.Ordinal))
            throw new OutboundReconciliationForbiddenException();
        return mutation;
    }

    private void ValidateRequest(
        OutboundMutationSnapshot mutation,
        OutboundOperatorResolutionRequest request)
    {
        if (!IsOpaqueReference(request.EvidenceReference)
            || !AllowedReasonCodes.Contains(request.ReasonCode))
            throw new OutboundReconciliationValidationException(
                "Evidence reference or reason code is invalid.");
        if (!HasSafeEvidenceReferenceShape(request.EvidenceType, request.EvidenceReference))
            throw new OutboundReconciliationValidationException(
                "Evidence reference must be an allow-listed digest identifier.");
        if (LooksLikeForbiddenTimeOrSessionEvidence(request.EvidenceReference)
            || LooksLikeForbiddenTimeOrSessionEvidence(request.ReasonCode))
            throw new OutboundReconciliationValidationException(
                "Session roll and elapsed time are not resolution evidence.");
        if (request.EvidenceType == OutboundOperatorEvidenceType.ManualAnnotation)
        {
            if (request.Decision != OutboundOperatorDecision.LeaveAmbiguous)
                throw new OutboundReconciliationValidationException(
                    "Manual annotation can only leave a mutation ambiguous.");
        }
        else if (request.Decision == OutboundOperatorDecision.LeaveAmbiguous)
        {
            throw new OutboundReconciliationValidationException(
                "Leave-ambiguous requires manual annotation evidence.");
        }
        // A genuine contradictory ER reopens the state to Ambiguous and must
        // remain correctable. Payload unavailability leaves the terminal state
        // intact, so only exact duplicates (handled by Resolve) are allowed.
        if (HasCommittedTerminalResolution(mutation)
            && mutation.State is OutboundMutationState.OperatorResolved
                or OutboundMutationState.VenueAcknowledged)
            throw new OutboundReconciliationValidationException(
                "The outbound mutation already has a terminal operator resolution.");
        if (request.Decision == OutboundOperatorDecision.VenueAcknowledged
            && request.EvidenceType != OutboundOperatorEvidenceType.TerminalExecutionReport)
            throw new OutboundReconciliationValidationException(
                "Venue acknowledgment requires terminal execution report evidence.");
        if (mutation.State == OutboundMutationState.VenueAcknowledged
            && request.Decision != OutboundOperatorDecision.VenueAcknowledged)
            throw new OutboundReconciliationValidationException(
                "Venue-acknowledged mutations can only be resolved as venue acknowledged.");
        if (!_ledger.HasAuthoritativeEvidence(
                mutation.MutationId,
                request.EvidenceType,
                request.EvidenceReference))
            throw new OutboundReconciliationValidationException(
                "Authoritative evidence does not cover this mutation.");
        if (request.EvidenceType == OutboundOperatorEvidenceType.TerminalExecutionReport
            && !_ledger.IsTerminalExecutionReportDecisionCompatible(
                mutation.MutationId,
                request.EvidenceReference,
                request.Decision))
            throw new OutboundReconciliationValidationException(
                "The terminal execution report does not support the requested decision.");
        if (!mutation.RequiresReconciliation
            && !(mutation.State == OutboundMutationState.VenueAcknowledged
                && request.EvidenceType
                    == OutboundOperatorEvidenceType.TerminalExecutionReport)
            && request.Decision != OutboundOperatorDecision.LeaveAmbiguous)
            throw new OutboundReconciliationConflictException(
                "Outbound mutation does not require reconciliation.");
    }

    private static void ValidateEvidenceRegistration(
        OutboundMutationSnapshot mutation,
        OutboundAuthoritativeEvidenceRegistrationRequest request)
    {
        var validReference = request.SourceType switch
        {
            OutboundAuthoritativeEvidenceSourceType.VenueMassAction =>
                HasDigestPrefix(request.EvidenceReference, "venue-report:"),
            OutboundAuthoritativeEvidenceSourceType.OfficialExtract =>
                HasDigestPrefix(request.EvidenceReference, "official-extract:"),
            _ => false,
        };
        if (!validReference
            || !HasDigestPrefix(request.AttestationReference, "attestation:")
            || request.CoverageEndUtc < request.CoverageStartUtc
            || mutation.RecordedAtUtc < request.CoverageStartUtc
            || mutation.RecordedAtUtc > request.CoverageEndUtc)
            throw new OutboundReconciliationValidationException(
                "Authoritative evidence registration is invalid or does not cover the mutation.");
    }

    private bool IsEvidenceRegistrationStillEligible(
        OutboundAuthoritativeEvidenceRegisteredEvent registered)
    {
        if (!_ledger.TryGet(registered.MutationId, out var current)
            || current is null
            || !string.Equals(
                current.FirmId,
                registered.Evidence.FirmId,
                StringComparison.Ordinal))
            return false;
        try
        {
            ValidateEvidenceRegistration(
                current,
                new OutboundAuthoritativeEvidenceRegistrationRequest(
                    registered.Evidence.SourceType,
                    registered.Evidence.EvidenceReference,
                    registered.Evidence.CoverageStartUtc,
                    registered.Evidence.CoverageEndUtc,
                    registered.Evidence.AttestationReference));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        return !current.AuthoritativeEvidence.Any(evidence =>
            evidence.EvidenceReference == registered.Evidence.EvidenceReference);
    }

    private bool IsProposalStillEligible(
        OutboundOperatorResolutionProposedEvent proposed,
        string firmId) =>
        IsProposalStillEligibleCore(proposed, firmId);

    private bool IsProposalStillEligibleCore(
        OutboundOperatorResolutionProposedEvent proposed,
        string firmId)
    {
        if (!_ledger.TryGet(proposed.MutationId, out var current)
            || current is null
            || !string.Equals(current.FirmId, firmId, StringComparison.Ordinal)
            || current.ResolutionProposals.Any(proposal => proposal.ApprovedAtUtc is null))
            return false;
        try
        {
            ValidateRequest(
                current,
                new OutboundOperatorResolutionRequest(
                    proposed.Decision,
                    proposed.EvidenceType,
                    proposed.EvidenceReference,
                    proposed.ReasonCode));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        return !current.OperatorEvidence.Any(
            evidence => evidence.EvidenceDigest == proposed.EvidenceDigest);
    }

    private bool IsResolutionStillEligible(OutboundOperatorResolvedEvent resolved)
    {
        if (!_ledger.TryGet(resolved.MutationId, out var current) || current is null)
            return false;
        if (current.OperatorEvidence.Any(
                evidence => evidence.EvidenceDigest == resolved.EvidenceDigest))
            return false;
        if (HasCommittedTerminalResolution(current)
            && current.State is OutboundMutationState.OperatorResolved
                or OutboundMutationState.VenueAcknowledged)
            return false;
        if (resolved.ProposalId is not { } proposalId)
        {
            if (resolved.EvidenceReference is not { } evidenceReference
                || resolved.ReasonCode is not { } reasonCode)
                return false;
            var request = new OutboundOperatorResolutionRequest(
                resolved.Decision,
                resolved.EvidenceType,
                evidenceReference,
                reasonCode);
            if (DigestResolution(resolved.MutationId, request) != resolved.EvidenceDigest)
                return false;
            try
            {
                ValidateRequest(current, request);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            return true;
        }
        var proposal = current.ResolutionProposals.FirstOrDefault(
            candidate => candidate.ProposalId == proposalId);
        if (proposal is null
            || proposal.ApprovedAtUtc is not null
            || !string.Equals(proposal.MakerRef, resolved.MakerRef, StringComparison.Ordinal)
            || string.Equals(proposal.MakerRef, resolved.CheckerRef, StringComparison.Ordinal))
            return false;
        try
        {
            ValidateRequest(
                current,
                new OutboundOperatorResolutionRequest(
                    proposal.Decision,
                    proposal.EvidenceType,
                    proposal.EvidenceReference,
                    proposal.ReasonCode));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        return proposal.Decision == resolved.Decision
            && proposal.EvidenceType == resolved.EvidenceType
            && proposal.EvidenceReference == resolved.EvidenceReference
            && proposal.EvidenceDigest == resolved.EvidenceDigest
            && proposal.ReasonCode == resolved.ReasonCode
            && (current.RequiresReconciliation
                || (current.State == OutboundMutationState.VenueAcknowledged
                    && proposal.EvidenceType
                        == OutboundOperatorEvidenceType.TerminalExecutionReport));
    }

    private static bool ReleasesCapacity(
        OutboundMutationSnapshot mutation,
        OutboundOperatorDecision decision) =>
        decision == OutboundOperatorDecision.VenueAbsent
        && mutation.Kind is OutboundMutationKind.New or OutboundMutationKind.Replace
        && mutation.State != OutboundMutationState.ProvenUnsent;

    private static bool HasCommittedTerminalResolution(
        OutboundMutationSnapshot mutation) =>
        mutation.OperatorEvidence.Any(
            evidence => evidence.Decision != OutboundOperatorDecision.LeaveAmbiguous);

    private static OutboundOperatorResolutionResult ResultFromEvidence(
        OutboundMutationSnapshot mutation,
        OutboundOperatorEvidenceSnapshot evidence) =>
        new(
            mutation.MutationId,
            evidence.Decision == OutboundOperatorDecision.LeaveAmbiguous
                ? OutboundOperatorResolutionStatus.Annotated
                : OutboundOperatorResolutionStatus.Resolved,
            evidence.ProposalId,
            evidence.CapacityReleased,
            evidence.Decision == OutboundOperatorDecision.LeaveAmbiguous
                || mutation.RequiresReconciliation);

    private static string DigestResolution(
        OutboundMutationId mutationId,
        OutboundOperatorResolutionRequest request)
    {
        var canonical = string.Join(
            "|",
            mutationId,
            request.Decision,
            request.EvidenceType,
            request.EvidenceReference,
            request.ReasonCode);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static bool IsOpaqueReference(string? value) =>
        value is { Length: > 0 and <= 128 }
        && value.All(static character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or ':');

    private static bool LooksLikeForbiddenTimeOrSessionEvidence(string value)
    {
        var normalized = value.Replace('-', '_').Replace('.', '_');
        return normalized.Contains("session_roll", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("session_rolled", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("elapsed_time", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ttl", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSafeEvidenceReferenceShape(
        OutboundOperatorEvidenceType evidenceType,
        string evidenceReference) =>
        evidenceType switch
        {
            OutboundOperatorEvidenceType.TerminalExecutionReport
                or OutboundOperatorEvidenceType.ContractedNotApplied =>
                IsLowerHexDigest(evidenceReference),
            OutboundOperatorEvidenceType.VenueMassAction =>
                HasDigestPrefix(evidenceReference, "venue-report:"),
            OutboundOperatorEvidenceType.OfficialExtract =>
                HasDigestPrefix(evidenceReference, "official-extract:"),
            OutboundOperatorEvidenceType.ManualAnnotation =>
                HasDigestPrefix(evidenceReference, "annotation:"),
            _ => false,
        };

    private static bool HasDigestPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal)
        && IsLowerHexDigest(value[prefix.Length..]);

    private static bool IsLowerHexDigest(string value) =>
        value.Length == 64
        && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string CanonicalizeOperatorRef(string operatorRef)
    {
        if (IsOpaqueReference(operatorRef))
            return operatorRef;
        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(operatorRef ?? string.Empty));
        return $"operator:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static string ToMetricValue<T>(T value) where T : struct, Enum =>
        value.ToString().ToLowerInvariant();
}
