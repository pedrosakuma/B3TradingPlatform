using System.Security.Claims;
using B3.Trading.Api.Auth;
using B3.Trading.Application.Outbound;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace B3.Trading.Api;

public sealed record AdminOutboundResolutionRequest(
    string? Decision,
    string? EvidenceType,
    string? EvidenceReference,
    string? Reason);

public sealed record AdminOutboundEvidenceRegistrationRequest(
    string? SourceType,
    string? EvidenceReference,
    DateTimeOffset? CoverageStartUtc,
    DateTimeOffset? CoverageEndUtc,
    string? AttestationReference);

public static class AdminOutboundMutationEndpoints
{
    public static IEndpointRouteBuilder MapAdminOutboundMutations(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/outbound-mutations")
            .RequireAuthorization("admin");

        group.MapGet("/", (
            HttpContext context,
            OutboundMutationLedger ledger,
            string? firmId,
            string? state,
            bool? requiresReconciliation) =>
        {
            var callerFirm = ResolveCallerFirm(context);
            if (!string.IsNullOrWhiteSpace(firmId)
                && !string.Equals(firmId, callerFirm, StringComparison.Ordinal))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            OutboundMutationState? stateFilter = null;
            if (!string.IsNullOrWhiteSpace(state))
            {
                if (!Enum.TryParse<OutboundMutationState>(
                        state,
                        ignoreCase: true,
                        out var parsedState))
                    return Error(StatusCodes.Status400BadRequest, "invalid_state");
                stateFilter = parsedState;
            }
            var mutations = ledger.SnapshotMutations()
                .Where(mutation =>
                    string.Equals(mutation.FirmId, callerFirm, StringComparison.Ordinal))
                .Where(mutation => stateFilter is null || mutation.State == stateFilter)
                .Where(mutation => requiresReconciliation is null
                    || mutation.RequiresReconciliation == requiresReconciliation)
                .OrderBy(mutation => mutation.RecordedAtUtc)
                .Select(ProjectSummary)
                .ToArray();
            return Results.Ok(new
            {
                firmId = callerFirm,
                mutations,
            });
        });

        group.MapGet("/{mutationId:guid}", (
            HttpContext context,
            Guid mutationId,
            OutboundMutationLedger ledger) =>
        {
            var id = new OutboundMutationId(mutationId);
            if (!ledger.TryGet(id, out var mutation) || mutation is null)
                return Error(StatusCodes.Status404NotFound, "mutation_not_found");
            if (!string.Equals(
                    mutation.FirmId,
                    ResolveCallerFirm(context),
                    StringComparison.Ordinal))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(ProjectDetail(
                mutation,
                ledger.GetInboundEvidenceForMutation(id)));
        });

        group.MapPost("/{mutationId:guid}/evidence", (
            HttpContext context,
            Guid mutationId,
            AdminOutboundEvidenceRegistrationRequest request,
            OutboundReconciliationService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseEvidenceRegistration(request, out var parsed))
                return Error(
                    StatusCodes.Status400BadRequest,
                    "invalid_evidence_registration");
            try
            {
                var evidence = service.RegisterAuthoritativeEvidence(
                    new OutboundMutationId(mutationId),
                    ResolveCallerFirm(context),
                    ResolveOperator(context),
                    parsed!,
                    cancellationToken);
                return Results.Ok(ProjectAuthoritativeEvidence(evidence));
            }
            catch (Exception exception)
            {
                return MapException(exception);
            }
        });

