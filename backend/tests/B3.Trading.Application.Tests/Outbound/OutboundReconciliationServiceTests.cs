using B3.Trading.Application;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Application.UserBots;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace B3.Trading.Application.Tests.Outbound;

public sealed class OutboundReconciliationServiceTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
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

        var duplicate = fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "maker",
            new(
                OutboundOperatorDecision.VenueAbsent,
                evidenceType,
                reference,
                ReasonFor(evidenceType)));

        Assert.Equal(OutboundOperatorResolutionStatus.Resolved, duplicate.Status);
        Assert.Equal(approved.ProposalId, duplicate.ProposalId);
        Assert.True(duplicate.CapacityReleased);
        Assert.Equal(1, fixture.Margin.ReleaseCount);
        Assert.Single(fixture.Ledger.SnapshotMutations().Single().OperatorEvidence);
    }

    [Fact]
    public void TerminalResolution_WithUnavailablePayload_RejectsDifferentFollowUp()
    {
        var writer = Fixture.Create();
        var firstReference = writer.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            '1');
        var secondReference = writer.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            '2');
        var snapshot = writer.Ledger.CaptureSnapshot();
        var ledger = new OutboundMutationLedger(CreateProtector("replacement"));
        ledger.Restore(
            snapshot.Mutations,
            snapshot.Correlations,
            snapshot.InboundEvidence);
        var margin = new RecordingMargin();
        var service = new OutboundReconciliationService(
            ledger,
            new EventDispatcher(new NullEventStore()),
            new NullAuditLogger(),
            margin,
            new RecordingReplaceMargin(),
            new PendingReplacementRegistry());
        var firstRequest = new OutboundOperatorResolutionRequest(
            OutboundOperatorDecision.VenueAbsent,
            OutboundOperatorEvidenceType.OfficialExtract,
            firstReference,
            "official_extract_attested");
        var proposed = service.Resolve(
            writer.MutationId,
            "F1",
            "maker",
            firstRequest);
        var approved = service.Approve(
            writer.MutationId,
            proposed.ProposalId!.Value,
            "F1",
            "checker");

        Assert.True(approved.CapacityReleased);
        Assert.True(approved.RequiresReconciliation);
        Assert.Equal(1, margin.ReleaseCount);
        var terminal = Assert.Single(ledger.SnapshotMutations());
        Assert.Equal(
            OutboundSensitivePayloadAvailability.MissingHistoricalKey,
            terminal.SensitivePayloadAvailability);
        Assert.Equal(OutboundMutationState.OperatorResolved, terminal.State);
        Assert.True(terminal.RequiresReconciliation);

        var duplicate = service.Resolve(
            writer.MutationId,
            "F1",
            "maker",
            firstRequest);
        Assert.Equal(OutboundOperatorResolutionStatus.Resolved, duplicate.Status);
        Assert.True(duplicate.RequiresReconciliation);
        Assert.Equal(1, margin.ReleaseCount);

        Assert.Throws<OutboundReconciliationValidationException>(() =>
            service.Resolve(
                writer.MutationId,
                "F1",
                "maker",
                new(
                    OutboundOperatorDecision.VenueAbsent,
                    OutboundOperatorEvidenceType.OfficialExtract,
                    secondReference,
                    "official_extract_attested")));
        Assert.Throws<InvalidOperationException>(() =>
            ledger.Apply(new OutboundOperatorResolvedEvent
            {
                MutationId = writer.MutationId,
                Decision = OutboundOperatorDecision.VenueAcknowledged,
                EvidenceType = OutboundOperatorEvidenceType.OfficialExtract,
                EvidenceReference = secondReference,
                EvidenceDigest = new string('d', 64),
                ReasonCode = "official_extract_attested",
                OperatorRef = "second-operator",
                ReleaseCapacity = false,
                ResolvedAtUtc = T0.AddMinutes(10),
                TimestampUtc = T0.AddMinutes(10),
            }));

        terminal = Assert.Single(ledger.SnapshotMutations());
        Assert.Single(terminal.OperatorEvidence);
        Assert.Single(terminal.ResolutionProposals);
        Assert.Equal(1, margin.ReleaseCount);
    }

    [Fact]
    public void DirectTerminalResolution_ReturnsActualUnavailablePayloadReconciliationState()
    {
        var writer = Fixture.Create(kind: OutboundMutationKind.Cancel);
        var evidenceReference = writer.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            '3');
        var snapshot = writer.Ledger.CaptureSnapshot();
        var ledger = new OutboundMutationLedger(CreateProtector("replacement"));
        ledger.Restore(
            snapshot.Mutations,
            snapshot.Correlations,
            snapshot.InboundEvidence);
        var service = new OutboundReconciliationService(
            ledger,
            new EventDispatcher(new NullEventStore()),
            new NullAuditLogger(),
            new RecordingMargin(),
            new RecordingReplaceMargin(),
            new PendingReplacementRegistry());

        var resolved = service.Resolve(
            writer.MutationId,
            "F1",
            "operator",
            new(
                OutboundOperatorDecision.VenueAbsent,
                OutboundOperatorEvidenceType.OfficialExtract,
                evidenceReference,
                "official_extract_attested"));

        Assert.Equal(OutboundOperatorResolutionStatus.Resolved, resolved.Status);
        Assert.False(resolved.CapacityReleased);
        Assert.True(resolved.RequiresReconciliation);
        Assert.True(Assert.Single(ledger.SnapshotMutations()).RequiresReconciliation);
    }

    [Fact]
    public void VenueAbsentCancel_ReleasesActiveRetryProjectionForTargetedRetry()
    {
        var fixture = Fixture.Create(
            kind: OutboundMutationKind.Cancel,
            retried: true);
        fixture.Drain.BeginDrain(
            "outbound_cancel_replace_reconciliation_required");
        var evidenceReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            '4');
        Assert.True(fixture.PendingCancels.TryGetByCancel(102, out var originalClOrdId));
        Assert.Equal(99UL, originalClOrdId);
        Assert.True(fixture.Ownership.TryResolveOrig(102, out _));

        fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "operator",
            new(
                OutboundOperatorDecision.VenueAbsent,
                OutboundOperatorEvidenceType.OfficialExtract,
                evidenceReference,
                "official_extract_attested"));

        Assert.False(fixture.PendingCancels.TryGetByCancel(102, out _));
        Assert.False(fixture.Ownership.TryResolveOrig(102, out _));
        Assert.False(fixture.Drain.IsDraining);
    }

    [Fact]
    public void VenueAbsentCancel_EndsColdStartLifecycleIntentsDrain_WhenRegistriesDrainToEmpty()
    {
        var fixture = Fixture.Create(
            kind: OutboundMutationKind.Cancel,
            retried: true);
        // Simulates the post-cold-start-restart state: the drain is held
        // open by the lifecycle guard, not the outbound reconciliation
        // reasons, because the process just came back from a crash with
        // outstanding pending cancels.
        fixture.Drain.BeginDrain("cold_start_unresolved_lifecycle_intents");
        var evidenceReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            '4');

        fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "operator",
            new(
                OutboundOperatorDecision.VenueAbsent,
                OutboundOperatorEvidenceType.OfficialExtract,
                evidenceReference,
                "official_extract_attested"));

        // The one pending cancel this fixture seeded is now released, and
        // there are no pending replacements either, so the stale lifecycle
        // drain reason must be cleared even though it isn't one of the
        // "outbound_*" reconciliation reasons.
        Assert.False(fixture.PendingCancels.TryGetByCancel(102, out _));
        Assert.Empty(fixture.Replacements.Snapshot());
        Assert.False(fixture.Drain.IsDraining);
    }

    [Fact]
    public async Task VenueAbsentCancel_AllowsGenuineNewCancelForSameOrder_AfterResolution()
    {
        // Reproduces the real-stack CI failure in
        // SuspendedTimeoutBoundarySpecTests: a first cancel attempt is
        // fired while the venue transport is severed (never applied),
        // gets resolved VenueAbsent ("never reached the venue"), and the
        // ORIGINAL order is still genuinely live at the venue. A second,
        // real cancel for that same original order must not be silently
        // swallowed as a "duplicate" of the just-resolved mutation.
        var fixture = Fixture.Create(kind: OutboundMutationKind.Cancel);
        var evidenceReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            '4');

        fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "operator",
            new(
                OutboundOperatorDecision.VenueAbsent,
                OutboundOperatorEvidenceType.OfficialExtract,
                evidenceReference,
                "official_extract_attested"));

        // The just-resolved cancel mutation must no longer be tracked as
        // "active" for the original order — otherwise a fresh cancel
        // attempt is indistinguishable from a retry of the stale one.
        Assert.False(fixture.Ledger.TryGetActiveForOriginal("F1", 99, out _));
        Assert.False(fixture.PendingCancels.TryGetByCancel(101, out _));

        var book = new WorkingOrderBook();
        var owner = new EndClientId("CLIENT-SECRET");
        Assert.True(book.TryAdd(new Order(
            99, owner, "PETR4", 123, OrderSide.Buy, OrderType.Limit, 10, 30m,
            firmId: "F1")));
        var gateway = new RecordingGateway();
        var cancelService = new OrderCancelService(
            new ClOrdIdPrefixRegistry(),
            fixture.Ownership,
            book,
            gateway,
            new EventDispatcher(new NullEventStore()),
            NullLogger<OrderCancelService>.Instance,
            pendingCancels: fixture.PendingCancels,
            outboundLedger: fixture.Ledger);

        var result = await cancelService.CancelAsync(
            owner,
            99,
            CancellationToken.None,
            firmId: "F1");

        Assert.Equal(OrderCancelResultKind.Accepted, result.Kind);
        // Must be a BRAND NEW cancel ClOrdId, dispatched to the gateway —
        // not the stale 101 reused from the resolved mutation.
        Assert.NotEqual(101UL, result.CancelClOrdId);
        Assert.Single(gateway.Calls);
        Assert.StartsWith("cancel:", gateway.Calls[0]);
    }

    [Fact]
    public void VenueAbsentCancelReplay_ReleasesOriginalWalProjectionAfterRetry()
    {
        var fixture = Fixture.Create(
            kind: OutboundMutationKind.Cancel,
            retried: true);
        Assert.True(fixture.PendingCancels.TryConsumeByCancel(102, out _));
        fixture.Ownership.RemoveCancelLink(102);
        Assert.True(fixture.PendingCancels.TryAdd(99, 101));
        fixture.Ownership.RegisterCancelLink(101, 99);
        var replayer = CreateReplayer(
            fixture.Ledger,
            fixture.PendingCancels,
            fixture.Ownership);

        replayer.Apply(new OutboundOperatorResolvedEvent
        {
            MutationId = fixture.MutationId,
            Decision = OutboundOperatorDecision.VenueAbsent,
            EvidenceType = OutboundOperatorEvidenceType.OfficialExtract,
            EvidenceReference = $"official-extract:{new string('d', 64)}",
            EvidenceDigest = new string('e', 64),
            ReasonCode = "official_extract_attested",
            OperatorRef = "operator",
            ReleaseCapacity = false,
            ResolvedAtUtc = T0.AddMinutes(1),
            TimestampUtc = T0.AddMinutes(1),
        });

        Assert.False(fixture.PendingCancels.TryGetByCancel(101, out _));
        Assert.False(fixture.Ownership.TryResolveOrig(101, out _));
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

    [Fact]
    public void VenueAcknowledgedReplace_CannotBeResolvedVenueAbsent()
    {
        var fixture = Fixture.Create(kind: OutboundMutationKind.Replace);
        var owner = new EndClientId("CLIENT-SECRET");
        var intent = new OrderReplacementIntent(
            OriginalClOrdId: 99,
            NewClOrdId: 101,
            Owner: owner,
            Symbol: "PETR4",
            SecurityId: 123,
            Side: OrderSide.Buy,
            Type: OrderType.Limit,
            NewQuantity: 10,
            NewPrice: 30m,
            FirmId: "F1",
            ParentAlgoId: null,
            AlgoSliceSeq: null);
        Assert.True(fixture.Replacements.TryAdd(intent, T0));
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var original = new Order(
            intent.OriginalClOrdId,
            owner,
            intent.Symbol,
            intent.SecurityId,
            intent.Side,
            intent.Type,
            20,
            29m,
            firmId: intent.FirmId);
        Assert.True(book.TryAdd(original));
        ownership.Register(original.ClOrdId, owner);
        ownership.RegisterReplaceLink(original.ClOrdId, intent.NewClOrdId);
        var processor = new ExecutionReportProcessor(
            ownership,
            book,
            new PositionKeeper(),
            new NoOpExecutionEventSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            replacements: fixture.Replacements,
            replaceMargin: fixture.ReplaceMargin);
        var client = new MockEntryPointClient();
        using var router = new EntryPointExecutionReportRouter(
            client,
            processor,
            new EventDispatcher(new NullEventStore()),
            book,
            bookTop: null,
            outboundLedger: fixture.Ledger);
        client.EmitExecutionReport(new ExecutionReportEnvelope(
            intent.NewClOrdId,
            EpExecType.Replaced,
            LeavesQuantity: intent.NewQuantity,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: intent.OriginalClOrdId,
            FirmId: intent.FirmId,
            SessionId: 11,
            SessionVerId: 2,
            InboundSeqNum: 90,
            SendingTime: T0.AddMinutes(2)));

        Assert.True(book.TryGet(intent.NewClOrdId, out var replacement));
        Assert.Equal(OrderStatus.Working, replacement!.Status);
        Assert.Equal(1, fixture.ReplaceMargin.CommitCount);
        Assert.Equal(0, fixture.ReplaceMargin.AbortCount);
        var evidence = Assert.Single(
            fixture.Ledger.GetInboundEvidenceForMutation(fixture.MutationId));

        Assert.Throws<OutboundReconciliationValidationException>(() =>
            fixture.Service.Resolve(
                fixture.MutationId,
                "F1",
                "maker",
                new(
                    OutboundOperatorDecision.VenueAbsent,
                    OutboundOperatorEvidenceType.TerminalExecutionReport,
                    evidence.EvidenceId,
                    "terminal_er_verified")));

        var mutation = fixture.Ledger.SnapshotMutations().Single();
        Assert.Equal(OutboundMutationState.VenueAcknowledged, mutation.State);
        Assert.Empty(mutation.ResolutionProposals);
        Assert.Equal(0, fixture.ReplaceMargin.AbortCount);
        Assert.Equal(OrderStatus.Working, replacement.Status);
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
        var evidenceReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            '8');
        fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "maker",
            new(
                OutboundOperatorDecision.VenueAbsent,
                OutboundOperatorEvidenceType.OfficialExtract,
                evidenceReference,
                "official_extract_attested"));
        var snapshot = fixture.Ledger.CaptureSnapshot();
        var restored = new OutboundMutationLedger(fixture.Protector);

        restored.Restore(
            snapshot.Mutations,
            snapshot.Correlations,
            snapshot.InboundEvidence);

        var mutation = Assert.Single(restored.SnapshotMutations());
        Assert.Equal(OutboundMutationState.Ambiguous, mutation.State);
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
        Assert.True(result.ShouldApplyDomain);
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

        var snapshot = fixture.Ledger.CaptureSnapshot();
        var restored = new OutboundMutationLedger(fixture.Protector);
        restored.Restore(
            snapshot.Mutations,
            snapshot.Correlations,
            snapshot.InboundEvidence);

        var restoredMutation = Assert.Single(restored.SnapshotMutations());
        Assert.Equal(OutboundMutationState.VenueAcknowledged, restoredMutation.State);
        Assert.False(restoredMutation.RequiresReconciliation);
        Assert.Equal(0, restored.ReadinessBlockingCount);
        Assert.Null(Assert.Single(restoredMutation.Attempts).AmbiguityReason);
    }

    [Fact]
    public void AuthoritativeContradictoryFill_ReopensAndAppliesDomainEffects()
    {
        var fixture = Fixture.Create();
        var evidenceReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            'e');
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
        var owner = new EndClientId("CLIENT-SECRET");
        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var cash = new CashLedger();
        var order = new Order(
            101,
            owner,
            "PETR4",
            123,
            OrderSide.Buy,
            OrderType.Limit,
            10,
            30m,
            firmId: "F1");
        Assert.True(book.TryAdd(order));
        ownership.Register(order.ClOrdId, owner);
        var processor = new ExecutionReportProcessor(
            ownership,
            book,
            positions,
            new NoOpExecutionEventSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            cash: cash);
        var client = new MockEntryPointClient();
        using var router = new EntryPointExecutionReportRouter(
            client,
            processor,
            new EventDispatcher(new NullEventStore()),
            book,
            bookTop: null,
            outboundLedger: fixture.Ledger);

        client.EmitExecutionReport(new ExecutionReportEnvelope(
            order.ClOrdId,
            EpExecType.Fill,
            LeavesQuantity: 0,
            CumulativeQuantity: 10,
            LastQuantity: 10,
            LastPrice: 30m,
            RejectReason: null,
            FirmId: "F1",
            SessionId: 11,
            SessionVerId: 3,
            InboundSeqNum: 94,
            SendingTime: T0.AddMinutes(3)));

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(10, order.CumulativeQuantity);
        Assert.Equal(10, positions.GetOrCreate("F1", owner, "PETR4").NetQuantity);
        Assert.Equal(-300m, cash.GetAvailable("F1", owner));
        var lateEvidence = Assert.Single(
            fixture.Ledger.GetInboundEvidenceForMutation(fixture.MutationId));
        Assert.Throws<OutboundReconciliationValidationException>(() =>
            fixture.Service.Resolve(
                fixture.MutationId,
                "F1",
                "second-maker",
                new(
                    OutboundOperatorDecision.VenueAbsent,
                    OutboundOperatorEvidenceType.TerminalExecutionReport,
                    lateEvidence.EvidenceId,
                    "terminal_er_verified")));
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Ledger.Apply(new OutboundOperatorResolvedEvent
            {
                MutationId = fixture.MutationId,
                Decision = OutboundOperatorDecision.VenueAbsent,
                EvidenceType = OutboundOperatorEvidenceType.TerminalExecutionReport,
                EvidenceReference = lateEvidence.EvidenceId,
                EvidenceDigest = new string('a', 64),
                ReasonCode = "terminal_er_verified",
                OperatorRef = "second-checker",
                ReleaseCapacity = false,
                ResolvedAtUtc = T0.AddMinutes(4),
                TimestampUtc = T0.AddMinutes(4),
            }));
        Assert.Equal(1, fixture.Margin.ReleaseCount);
        var mutation = fixture.Ledger.SnapshotMutations().Single();
        Assert.Equal(OutboundMutationState.Ambiguous, mutation.State);
        Assert.True(mutation.RequiresReconciliation);
        Assert.Single(mutation.OperatorEvidence);
        Assert.Single(mutation.ResolutionProposals);
    }

    [Fact]
    public void AuthoritativeContradictoryReplace_ReopensAndAppliesReplacementTerms()
    {
        var fixture = Fixture.Create(kind: OutboundMutationKind.Replace);
        var owner = new EndClientId("CLIENT-SECRET");
        var intent = new OrderReplacementIntent(
            OriginalClOrdId: 99,
            NewClOrdId: 101,
            Owner: owner,
            Symbol: "PETR4",
            SecurityId: 123,
            Side: OrderSide.Buy,
            Type: OrderType.Limit,
            NewQuantity: 10,
            NewPrice: 30m,
            FirmId: "F1",
            ParentAlgoId: null,
            AlgoSliceSeq: null);
        Assert.True(fixture.Replacements.TryAdd(intent, T0));
        Assert.True(fixture.Replacements.MarkAmbiguousMarginHeld(101, T0.AddSeconds(3)));

        var evidenceReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            'f');
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

        Assert.False(fixture.Replacements.IsOriginalInFlight(99));
        Assert.True(fixture.Replacements.TryGet(101, out _));
        var retained = Assert.Single(fixture.Replacements.Snapshot());
        Assert.True(retained.ReleasedForVenueAbsent);
        var restoredReplacements = new PendingReplacementRegistry();
        restoredReplacements.Restore([retained]);
        Assert.False(restoredReplacements.IsOriginalInFlight(99));
        Assert.True(restoredReplacements.TryGet(101, out _));

        var ownership = new OrderOwnershipMap();
        var book = new WorkingOrderBook();
        var original = new Order(
            99,
            owner,
            "PETR4",
            123,
            OrderSide.Buy,
            OrderType.Limit,
            20,
            29m,
            firmId: "F1");
        Assert.True(book.TryAdd(original));
        ownership.Register(original.ClOrdId, owner);
        ownership.RegisterReplaceLink(original.ClOrdId, intent.NewClOrdId);
        var processor = new ExecutionReportProcessor(
            ownership,
            book,
            new PositionKeeper(),
            new NoOpExecutionEventSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            replacements: restoredReplacements,
            replaceMargin: fixture.ReplaceMargin);
        var client = new MockEntryPointClient();
        using var router = new EntryPointExecutionReportRouter(
            client,
            processor,
            new EventDispatcher(new NullEventStore()),
            book,
            bookTop: null,
            outboundLedger: fixture.Ledger);

        client.EmitExecutionReport(new ExecutionReportEnvelope(
            intent.NewClOrdId,
            EpExecType.Replaced,
            LeavesQuantity: 10,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: intent.OriginalClOrdId,
            FirmId: "F1",
            SessionId: 11,
            SessionVerId: 3,
            InboundSeqNum: 95,
            SendingTime: T0.AddMinutes(3)));

        Assert.Equal(OrderStatus.Replaced, original.Status);
        Assert.True(book.TryGet(intent.NewClOrdId, out var replacement));
        Assert.NotNull(replacement);
        Assert.Equal(intent.NewQuantity, replacement.Quantity);
        Assert.Equal(intent.NewPrice, replacement.Price);
        Assert.Equal(OrderStatus.Working, replacement.Status);
        Assert.False(restoredReplacements.TryGet(intent.NewClOrdId, out _));
        var lateEvidence = Assert.Single(
            fixture.Ledger.GetInboundEvidenceForMutation(fixture.MutationId));
        Assert.Throws<OutboundReconciliationValidationException>(() =>
            fixture.Service.Resolve(
                fixture.MutationId,
                "F1",
                "second-maker",
                new(
                    OutboundOperatorDecision.VenueAbsent,
                    OutboundOperatorEvidenceType.TerminalExecutionReport,
                    lateEvidence.EvidenceId,
                    "terminal_er_verified")));
        Assert.Equal(1, fixture.ReplaceMargin.AbortCount);
        var mutation = fixture.Ledger.SnapshotMutations().Single();
        Assert.Equal(OutboundMutationState.Ambiguous, mutation.State);
        Assert.True(mutation.RequiresReconciliation);
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
    public void EvidenceRevokedDuringAuditWindow_BlocksNonProposalResolution()
    {
        var fixture = Fixture.Create();
        var evidenceReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            '3');
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
            InboundSeqNum = 93,
            TimestampUtc = T0.AddMinutes(3),
        };
        fixture.Ledger.ApplyVenueAcknowledgement(lateEr);
        var authoritativeId = Assert.Single(
            fixture.Ledger.GetInboundEvidenceForMutation(fixture.MutationId))
            .EvidenceId;
        var audit = new CallbackAuditLogger
        {
            OnCommitted = () => fixture.Ledger.ApplyVenueAcknowledgement(lateEr with
            {
                ExecKind = "Canceled",
                RejectReason = null,
                TimestampUtc = T0.AddMinutes(4),
            }),
        };
        var service = new OutboundReconciliationService(
            fixture.Ledger,
            new EventDispatcher(new NullEventStore()),
            audit,
            fixture.Margin,
            fixture.ReplaceMargin,
            fixture.Replacements);

        Assert.Throws<OutboundReconciliationConflictException>(() =>
            service.Resolve(
                fixture.MutationId,
                "F1",
                "second-operator",
                new(
                    OutboundOperatorDecision.VenueAcknowledged,
                    OutboundOperatorEvidenceType.TerminalExecutionReport,
                    authoritativeId,
                    "late_contradiction_reconciled")));

        var evidence = fixture.Ledger
            .GetInboundEvidenceForMutation(fixture.MutationId)
            .Single(item => item.EvidenceId == authoritativeId);
        Assert.False(evidence.AuthoritativeTerminalContradiction);
        var mutation = fixture.Ledger.SnapshotMutations().Single();
        Assert.Equal(OutboundMutationState.Ambiguous, mutation.State);
        Assert.True(mutation.RequiresReconciliation);
        Assert.Single(mutation.OperatorEvidence);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Ledger.Apply(new OutboundOperatorResolvedEvent
            {
                MutationId = fixture.MutationId,
                Decision = OutboundOperatorDecision.VenueAcknowledged,
                EvidenceType = OutboundOperatorEvidenceType.TerminalExecutionReport,
                EvidenceReference = authoritativeId,
                EvidenceDigest = new string('e', 64),
                ReasonCode = "late_contradiction_reconciled",
                OperatorRef = "second-operator",
                ReleaseCapacity = false,
                ResolvedAtUtc = T0.AddMinutes(5),
                TimestampUtc = T0.AddMinutes(5),
            }));
    }

    [Theory]
    [InlineData(OutboundOperatorEvidenceType.ContractedNotApplied)]
    [InlineData(OutboundOperatorEvidenceType.VenueMassAction)]
    [InlineData(OutboundOperatorEvidenceType.OfficialExtract)]
    public void AbsenceEvidence_CannotProveVenueAcknowledgment(
        OutboundOperatorEvidenceType evidenceType)
    {
        var fixture = Fixture.Create();
        var evidenceReference = fixture.PrepareEvidence(evidenceType);

        Assert.Throws<OutboundReconciliationValidationException>(() =>
            fixture.Service.Resolve(
                fixture.MutationId,
                "F1",
                "operator",
                new(
                    OutboundOperatorDecision.VenueAcknowledged,
                    evidenceType,
                    evidenceReference,
                    ReasonFor(evidenceType))));

        var mutation = fixture.Ledger.SnapshotMutations().Single();
        Assert.Empty(mutation.OperatorEvidence);
        Assert.Empty(mutation.ResolutionProposals);
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
                VenueOrderId = 9001,
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
        Assert.Equal(
            9001UL,
            Assert.Single(fixture.Ledger.SnapshotMutations())
                .Resolution!
                .VenueOrderId);
        var cancelApproval = new CancelReplaceApprovalFactory(
                fixture.Protector,
                outboundLedger: fixture.Ledger)
            .CreateCancel(
                OutboundMutationId.New(),
                new Order(
                    101,
                    new EndClientId("CLIENT-SECRET"),
                    "PETR4",
                    123,
                    OrderSide.Buy,
                    OrderType.Limit,
                    10,
                    30m,
                    "F1"),
                cancelClOrdId: 202,
                T0.AddMinutes(4));
        Assert.Equal(9001UL, cancelApproval.Approval.VenueOrderId);
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
        var officialReference = fixture.RegisterEvidence(
            OutboundOperatorEvidenceType.OfficialExtract,
            'f');
        var annotationReference = $"annotation:{new string('f', 64)}";
        fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "maker",
            new(
                OutboundOperatorDecision.LeaveAmbiguous,
                OutboundOperatorEvidenceType.ManualAnnotation,
                annotationReference,
                "manual_comparison_recorded"));
        var proposed = fixture.Service.Resolve(
            fixture.MutationId,
            "F1",
            "maker",
            new(
                OutboundOperatorDecision.VenueAbsent,
                OutboundOperatorEvidenceType.OfficialExtract,
                officialReference,
                "official_extract_attested"));
        fixture.Service.Approve(
            fixture.MutationId,
            proposed.ProposalId!.Value,
            "F1",
            "checker");

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
        var resolutionAudits = audit.Events
            .Where(evt => evt.Details?.GetValueOrDefault("action")
                ?.StartsWith("outbound_resolution_", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(3, resolutionAudits.Length);
        Assert.Equal(
            annotationReference,
            resolutionAudits.Single(evt =>
                evt.Details!["action"] == "outbound_resolution_commit")
                .Details!["evidence_reference"]);
        foreach (var evt in resolutionAudits.Where(item =>
                     item.Details!["action"] != "outbound_resolution_commit"))
        {
            Assert.Equal(officialReference, evt.Details!["evidence_reference"]);
            Assert.NotEqual(
                evt.Details["evidence_digest"],
                evt.Details["evidence_reference"]);
        }
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

    private static IOutboundCommandProtector CreateProtector(string keyId)
    {
        var key = Convert.ToBase64String(Enumerable.Range(1, 32)
            .Select(value => (byte)value)
            .ToArray());
        return new AeadOutboundCommandProtector(
            new OutboundCommandProtectionOptions
            {
                ActiveKeyId = keyId,
                ActiveKeyVersion = 1,
                StableReferenceKeyId = keyId,
                StableReferenceKeyVersion = 1,
                Keys =
                [
                    new OutboundCommandProtectionKeyOptions
                    {
                        KeyId = keyId,
                        Version = 1,
                        KeyBase64 = key,
                    },
                ],
            });
    }

    private sealed class Fixture
    {
        public required OutboundMutationLedger Ledger { get; init; }
        public required OutboundReconciliationService Service { get; init; }
        public required RecordingMargin Margin { get; init; }
        public required RecordingReplaceMargin ReplaceMargin { get; init; }
        public required PendingReplacementRegistry Replacements { get; init; }
        public required PendingCancelRegistry PendingCancels { get; init; }
        public required OrderOwnershipMap Ownership { get; init; }
        public required RecordingDrain Drain { get; init; }
        public required OutboundMutationId MutationId { get; init; }
        public required OutboundAttemptId AttemptId { get; init; }
        public required IOutboundCommandProtector Protector { get; init; }
        public required OutboundMutationKind Kind { get; init; }

        public static Fixture Create(
            IAuditLogger? audit = null,
            OutboundMutationKind kind = OutboundMutationKind.New,
            bool retried = false)
        {
            var protector = CreateProtector("test");
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
            if (retried)
            {
                ledger.Apply(new OutboundProvenUnsentEvent
                {
                    MutationId = mutationId,
                    AttemptId = attemptId,
                    Evidence = OutboundProvenUnsentEvidence.TypedPreFrameFailure,
                    TimestampUtc = T0.AddSeconds(2),
                });
                var retryAttemptId = OutboundAttemptId.New();
                ledger.Apply(new OutboundAttemptIntentPreparedEvent
                {
                    MutationId = mutationId,
                    AttemptId = retryAttemptId,
                    AttemptNo = 2,
                    ClOrdId = 102,
                    ProcessEpochId = new ProcessEpochId(Guid.Parse(
                        "88888888-7777-6666-5555-444444444444")),
                    IntentPreparedAtUtc = T0.AddSeconds(3),
                    TimestampUtc = T0.AddSeconds(3),
                });
                ledger.Apply(new OutboundFramePreparedEvent
                {
                    MutationId = mutationId,
                    AttemptId = retryAttemptId,
                    FirmId = "F1",
                    SessionId = 11,
                    SessionVerId = 2,
                    OutboundSeqNum = 78,
                    EncodedFrameSha256 = new string('e', 64),
                    PreparedAtUtc = T0.AddSeconds(4),
                    TimestampUtc = T0.AddSeconds(4),
                });
                ledger.MarkAmbiguous(
                    mutationId,
                    retryAttemptId,
                    OutboundAmbiguityReason.GatewayOutcomeUnknown,
                    T0.AddSeconds(5));
            }
            else
            {
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
            }
            var margin = new RecordingMargin();
            var replace = new RecordingReplaceMargin();
            var replacements = new PendingReplacementRegistry();
            var pendingCancels = new PendingCancelRegistry();
            var ownership = new OrderOwnershipMap();
            var drain = new RecordingDrain();
            if (kind == OutboundMutationKind.Cancel)
            {
                ownership.Register(99, new EndClientId(sensitive.EndClientId));
                var activeCancelClOrdId = retried ? 102UL : 101UL;
                ownership.RegisterCancelLink(activeCancelClOrdId, 99);
                Assert.True(pendingCancels.TryAdd(99, activeCancelClOrdId));
            }
            var service = new OutboundReconciliationService(
                ledger,
                new EventDispatcher(new NullEventStore()),
                audit ?? new NullAuditLogger(),
                margin,
                replace,
                replacements,
                pendingCancels: pendingCancels,
                ownership: ownership,
                drain: drain);
            return new Fixture
            {
                Ledger = ledger,
                Service = service,
                Margin = margin,
                ReplaceMargin = replace,
                Replacements = replacements,
                PendingCancels = pendingCancels,
                Ownership = ownership,
                Drain = drain,
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

    private sealed class RecordingGateway : IExchangeGateway
    {
        public List<string> Calls { get; } = new();

        public Task SubmitAsync(Order order, CancellationToken cancellationToken)
        {
            Calls.Add("submit");
            return Task.CompletedTask;
        }

        public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken)
        {
            Calls.Add($"cancel:{newClOrdId}");
            return Task.CompletedTask;
        }

        public Task CancelReplaceAsync(
            Order original, ulong newClOrdId, long newQuantity, decimal? newPrice,
            TimeInForce? requestedTimeInForce, decimal? requestedStopPrice, DateTimeOffset? requestedGoodTillDate,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RecordingDrain : Lifecycle.IDrainController
    {
        private string? _reason;

        public bool IsDraining { get; private set; }

        public void BeginDrain(string reason)
        {
            _reason = reason;
            IsDraining = true;
        }

        public bool TryEndOutboundReconciliationDrain()
        {
            if (_reason is not (
                    "outbound_new_order_reconciliation_required" or
                    "outbound_cancel_replace_reconciliation_required"))
            {
                return false;
            }

            _reason = null;
            IsDraining = false;
            return true;
        }

        public bool TryEndColdStartLifecycleIntentsDrain()
        {
            if (_reason is not "cold_start_unresolved_lifecycle_intents")
                return false;

            _reason = null;
            IsDraining = false;
            return true;
        }
    }

    private static EventReplayer CreateReplayer(
        OutboundMutationLedger ledger,
        PendingCancelRegistry pendingCancels,
        OrderOwnershipMap ownership)
    {
        var orders = new WorkingOrderBook();
        var processor = new ExecutionReportProcessor(
            ownership,
            orders,
            new PositionKeeper(),
            new NoOpExecutionEventSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            pendingCancels: pendingCancels);
        return new EventReplayer(
            orders,
            ownership,
            new KillSwitchService(),
            new SymbolHaltService(),
            new SessionPhaseService(),
            processor,
            new AlgoBook(),
            new ClOrdIdPrefixRegistry(),
            new AlgoIdRegistry(),
            pendingCancels: pendingCancels,
            outboundLedger: ledger);
    }

    private sealed class RecordingReplaceMargin : IReplaceMarginCoordinator
    {
        public int AbortCount { get; private set; }
        public int CommitCount { get; private set; }

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
            CommitCount++;
        }

        public void AbortReplace(ulong newClOrdId) => AbortCount++;
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
