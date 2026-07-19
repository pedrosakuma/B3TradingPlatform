using System.Text.Json.Serialization;

namespace B3.Trading.Application.Outbound;

public readonly record struct OutboundMutationId(Guid Value)
{
    public static OutboundMutationId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct OutboundAttemptId(Guid Value)
{
    public static OutboundAttemptId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct ProcessEpochId(Guid Value)
{
    public static ProcessEpochId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public sealed class OutboundProcessEpoch
{
    public ProcessEpochId Id { get; }

    public OutboundProcessEpoch() : this(ProcessEpochId.New()) { }

    public OutboundProcessEpoch(ProcessEpochId id)
    {
        if (id.Value == Guid.Empty)
            throw new ArgumentException("Process epoch is required.", nameof(id));
        Id = id;
    }
}

public enum OutboundMutationKind
{
    New,
    Cancel,
    Replace,
}

public enum OutboundMutationOrigin
{
    Rest,
    UserBotFixp,
    Algo,
    Scheduler,
    Operator,
    Legacy,
}

public enum OutboundMutationState
{
    ApprovedToSend,
    AttemptIntentPrepared,
    FramePrepared,
    TransportWriteCompleted,
    ProvenUnsent,
    Ambiguous,
    VenueAcknowledged,
    OperatorResolved,
    LegacyUnknown,
    LegacyUnknownCancel,
    LegacyUnknownReplace,
    LegacyTerminal,
}

public enum OutboundSensitiveFieldRef
{
    Account,
    InvestorId,
    EndClientId,
    CustomerIdentifier,
    TradingSubAccount,
}

public enum OutboundProvenUnsentEvidence
{
    TypedPreFrameFailure,
    RetryProjectionNotPrepared,
    DeadEpochIntentWithoutFrame,
    LegacyWave1CancelPreSend,
    LegacyWave1ReplacePreSend,
}

public enum OutboundAmbiguityReason
{
    DeadEpochFramePrepared,
    DeadEpochTransportWriteCompleted,
    LegacyUnknown,
    LegacyWave1ReplaceAmbiguous,
    MissingHistoricalEncryptionKey,
    UndecryptableCommandEnvelope,
    UnsupportedCommandVersion,
    ConflictingVenueEvidence,
    NotAppliedEvidence,
    IncompleteVenueEvidence,
    GatewayOutcomeUnknown,
}

public enum InboundVenueEvidenceKind
{
    ExecutionReport,
    BusinessReject,
    NotApplied,
}

public enum InboundVenueEvidenceDisposition
{
    Matched,
    Unmatched,
    Conflicting,
}

public enum InboundVenueEvidenceApplyStatus
{
    RecordedMatched,
    RecordedUnmatched,
    RecordedConflicting,
    Duplicate,
}

public readonly record struct InboundVenueEvidenceApplyResult(
    InboundVenueEvidenceApplyStatus Status)
{
    public bool ShouldApplyDomain =>
        Status is InboundVenueEvidenceApplyStatus.RecordedMatched
            or InboundVenueEvidenceApplyStatus.RecordedUnmatched;
}

public enum OutboundOperatorDecision
{
    VenueAcknowledged,
    VenueAbsent,
    LeaveAmbiguous,
}

public enum OutboundOperatorEvidenceType
{
    TerminalExecutionReport,
    ContractedNotApplied,
    VenueMassAction,
    OfficialExtract,
    ManualAnnotation,
}

public enum OutboundSensitivePayloadAvailability
{
    Available,
    MissingHistoricalKey,
    AuthenticationFailed,
    UnsupportedVersion,
}

public sealed record OutboundCanonicalCommand
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public required ulong ClOrdId { get; init; }
    public ulong? OriginalClOrdId { get; init; }
    public required ulong SecurityId { get; init; }
    public required string Symbol { get; init; }
    public required string Side { get; init; }
    public required string OrderType { get; init; }
    public required long Quantity { get; init; }
    public decimal? Price { get; init; }
    public string TimeInForce { get; init; } = "Day";
    public decimal? StopPrice { get; init; }
    public DateTimeOffset? GoodTillDate { get; init; }
    public long? MinQty { get; init; }
    public long? MaxFloor { get; init; }
    public string? SelfTradePreventionInstruction { get; init; }
    public string? RoutingInstruction { get; init; }
}

public sealed class SensitiveOutboundCommand
{
    public string? Account { get; init; }
    public string? InvestorId { get; init; }
    public string? InvestorIdPrefix { get; init; }
    public string? InvestorIdDocument { get; init; }
    public required string EndClientId { get; init; }
    public string? CustomerIdentifier { get; init; }
    public string? TradingSubAccount { get; init; }

    public override string ToString() => "[REDACTED sensitive outbound command]";
}

public sealed record OutboundNewOrderCommand(
    OutboundMutationId MutationId,
    string FirmId,
    OutboundCanonicalCommand Canonical,
    SensitiveOutboundCommand Sensitive);

public sealed record OutboundCancelCommand(
    OutboundMutationId MutationId,
    string FirmId,
    OutboundCanonicalCommand Canonical,
    SensitiveOutboundCommand Sensitive);

public sealed record OutboundReplaceCommand(
    OutboundMutationId MutationId,
    string FirmId,
    OutboundCanonicalCommand Canonical,
    SensitiveOutboundCommand Sensitive);

public sealed class EncryptedOutboundCommandEnvelope
{
    public const int CurrentAlgorithmVersion = 1;

    public required string KeyId { get; init; }
    public required int KeyVersion { get; init; }
    public int AlgorithmVersion { get; init; } = CurrentAlgorithmVersion;
    public required string NonceBase64 { get; init; }
    public required string CiphertextBase64 { get; init; }
    public required string AuthenticationTagBase64 { get; init; }

    public override string ToString() => "[Encrypted outbound command envelope]";
}

public sealed record OutboundApprovalSnapshot
{
    public required int ApprovalVersion { get; init; }
    public required DateTimeOffset ApprovedAtUtc { get; init; }
    public string? RiskDecisionRef { get; init; }
    public string? RiskPolicyVersion { get; init; }
    public string? MarginReservationRef { get; init; }
    public decimal? MarginAmount { get; init; }
    public string? MarginBasis { get; init; }
    public required OutboundCanonicalCommand CanonicalCommandNonSensitive { get; init; }
    public IReadOnlyList<OutboundSensitiveFieldRef> SensitiveFieldRefs { get; init; } =
        Array.Empty<OutboundSensitiveFieldRef>();
    public required EncryptedOutboundCommandEnvelope SensitiveCommandEnvelope { get; init; }
    public required string StoredCommandIntegritySha256 { get; init; }
}

public sealed record OutboundFramePreparedSnapshot
{
    public required ulong SessionId { get; init; }
    public required uint SessionVerId { get; init; }
    public required ulong OutboundSeqNum { get; init; }
    public required string EncodedFrameSha256 { get; init; }
    public required DateTimeOffset PreparedAtUtc { get; init; }
}

public sealed record OutboundAttemptSnapshot
{
    public required OutboundAttemptId AttemptId { get; init; }
    public required int AttemptNo { get; init; }
    public required ulong ClOrdId { get; init; }
    public required ProcessEpochId ProcessEpochId { get; init; }
    public required DateTimeOffset IntentPreparedAtUtc { get; init; }
    public OutboundFramePreparedSnapshot? FramePrepared { get; init; }
    public DateTimeOffset? TransportWriteCompletedAtUtc { get; init; }
    public int? GatewayReceiptVersion { get; init; }
    public OutboundProvenUnsentEvidence? ProvenUnsentEvidence { get; init; }
    public OutboundAmbiguityReason? AmbiguityReason { get; init; }
}

public sealed record OutboundResolutionSnapshot
{
    public required OutboundMutationState State { get; init; }
    public required DateTimeOffset ResolvedAtUtc { get; init; }
    public string? EvidenceKind { get; init; }
    public string? EvidenceDigest { get; init; }
    public ulong? VenueOrderId { get; init; }
}

public sealed record OutboundOperatorEvidenceSnapshot
{
    public required OutboundOperatorDecision Decision { get; init; }
    public required OutboundOperatorEvidenceType EvidenceType { get; init; }
    public required string EvidenceDigest { get; init; }
    public required string OperatorRef { get; init; }
    public required DateTimeOffset RecordedAtUtc { get; init; }
}

public sealed record OutboundLegacyEvidenceSnapshot
{
    public required string EvidenceKind { get; init; }
    public required string EvidenceDigest { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
}

public sealed record OutboundMutationSnapshot
{
    public required OutboundMutationId MutationId { get; init; }
    public required OutboundMutationKind Kind { get; init; }
    public required string FirmId { get; init; }
    public required string EndClientRef { get; init; }
    public required OutboundMutationOrigin Origin { get; init; }
    public required ulong PrimaryClOrdId { get; init; }
    public ulong? OriginalClOrdId { get; init; }
    public required DateTimeOffset RecordedAtUtc { get; init; }
    public OutboundApprovalSnapshot? Approval { get; init; }
    public IReadOnlyList<OutboundAttemptSnapshot> Attempts { get; init; } =
        Array.Empty<OutboundAttemptSnapshot>();
    public required OutboundMutationState State { get; init; }
    public required DateTimeOffset StateChangedAtUtc { get; init; }
    public OutboundResolutionSnapshot? Resolution { get; init; }
    public IReadOnlyList<OutboundOperatorEvidenceSnapshot> OperatorEvidence { get; init; } =
        Array.Empty<OutboundOperatorEvidenceSnapshot>();
    public IReadOnlyList<OutboundLegacyEvidenceSnapshot> LegacyEvidence { get; init; } =
        Array.Empty<OutboundLegacyEvidenceSnapshot>();
    public OutboundSensitivePayloadAvailability SensitivePayloadAvailability { get; init; } =
        OutboundSensitivePayloadAvailability.Available;
    public bool RequiresReconciliation { get; init; }
}

public sealed record OutboundCorrelationTombstone
{
    public required ulong ClOrdId { get; init; }
    public required OutboundMutationId MutationId { get; init; }
    public required OutboundMutationKind Kind { get; init; }
    public required bool Terminal { get; init; }
    public required DateTimeOffset RetainFromUtc { get; init; }
}

public sealed record InboundVenueEvidenceSnapshot
{
    public required string EvidenceId { get; init; }
    public required InboundVenueEvidenceKind Kind { get; init; }
    public required InboundVenueEvidenceDisposition Disposition { get; init; }
    public required string FirmId { get; init; }
    public ulong? SessionId { get; init; }
    public uint? SessionVerId { get; init; }
    public ulong? InboundSeqNum { get; init; }
    public DateTimeOffset? SendingTime { get; init; }
    public bool PossibleResend { get; init; }
    public string? MessageKind { get; init; }
    public ulong? ClOrdId { get; init; }
    public ulong? OrigClOrdId { get; init; }
    public ulong? VenueOrderId { get; init; }
    public ulong? BusinessRejectRefSeqNum { get; init; }
    public ulong? NotAppliedFromSeqNo { get; init; }
    public uint? NotAppliedCount { get; init; }
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public IReadOnlyList<OutboundMutationId> MatchedMutationIds { get; init; } =
        Array.Empty<OutboundMutationId>();
}

public sealed record InboundVenueEvidenceDiagnostic(
    string EvidenceId,
    InboundVenueEvidenceKind Kind,
    InboundVenueEvidenceDisposition Disposition,
    string FirmId,
    ulong? SessionId,
    uint? SessionVerId,
    ulong? InboundSeqNum,
    ulong? BusinessRejectRefSeqNum,
    ulong? NotAppliedFromSeqNo,
    uint? NotAppliedCount,
    DateTimeOffset ObservedAtUtc,
    int MatchedMutationCount);

public sealed record OutboundMutationDiagnostic(
    OutboundMutationId MutationId,
    string FirmId,
    OutboundMutationKind Kind,
    OutboundMutationState State,
    DateTimeOffset StateChangedAtUtc,
    int AttemptCount,
    bool RequiresReconciliation,
    string? EncryptionKeyId,
    int? EncryptionKeyVersion);

public sealed record OutboundMutationMetricDimensions(
    string FirmId,
    OutboundMutationKind Kind,
    OutboundMutationState State,
    OutboundMutationOrigin Origin);

public sealed class OutboundLedgerRecoveryException : InvalidOperationException
{
    public OutboundLedgerRecoveryException(string message) : base(message) { }
}

public sealed class OutboundCommandEnvelopeException : InvalidOperationException
{
    public OutboundSensitivePayloadAvailability Availability { get; }

    public OutboundCommandEnvelopeException(
        OutboundSensitivePayloadAvailability availability,
        string message)
        : base(message)
    {
        Availability = availability;
    }
}
