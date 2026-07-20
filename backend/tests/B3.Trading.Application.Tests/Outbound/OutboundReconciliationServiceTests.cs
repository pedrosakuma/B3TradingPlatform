using B3.Trading.Application.Audit;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure.Persistence;
using System.Text.Json;

namespace B3.Trading.Application.Tests.Outbound;

public sealed class OutboundReconciliationServiceTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(OutboundOperatorEvidenceType.TerminalExecutionReport)]
    [InlineData(OutboundOperatorEvidenceType.ContractedNotApplied)]
    [InlineData(OutboundOperatorEvidenceType.VenueMassAction)]
    [InlineData(OutboundOperatorEvidenceType.OfficialExtract)]
    public void AuthoritativeEvidence_ReleasesOnlyAfterDistinctChecker(
        OutboundOperatorEvidenceType evidenceType)
    {
        var fixture = Fixture.Create();
        var reference = fixture.PrepareEvidence(evidenceType);

        var proposed = fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "maker",
            new(
                OutboundOperatorDecision.VenueAbsent,
                evidenceType,
                reference,
                ReasonFor(evidenceType)));

        Assert.Equal(OutboundOperatorResolutionStatus.PendingApproval, proposed.Status);
        Assert.Equal(0, fixture.Margin.ReleaseCount);
        Assert.Throws<OutboundReconciliationValidationException>(() =>
            fixture.Service.Approve(
                fixture.MutationId,
                proposed.ProposalId!.Value,
                "F1",
                "maker"));

        var approved = fixture.Service.Approve(
            fixture.MutationId,
            proposed.ProposalId!.Value,
            "F1",
            "checker");

        Assert.Equal(OutboundOperatorResolutionStatus.Resolved, approved.Status);
        Assert.True(approved.CapacityReleased);
        Assert.Equal(1, fixture.Margin.ReleaseCount);
        Assert.True(fixture.Ledger.TryGet(fixture.MutationId, out var mutation));
        Assert.False(mutation!.RequiresReconciliation);
        Assert.Equal("maker", Assert.Single(mutation.ResolutionProposals).MakerRef);
        Assert.Equal("checker", Assert.Single(mutation.OperatorEvidence).CheckerRef);
    }

    [Fact]
    public void ManualAnnotation_NeverReleasesCapacity()
    {
        var fixture = Fixture.Create();
        var result = fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "maker",
            new(
                OutboundOperatorDecision.LeaveAmbiguous,
                OutboundOperatorEvidenceType.ManualAnnotation,
                $"annotation:{new string('a', 64)}",
                "manual_comparison_recorded"));

        Assert.Equal(OutboundOperatorResolutionStatus.Annotated, result.Status);
        Assert.Equal(0, fixture.Margin.ReleaseCount);
        Assert.True(fixture.Ledger.TryGet(fixture.MutationId, out var mutation));
        Assert.True(mutation!.RequiresReconciliation);
        Assert.Equal(OutboundMutationState.Ambiguous, mutation.State);
    }

    [Theory]
    [InlineData("session_roll")]
    [InlineData("elapsed_time")]
    [InlineData("ttl")]
    public void TimeOrSessionEvidence_IsRejectedWithoutStateChange(string reason)
    {
        var fixture = Fixture.Create();
        Assert.Throws<OutboundReconciliationValidationException>(() =>
            fixture.Service.Resolve(
                fixture.MutationId,
                "F1",
                "maker",
                new(
                    OutboundOperatorDecision.LeaveAmbiguous,
                    OutboundOperatorEvidenceType.ManualAnnotation,
                    $"annotation:{new string('a', 64)}",
                    reason)));

        Assert.Empty(fixture.Ledger.SnapshotMutations().Single().OperatorEvidence);
        Assert.Equal(0, fixture.Margin.ReleaseCount);
    }

    [Fact]
    public void AuditFailure_DoesNotWriteProposalOrReleaseCapacity()
    {
        var fixture = Fixture.Create(new ThrowingAuditLogger());
        var evidenceReference = fixture.SeedRegisteredEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            'b');

        Assert.Throws<OutboundReconciliationUnavailableException>(() =>
            fixture.Service.Resolve(
                fixture.MutationId,
                "F1",
                "maker",
                new(
                    OutboundOperatorDecision.VenueAbsent,
                    OutboundOperatorEvidenceType.OfficialExtract,
                    evidenceReference,
                    "official_extract_attested")));

        var mutation = fixture.Ledger.SnapshotMutations().Single();
        Assert.Empty(mutation.ResolutionProposals);
        Assert.Empty(mutation.OperatorEvidence);
        Assert.Equal(0, fixture.Margin.ReleaseCount);
    }

    [Theory]
    [InlineData(OutboundOperatorEvidenceType.VenueMassAction, '4')]
    [InlineData(OutboundOperatorEvidenceType.OfficialExtract, '5')]
    public void BareExternalEvidenceDigest_CannotAuthorizeCapacityRelease(
        OutboundOperatorEvidenceType evidenceType,
        char digestCharacter)
    {
        var fixture = Fixture.Create();
        var prefix = evidenceType == OutboundOperatorEvidenceType.VenueMassAction
            ? "venue-report:"
            : "official-extract:";

        Assert.Throws<OutboundReconciliationValidationException>(() =>
            fixture.Service.Resolve(
                fixture.MutationId,
                "F1",
                "maker",
                new(
                    OutboundOperatorDecision.VenueAbsent,
                    evidenceType,
                    $"{prefix}{new string(digestCharacter, 64)}",
                    ReasonFor(evidenceType))));
        Assert.Empty(fixture.Ledger.SnapshotMutations().Single().ResolutionProposals);
    }

    [Fact]
    public void ExternalEvidenceRegistration_MustCoverMutationTimestamp()
    {
        var fixture = Fixture.Create();

        Assert.Throws<OutboundReconciliationValidationException>(() =>
            fixture.Service.RegisterAuthoritativeEvidence(
                fixture.MutationId,
                "F1",
                "attestor",
                new(
                    OutboundAuthoritativeEvidenceSourceType.OfficialExtract,
                    $"official-extract:{new string('3', 64)}",
                    T0.AddDays(1),
                    T0.AddDays(2),
                    $"attestation:{new string('2', 64)}")));
        Assert.Empty(fixture.Ledger.SnapshotMutations().Single().AuthoritativeEvidence);
    }

    [Fact]
    public void EvidenceRegistrationAuditFailure_DoesNotPersistRecord()
    {
        var fixture = Fixture.Create(new ThrowingAuditLogger());

        Assert.Throws<OutboundReconciliationUnavailableException>(() =>
            fixture.Service.RegisterAuthoritativeEvidence(
                fixture.MutationId,
                "F1",
                "attestor",
                new(
                    OutboundAuthoritativeEvidenceSourceType.VenueMassAction,
                    $"venue-report:{new string('9', 64)}",
                    T0.AddHours(-1),
                    T0.AddHours(1),
                    $"attestation:{new string('9', 64)}")));
        Assert.Empty(fixture.Ledger.SnapshotMutations().Single().AuthoritativeEvidence);
    }

    [Fact]
    public void IncompleteTerminalExecutionReport_CannotAuthorizeCapacityRelease()
    {
        var fixture = Fixture.Create();
        var incomplete = fixture.TerminalEr() with { InboundSeqNum = null };
        fixture.Ledger.ApplyVenueAcknowledgement(incomplete);
        var evidenceReference = fixture.Ledger
            .GetInboundEvidenceForMutation(fixture.MutationId)
            .Single()
            .EvidenceId;

        Assert.Throws<OutboundReconciliationValidationException>(() =>
            fixture.Service.Resolve(
                fixture.MutationId,
                "F1",
                "maker",
                new(
                    OutboundOperatorDecision.VenueAbsent,
                    OutboundOperatorEvidenceType.TerminalExecutionReport,
                    evidenceReference,
                    "terminal_er_verified")));
        Assert.Equal(0, fixture.Margin.ReleaseCount);
    }

    [Fact]
    public void MutationChangeDuringApprovalAudit_PreventsStaleProposalCommit()
    {
        var audit = new CallbackAuditLogger();
        var fixture = Fixture.Create(audit);
        var evidenceReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            '8');
        var proposed = fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "maker",
            new(
                OutboundOperatorDecision.VenueAbsent,
                OutboundOperatorEvidenceType.OfficialExtract,
                evidenceReference,
                "official_extract_attested"));
        audit.OnCommitted = () =>
        {
            audit.OnCommitted = null;
            fixture.Ledger.ApplyVenueAcknowledgement(fixture.TerminalEr());
        };

        Assert.Throws<OutboundReconciliationConflictException>(() =>
            fixture.Service.Approve(
                fixture.MutationId,
                proposed.ProposalId!.Value,
                "F1",
                "checker"));

        Assert.Equal(0, fixture.Margin.ReleaseCount);
        var mutation = fixture.Ledger.SnapshotMutations().Single();
        Assert.DoesNotContain(
            mutation.OperatorEvidence,
            evidence => evidence.ProposalId == proposed.ProposalId);
    }

    [Fact]
    public void MutationChangeDuringProposalAudit_PreventsStaleProposalCommit()
    {
        var audit = new CallbackAuditLogger();
        var fixture = Fixture.Create(audit);
        var evidenceReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            '7');
        audit.OnCommitted = () =>
        {
            audit.OnCommitted = null;
            fixture.Ledger.ApplyVenueAcknowledgement(fixture.TerminalEr());
        };

        Assert.Throws<OutboundReconciliationConflictException>(() =>
            fixture.Service.Resolve(
                fixture.MutationId,
                "F1",
                "maker",
                new(
                    OutboundOperatorDecision.VenueAbsent,
                    OutboundOperatorEvidenceType.OfficialExtract,
                    evidenceReference,
                    "official_extract_attested")));

        Assert.Empty(fixture.Ledger.SnapshotMutations().Single().ResolutionProposals);
        Assert.Equal(0, fixture.Margin.ReleaseCount);
    }

    [Fact]
    public void NonOpaqueOperatorSubjects_AreCanonicalizedBeforePersistence()
    {
        var fixture = Fixture.Create();
        var evidenceReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            '6');
        var proposed = fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "maker@example.com",
            new(
                OutboundOperatorDecision.VenueAbsent,
                OutboundOperatorEvidenceType.OfficialExtract,
                evidenceReference,
                "official_extract_attested"));
        fixture.Service.Approve(
            fixture.MutationId,
            proposed.ProposalId!.Value,
            "F1",
            "checker@example.com");

        var mutation = fixture.Ledger.SnapshotMutations().Single();
        var proposal = Assert.Single(mutation.ResolutionProposals);
        var evidence = Assert.Single(mutation.OperatorEvidence);
        Assert.StartsWith("operator:", proposal.MakerRef, StringComparison.Ordinal);
        Assert.StartsWith("operator:", evidence.CheckerRef, StringComparison.Ordinal);
        Assert.DoesNotContain("@", proposal.MakerRef, StringComparison.Ordinal);
        Assert.DoesNotContain("@", evidence.CheckerRef!, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotRestore_PreservesPendingProposalReconciliationFlag()
    {
        var fixture = Fixture.Create();
        var terminalEvidence = fixture.PrepareEvidence(
            OutboundOperatorEvidenceType.TerminalExecutionReport);
        fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "maker",
            new(
                OutboundOperatorDecision.VenueAbsent,
                OutboundOperatorEvidenceType.TerminalExecutionReport,
                terminalEvidence,
                "terminal_er_verified"));
        var snapshot = fixture.Ledger.CaptureSnapshot();
        var restored = new OutboundMutationLedger(fixture.Protector);

        restored.Restore(
            snapshot.Mutations,
            snapshot.Correlations,
            snapshot.InboundEvidence);

        var mutation = Assert.Single(restored.SnapshotMutations());
        Assert.Equal(OutboundMutationState.VenueAcknowledged, mutation.State);
        Assert.True(mutation.RequiresReconciliation);
        Assert.Null(Assert.Single(mutation.ResolutionProposals).ApprovedAtUtc);
    }

    [Fact]
    public void LateContradictoryExecutionReport_IsRetainedAndReopensReconciliation()
    {
        var fixture = Fixture.Create();
        var evidenceReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            'c');
        var proposed = fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "maker",
            new(
                OutboundOperatorDecision.VenueAbsent,
                OutboundOperatorEvidenceType.OfficialExtract,
                evidenceReference,
                "official_extract_attested"));
        fixture.Service.Approve(
            fixture.MutationId,
            proposed.ProposalId!.Value,
            "F1",
            "checker");

        var result = fixture.Ledger.ApplyVenueAcknowledgement(fixture.TerminalEr());

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedConflicting, result.Status);
        Assert.True(result.ReopenedReconciliation);
        Assert.True(fixture.Ledger.TryGet(fixture.MutationId, out var mutation));
        Assert.Equal(OutboundMutationState.Ambiguous, mutation!.State);
        Assert.True(mutation.RequiresReconciliation);
        Assert.Contains(
            fixture.Ledger.GetInboundEvidenceForMutation(fixture.MutationId),
            evidence => evidence.Disposition == InboundVenueEvidenceDisposition.Conflicting);
    }

    [Fact]
    public void PostSessionRollContradictoryEr_ReopensAndCanResolveReopenedMutation()
    {
        var fixture = Fixture.Create();
        var evidenceReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            '1');
        var proposed = fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "maker",
            new(
                OutboundOperatorDecision.VenueAbsent,
                OutboundOperatorEvidenceType.OfficialExtract,
                evidenceReference,
                "official_extract_attested"));
        fixture.Service.Approve(
            fixture.MutationId,
            proposed.ProposalId!.Value,
            "F1",
            "checker");
        var postRollEr = fixture.TerminalEr() with
        {
            SessionVerId = 3,
            InboundSeqNum = 91,
            TimestampUtc = T0.AddMinutes(3),
        };

        var result = fixture.Ledger.ApplyVenueAcknowledgement(postRollEr);
        var lateEvidence = Assert.Single(
            fixture.Ledger.GetInboundEvidenceForMutation(fixture.MutationId));

        Assert.True(result.ReopenedReconciliation);
        Assert.Equal(InboundVenueEvidenceDisposition.Conflicting, lateEvidence.Disposition);
        Assert.True(lateEvidence.AuthoritativeTerminalContradiction);
        var resolved = fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "second-operator",
            new(
                OutboundOperatorDecision.VenueAcknowledged,
                OutboundOperatorEvidenceType.TerminalExecutionReport,
                lateEvidence.EvidenceId,
                "late_contradiction_reconciled"));
        Assert.Equal(OutboundOperatorResolutionStatus.Resolved, resolved.Status);
        Assert.False(resolved.RequiresReconciliation);
        Assert.Equal(
            OutboundMutationState.VenueAcknowledged,
            fixture.Ledger.SnapshotMutations().Single().State);
    }

    [Fact]
    public void IdentityConflict_RevokesLateErAuthoritativeStatus()
    {
        var fixture = Fixture.Create();
        var evidenceReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            '0');
        var proposed = fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "maker",
            new(
                OutboundOperatorDecision.VenueAbsent,
                OutboundOperatorEvidenceType.OfficialExtract,
                evidenceReference,
                "official_extract_attested"));
        fixture.Service.Approve(
            fixture.MutationId,
            proposed.ProposalId!.Value,
            "F1",
            "checker");
        var lateEr = fixture.TerminalEr() with
        {
            SessionVerId = 3,
            InboundSeqNum = 92,
            TimestampUtc = T0.AddMinutes(3),
        };
        fixture.Ledger.ApplyVenueAcknowledgement(lateEr);
        var authoritativeId = Assert.Single(
            fixture.Ledger.GetInboundEvidenceForMutation(fixture.MutationId))
            .EvidenceId;

        fixture.Ledger.ApplyVenueAcknowledgement(lateEr with
        {
            ExecKind = "Canceled",
            RejectReason = null,
            TimestampUtc = T0.AddMinutes(4),
        });

        var original = fixture.Ledger
            .GetInboundEvidenceForMutation(fixture.MutationId)
            .Single(evidence => evidence.EvidenceId == authoritativeId);
        Assert.False(original.AuthoritativeTerminalContradiction);
        Assert.Throws<OutboundReconciliationValidationException>(() =>
            fixture.Service.Resolve(
                fixture.MutationId,
                "F1",
                "second-operator",
                new(
                    OutboundOperatorDecision.VenueAcknowledged,
                    OutboundOperatorEvidenceType.TerminalExecutionReport,
                    authoritativeId,
                    "late_contradiction_reconciled")));
    }

    [Fact]
    public void ReopenedReplace_RestoresOriginalGuardAndReplacedErCanResolve()
    {
        var fixture = Fixture.Create(kind: OutboundMutationKind.Replace);
        var evidenceReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            'a');
        var proposed = fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "maker",
            new(
                OutboundOperatorDecision.VenueAbsent,
                OutboundOperatorEvidenceType.OfficialExtract,
                evidenceReference,
                "official_extract_attested"));
        fixture.Service.Approve(
            fixture.MutationId,
            proposed.ProposalId!.Value,
            "F1",
            "checker");
        var result = fixture.Ledger.ApplyVenueAcknowledgement(
            fixture.TerminalEr() with
            {
                ExecKind = "Replaced",
                OrigClOrdId = 99,
                SessionVerId = 3,
                InboundSeqNum = 93,
                TimestampUtc = T0.AddMinutes(3),
            });
        var lateEvidence = Assert.Single(
            fixture.Ledger.GetInboundEvidenceForMutation(fixture.MutationId));

        Assert.True(result.ReopenedReconciliation);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Ledger.Apply(fixture.CreateCompetingApproval()));
        var resolved = fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "second-operator",
            new(
                OutboundOperatorDecision.VenueAcknowledged,
                OutboundOperatorEvidenceType.TerminalExecutionReport,
                lateEvidence.EvidenceId,
                "late_contradiction_reconciled"));
        Assert.Equal(OutboundOperatorResolutionStatus.Resolved, resolved.Status);
    }

    [Fact]
    public void OperatorResolutionRecords_AreNotPurgedWithTerminalCorrelations()
    {
        var fixture = Fixture.Create();
        var evidenceReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.VenueMassAction,
            'd');
        var proposed = fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "maker",
            new(
                OutboundOperatorDecision.VenueAbsent,
                OutboundOperatorEvidenceType.VenueMassAction,
                evidenceReference,
                "venue_mass_action_verified"));
        fixture.Service.Approve(
            fixture.MutationId,
            proposed.ProposalId!.Value,
            "F1",
            "checker");

        var purged = fixture.Ledger.PurgeTerminalCorrelations(
            T0.AddDays(60),
            OutboundMutationLedger.DefaultTerminalCorrelationRetention);

        Assert.Equal(0, purged);
        Assert.True(fixture.Ledger.TryGet(fixture.MutationId, out _));
    }

    [Fact]
    public void FirmScope_IsEnforcedByService()
    {
        var fixture = Fixture.Create();
        Assert.Throws<OutboundReconciliationForbiddenException>(() =>
            fixture.Service.Resolve(
                fixture.MutationId,
                "F2",
                "maker",
                new(
                    OutboundOperatorDecision.LeaveAmbiguous,
                    OutboundOperatorEvidenceType.ManualAnnotation,
                    $"annotation:{new string('e', 64)}",
                    "manual_comparison_recorded")));
    }

    [Fact]
    public void SnapshotAndAuditPayloads_NeverContainSensitivePlaintext()
    {
        var audit = new CapturingAuditLogger();
        var fixture = Fixture.Create(audit);
        fixture.RegisterEvidence(OutboundOperatorEvidenceType.OfficialExtract, 'f');
        fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "maker",
            new(
                OutboundOperatorDecision.LeaveAmbiguous,
                OutboundOperatorEvidenceType.ManualAnnotation,
                $"annotation:{new string('f', 64)}",
                "manual_comparison_recorded"));

        var payload = JsonSerializer.Serialize(new
        {
            snapshot = fixture.Ledger.CaptureSnapshot(),
            audit.Events,
        });

        Assert.DoesNotContain("ACCOUNT-SECRET", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("INVESTOR-SECRET", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("CLIENT-SECRET", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("CiphertextBase64\":\"ACCOUNT", payload, StringComparison.Ordinal);
        Assert.Single(fixture.Ledger.SnapshotMutations().Single().AuthoritativeEvidence);
    }

    private static string ReasonFor(OutboundOperatorEvidenceType evidenceType) =>
        evidenceType switch
        {
            OutboundOperatorEvidenceType.TerminalExecutionReport =>
                "terminal_er_verified",
            OutboundOperatorEvidenceType.ContractedNotApplied =>
                "contracted_not_applied_verified",
            OutboundOperatorEvidenceType.VenueMassAction =>
                "venue_mass_action_verified",
            OutboundOperatorEvidenceType.OfficialExtract =>
                "official_extract_attested",
            _ => throw new ArgumentOutOfRangeException(nameof(evidenceType)),
        };

    private sealed class Fixture
    {
        public required OutboundMutationLedger Ledger { get; init; }
        public required OutboundReconciliationService Service { get; init; }
        public required RecordingMargin Margin { get; init; }
        public required OutboundMutationId MutationId { get; init; }
        public required OutboundAttemptId AttemptId { get; init; }
        public required IOutboundCommandProtector Protector { get; init; }
        public required OutboundMutationKind Kind { get; init; }

        public static Fixture Create(
            IAuditLogger? audit = null,
            OutboundMutationKind kind = OutboundMutationKind.New)
        {
            var key = Convert.ToBase64String(Enumerable.Range(1, 32)
                .Select(value => (byte)value)
                .ToArray());
            var protector = new AeadOutboundCommandProtector(
                new OutboundCommandProtectionOptions
                {
                    ActiveKeyId = "test",
                    ActiveKeyVersion = 1,
                    StableReferenceKeyId = "test",
                    StableReferenceKeyVersion = 1,
                    Keys =
                    [
                        new OutboundCommandProtectionKeyOptions
                        {
                            KeyId = "test",
                            Version = 1,
                            KeyBase64 = key,
                        },
                    ],
                });
            var mutationId = new OutboundMutationId(Guid.Parse(
                "11111111-2222-3333-4444-555555555555"));
            var attemptId = new OutboundAttemptId(Guid.Parse(
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
            var canonical = new OutboundCanonicalCommand
            {
                ClOrdId = 101,
                OriginalClOrdId = kind == OutboundMutationKind.New ? null : 99,
                SecurityId = 123,
                Symbol = "PETR4",
                Side = "Buy",
                OrderType = "Limit",
                Quantity = 10,
                Price = 30m,
            };
            var sensitive = new SensitiveOutboundCommand
            {
                Account = "ACCOUNT-SECRET",
                InvestorId = "INVESTOR-SECRET",
                EndClientId = "CLIENT-SECRET",
            };
            var approval = OutboundApprovalFactory.Create(
                mutationId,
                "F1",
                canonical,
                sensitive,
                [
                    OutboundSensitiveFieldRef.Account,
                    OutboundSensitiveFieldRef.InvestorId,
                    OutboundSensitiveFieldRef.EndClientId,
                ],
                protector,
                T0);
            var ledger = new OutboundMutationLedger(protector);
            ledger.Apply(new OutboundApprovedEvent
            {
                MutationId = mutationId,
                MutationKind = kind,
                FirmId = "F1",
                EndClientRef = protector.CreateStableEndClientRef("F1", sensitive.EndClientId),
                Origin = OutboundMutationOrigin.Rest,
                PrimaryClOrdId = 101,
                OriginalClOrdId = kind == OutboundMutationKind.New ? null : 99,
                RecordedAtUtc = T0,
                Approval = approval,
                TimestampUtc = T0,
            });
            ledger.Apply(new OutboundAttemptIntentPreparedEvent
            {
                MutationId = mutationId,
                AttemptId = attemptId,
                AttemptNo = 1,
                ClOrdId = 101,
                ProcessEpochId = new ProcessEpochId(Guid.Parse(
                    "99999999-8888-7777-6666-555555555555")),
                IntentPreparedAtUtc = T0.AddSeconds(1),
                TimestampUtc = T0.AddSeconds(1),
            });
            ledger.Apply(new OutboundFramePreparedEvent
            {
                MutationId = mutationId,
                AttemptId = attemptId,
                FirmId = "F1",
                SessionId = 11,
                SessionVerId = 2,
                OutboundSeqNum = 77,
                EncodedFrameSha256 = new string('f', 64),
                PreparedAtUtc = T0.AddSeconds(2),
                TimestampUtc = T0.AddSeconds(2),
            });
            ledger.MarkAmbiguous(
                mutationId,
                attemptId,
                OutboundAmbiguityReason.GatewayOutcomeUnknown,
                T0.AddSeconds(3));
            var margin = new RecordingMargin();
            var replace = new RecordingReplaceMargin();
            var service = new OutboundReconciliationService(
                ledger,
                new EventDispatcher(new NullEventStore()),
                audit ?? new NullAuditLogger(),
                margin,
                replace,
                new PendingReplacementRegistry());
            return new Fixture
            {
                Ledger = ledger,
                Service = service,
                Margin = margin,
                MutationId = mutationId,
                AttemptId = attemptId,
                Protector = protector,
                Kind = kind,
            };
        }

        public string PrepareEvidence(OutboundOperatorEvidenceType evidenceType)
        {
            if (evidenceType == OutboundOperatorEvidenceType.TerminalExecutionReport)
            {
                Ledger.ApplyVenueAcknowledgement(TerminalEr());
                return Ledger.GetInboundEvidenceForMutation(MutationId).Single().EvidenceId;
            }
            if (evidenceType == OutboundOperatorEvidenceType.ContractedNotApplied)
            {
                Ledger.ApplyNotApplied(new NotAppliedReceivedEvent
                {
                    FirmId = "F1",
                    SessionId = 11,
                    SessionVerId = 2,
                    FromSeqNo = 77,
                    Count = 1,
                    ObservedAtUtc = T0.AddMinutes(1),
                    TimestampUtc = T0.AddMinutes(1),
                });
                return Ledger.GetInboundEvidenceForMutation(MutationId).Single().EvidenceId;
            }
            return RegisterEvidence(
                evidenceType,
                evidenceType == OutboundOperatorEvidenceType.VenueMassAction
                    ? 'a'
                    : 'b');
        }

        public string RegisterEvidence(
            OutboundOperatorEvidenceType evidenceType,
            char digestCharacter)
        {
            var sourceType = evidenceType switch
            {
                OutboundOperatorEvidenceType.VenueMassAction =>
                    OutboundAuthoritativeEvidenceSourceType.VenueMassAction,
                OutboundOperatorEvidenceType.OfficialExtract =>
                    OutboundAuthoritativeEvidenceSourceType.OfficialExtract,
                _ => throw new ArgumentOutOfRangeException(nameof(evidenceType)),
            };
            var prefix = sourceType
                == OutboundAuthoritativeEvidenceSourceType.VenueMassAction
                ? "venue-report:"
                : "official-extract:";
            var reference = $"{prefix}{new string(digestCharacter, 64)}";
            Service.RegisterAuthoritativeEvidence(
                MutationId,
                "F1",
                "evidence-attestor",
                new(
                    sourceType,
                    reference,
                    T0.AddHours(-1),
                    T0.AddHours(1),
                    $"attestation:{new string(digestCharacter, 64)}"));
            return reference;
        }

        public string SeedRegisteredEvidence(
            OutboundOperatorEvidenceType evidenceType,
            char digestCharacter)
        {
            var sourceType = evidenceType switch
            {
                OutboundOperatorEvidenceType.VenueMassAction =>
                    OutboundAuthoritativeEvidenceSourceType.VenueMassAction,
                OutboundOperatorEvidenceType.OfficialExtract =>
                    OutboundAuthoritativeEvidenceSourceType.OfficialExtract,
                _ => throw new ArgumentOutOfRangeException(nameof(evidenceType)),
            };
            var prefix = sourceType
                == OutboundAuthoritativeEvidenceSourceType.VenueMassAction
                ? "venue-report:"
                : "official-extract:";
            var reference = $"{prefix}{new string(digestCharacter, 64)}";
            Ledger.Apply(new OutboundAuthoritativeEvidenceRegisteredEvent
            {
                MutationId = MutationId,
                Evidence = new OutboundAuthoritativeEvidenceSnapshot
                {
                    EvidenceReference = reference,
                    EvidenceDigest = new string(digestCharacter, 64),
                    FirmId = "F1",
                    SourceType = sourceType,
                    CoverageStartUtc = T0.AddHours(-1),
                    CoverageEndUtc = T0.AddHours(1),
                    CoveredMutationIds = [MutationId],
                    AttestationReference =
                        $"attestation:{new string(digestCharacter, 64)}",
                    AttestedBy = "evidence-attestor",
                    AttestedAtUtc = T0.AddMinutes(5),
                    RegisteredAtUtc = T0.AddMinutes(5),
                },
                TimestampUtc = T0.AddMinutes(5),
            });
            return reference;
        }

        public ExecutionReportReceivedEvent TerminalEr() => new()
        {
            ClOrdId = 101,
            ExecKind = "Rejected",
            LeavesQuantity = 0,
            CumulativeQuantity = 0,
            LastQuantity = 0,
            LastPrice = 0m,
            RejectReason = "VENUE_REJECTED",
            Synthetic = false,
            FirmId = "F1",
            SessionId = 11,
            SessionVerId = 2,
            InboundSeqNum = 90,
            VenueSendingTime = T0.AddMinutes(2),
            TimestampUtc = T0.AddMinutes(2),
        };

        public OutboundApprovedEvent CreateCompetingApproval()
        {
            if (Kind == OutboundMutationKind.New)
                throw new InvalidOperationException("New mutations have no original order guard.");
            var mutationId = new OutboundMutationId(Guid.Parse(
                "22222222-3333-4444-5555-666666666666"));
            var canonical = new OutboundCanonicalCommand
            {
                ClOrdId = 202,
                OriginalClOrdId = 99,
                SecurityId = 123,
                Symbol = "PETR4",
                Side = "Buy",
                OrderType = "Limit",
                Quantity = 10,
                Price = 30m,
            };
            var sensitive = new SensitiveOutboundCommand
            {
                Account = "ACCOUNT-SECRET",
                InvestorId = "INVESTOR-SECRET",
                EndClientId = "CLIENT-SECRET",
            };
            return new OutboundApprovedEvent
            {
                MutationId = mutationId,
                MutationKind = Kind,
                FirmId = "F1",
                EndClientRef = Protector.CreateStableEndClientRef(
                    "F1",
                    sensitive.EndClientId),
                Origin = OutboundMutationOrigin.Rest,
                PrimaryClOrdId = 202,
                OriginalClOrdId = 99,
                RecordedAtUtc = T0.AddMinutes(4),
                Approval = OutboundApprovalFactory.Create(
                    mutationId,
                    "F1",
                    canonical,
                    sensitive,
                    [
                        OutboundSensitiveFieldRef.Account,
                        OutboundSensitiveFieldRef.InvestorId,
                        OutboundSensitiveFieldRef.EndClientId,
                    ],
                    Protector,
                    T0.AddMinutes(4)),
                TimestampUtc = T0.AddMinutes(4),
            };
        }
    }

    private sealed class RecordingMargin : IMarginProvider
    {
        public int ReleaseCount { get; private set; }

        public Task<RiskDecision> TryReserveAsync(
            ulong clOrdId,
            RiskContext ctx,
            CancellationToken ct) =>
            Task.FromResult(RiskDecision.Approve);

        public void ReleaseReservation(ulong clOrdId) => ReleaseCount++;
    }

    private sealed class RecordingReplaceMargin : IReplaceMarginCoordinator
    {
        public Task<RiskDecision> PrepareReplaceAsync(
            ulong originalClOrdId,
            ulong newClOrdId,
            EndClientId owner,
            decimal newRemainingNotional,
            CancellationToken ct) =>
            Task.FromResult(RiskDecision.Approve);

        public void CommitReplace(
            ulong originalClOrdId,
            ulong newClOrdId,
            decimal confirmedRemainingNotional)
        {
        }

        public void AbortReplace(ulong newClOrdId) { }
    }

    private sealed class ThrowingAuditLogger : IAuditLogger
    {
        public void Log(AuditLogEvent evt) { }
        public void LogOrFail(AuditLogEvent evt) =>
            throw new WalBackpressureException("injected");
        public void LogCommittedOrFail(
            AuditLogEvent evt,
            CancellationToken cancellationToken = default) =>
            throw new WalBackpressureException("injected");
    }

    private sealed class CapturingAuditLogger : IAuditLogger
    {
        public List<AuditLogEvent> Events { get; } = [];
        public void Log(AuditLogEvent evt) => Events.Add(evt);
        public void LogOrFail(AuditLogEvent evt) => Events.Add(evt);
        public void LogCommittedOrFail(
            AuditLogEvent evt,
            CancellationToken cancellationToken = default) =>
            Events.Add(evt);
    }

    private sealed class CallbackAuditLogger : IAuditLogger
    {
        public Action? OnCommitted { get; set; }
        public void Log(AuditLogEvent evt) { }
        public void LogOrFail(AuditLogEvent evt) { }
        public void LogCommittedOrFail(
            AuditLogEvent evt,
            CancellationToken cancellationToken = default) =>
            OnCommitted?.Invoke();
    }
}