        group.MapPost("/{mutationId:guid}/resolve", (
            HttpContext context,
            Guid mutationId,
            AdminOutboundResolutionRequest request,
            OutboundReconciliationService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseRequest(request, out var parsed))
                return Error(StatusCodes.Status400BadRequest, "invalid_resolution_request");
            try
            {
                var result = service.Resolve(
                    new OutboundMutationId(mutationId),
                    ResolveCallerFirm(context),
                    ResolveOperator(context),
                    parsed!,
                    cancellationToken);
                return result.Status == OutboundOperatorResolutionStatus.PendingApproval
                    ? Results.Accepted(
                        $"/admin/outbound-mutations/{mutationId:D}",
                        ProjectResolutionResult(result))
                    : Results.Ok(ProjectResolutionResult(result));
            }
            catch (Exception exception)
            {
                return MapException(exception);
            }
        });

        group.MapPost("/{mutationId:guid}/resolve/{proposalId:guid}/approve", (
            HttpContext context,
            Guid mutationId,
            Guid proposalId,
            OutboundReconciliationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = service.Approve(
                    new OutboundMutationId(mutationId),
                    new OutboundResolutionProposalId(proposalId),
                    ResolveCallerFirm(context),
                    ResolveOperator(context),
                    cancellationToken);
                return Results.Ok(ProjectResolutionResult(result));
            }
            catch (Exception exception)
            {
                return MapException(exception);
            }
        });

        return app;
    }

    private static object ProjectSummary(OutboundMutationSnapshot mutation) => new
    {
        mutationId = mutation.MutationId.ToString(),
        mutation.FirmId,
        kind = ToWire(mutation.Kind),
        state = ToWire(mutation.State),
        origin = ToWire(mutation.Origin),
        mutation.PrimaryClOrdId,
        mutation.OriginalClOrdId,
        mutation.RecordedAtUtc,
        mutation.StateChangedAtUtc,
        mutation.RequiresReconciliation,
        ambiguityReason = mutation.Attempts.LastOrDefault()?.AmbiguityReason is { } reason
            ? ToWire(reason)
            : null,
        encryptedFieldReferences = ProjectEncryptedFieldReferences(mutation),
        command = mutation.Approval is null
            ? null
            : ProjectCanonicalCommand(mutation.Approval.CanonicalCommandNonSensitive),
        pendingApproval = mutation.ResolutionProposals.Any(
            proposal => proposal.ApprovedAtUtc is null),
    };

    private static object ProjectDetail(
        OutboundMutationSnapshot mutation,
        IReadOnlyList<InboundVenueEvidenceSnapshot> inboundEvidence) => new
        {
            mutation = ProjectSummary(mutation),
            attempts = mutation.Attempts.Select(attempt => new
            {
                attemptId = attempt.AttemptId.ToString(),
                attempt.AttemptNo,
                attempt.ClOrdId,
                processEpochId = attempt.ProcessEpochId.ToString(),
                attempt.IntentPreparedAtUtc,
                framePrepared = attempt.FramePrepared is null
                    ? null
                    : new
                    {
                        attempt.FramePrepared.SessionId,
                        attempt.FramePrepared.SessionVerId,
                        attempt.FramePrepared.OutboundSeqNum,
                        attempt.FramePrepared.EncodedFrameSha256,
                        attempt.FramePrepared.PreparedAtUtc,
                    },
                attempt.TransportWriteCompletedAtUtc,
                attempt.GatewayReceiptVersion,
                provenUnsentEvidence = attempt.ProvenUnsentEvidence is { } unsent
                    ? ToWire(unsent)
                    : null,
                ambiguityReason = attempt.AmbiguityReason is { } ambiguity
                    ? ToWire(ambiguity)
                    : null,
            }),
            inboundEvidence = inboundEvidence.Select(evidence => new
            {
                evidence.EvidenceId,
                kind = ToWire(evidence.Kind),
                disposition = ToWire(evidence.Disposition),
                evidence.SessionId,
                evidence.SessionVerId,
                evidence.InboundSeqNum,
                evidence.SendingTime,
                evidence.PossibleResend,
                evidence.AuthoritativeTerminalContradiction,
                evidence.MessageKind,
                evidence.ClOrdId,
                evidence.OrigClOrdId,
                evidence.VenueOrderId,
                evidence.BusinessRejectRefSeqNum,
                evidence.NotAppliedFromSeqNo,
                evidence.NotAppliedCount,
                evidence.ObservedAtUtc,
            }),
            proposals = mutation.ResolutionProposals.Select(proposal => new
            {
                proposalId = proposal.ProposalId.ToString(),
                decision = ToWire(proposal.Decision),
                evidenceType = ToWire(proposal.EvidenceType),
                proposal.EvidenceReference,
                proposal.EvidenceDigest,
                proposal.ReasonCode,
                proposal.MakerRef,
                proposal.ProposedAtUtc,
                proposal.CheckerRef,
                proposal.ApprovedAtUtc,
            }),
            authoritativeEvidence = mutation.AuthoritativeEvidence.Select(
                ProjectAuthoritativeEvidence),
            operatorEvidence = mutation.OperatorEvidence.Select(evidence => new
            {
                decision = ToWire(evidence.Decision),
                evidenceType = ToWire(evidence.EvidenceType),
                evidence.EvidenceReference,
                evidence.EvidenceDigest,
                evidence.ReasonCode,
                evidence.OperatorRef,
                evidence.MakerRef,
                evidence.CheckerRef,
                proposalId = evidence.ProposalId?.ToString(),
                evidence.CapacityReleased,
                evidence.RecordedAtUtc,
            }),
            legacyEvidence = mutation.LegacyEvidence.Select(evidence => new
            {
                evidence.EvidenceKind,
                evidence.EvidenceDigest,
                evidence.ObservedAtUtc,
            }),
            resolution = mutation.Resolution is null
                ? null
                : new
                {
                    state = ToWire(mutation.Resolution.State),
                    mutation.Resolution.ResolvedAtUtc,
                    mutation.Resolution.EvidenceKind,
                    mutation.Resolution.EvidenceDigest,
                    mutation.Resolution.VenueOrderId,
                },
        };

    private static object ProjectAuthoritativeEvidence(
        OutboundAuthoritativeEvidenceSnapshot evidence) => new
        {
            evidence.EvidenceReference,
            evidence.EvidenceDigest,
            evidence.FirmId,
            sourceType = ToWire(evidence.SourceType),
            evidence.CoverageStartUtc,
            evidence.CoverageEndUtc,
            coveredMutationIds = evidence.CoveredMutationIds
                .Select(id => id.ToString())
                .ToArray(),
            evidence.AttestationReference,
            evidence.AttestedBy,
            evidence.AttestedAtUtc,
            evidence.RegisteredAtUtc,
        };

    private static object ProjectEncryptedFieldReferences(
        OutboundMutationSnapshot mutation) => new
        {
            endClient = mutation.EndClientRef,
            fields = mutation.Approval?.SensitiveFieldRefs
                .Select(ToWire)
                .ToArray()
                ?? Array.Empty<string>(),
            keyId = mutation.Approval?.SensitiveCommandEnvelope.KeyId,
            keyVersion = mutation.Approval?.SensitiveCommandEnvelope.KeyVersion,
        };

    private static object ProjectCanonicalCommand(OutboundCanonicalCommand command) => new
    {
        command.Version,
        command.ClOrdId,
        command.OriginalClOrdId,
        command.SecurityId,
        command.Symbol,
        command.Side,
        command.OrderType,
        command.Quantity,
        command.Price,
        command.TimeInForce,
        command.StopPrice,
        command.GoodTillDate,
        command.MinQty,
        command.MaxFloor,
        command.SelfTradePreventionInstruction,
        command.RoutingInstruction,
    };

    private static object ProjectResolutionResult(
        OutboundOperatorResolutionResult result) => new
        {
            mutationId = result.MutationId.ToString(),
            status = ToWire(result.Status),
            proposalId = result.ProposalId?.ToString(),
            result.CapacityReleased,
            result.RequiresReconciliation,
        };

    private static bool TryParseRequest(
        AdminOutboundResolutionRequest request,
        out OutboundOperatorResolutionRequest? parsed)
    {
        parsed = null;
        if (!TryParseDecision(request.Decision, out var decision)
            || !TryParseEvidenceType(request.EvidenceType, out var evidenceType)
            || string.IsNullOrWhiteSpace(request.EvidenceReference)
            || string.IsNullOrWhiteSpace(request.Reason))
            return false;
        parsed = new OutboundOperatorResolutionRequest(
            decision,
            evidenceType,
            request.EvidenceReference,
            request.Reason);
        return true;
    }

    private static bool TryParseEvidenceRegistration(
        AdminOutboundEvidenceRegistrationRequest request,
        out OutboundAuthoritativeEvidenceRegistrationRequest? parsed)
    {
        parsed = null;
        var sourceType = request.SourceType switch
        {
            "venue_mass_action" =>
                OutboundAuthoritativeEvidenceSourceType.VenueMassAction,
            "official_extract" =>
                OutboundAuthoritativeEvidenceSourceType.OfficialExtract,
            _ => (OutboundAuthoritativeEvidenceSourceType?)null,
        };
        if (sourceType is null
            || string.IsNullOrWhiteSpace(request.EvidenceReference)
            || request.CoverageStartUtc is null
            || request.CoverageEndUtc is null
            || string.IsNullOrWhiteSpace(request.AttestationReference))
            return false;
        parsed = new OutboundAuthoritativeEvidenceRegistrationRequest(
            sourceType.Value,
            request.EvidenceReference,
            request.CoverageStartUtc.Value,
            request.CoverageEndUtc.Value,
            request.AttestationReference);
        return true;
    }

    private static bool TryParseDecision(
        string? value,
        out OutboundOperatorDecision decision)
    {
        decision = value switch
        {
            "venue_acknowledged" => OutboundOperatorDecision.VenueAcknowledged,
            "venue_absent" => OutboundOperatorDecision.VenueAbsent,
            "leave_ambiguous" => OutboundOperatorDecision.LeaveAmbiguous,
            _ => default,
        };
        return value is "venue_acknowledged" or "venue_absent" or "leave_ambiguous";
    }

    private static bool TryParseEvidenceType(
        string? value,
        out OutboundOperatorEvidenceType evidenceType)
    {
        evidenceType = value switch
        {
            "terminal_er" => OutboundOperatorEvidenceType.TerminalExecutionReport,
            "contracted_not_applied" => OutboundOperatorEvidenceType.ContractedNotApplied,
            "venue_mass_action" => OutboundOperatorEvidenceType.VenueMassAction,
            "official_extract" => OutboundOperatorEvidenceType.OfficialExtract,
            "manual_annotation" => OutboundOperatorEvidenceType.ManualAnnotation,
            _ => default,
        };
        return value is "terminal_er"
            or "contracted_not_applied"
            or "venue_mass_action"
            or "official_extract"
            or "manual_annotation";
    }

    private static IResult MapException(Exception exception) => exception switch
    {
        OutboundReconciliationValidationException =>
            Error(StatusCodes.Status422UnprocessableEntity, "invalid_resolution_evidence"),
        OutboundReconciliationNotFoundException =>
            Error(StatusCodes.Status404NotFound, "mutation_not_found"),
        OutboundReconciliationForbiddenException =>
            Error(StatusCodes.Status403Forbidden, "firm_scope_forbidden"),
        OutboundReconciliationConflictException =>
            Error(StatusCodes.Status409Conflict, "resolution_conflict"),
        OutboundReconciliationUnavailableException =>
            Error(StatusCodes.Status503ServiceUnavailable, "reconciliation_unavailable"),
        _ => Error(StatusCodes.Status503ServiceUnavailable, "reconciliation_unavailable"),
    };

    private static string ResolveCallerFirm(HttpContext context) =>
        context.User.FindFirstValue(JwtIssuer.FirmClaim) ?? "default";

    private static string ResolveOperator(HttpContext context) =>
        context.User.FindFirstValue("sub") ?? "unknown-operator";

    private static string ToWire<T>(T value) where T : struct, Enum =>
        string.Concat(value.ToString().Select((character, index) =>
            index > 0 && char.IsUpper(character)
                ? $"_{char.ToLowerInvariant(character)}"
                : char.ToLowerInvariant(character).ToString()));

    private static IResult Error(int statusCode, string code) =>
        Results.Json(new { error = code }, statusCode: statusCode);
}
