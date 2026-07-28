using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using B3.Trading.Application.Lifecycle;
using B3.Trading.Application.MarketData;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using B3.Trading.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Up = B3.EntryPoint.Client.Models;

namespace B3.Trading.Application.Tests.Outbound;

public sealed class OutboundMutationLedgerTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 7, 18, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void BotBusinessIdentity_RoundTripsIntoOperatorDiagnostics()
    {
        var fixture = Fixture.Create();
        var credentialId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var approved = fixture.Approved with
        {
            Origin = OutboundMutationOrigin.UserBotFixp,
            BotBusinessIdentity = new OutboundBotBusinessIdentity(credentialId, 77),
        };
        fixture.Ledger.Apply(approved);

        var snapshot = Assert.Single(fixture.Ledger.SnapshotMutations());
        Assert.Equal(credentialId, snapshot.BotBusinessIdentity!.CredentialId);
        Assert.Equal(77UL, snapshot.BotBusinessIdentity.ExternalClOrdId);
        var diagnostic = Assert.Single(fixture.Ledger.GetDiagnostics());
        Assert.Equal(credentialId, diagnostic.BotCredentialId);
        Assert.Equal(77UL, diagnostic.ExternalClOrdId);

        var restored = new OutboundMutationLedger(fixture.Protector);
        restored.Restore(
            fixture.Ledger.SnapshotMutations(),
            fixture.Ledger.SnapshotCorrelations());

        var restoredSnapshot = Assert.Single(restored.SnapshotMutations());
        Assert.Equal(snapshot.BotBusinessIdentity, restoredSnapshot.BotBusinessIdentity);
    }

    [Fact]
    public void StateMachine_AppliesOrderedEvidence_Idempotently()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Frame);
        fixture.Ledger.Apply(fixture.Frame);
        fixture.Ledger.Apply(fixture.Write);
        fixture.Ledger.Apply(fixture.Write);

        Assert.True(fixture.Ledger.TryGet(fixture.MutationId, out var mutation));
        Assert.Equal(OutboundMutationState.TransportWriteCompleted, mutation!.State);
        var attempt = Assert.Single(mutation.Attempts);
        Assert.NotNull(attempt.FramePrepared);
        Assert.Equal(T0.AddSeconds(4), attempt.TransportWriteCompletedAtUtc);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Ledger.Apply(fixture.Unsent));
    }

    [Fact]
    public void GatewayMappedFrameIdentity_CanBePersistedDirectly()
    {
        var fixture = Fixture.Create(clOrdId: 101);
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);
        var mapped = B3EntryPointClientGateway.MapFrameIdentity(
            new Up.OutboundFrameIdentity(
                sessionId: 11,
                sessionVerId: 2,
                msgSeqNum: 77,
                Up.OutboundOperationKind.NewOrder,
                new Up.ClOrdID(fixture.ClOrdId),
                encodedFrameLength: 128,
                encodedFrameSha256: new string('A', 64)),
            fixture.Frame.FirmId);
        var frameEvent = fixture.Frame with
        {
            FirmId = mapped.FirmId,
            SessionId = mapped.SessionId,
            SessionVerId = mapped.SessionVerId,
            OutboundSeqNum = mapped.OutboundSeqNum,
            EncodedFrameSha256 = mapped.EncodedFrameSha256,
        };

        fixture.Ledger.Apply(frameEvent);

        Assert.True(fixture.Ledger.TryGet(fixture.MutationId, out var mutation));
        var attempt = Assert.Single(mutation!.Attempts);
        Assert.Equal(new string('a', 64), attempt.FramePrepared!.EncodedFrameSha256);
    }

    [Fact]
    public void StateMachine_RejectsOutOfOrderAndConflictingEvidence()
    {
        var fixture = Fixture.Create();
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Ledger.Apply(fixture.Intent));
        fixture.Ledger.Apply(fixture.Approved);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Ledger.Apply(fixture.Frame));
        fixture.Ledger.Apply(fixture.Intent);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Ledger.Apply(fixture.Intent with
            {
                ClOrdId = 2,
            }));
        fixture.Ledger.Apply(fixture.Frame);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Ledger.Apply(fixture.Frame with
            {
                OutboundSeqNum = 88,
            }));
    }

    [Fact]
    public void RecoveredWriteWithoutFrame_IsQuarantinedButLiveTransitionStillRejects()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Ledger.Apply(fixture.Write));

        fixture.Ledger.ApplyRecovered(fixture.Write);
        fixture.Ledger.ApplyRecovered(fixture.Write);

        var mutation = Assert.Single(fixture.Ledger.SnapshotMutations());
        Assert.Equal(OutboundMutationState.Ambiguous, mutation.State);
        Assert.True(mutation.RequiresReconciliation);
        Assert.Equal(1, fixture.Ledger.ReadinessBlockingCount);
        var attempt = Assert.Single(mutation.Attempts);
        Assert.Null(attempt.FramePrepared);
        Assert.Equal(fixture.Write.CompletedAtUtc, attempt.TransportWriteCompletedAtUtc);
        Assert.Equal(fixture.Write.GatewayReceiptVersion, attempt.GatewayReceiptVersion);
        Assert.Equal(
            OutboundAmbiguityReason.MissingFramePreparedEvidence,
            attempt.AmbiguityReason);
        Assert.All(
            fixture.Ledger.SnapshotCorrelations(),
            correlation => Assert.False(correlation.Terminal));

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Ledger.ApplyRecovered(fixture.Write with
            {
                GatewayReceiptVersion = fixture.Write.GatewayReceiptVersion + 1,
            }));

        var restored = RestoreLedger(fixture);
        var restoredMutation = Assert.Single(restored.SnapshotMutations());
        Assert.Equal(OutboundMutationState.Ambiguous, restoredMutation.State);
        Assert.True(restoredMutation.RequiresReconciliation);
        Assert.Equal(
            OutboundAmbiguityReason.MissingFramePreparedEvidence,
            Assert.Single(restoredMutation.Attempts).AmbiguityReason);
    }

    [Fact]
    public void RecoveredWriteWithoutFrame_RejectsConflictWithProvenUnsentEvidence()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Unsent);

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Ledger.ApplyRecovered(fixture.Write));

        var mutation = Assert.Single(fixture.Ledger.SnapshotMutations());
        Assert.Equal(OutboundMutationState.ProvenUnsent, mutation.State);
        Assert.Null(Assert.Single(mutation.Attempts).TransportWriteCompletedAtUtc);
    }

    [Fact]
    public void Recovery_IntentOnlyIsProvenUnsent_FrameAndWriteAreAmbiguous()
    {
        var intentOnly = Fixture.Create();
        intentOnly.Ledger.Apply(intentOnly.Approved);
        intentOnly.Ledger.Apply(intentOnly.Intent);

        var frame = Fixture.Create(clOrdId: 2);
        frame.Ledger.Apply(frame.Approved);
        frame.Ledger.Apply(frame.Intent);
        frame.Ledger.Apply(frame.Frame);

        var write = Fixture.Create(clOrdId: 3);
        write.Ledger.Apply(write.Approved);
        write.Ledger.Apply(write.Intent);
        write.Ledger.Apply(write.Frame);
        write.Ledger.Apply(write.Write);

        var activeEpoch = new ProcessEpochId(Guid.Parse(
            "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"));
        Assert.Equal(1, intentOnly.Ledger.ClassifyRecoveredAttempts(activeEpoch, T0.AddMinutes(1)));
        Assert.Equal(1, frame.Ledger.ClassifyRecoveredAttempts(activeEpoch, T0.AddMinutes(1)));
        Assert.Equal(1, write.Ledger.ClassifyRecoveredAttempts(activeEpoch, T0.AddMinutes(1)));

        AssertState(intentOnly, OutboundMutationState.ProvenUnsent);
        AssertState(frame, OutboundMutationState.Ambiguous);
        AssertState(write, OutboundMutationState.Ambiguous);
        Assert.Equal(1, intentOnly.Ledger.ReadinessBlockingCount);
        Assert.Equal(1, frame.Ledger.ReadinessBlockingCount);
        Assert.Equal(1, write.Ledger.ReadinessBlockingCount);
    }

    [Fact]
    public async Task ColdStartCoordinator_CommitsIntentOnlyProvenUnsent_AndDoesNotResendFramePrepared()
    {
        var intentOnly = Fixture.Create();
        intentOnly.Ledger.Apply(intentOnly.Approved);
        intentOnly.Ledger.Apply(intentOnly.Intent);
        var coordinator = new OutboundColdStartRecoveryCoordinator(
            intentOnly.Ledger,
            new OutboundProcessEpoch(ProcessEpochId.New()),
            new EventDispatcher(new NullEventStore()),
            NullLogger<OutboundColdStartRecoveryCoordinator>.Instance);

        var result = await coordinator.RunAsync();

        Assert.Equal(1, result.ProvenUnsent);
        AssertState(intentOnly, OutboundMutationState.ProvenUnsent);
        Assert.Equal(
            OutboundProvenUnsentEvidence.DeadEpochIntentWithoutFrame,
            intentOnly.Ledger.SnapshotMutations().Single().Attempts.Single().ProvenUnsentEvidence);

        var framed = Fixture.Create(clOrdId: 202);
        framed.Ledger.Apply(framed.Approved);
        framed.Ledger.Apply(framed.Intent);
        framed.Ledger.Apply(framed.Frame);
        var framedCoordinator = new OutboundColdStartRecoveryCoordinator(
            framed.Ledger,
            new OutboundProcessEpoch(ProcessEpochId.New()),
            new EventDispatcher(new NullEventStore()),
            NullLogger<OutboundColdStartRecoveryCoordinator>.Instance);

        result = await framedCoordinator.RunAsync();

        Assert.Equal(1, result.Ambiguous);
        AssertState(framed, OutboundMutationState.Ambiguous);
        Assert.Equal(
            OutboundAmbiguityReason.DeadEpochFramePrepared,
            framed.Ledger.SnapshotMutations().Single().Attempts.Single().AmbiguityReason);
    }

    [Fact]
    public void RecoveryGate_BlocksOnlyFirmsCapturedDuringColdClassification()
    {
        var recovered = Fixture.Create();
        recovered.Ledger.Apply(recovered.Approved);
        recovered.Ledger.Apply(recovered.Intent);
        recovered.Ledger.Apply(recovered.Frame);
        recovered.Ledger.MarkAmbiguous(
            recovered.MutationId,
            recovered.AttemptId,
            OutboundAmbiguityReason.DeadEpochFramePrepared,
            T0.AddMinutes(1));
        var state = new OutboundRecoveryState(recovered.Ledger);
        state.ConfigureRequiredFirms(["F2"]);

        state.Complete();

        Assert.True(state.IsReady);
        Assert.False(state.IsBusinessIngressOpen("F1"));
        Assert.True(state.IsBusinessIngressOpen("F2"));

        var live = Fixture.Create(clOrdId: 202);
        recovered.Ledger.Apply(live.Approved with { FirmId = "F2" });
        recovered.Ledger.Apply(live.Intent);
        recovered.Ledger.Apply(live.Frame with { FirmId = "F2" });
        recovered.Ledger.MarkAmbiguous(
            live.MutationId,
            live.AttemptId,
            OutboundAmbiguityReason.GatewayOutcomeUnknown,
            T0.AddMinutes(2));

        Assert.True(state.IsBusinessIngressOpen("F2"));
    }

    [Fact]
    public void RetryAfterProvenUnsent_RequiresFreshAttemptAndClOrdId_AndIsFinite()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Unsent);

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Ledger.Apply(fixture.Intent with
            {
                AttemptId = OutboundAttemptId.New(),
                AttemptNo = 2,
            }));
        var retry = fixture.Intent with
        {
            AttemptId = new OutboundAttemptId(Guid.Parse(
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
            AttemptNo = 2,
            ClOrdId = 2,
            IntentPreparedAtUtc = T0.AddSeconds(6),
            TimestampUtc = T0.AddSeconds(6),
        };
        fixture.Ledger.Apply(retry);

        fixture.Ledger.Apply(fixture.Unsent);
        fixture.Ledger.Apply(new OutboundProvenUnsentEvent
        {
            MutationId = fixture.MutationId,
            AttemptId = retry.AttemptId,
            Evidence = OutboundProvenUnsentEvidence.TypedPreFrameFailure,
            TimestampUtc = T0.AddSeconds(7),
        });
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Ledger.Apply(retry with
            {
                AttemptId = OutboundAttemptId.New(),
                AttemptNo = 3,
                ClOrdId = 3,
            }));
    }

    [Fact]
    public void LegacyPendingRowsAndSidecars_AreRetainedAndDrainReadiness()
    {
        var ledger = new OutboundMutationLedger();
        ledger.ImportLegacyNew(new OrderSubmittedEvent
        {
            ClOrdId = 1,
            EndClientId = "customer-secret",
            FirmId = "F1",
            Symbol = "PETR4",
            SecurityId = 123,
            Side = "Buy",
            Type = "Limit",
            Quantity = 10,
            Price = 30m,
            TimestampUtc = T0,
        });
        ledger.ImportLegacyCancel(new OrderCancelRequestedEvent
        {
            CancelClOrdId = 2,
            OriginalClOrdId = 1,
            OwnerEndClientId = "customer-secret",
            TimestampUtc = T0,
        });
        ledger.ImportLegacyReplace(new OrderReplaceRequestedEvent
        {
            OriginalClOrdId = 4,
            NewClOrdId = 3,
            EndClientId = "customer-secret",
            FirmId = "F1",
            Symbol = "PETR4",
            SecurityId = 123,
            Side = "Buy",
            Type = "Limit",
            NewQuantity = 20,
            NewPrice = 31m,
            TimestampUtc = T0,
        });
        ledger.ImportReconciliationMarker(new ReconciliationMarker(
            ReconciliationMarkerKind.ReplaceAmbiguous,
            OriginalClOrdId: 4,
            MutationClOrdId: 3,
            OwnerEndClientId: "customer-secret",
            NewRemainingNotional: 620m,
            AmbiguousAtUtc: T0.AddSeconds(1)));

        Assert.Equal(3, ledger.Count);
        Assert.Equal(3, ledger.ReadinessBlockingCount);
        Assert.Contains(ledger.SnapshotMutations(),
            m => m.State == OutboundMutationState.LegacyUnknown);
        Assert.Contains(ledger.SnapshotMutations(),
            m => m.State == OutboundMutationState.LegacyUnknownCancel);
        Assert.Contains(ledger.SnapshotMutations(),
            m => m.State == OutboundMutationState.Ambiguous);
        Assert.All(ledger.SnapshotCorrelations(), c => Assert.False(c.Terminal));
    }

    [Theory]
    [InlineData(OutboundMutationKind.Cancel)]
    [InlineData(OutboundMutationKind.Replace)]
    public void LegacyPendingMutation_BlocksNewMutationForSameOriginal(
        OutboundMutationKind legacyKind)
    {
        var fixture = Fixture.Create(clOrdId: 99);
        if (legacyKind == OutboundMutationKind.Cancel)
        {
            fixture.Ledger.ImportLegacyNew(new OrderSubmittedEvent
            {
                ClOrdId = 1,
                EndClientId = "customer-secret",
                FirmId = "F1",
                Symbol = "PETR4",
                SecurityId = 123,
                Side = "Buy",
                Type = "Limit",
                Quantity = 10,
                Price = 30m,
                TimestampUtc = T0,
            });
            fixture.Ledger.ImportLegacyCancel(new OrderCancelRequestedEvent
            {
                CancelClOrdId = 2,
                OriginalClOrdId = 1,
                OwnerEndClientId = "customer-secret",
                TimestampUtc = T0,
            });
        }
        else
        {
            fixture.Ledger.ImportLegacyReplace(new OrderReplaceRequestedEvent
            {
                OriginalClOrdId = 1,
                NewClOrdId = 2,
                EndClientId = "customer-secret",
                FirmId = "F1",
                Symbol = "PETR4",
                SecurityId = 123,
                Side = "Buy",
                Type = "Limit",
                NewQuantity = 20,
                NewPrice = 31m,
                TimestampUtc = T0,
            });
        }

        Assert.True(fixture.Ledger.TryGetActiveForOriginal("F1", 1, out var active));
        Assert.Equal(legacyKind, active!.Kind);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Ledger.Apply(fixture.Approved with
            {
                MutationKind = OutboundMutationKind.Cancel,
                OriginalClOrdId = 1,
            }));
        fixture.Ledger.ImportLegacyProvenUnsent(
            2,
            legacyKind,
            1,
            T0.AddSeconds(1),
            legacyKind == OutboundMutationKind.Cancel
                ? OutboundProvenUnsentEvidence.LegacyWave1CancelPreSend
                : OutboundProvenUnsentEvidence.LegacyWave1ReplacePreSend);
        Assert.False(fixture.Ledger.TryGetActiveForOriginal("F1", 1, out _));
        fixture.Ledger.Apply(fixture.Approved with
        {
            MutationKind = OutboundMutationKind.Cancel,
            OriginalClOrdId = 1,
        });
        Assert.True(fixture.Ledger.TryGetActiveForOriginal("F1", 1, out var fresh));
        Assert.Equal(fixture.MutationId, fresh!.MutationId);
    }

    [Fact]
    public void RestoredLegacyAmbiguousReplace_NeverExpiresOrReleasesItsFence()
    {
        var intent = new OrderReplacementIntent(
            OriginalClOrdId: 1,
            NewClOrdId: 2,
            Owner: new EndClientId("sensitive-owner"),
            Symbol: "PETR4",
            SecurityId: 123,
            Side: OrderSide.Buy,
            Type: OrderType.Limit,
            NewQuantity: 20,
            NewPrice: 31m,
            FirmId: "F1",
            ParentAlgoId: null,
            AlgoSliceSeq: null);
        var registry = new PendingReplacementRegistry();
        registry.Restore(
        [
            new PendingReplacementEntrySnapshot(
                intent,
                T0,
                AmbiguousMarginHeld: true,
                AmbiguousAt: T0,
                NewRemainingNotional: 620m),
        ]);

        Assert.Empty(registry.SweepExpiredAmbiguous(
            T0.AddYears(10), TimeSpan.FromMinutes(1)));
        Assert.True(registry.TryGet(2, out _));
        Assert.True(registry.IsAmbiguous(2));
    }

    [Fact]
    public void StaleWave1Sidecar_CannotOverrideCommittedVenueResolution()
    {
        var ledger = new OutboundMutationLedger();
        ledger.ImportLegacyReplace(new OrderReplaceRequestedEvent
        {
            OriginalClOrdId = 1,
            NewClOrdId = 2,
            EndClientId = "sensitive-owner",
            FirmId = "F1",
            Symbol = "PETR4",
            SecurityId = 123,
            Side = "Buy",
            Type = "Limit",
            NewQuantity = 10,
            NewPrice = 30m,
            TimestampUtc = T0,
        });
        ledger.ApplyVenueAcknowledgement(new ExecutionReportReceivedEvent
        {
            ClOrdId = 2,
            OrigClOrdId = 1,
            ExecKind = "Cancelled",
            LeavesQuantity = 0,
            CumulativeQuantity = 0,
            LastQuantity = 0,
            LastPrice = 0,
            Synthetic = false,
            FirmId = "F1",
            TimestampUtc = T0.AddSeconds(1),
        });
        ledger.ImportReconciliationMarker(new ReconciliationMarker(
            ReconciliationMarkerKind.ReplacePreSend,
            OriginalClOrdId: 1,
            MutationClOrdId: 2,
            OwnerEndClientId: "sensitive-owner"));

        var mutation = Assert.Single(ledger.SnapshotMutations());
        Assert.Equal(OutboundMutationState.ProvenUnsent, mutation.State);
        Assert.True(Assert.Single(ledger.SnapshotCorrelations()).Terminal);
        Assert.Single(ledger.GetInboundEvidenceDiagnostics());
    }

    [Fact]
    public void VenueAcknowledgement_FirmOrSessionMismatchFailsClosed()
    {
        var wrongFirm = Fixture.Create();
        wrongFirm.Ledger.Apply(wrongFirm.Approved);
        wrongFirm.Ledger.Apply(wrongFirm.Intent);
        wrongFirm.Ledger.Apply(wrongFirm.Frame);
        wrongFirm.Ledger.ApplyVenueAcknowledgement(Acknowledgement(
            wrongFirm, firmId: "OTHER", sessionId: 11, sessionVerId: 2));

        AssertState(wrongFirm, OutboundMutationState.Ambiguous);
        Assert.Equal(1, wrongFirm.Ledger.ReadinessBlockingCount);
        Assert.Null(wrongFirm.Ledger.SnapshotMutations()[0].Resolution);
        Assert.All(wrongFirm.Ledger.SnapshotCorrelations(),
            correlation => Assert.False(correlation.Terminal));

        var wrongSession = Fixture.Create(clOrdId: 2);
        wrongSession.Ledger.Apply(wrongSession.Approved);
        wrongSession.Ledger.Apply(wrongSession.Intent);
        wrongSession.Ledger.Apply(wrongSession.Frame);
        wrongSession.Ledger.ApplyVenueAcknowledgement(Acknowledgement(
            wrongSession, firmId: "F1", sessionId: 99, sessionVerId: 2));

        AssertState(wrongSession, OutboundMutationState.Ambiguous);
        Assert.Equal(
            OutboundAmbiguityReason.ConflictingVenueEvidence,
            Assert.Single(wrongSession.Ledger.SnapshotMutations()[0].Attempts)
                .AmbiguityReason);
    }

    [Fact]
    public void MultipleVenueAcknowledgements_AfterSessionVersionRoll_RemainDomainApplicable()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Frame);

        var first = fixture.Ledger.ApplyVenueAcknowledgement(Acknowledgement(
            fixture,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 3) with
        {
            ExecKind = "PartialFill",
            InboundSeqNum = 88,
        });
        var second = fixture.Ledger.ApplyVenueAcknowledgement(Acknowledgement(
            fixture,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 3) with
        {
            ExecKind = "PartialFill",
            InboundSeqNum = 89,
        });

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedUnmatched, first.Status);
        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedUnmatched, second.Status);
        Assert.True(first.ShouldApplyDomain);
        Assert.True(second.ShouldApplyDomain);
        AssertState(fixture, OutboundMutationState.Ambiguous);
        Assert.Equal(
            OutboundAmbiguityReason.SessionVersionMismatchEvidence,
            Assert.Single(fixture.Ledger.SnapshotMutations()[0].Attempts).AmbiguityReason);
        Assert.All(
            fixture.Ledger.CaptureSnapshot().InboundEvidence,
            evidence => Assert.Equal(
                InboundVenueEvidenceDisposition.Unmatched,
                evidence.Disposition));
        Assert.Equal(1, fixture.Ledger.ReadinessBlockingCount);
    }

    [Fact]
    public void LateExecutionReport_AfterSessionVersionRoll_AppliesDomainWithoutChangingTerminalResolution()
    {
        var fixture = Fixture.Create();
        ApplyPrepared(fixture.Ledger, fixture, fixture.Frame);
        var acknowledged = fixture.Ledger.ApplyVenueAcknowledgement(Acknowledgement(
            fixture,
            firmId: "F1",
            sessionId: fixture.Frame.SessionId,
            sessionVerId: fixture.Frame.SessionVerId));
        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedMatched, acknowledged.Status);

        var lateFill = fixture.Ledger.ApplyVenueAcknowledgement(Acknowledgement(
            fixture,
            firmId: "F1",
            sessionId: fixture.Frame.SessionId,
            sessionVerId: fixture.Frame.SessionVerId + 1) with
        {
            ExecKind = "Fill",
            InboundSeqNum = 89,
        });

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedUnmatched, lateFill.Status);
        Assert.True(lateFill.ShouldApplyDomain);
        AssertState(fixture, OutboundMutationState.VenueAcknowledged);
        Assert.Equal(0, fixture.Ledger.ReadinessBlockingCount);
    }

    [Fact]
    public void VenueAcknowledgement_ForProvenUnsentOrSupersededAttemptStaysConflicting()
    {
        var provenUnsent = Fixture.Create();
        provenUnsent.Ledger.Apply(provenUnsent.Approved);
        provenUnsent.Ledger.Apply(provenUnsent.Intent);
        provenUnsent.Ledger.Apply(provenUnsent.Unsent);
        provenUnsent.Ledger.ApplyVenueAcknowledgement(Acknowledgement(
            provenUnsent, firmId: "F1", sessionId: 11, sessionVerId: 2));
        AssertState(provenUnsent, OutboundMutationState.Ambiguous);
        Assert.Equal(1, provenUnsent.Ledger.ReadinessBlockingCount);

        var superseded = Fixture.Create();
        superseded.Ledger.Apply(superseded.Approved);
        superseded.Ledger.Apply(superseded.Intent);
        superseded.Ledger.Apply(superseded.Unsent);
        var retryId = new OutboundAttemptId(Guid.Parse(
            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        superseded.Ledger.Apply(superseded.Intent with
        {
            AttemptId = retryId,
            AttemptNo = 2,
            ClOrdId = 2,
            IntentPreparedAtUtc = T0.AddSeconds(6),
            TimestampUtc = T0.AddSeconds(6),
        });
        superseded.Ledger.Apply(superseded.Frame with
        {
            AttemptId = retryId,
            PreparedAtUtc = T0.AddSeconds(7),
            TimestampUtc = T0.AddSeconds(7),
        });

        superseded.Ledger.ApplyVenueAcknowledgement(Acknowledgement(
            superseded, firmId: "F1", sessionId: 11, sessionVerId: 2));
        AssertState(superseded, OutboundMutationState.Ambiguous);
        Assert.Equal(1, superseded.Ledger.ReadinessBlockingCount);

        superseded.Ledger.ApplyVenueAcknowledgement(Acknowledgement(
            superseded,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2,
            clOrdId: 2));
        AssertState(superseded, OutboundMutationState.Ambiguous);
        Assert.Null(superseded.Ledger.SnapshotMutations()[0].Resolution);
    }

    [Fact]
    public void LateExecutionReportForOlderAttempt_ReopensTerminalLatestAttempt()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Unsent);
        var retryId = new OutboundAttemptId(Guid.Parse(
            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        fixture.Ledger.Apply(fixture.Intent with
        {
            AttemptId = retryId,
            AttemptNo = 2,
            ClOrdId = 2,
            IntentPreparedAtUtc = T0.AddSeconds(6),
            TimestampUtc = T0.AddSeconds(6),
        });
        fixture.Ledger.Apply(fixture.Frame with
        {
            AttemptId = retryId,
            PreparedAtUtc = T0.AddSeconds(7),
            TimestampUtc = T0.AddSeconds(7),
        });
        fixture.Ledger.ApplyVenueAcknowledgement(Acknowledgement(
            fixture,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2,
            clOrdId: 2));

        var result = fixture.Ledger.ApplyVenueAcknowledgement(Acknowledgement(
            fixture,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2) with
        {
            ExecKind = "Fill",
            InboundSeqNum = 91,
            TimestampUtc = T0.AddMinutes(3),
        });

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedConflicting, result.Status);
        Assert.True(result.ReopenedReconciliation);
        var mutation = Assert.Single(fixture.Ledger.SnapshotMutations());
        Assert.Equal(OutboundMutationState.Ambiguous, mutation.State);
        Assert.True(mutation.RequiresReconciliation);
        Assert.Equal(1, fixture.Ledger.ReadinessBlockingCount);
    }

    [Fact]
    public void CancelAcknowledgement_MissingOrigClOrdIdUsesExactDirectAttempt()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved with
        {
            MutationKind = OutboundMutationKind.Cancel,
            OriginalClOrdId = 50,
        });
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Frame);

        var result = fixture.Ledger.ApplyVenueAcknowledgement(
            Acknowledgement(
                fixture,
                firmId: "F1",
                sessionId: 11,
                sessionVerId: 2) with
            {
                ExecKind = "Canceled",
                OrigClOrdId = 0,
            });

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedMatched, result.Status);
        AssertState(fixture, OutboundMutationState.VenueAcknowledged);
    }

    [Theory]
    [InlineData("New", OutboundMutationKind.New, 0UL)]
    [InlineData("Rejected", OutboundMutationKind.New, 0UL)]
    [InlineData("Fill", OutboundMutationKind.New, 0UL)]
    [InlineData("Canceled", OutboundMutationKind.Cancel, 50UL)]
    [InlineData("Replaced", OutboundMutationKind.Replace, 50UL)]
    public void Pre640WalEr_MissingSessionEvidenceRemainsUnmatchedAndFailClosed(
        string execKind,
        OutboundMutationKind mutationKind,
        ulong originalClOrdId)
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved with
        {
            MutationKind = mutationKind,
            OriginalClOrdId = originalClOrdId == 0 ? null : originalClOrdId,
        });
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Frame);
        var oldEr = OldEr(fixture.ClOrdId, execKind, originalClOrdId) with
        {
            FirmId = "F1",
        };

        var result = fixture.Ledger.ApplyVenueAcknowledgement(oldEr);

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedUnmatched, result.Status);
        Assert.True(result.ShouldApplyDomain);
        AssertState(fixture, OutboundMutationState.Ambiguous);
        var mutation = Assert.Single(fixture.Ledger.SnapshotMutations());
        Assert.Null(mutation.Resolution);
        Assert.Equal(
            OutboundAmbiguityReason.IncompleteVenueEvidence,
            Assert.Single(mutation.Attempts).AmbiguityReason);
        Assert.Equal(1, fixture.Ledger.ReadinessBlockingCount);
        Assert.Equal(
            InboundVenueEvidenceDisposition.Unmatched,
            Assert.Single(fixture.Ledger.CaptureSnapshot().InboundEvidence).Disposition);
    }

    [Fact]
    public void Pre640WalEr_PartialEvidenceAppliesUnlessAProvidedFieldMismatches()
    {
        var partial = Fixture.Create();
        ApplyPrepared(partial.Ledger, partial, partial.Frame);
        var missingInboundSeq = OldEr(partial.ClOrdId, "New") with
        {
            FirmId = "F1",
            SessionId = 11,
            SessionVerId = 2,
        };

        var partialResult = partial.Ledger.ApplyVenueAcknowledgement(missingInboundSeq);

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedUnmatched, partialResult.Status);
        Assert.True(partialResult.ShouldApplyDomain);
        AssertState(partial, OutboundMutationState.Ambiguous);

        var mismatch = Fixture.Create(clOrdId: 2);
        ApplyPrepared(mismatch.Ledger, mismatch, mismatch.Frame);
        var wrongProvidedSession = OldEr(mismatch.ClOrdId, "New") with
        {
            FirmId = "F1",
            SessionId = 99,
        };

        var mismatchResult = mismatch.Ledger.ApplyVenueAcknowledgement(
            wrongProvidedSession);

        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedConflicting,
            mismatchResult.Status);
        Assert.False(mismatchResult.ShouldApplyDomain);
        AssertState(mismatch, OutboundMutationState.Ambiguous);
    }

    [Fact]
    public void Pre640WalEr_FirmMismatchRemainsConflicting()
    {
        var fixture = Fixture.Create();
        ApplyPrepared(fixture.Ledger, fixture, fixture.Frame);

        var result = fixture.Ledger.ApplyVenueAcknowledgement(
            OldEr(fixture.ClOrdId, "New") with { FirmId = "OTHER" });

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedConflicting, result.Status);
        Assert.False(result.ShouldApplyDomain);
        AssertState(fixture, OutboundMutationState.Ambiguous);
    }

    [Fact]
    public void Pre640WalEr_CannotOverridePriorConflictOrProvenUnsent()
    {
        var conflicting = Fixture.Create();
        ApplyPrepared(conflicting.Ledger, conflicting, conflicting.Frame);
        conflicting.Ledger.ApplyVenueAcknowledgement(
            Acknowledgement(
                conflicting,
                firmId: "F1",
                sessionId: 99,
                sessionVerId: 2));

        var afterConflict = conflicting.Ledger.ApplyVenueAcknowledgement(
            OldEr(conflicting.ClOrdId, "New") with { FirmId = "F1" });

        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedConflicting,
            afterConflict.Status);
        Assert.False(afterConflict.ShouldApplyDomain);
        Assert.Equal(
            OutboundAmbiguityReason.ConflictingVenueEvidence,
            Assert.Single(conflicting.Ledger.SnapshotMutations()).Attempts[^1]
                .AmbiguityReason);

        var provenUnsent = Fixture.Create(clOrdId: 2);
        provenUnsent.Ledger.Apply(provenUnsent.Approved);
        provenUnsent.Ledger.Apply(provenUnsent.Intent);
        provenUnsent.Ledger.Apply(provenUnsent.Unsent);

        var afterProvenUnsent = provenUnsent.Ledger.ApplyVenueAcknowledgement(
            OldEr(provenUnsent.ClOrdId, "New") with { FirmId = "F1" });

        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedConflicting,
            afterProvenUnsent.Status);
        Assert.False(afterProvenUnsent.ShouldApplyDomain);
        AssertState(provenUnsent, OutboundMutationState.Ambiguous);
    }

    [Theory]
    [InlineData("New", "Working", 0L)]
    [InlineData("Rejected", "Rejected", 0L)]
    [InlineData("Fill", "Filled", 10L)]
    public void EventReplayer_Pre640NewRejectAndFillStillUpdateDomain(
        string execKind,
        string expectedStatus,
        long expectedCumulativeQuantity)
    {
        var fixture = Fixture.Create();
        var ownership = new OrderOwnershipMap();
        var orders = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var processor = new ExecutionReportProcessor(
            ownership,
            orders,
            positions,
            new NoOpExecutionEventSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        var replayer = new EventReplayer(
            orders,
            ownership,
            new KillSwitchService(),
            new SymbolHaltService(),
            new SessionPhaseService(),
            processor,
            new AlgoBook(),
            new ClOrdIdPrefixRegistry(),
            new AlgoIdRegistry(),
            outboundLedger: fixture.Ledger);
        replayer.Apply(LegacySubmit(fixture.ClOrdId));
        replayer.Apply(fixture.Approved);
        replayer.Apply(fixture.Intent);
        replayer.Apply(fixture.Frame);
        var oldEr = OldEr(fixture.ClOrdId, execKind) with
        {
            FirmId = "F1",
            LeavesQuantity = expectedCumulativeQuantity == 10 ? 0 : 10,
            CumulativeQuantity = expectedCumulativeQuantity,
            LastQuantity = expectedCumulativeQuantity,
            LastPrice = expectedCumulativeQuantity == 0 ? 0 : 30m,
        };

        replayer.Apply(oldEr);

        Assert.True(orders.TryGet(fixture.ClOrdId, out var order));
        Assert.NotNull(order);
        Assert.Equal(expectedStatus, order.Status.ToString());
        Assert.Equal(expectedCumulativeQuantity, order.CumulativeQuantity);
        AssertState(fixture, OutboundMutationState.Ambiguous);
        Assert.Equal(1, fixture.Ledger.ReadinessBlockingCount);
    }

    [Fact]
    public void EventReplayer_Pre640CancelStillUpdatesDomain()
    {
        var fixture = Fixture.Create();
        var ownership = new OrderOwnershipMap();
        var orders = new WorkingOrderBook();
        var pendingCancels = new PendingCancelRegistry();
        var processor = new ExecutionReportProcessor(
            ownership,
            orders,
            new PositionKeeper(),
            new NoOpExecutionEventSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            pendingCancels: pendingCancels);
        var replayer = new EventReplayer(
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
            outboundLedger: fixture.Ledger);
        replayer.Apply(LegacySubmit(50));
        replayer.Apply(fixture.Approved with
        {
            MutationKind = OutboundMutationKind.Cancel,
            OriginalClOrdId = 50,
        });
        replayer.Apply(fixture.Intent);
        replayer.Apply(fixture.Frame);
        replayer.Apply(new OrderCancelRequestedEvent
        {
            CancelClOrdId = fixture.ClOrdId,
            OriginalClOrdId = 50,
            OwnerEndClientId = "sensitive-owner",
            TimestampUtc = T0.AddSeconds(1),
        });

        replayer.Apply(OldEr(fixture.ClOrdId, "Canceled", origClOrdId: 50) with
        {
            FirmId = "F1",
        });

        Assert.True(orders.TryGet(50, out var original));
        Assert.NotNull(original);
        Assert.Equal(OrderStatus.Cancelled, original.Status);
        Assert.Empty(pendingCancels.Snapshot());
        AssertState(fixture, OutboundMutationState.Ambiguous);
        Assert.Equal(2, fixture.Ledger.ReadinessBlockingCount);
    }

    [Fact]
    public void EventReplayer_Pre640ReplaceStillUpdatesDomain()
    {
        var fixture = Fixture.Create();
        var ownership = new OrderOwnershipMap();
        var orders = new WorkingOrderBook();
        var replacements = new PendingReplacementRegistry();
        var processor = new ExecutionReportProcessor(
            ownership,
            orders,
            new PositionKeeper(),
            new NoOpExecutionEventSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            replacements: replacements);
        var replayer = new EventReplayer(
            orders,
            ownership,
            new KillSwitchService(),
            new SymbolHaltService(),
            new SessionPhaseService(),
            processor,
            new AlgoBook(),
            new ClOrdIdPrefixRegistry(),
            new AlgoIdRegistry(),
            replacements: replacements,
            outboundLedger: fixture.Ledger);
        replayer.Apply(LegacySubmit(50));
        replayer.Apply(fixture.Approved with
        {
            MutationKind = OutboundMutationKind.Replace,
            OriginalClOrdId = 50,
        });
        replayer.Apply(fixture.Intent);
        replayer.Apply(fixture.Frame);
        replayer.Apply(new OrderReplaceRequestedEvent
        {
            OriginalClOrdId = 50,
            NewClOrdId = fixture.ClOrdId,
            EndClientId = "sensitive-owner",
            FirmId = "F1",
            Symbol = "PETR4",
            SecurityId = 123,
            Side = "Buy",
            Type = "Limit",
            NewQuantity = 20,
            NewPrice = 31m,
            TimestampUtc = T0.AddSeconds(1),
        });

        replayer.Apply(OldEr(fixture.ClOrdId, "Replaced", origClOrdId: 50) with
        {
            FirmId = "F1",
            LeavesQuantity = 20,
        });

        Assert.True(orders.TryGet(50, out var original));
        Assert.NotNull(original);
        Assert.Equal(OrderStatus.Replaced, original.Status);
        Assert.True(orders.TryGet(fixture.ClOrdId, out var replacement));
        Assert.NotNull(replacement);
        Assert.Equal(OrderStatus.Working, replacement.Status);
        Assert.Empty(replacements.Snapshot());
        AssertState(fixture, OutboundMutationState.Ambiguous);
        Assert.Equal(2, fixture.Ledger.ReadinessBlockingCount);
    }

    [Fact]
    public void SnapshotAndPre640Tail_PreserveDomainApplyAndUnprovenReadiness()
    {
        var writer = Fixture.Create();
        ApplyPrepared(writer.Ledger, writer, writer.Frame);
        writer.Ledger.ApplyVenueAcknowledgement(
            OldEr(writer.ClOrdId, "New") with { FirmId = "F1" });
        var capture = writer.Ledger.CaptureSnapshot();
        var restoredLedger = new OutboundMutationLedger(writer.Protector);
        restoredLedger.Restore(
            capture.Mutations,
            capture.Correlations,
            capture.InboundEvidence);
        var ownership = new OrderOwnershipMap();
        var orders = new WorkingOrderBook();
        var owner = new EndClientId("sensitive-owner");
        var order = new Order(
            writer.ClOrdId,
            owner,
            "PETR4",
            123,
            OrderSide.Buy,
            OrderType.Limit,
            10,
            30m,
            "F1");
        order.MarkWorking();
        Assert.True(orders.TryAdd(order));
        ownership.Register(writer.ClOrdId, owner);
        var replayer = new EventReplayer(
            orders,
            ownership,
            new KillSwitchService(),
            new SymbolHaltService(),
            new SessionPhaseService(),
            new ExecutionReportProcessor(
                ownership,
                orders,
                new PositionKeeper(),
                new NoOpExecutionEventSink(),
                new NoOpMarginProvider(),
                NullLogger<ExecutionReportProcessor>.Instance),
            new AlgoBook(),
            new ClOrdIdPrefixRegistry(),
            new AlgoIdRegistry(),
            outboundLedger: restoredLedger);

        replayer.Apply(OldEr(writer.ClOrdId, "Fill") with
        {
            FirmId = "F1",
            LeavesQuantity = 0,
            CumulativeQuantity = 10,
            LastQuantity = 10,
            LastPrice = 30m,
            TimestampUtc = T0.AddMinutes(3),
        });

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(10, order.CumulativeQuantity);
        AssertState(restoredLedger, writer.MutationId, OutboundMutationState.Ambiguous);
        Assert.Equal(1, restoredLedger.ReadinessBlockingCount);
        Assert.Equal(2, restoredLedger.InboundEvidenceCount);
    }

    [Fact]
    public void SnapshotRestore_Pre640MissingFirmDuplicateRemainsIdempotent()
    {
        var writer = Fixture.Create();
        ApplyPrepared(writer.Ledger, writer, writer.Frame);
        var oldEr = OldEr(writer.ClOrdId, "New");
        var first = writer.Ledger.ApplyVenueAcknowledgement(oldEr);
        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedUnmatched, first.Status);
        var capture = writer.Ledger.CaptureSnapshot();
        var restored = new OutboundMutationLedger(writer.Protector);
        restored.Restore(
            capture.Mutations,
            capture.Correlations,
            capture.InboundEvidence);

        var duplicate = restored.ApplyVenueAcknowledgement(oldEr);

        Assert.Equal(InboundVenueEvidenceApplyStatus.Duplicate, duplicate.Status);
        Assert.False(duplicate.ShouldApplyDomain);
        Assert.Equal(1, restored.InboundEvidenceCount);
        AssertState(restored, writer.MutationId, OutboundMutationState.Ambiguous);
        Assert.Equal(1, restored.ReadinessBlockingCount);
    }

    [Fact]
    public void LateConflictingEr_CannotReopenOrRewriteTerminalTombstone()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Frame);
        fixture.Ledger.ApplyVenueAcknowledgement(Acknowledgement(
            fixture, firmId: "F1", sessionId: 11, sessionVerId: 2));
        var resolved = fixture.Ledger.SnapshotMutations()[0].Resolution;

        fixture.Ledger.ApplyVenueAcknowledgement(Acknowledgement(
            fixture, firmId: "OTHER", sessionId: 99, sessionVerId: 9));

        AssertState(fixture, OutboundMutationState.VenueAcknowledged);
        Assert.Equal(resolved, fixture.Ledger.SnapshotMutations()[0].Resolution);
        Assert.All(fixture.Ledger.SnapshotCorrelations(),
            correlation => Assert.True(correlation.Terminal));
    }

    [Fact]
    public void ConflictingLegacySidecars_AreMonotonicIdempotentAndSnapshotDurable()
    {
        var ambiguousFirst = LegacyReplaceLedger();
        ambiguousFirst.ImportLegacyAmbiguous(2, 1, T0.AddSeconds(1));
        ambiguousFirst.ImportLegacyProvenUnsent(
            2, OutboundMutationKind.Replace, 1, T0.AddSeconds(2),
            OutboundProvenUnsentEvidence.LegacyWave1ReplacePreSend);
        ambiguousFirst.ImportLegacyAmbiguous(2, 1, T0.AddSeconds(3));
        ambiguousFirst.ImportLegacyProvenUnsent(
            2, OutboundMutationKind.Replace, 1, T0.AddSeconds(4),
            OutboundProvenUnsentEvidence.LegacyWave1ReplacePreSend);

        var provenFirst = LegacyReplaceLedger();
        provenFirst.ImportLegacyProvenUnsent(
            2, OutboundMutationKind.Replace, 1, T0.AddSeconds(1),
            OutboundProvenUnsentEvidence.LegacyWave1ReplacePreSend);
        provenFirst.ImportLegacyAmbiguous(2, 1, T0.AddSeconds(2));
        provenFirst.ImportLegacyProvenUnsent(
            2, OutboundMutationKind.Replace, 1, T0.AddSeconds(3),
            OutboundProvenUnsentEvidence.LegacyWave1ReplacePreSend);
        provenFirst.ImportLegacyAmbiguous(2, 1, T0.AddSeconds(4));

        foreach (var ledger in new[] { ambiguousFirst, provenFirst })
        {
            var mutation = Assert.Single(ledger.SnapshotMutations());
            Assert.Equal(OutboundMutationState.Ambiguous, mutation.State);
            Assert.True(mutation.RequiresReconciliation);
            Assert.Null(mutation.Resolution);
            Assert.Equal(2, mutation.LegacyEvidence.Count);
            Assert.All(ledger.SnapshotCorrelations(),
                correlation => Assert.False(correlation.Terminal));
        }

        var restored = new OutboundMutationLedger();
        var snapshot = ambiguousFirst.CaptureSnapshot();
        restored.Restore(snapshot.Mutations, snapshot.Correlations);
        var restoredMutation = Assert.Single(restored.SnapshotMutations());
        Assert.Equal(OutboundMutationState.Ambiguous, restoredMutation.State);
        Assert.Equal(2, restoredMutation.LegacyEvidence.Count);
        Assert.Equal(1, restored.ReadinessBlockingCount);
    }

    [Fact]
    public void LegacySyntheticTerminal_DoesNotRemainAsPendingNew()
    {
        var ledger = new OutboundMutationLedger();
        ledger.ImportLegacyNew(new OrderSubmittedEvent
        {
            ClOrdId = 1,
            EndClientId = "sensitive-owner",
            FirmId = "F1",
            Symbol = "PETR4",
            SecurityId = 123,
            Side = "Buy",
            Type = "Limit",
            Quantity = 10,
            Price = 30m,
            TimestampUtc = T0,
        });
        ledger.ApplyVenueAcknowledgement(new ExecutionReportReceivedEvent
        {
            ClOrdId = 1,
            ExecKind = "Rejected",
            LeavesQuantity = 10,
            CumulativeQuantity = 0,
            LastQuantity = 0,
            LastPrice = 0,
            RejectReason = "gateway_unavailable",
            Synthetic = true,
            TimestampUtc = T0.AddSeconds(1),
        });

        var mutation = Assert.Single(ledger.SnapshotMutations());
        Assert.Equal(OutboundMutationState.OperatorResolved, mutation.State);
        Assert.Equal(0, ledger.ReadinessBlockingCount);
    }

    [Fact]
    public void OldWalNullableFirm_LegacyNewCancelAndReplaceRemainUnmatchedUntilProjectionReconciles()
    {
        var legacyNew = new OutboundMutationLedger();
        legacyNew.ImportLegacyNew(LegacySubmit(1));
        legacyNew.ApplyVenueAcknowledgement(OldEr(1, "New"));
        Assert.Equal(
            OutboundMutationState.LegacyUnknown,
            Assert.Single(legacyNew.SnapshotMutations()).State);
        Assert.Equal(1, legacyNew.ReadinessBlockingCount);
        Assert.Equal(1, legacyNew.ReconcileLegacyPendingState([], [], [], T0.AddMinutes(1)));
        Assert.Equal(0, legacyNew.ReadinessBlockingCount);

        var legacyReplace = LegacyReplaceLedger();
        legacyReplace.ApplyVenueAcknowledgement(OldEr(2, "Replaced", origClOrdId: 1));
        Assert.Equal(
            OutboundMutationState.LegacyUnknownReplace,
            Assert.Single(legacyReplace.SnapshotMutations()).State);
        Assert.Equal(1, legacyReplace.ReconcileLegacyPendingState([], [], [], T0.AddMinutes(1)));
        Assert.Equal(0, legacyReplace.ReadinessBlockingCount);

        var legacyCancel = new OutboundMutationLedger();
        legacyCancel.ImportLegacyNew(LegacySubmit(1));
        legacyCancel.ReconcileLegacyPendingState([], [], [], T0.AddSeconds(1));
        legacyCancel.ImportLegacyCancel(new OrderCancelRequestedEvent
        {
            CancelClOrdId = 2,
            OriginalClOrdId = 1,
            OwnerEndClientId = "sensitive-owner",
            TimestampUtc = T0.AddSeconds(2),
        });
        var cancel = Assert.Single(
            legacyCancel.SnapshotMutations(), mutation => mutation.PrimaryClOrdId == 2);
        Assert.Equal("F1", cancel.FirmId);
        legacyCancel.ApplyVenueAcknowledgement(OldEr(2, "Cancelled", origClOrdId: 1));
        Assert.Equal(
            OutboundMutationState.LegacyUnknownCancel,
            Assert.Single(
                legacyCancel.SnapshotMutations(),
                mutation => mutation.PrimaryClOrdId == 2).State);
        Assert.Equal(1, legacyCancel.ReconcileLegacyPendingState([], [], [], T0.AddMinutes(1)));
        Assert.Equal(0, legacyCancel.ReadinessBlockingCount);
    }

    [Fact]
    public void LegacyActualFirmMismatch_FailsClosedWithoutCrossFirmAcknowledgement()
    {
        var legacyNew = new OutboundMutationLedger();
        legacyNew.ImportLegacyNew(LegacySubmit(1));
        legacyNew.ApplyVenueAcknowledgement(OldEr(1, "New") with { FirmId = "OTHER" });
        Assert.Equal(
            OutboundMutationState.Ambiguous,
            Assert.Single(legacyNew.SnapshotMutations()).State);

        var legacyReplace = LegacyReplaceLedger();
        legacyReplace.ApplyVenueAcknowledgement(
            OldEr(2, "Replaced", origClOrdId: 1) with { FirmId = "OTHER" });
        Assert.Equal(
            OutboundMutationState.Ambiguous,
            Assert.Single(legacyReplace.SnapshotMutations()).State);

        var legacyCancel = new OutboundMutationLedger();
        legacyCancel.ImportLegacyCancel(new OrderCancelRequestedEvent
        {
            CancelClOrdId = 2,
            OriginalClOrdId = 1,
            OwnerEndClientId = "sensitive-owner",
            TimestampUtc = T0,
        }, authoritativeFirmId: "F1");
        legacyCancel.ApplyVenueAcknowledgement(
            OldEr(2, "Cancelled", origClOrdId: 1) with { FirmId = "OTHER" });
        var cancel = Assert.Single(legacyCancel.SnapshotMutations());
        Assert.Equal(OutboundMutationState.Ambiguous, cancel.State);
        Assert.Equal(1, legacyCancel.ReadinessBlockingCount);
        Assert.Null(cancel.Resolution);
    }

    [Fact]
    public void LegacyCancelWithoutAuthoritativeFirm_RemainsUnmatchedNotConflicting()
    {
        var ledger = new OutboundMutationLedger();
        ledger.ImportLegacyCancel(new OrderCancelRequestedEvent
        {
            CancelClOrdId = 2,
            OriginalClOrdId = 1,
            OwnerEndClientId = "sensitive-owner",
            TimestampUtc = T0,
        });
        ledger.ApplyVenueAcknowledgement(
            OldEr(2, "Cancelled", origClOrdId: 1) with { FirmId = "F1" });

        var mutation = Assert.Single(ledger.SnapshotMutations());
        Assert.Equal(string.Empty, mutation.FirmId);
        Assert.Equal(OutboundMutationState.LegacyUnknownCancel, mutation.State);
        Assert.Equal(1, ledger.ReconcileLegacyPendingState([], [], [], T0.AddMinutes(1)));
        Assert.Equal(0, ledger.ReadinessBlockingCount);
    }

    [Fact]
    public void EventReplayer_OldWalNullableFirmCancelUsesOriginalOrderFirmAndProjectionReconciliation()
    {
        var ledger = new OutboundMutationLedger();
        var pendingCancels = new PendingCancelRegistry();
        var ownership = new OrderOwnershipMap();
        var orders = new WorkingOrderBook();
        var processor = new ExecutionReportProcessor(
            ownership,
            orders,
            new PositionKeeper(),
            new NoOpExecutionEventSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance,
            pendingCancels: pendingCancels);
        var replayer = new EventReplayer(
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

        replayer.Apply(LegacySubmit(1));
        replayer.Apply(OldEr(1, "New"));
        replayer.Apply(new OrderCancelRequestedEvent
        {
            CancelClOrdId = 2,
            OriginalClOrdId = 1,
            OwnerEndClientId = "sensitive-owner",
            TimestampUtc = T0.AddSeconds(1),
        });
        Assert.Equal(
            "F1",
            Assert.Single(
                ledger.SnapshotMutations(),
                mutation => mutation.PrimaryClOrdId == 2).FirmId);
        replayer.Apply(OldEr(2, "Canceled", origClOrdId: 1));

        Assert.Empty(pendingCancels.Snapshot());
        ledger.ReconcileLegacyPendingState(
            orders.Snapshot()
                .Where(order => order.Status == nameof(OrderStatus.PendingNew))
                .Select(order => order.ClOrdId),
            pendingCancels.Snapshot().Select(cancel => cancel.CancelClOrdId),
            [],
            T0.AddMinutes(1));
        Assert.Equal(0, ledger.ReadinessBlockingCount);
        Assert.All(ledger.SnapshotMutations(),
            mutation => Assert.Equal(OutboundMutationState.LegacyTerminal, mutation.State));
    }

    [Fact]
    public void LegacyMigration_OnlyKeepsRowsWhoseDomainProjectionIsStillPending()
    {
        var ledger = new OutboundMutationLedger();
        ledger.ImportLegacyNew(LegacySubmit(1));
        ledger.ImportLegacyNew(LegacySubmit(2));
        ledger.ImportLegacyCancel(new OrderCancelRequestedEvent
        {
            CancelClOrdId = 3,
            OriginalClOrdId = 1,
            OwnerEndClientId = "sensitive-owner",
            TimestampUtc = T0,
        });

        Assert.Equal(2, ledger.ReconcileLegacyPendingState(
            pendingNewClOrdIds: [2],
            pendingCancelClOrdIds: [],
            pendingReplaceClOrdIds: [],
            atUtc: T0.AddMinutes(1)));

        Assert.Equal(1, ledger.ReadinessBlockingCount);
        Assert.Contains(ledger.SnapshotMutations(),
            m => m.PrimaryClOrdId == 2
                 && m.State == OutboundMutationState.LegacyUnknown);
        Assert.Equal(2, ledger.SnapshotMutations()
            .Count(m => m.State == OutboundMutationState.LegacyTerminal));
    }

    [Fact]
    public void TerminalCorrelationPurge_NeverPurgesUnresolvedOrReducesWatermark()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Frame);
        fixture.Ledger.ApplyVenueAcknowledgement(new ExecutionReportReceivedEvent
        {
            ClOrdId = fixture.ClOrdId,
            ExecKind = "New",
            LeavesQuantity = 10,
            CumulativeQuantity = 0,
            LastQuantity = 0,
            LastPrice = 0,
            Synthetic = false,
            FirmId = "F1",
            SessionId = 11,
            SessionVerId = 2,
            InboundSeqNum = 90,
            TimestampUtc = T0.AddDays(1),
        });

        var unresolved = Fixture.Create(clOrdId: 2);
        unresolved.Ledger.ImportLegacyNew(new OrderSubmittedEvent
        {
            ClOrdId = 2,
            EndClientId = "sensitive-owner",
            FirmId = "F1",
            Symbol = "VALE3",
            SecurityId = 456,
            Side = "Buy",
            Type = "Limit",
            Quantity = 1,
            Price = 1,
            TimestampUtc = T0,
        });
        var registry = new ClOrdIdPrefixRegistry();
        var owner = new EndClientId("sensitive-owner");
        registry.AdvanceCounterTo(owner, 2);

        Assert.Equal(1, fixture.Ledger.PurgeTerminalCorrelations(T0.AddDays(32)));
        Assert.Empty(fixture.Ledger.SnapshotCorrelations());
        Assert.Equal(0, unresolved.Ledger.PurgeTerminalCorrelations(T0.AddYears(10)));
        Assert.Single(unresolved.Ledger.SnapshotMutations());
        Assert.Equal(3UL, registry.Generate(owner));
    }

    [Fact]
    public void CorrelationPurge_RetainsProvenUnsentMutationUntilItIsTerminallyResolved()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Unsent);

        Assert.Equal(0, fixture.Ledger.PurgeTerminalCorrelations(T0.AddYears(10)));
        Assert.Single(fixture.Ledger.SnapshotMutations());
        Assert.Single(fixture.Ledger.SnapshotCorrelations());
    }

    [Fact]
    public void OperatorAnnotation_IsRetainedAndCanBeFollowedByAuthoritativeResolution()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Frame);
        fixture.Ledger.ClassifyRecoveredAttempts(
            new ProcessEpochId(Guid.NewGuid()), T0.AddMinutes(1));
        var annotation = new OutboundOperatorResolvedEvent
        {
            MutationId = fixture.MutationId,
            Decision = OutboundOperatorDecision.LeaveAmbiguous,
            EvidenceType = OutboundOperatorEvidenceType.ManualAnnotation,
            EvidenceDigest = new string('a', 64),
            OperatorRef = "operator-17",
            ResolvedAtUtc = T0.AddMinutes(2),
            TimestampUtc = T0.AddMinutes(2),
        };
        fixture.Ledger.Apply(annotation);
        fixture.Ledger.Apply(annotation);
        fixture.Ledger.Apply(annotation with
        {
            Decision = OutboundOperatorDecision.VenueAbsent,
            EvidenceType = OutboundOperatorEvidenceType.OfficialExtract,
            EvidenceDigest = new string('b', 64),
            ResolvedAtUtc = T0.AddMinutes(3),
            TimestampUtc = T0.AddMinutes(3),
        });

        Assert.True(fixture.Ledger.TryGet(fixture.MutationId, out var mutation));
        Assert.Equal(OutboundMutationState.OperatorResolved, mutation!.State);
        Assert.Equal(2, mutation.OperatorEvidence.Count);
        Assert.NotNull(mutation.Resolution);
    }

    [Fact]
    public void BusinessReject_RequiresExactFrameCorrelation_AndLateErResolvesAmbiguity()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Frame);
        fixture.Ledger.ApplyBusinessReject(new BusinessRejectReceivedEvent
        {
            FirmId = "F1",
            SessionId = 11,
            SessionVerId = 2,
            RefSeqNum = 78,
            RejectReason = 3,
            SeqNum = 91,
            SendingTime = T0,
            TimestampUtc = T0,
        });
        AssertState(fixture, OutboundMutationState.FramePrepared);

        fixture.Ledger.ClassifyRecoveredAttempts(
            new ProcessEpochId(Guid.NewGuid()), T0.AddMinutes(1));
        AssertState(fixture, OutboundMutationState.Ambiguous);
        fixture.Ledger.ApplyVenueAcknowledgement(new ExecutionReportReceivedEvent
        {
            ClOrdId = fixture.ClOrdId,
            ExecKind = "New",
            LeavesQuantity = 10,
            CumulativeQuantity = 0,
            LastQuantity = 0,
            LastPrice = 0,
            Synthetic = false,
            FirmId = "F1",
            SessionId = 11,
            SessionVerId = 2,
            InboundSeqNum = 92,
            TimestampUtc = T0.AddMinutes(2),
        });
        AssertState(fixture, OutboundMutationState.VenueAcknowledged);
    }

    [Fact]
    public void BusinessReject_CorrelatesOnlyExactFirmSessionVersionAndSequence()
    {
        var first = Fixture.Create(101);
        var second = Fixture.Create(102);
        var third = Fixture.Create(103);
        var ledger = new OutboundMutationLedger(first.Protector);
        ApplyPrepared(ledger, first, first.Frame with
        {
            FirmId = "F1",
            SessionId = 11,
            SessionVerId = 2,
            OutboundSeqNum = 77,
        });
        ApplyPrepared(ledger, second, second.Frame with
        {
            FirmId = "F2",
            SessionId = 11,
            SessionVerId = 2,
            OutboundSeqNum = 77,
        }, approvedFirm: "F2");
        ApplyPrepared(ledger, third, third.Frame with
        {
            FirmId = "F1",
            SessionId = 12,
            SessionVerId = 2,
            OutboundSeqNum = 77,
        });

        var wrongVersion = BusinessReject("F1", 11, 3, refSeqNum: 77, inboundSeqNum: 90);
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedUnmatched,
            ledger.ApplyBusinessReject(wrongVersion).Status);
        AssertState(ledger, first.MutationId, OutboundMutationState.FramePrepared);
        AssertState(ledger, second.MutationId, OutboundMutationState.FramePrepared);
        AssertState(ledger, third.MutationId, OutboundMutationState.FramePrepared);

        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedMatched,
            ledger.ApplyBusinessReject(
                BusinessReject("F1", 12, 2, refSeqNum: 77, inboundSeqNum: 91)).Status);
        AssertState(ledger, first.MutationId, OutboundMutationState.FramePrepared);
        AssertState(ledger, second.MutationId, OutboundMutationState.FramePrepared);
        AssertState(ledger, third.MutationId, OutboundMutationState.VenueAcknowledged);

        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedMatched,
            ledger.ApplyBusinessReject(
                BusinessReject("F2", 11, 2, refSeqNum: 77, inboundSeqNum: 92)).Status);
        AssertState(ledger, first.MutationId, OutboundMutationState.FramePrepared);
        AssertState(ledger, second.MutationId, OutboundMutationState.VenueAcknowledged);
    }

    [Fact]
    public void BusinessReject_MissingIdentityRemainsUnmatchedAndDoesNotUseText()
    {
        var fixture = Fixture.Create();
        ApplyPrepared(fixture.Ledger, fixture, fixture.Frame);

        var result = fixture.Ledger.ApplyBusinessReject(new BusinessRejectReceivedEvent
        {
            FirmId = "F1",
            RefSeqNum = 77,
            RejectReason = 3,
            Text = $"pretend clOrdId={fixture.ClOrdId}",
            SeqNum = 90,
            SendingTime = T0,
            TimestampUtc = T0,
        });

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedUnmatched, result.Status);
        AssertState(fixture, OutboundMutationState.FramePrepared);
        Assert.Equal(1, fixture.Ledger.InboundEvidenceCount);
    }

    [Fact]
    public void NotApplied_UsesOverflowSafeHalfOpenRange_AndNeverAutoResends()
    {
        var fixture = Fixture.Create();
        ApplyPrepared(fixture.Ledger, fixture, fixture.Frame with
        {
            OutboundSeqNum = ulong.MaxValue,
        });

        var result = fixture.Ledger.ApplyNotApplied(new NotAppliedReceivedEvent
        {
            FirmId = "F1",
            SessionId = 11,
            SessionVerId = 2,
            FromSeqNo = ulong.MaxValue - 1,
            Count = 2,
            ObservedAtUtc = T0.AddMinutes(1),
            TimestampUtc = T0.AddMinutes(1),
        });

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedMatched, result.Status);
        AssertState(fixture, OutboundMutationState.Ambiguous);
        Assert.Equal(1, fixture.Ledger.ReadinessBlockingCount);
        Assert.Equal(
            OutboundAmbiguityReason.NotAppliedEvidence,
            Assert.Single(fixture.Ledger.SnapshotMutations()).Attempts[^1].AmbiguityReason);
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.Duplicate,
            fixture.Ledger.ApplyNotApplied(new NotAppliedReceivedEvent
            {
                FirmId = "F1",
                SessionId = 11,
                SessionVerId = 2,
                FromSeqNo = ulong.MaxValue - 1,
                Count = 2,
                ObservedAtUtc = T0.AddMinutes(2),
                TimestampUtc = T0.AddMinutes(2),
            }).Status);
        Assert.Single(fixture.Ledger.GetInboundEvidenceDiagnostics());
    }

    [Fact]
    public void NotApplied_ZeroCountIsBoundedUnmatchedEvidence()
    {
        var ledger = new OutboundMutationLedger();
        var result = ledger.ApplyNotApplied(new NotAppliedReceivedEvent
        {
            FirmId = "F1",
            SessionId = 11,
            SessionVerId = 2,
            FromSeqNo = ulong.MaxValue,
            Count = 0,
            ObservedAtUtc = T0,
            TimestampUtc = T0,
        });

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedUnmatched, result.Status);
        Assert.Single(ledger.GetInboundEvidenceDiagnostics());
    }

    [Fact]
    public void NotApplied_AfterExactVenueAcknowledgementIsConflictingAndFailClosed()
    {
        var fixture = Fixture.Create();
        ApplyPrepared(fixture.Ledger, fixture, fixture.Frame);
        fixture.Ledger.ApplyVenueAcknowledgement(Acknowledgement(
            fixture,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2));

        var result = fixture.Ledger.ApplyNotApplied(new NotAppliedReceivedEvent
        {
            FirmId = "F1",
            SessionId = 11,
            SessionVerId = 2,
            FromSeqNo = 77,
            Count = 1,
            ObservedAtUtc = T0.AddMinutes(1),
            TimestampUtc = T0.AddMinutes(1),
        });

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedConflicting, result.Status);
        AssertState(fixture, OutboundMutationState.VenueAcknowledged);
        var mutation = Assert.Single(fixture.Ledger.SnapshotMutations());
        Assert.Equal(
            OutboundAmbiguityReason.ConflictingVenueEvidence,
            Assert.Single(mutation.Attempts).AmbiguityReason);
        Assert.Equal(1, fixture.Ledger.ReadinessBlockingCount);
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.Duplicate,
            fixture.Ledger.ApplyNotApplied(new NotAppliedReceivedEvent
            {
                FirmId = "F1",
                SessionId = 11,
                SessionVerId = 2,
                FromSeqNo = 77,
                Count = 1,
                ObservedAtUtc = T0.AddMinutes(2),
                TimestampUtc = T0.AddMinutes(2),
            }).Status);
    }

    [Fact]
    public void NotAppliedThenExecutionReport_IsMonotonicConflictAndSuppressesDomain()
    {
        var fixture = Fixture.Create();
        ApplyPrepared(fixture.Ledger, fixture, fixture.Frame);
        var notApplied = ExactNotApplied();
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedMatched,
            fixture.Ledger.ApplyNotApplied(notApplied).Status);

        var acknowledgement = Acknowledgement(
            fixture,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2);
        var conflict = fixture.Ledger.ApplyVenueAcknowledgement(acknowledgement);
        var duplicate = fixture.Ledger.ApplyVenueAcknowledgement(
            acknowledgement with { PossibleResend = true });

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedConflicting, conflict.Status);
        Assert.False(conflict.ShouldApplyDomain);
        Assert.Equal(InboundVenueEvidenceApplyStatus.Duplicate, duplicate.Status);
        Assert.False(duplicate.ShouldApplyDomain);
        AssertState(fixture, OutboundMutationState.Ambiguous);
        var mutation = Assert.Single(fixture.Ledger.SnapshotMutations());
        Assert.Null(mutation.Resolution);
        Assert.Equal(
            OutboundAmbiguityReason.ConflictingVenueEvidence,
            Assert.Single(mutation.Attempts).AmbiguityReason);
        Assert.Equal(1, fixture.Ledger.ReadinessBlockingCount);
        Assert.Contains(
            fixture.Ledger.CaptureSnapshot().InboundEvidence,
            evidence => evidence.Kind == InboundVenueEvidenceKind.ExecutionReport
                && evidence.Disposition == InboundVenueEvidenceDisposition.Conflicting
                && evidence.PossibleResend);
    }

    [Theory]
    [InlineData("live", false)]
    [InlineData("live", true)]
    [InlineData("replay", false)]
    [InlineData("replay", true)]
    [InlineData("restart", false)]
    [InlineData("restart", true)]
    public void NotAppliedAndBusinessReject_AreConflictingInBothOrders(
        string mode,
        bool businessRejectFirst)
    {
        var fixture = Fixture.Create();
        ApplyPrepared(fixture.Ledger, fixture, fixture.Frame);
        var ledger = fixture.Ledger;
        var notApplied = ExactNotApplied();
        var businessReject = BusinessReject(
            "F1", 11, 2, refSeqNum: 77, inboundSeqNum: 90);

        if (mode == "live")
        {
            var client = new MockEntryPointClient();
            var ownership = new OrderOwnershipMap();
            var orders = new WorkingOrderBook();
            using var router = new EntryPointExecutionReportRouter(
                client,
                new ExecutionReportProcessor(
                    ownership,
                    orders,
                    new PositionKeeper(),
                    new NoOpExecutionEventSink(),
                    new NoOpMarginProvider(),
                    NullLogger<ExecutionReportProcessor>.Instance),
                new EventDispatcher(new NullEventStore()),
                orders,
                bookTop: null,
                drain: null,
                outboundLedger: ledger);
            if (businessRejectFirst)
            {
                client.EmitBusinessReject(ExactBusinessRejectEnvelope());
                client.EmitNotApplied(ExactNotAppliedEnvelope());
                client.EmitNotApplied(ExactNotAppliedEnvelope() with
                {
                    ObservedAtUtc = T0.AddMinutes(2),
                });
            }
            else
            {
                client.EmitNotApplied(ExactNotAppliedEnvelope());
                client.EmitBusinessReject(ExactBusinessRejectEnvelope());
                client.EmitBusinessReject(ExactBusinessRejectEnvelope() with
                {
                    PossibleResend = true,
                });
            }
        }
        else if (mode == "replay")
        {
            var replayer = NewReplayer(ledger, new ClOrdIdPrefixRegistry());
            if (businessRejectFirst)
            {
                replayer.Apply(businessReject);
                replayer.Apply(notApplied);
                replayer.Apply(notApplied with
                {
                    ObservedAtUtc = T0.AddMinutes(2),
                    TimestampUtc = T0.AddMinutes(2),
                });
            }
            else
            {
                replayer.Apply(notApplied);
                replayer.Apply(businessReject);
                replayer.Apply(businessReject with { PossibleResend = true });
            }
        }
        else
        {
            if (businessRejectFirst)
                ledger.ApplyBusinessReject(businessReject);
            else
                ledger.ApplyNotApplied(notApplied);
            ledger = RestoreLedger(ledger, fixture.Protector);
            if (businessRejectFirst)
            {
                ledger.ApplyNotApplied(notApplied);
                ledger.ApplyNotApplied(notApplied with
                {
                    ObservedAtUtc = T0.AddMinutes(2),
                    TimestampUtc = T0.AddMinutes(2),
                });
            }
            else
            {
                ledger.ApplyBusinessReject(businessReject);
                ledger.ApplyBusinessReject(
                    businessReject with { PossibleResend = true });
            }
        }

        var mutation = Assert.Single(ledger.SnapshotMutations());
        Assert.Equal(
            businessRejectFirst
                ? OutboundMutationState.VenueAcknowledged
                : OutboundMutationState.Ambiguous,
            mutation.State);
        Assert.Equal(
            businessRejectFirst ? "BusinessReject" : null,
            mutation.Resolution?.EvidenceKind);
        Assert.Equal(
            OutboundAmbiguityReason.ConflictingVenueEvidence,
            Assert.Single(mutation.Attempts).AmbiguityReason);
        Assert.True(mutation.RequiresReconciliation);
        Assert.Equal(1, ledger.ReadinessBlockingCount);
        Assert.Equal(2, ledger.InboundEvidenceCount);
        Assert.Single(
            ledger.CaptureSnapshot().InboundEvidence,
            evidence => evidence.Disposition
                == InboundVenueEvidenceDisposition.Conflicting);
        if (!businessRejectFirst)
        {
            Assert.Contains(
                ledger.CaptureSnapshot().InboundEvidence,
                evidence => evidence.Kind
                        == InboundVenueEvidenceKind.BusinessReject
                    && evidence.PossibleResend);
        }
    }

    [Fact]
    public void NotAppliedRangeOverlap_ConflictsTerminalRejectAndKeepsOtherAttemptNegative()
    {
        var (ledger, first, second) = PreparedMutationPair();
        ledger.ApplyBusinessReject(
            BusinessReject("F1", 11, 2, refSeqNum: 77, inboundSeqNum: 90));
        var range = ExactNotApplied() with
        {
            FromSeqNo = 77,
            Count = 2,
        };

        var result = ledger.ApplyNotApplied(range);
        var duplicate = ledger.ApplyNotApplied(range with
        {
            ObservedAtUtc = T0.AddMinutes(2),
            TimestampUtc = T0.AddMinutes(2),
        });

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedConflicting, result.Status);
        Assert.Equal(InboundVenueEvidenceApplyStatus.Duplicate, duplicate.Status);
        AssertState(
            ledger,
            first.MutationId,
            OutboundMutationState.VenueAcknowledged);
        AssertState(
            ledger,
            second.MutationId,
            OutboundMutationState.Ambiguous);
        Assert.Equal(
            OutboundAmbiguityReason.ConflictingVenueEvidence,
            Assert.Single(
                ledger.SnapshotMutations(),
                mutation => mutation.MutationId == first.MutationId)
                .Attempts[^1].AmbiguityReason);
        Assert.Equal(
            OutboundAmbiguityReason.NotAppliedEvidence,
            Assert.Single(
                ledger.SnapshotMutations(),
                mutation => mutation.MutationId == second.MutationId)
                .Attempts[^1].AmbiguityReason);
        Assert.Equal(2, ledger.ReadinessBlockingCount);

        var rejectAfterRange = ledger.ApplyBusinessReject(
            BusinessReject("F1", 11, 2, refSeqNum: 78, inboundSeqNum: 91));
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedConflicting,
            rejectAfterRange.Status);
        AssertState(ledger, second.MutationId, OutboundMutationState.Ambiguous);
        Assert.Equal(
            OutboundAmbiguityReason.ConflictingVenueEvidence,
            Assert.Single(
                ledger.SnapshotMutations(),
                mutation => mutation.MutationId == second.MutationId)
                .Attempts[^1].AmbiguityReason);
    }

    [Fact]
    public void BusinessRejectAndExecutionReport_AreConflictingInBothOrders()
    {
        var rejectFirst = Fixture.Create();
        ApplyPrepared(rejectFirst.Ledger, rejectFirst, rejectFirst.Frame);
        var reject = BusinessReject("F1", 11, 2, refSeqNum: 77, inboundSeqNum: 90);
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedMatched,
            rejectFirst.Ledger.ApplyBusinessReject(reject).Status);
        var erAfterReject = rejectFirst.Ledger.ApplyVenueAcknowledgement(
            Acknowledgement(
                rejectFirst,
                firmId: "F1",
                sessionId: 11,
                sessionVerId: 2));
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedConflicting,
            erAfterReject.Status);
        Assert.False(erAfterReject.ShouldApplyDomain);
        AssertTerminalConflict(
            rejectFirst,
            expectedEvidenceKind: "BusinessReject");
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.Duplicate,
            rejectFirst.Ledger.ApplyVenueAcknowledgement(
                Acknowledgement(
                    rejectFirst,
                    firmId: "F1",
                    sessionId: 11,
                    sessionVerId: 2) with
                {
                    PossibleResend = true,
                }).Status);

        var erFirst = Fixture.Create(clOrdId: 2);
        ApplyPrepared(erFirst.Ledger, erFirst, erFirst.Frame);
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedMatched,
            erFirst.Ledger.ApplyVenueAcknowledgement(
                Acknowledgement(
                    erFirst,
                    firmId: "F1",
                    sessionId: 11,
                    sessionVerId: 2)).Status);
        var rejectAfterEr = erFirst.Ledger.ApplyBusinessReject(
            BusinessReject("F1", 11, 2, refSeqNum: 77, inboundSeqNum: 91));
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedConflicting,
            rejectAfterEr.Status);
        AssertTerminalConflict(erFirst, expectedEvidenceKind: "ExecutionReport");
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.Duplicate,
            erFirst.Ledger.ApplyBusinessReject(
                BusinessReject("F1", 11, 2, refSeqNum: 77, inboundSeqNum: 91) with
                {
                    PossibleResend = true,
                }).Status);
    }

    [Fact]
    public void SameKindTerminalEvidenceAndPossResendDuplicatesRemainValid()
    {
        var erFixture = Fixture.Create();
        ApplyPrepared(erFixture.Ledger, erFixture, erFixture.Frame);
        var firstEr = Acknowledgement(
            erFixture,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2);
        erFixture.Ledger.ApplyVenueAcknowledgement(firstEr);
        var laterSameKind = erFixture.Ledger.ApplyVenueAcknowledgement(firstEr with
        {
            InboundSeqNum = 91,
            ExecKind = "PartialFill",
            LeavesQuantity = 5,
            CumulativeQuantity = 5,
            LastQuantity = 5,
            LastPrice = 30m,
        });
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedMatched,
            laterSameKind.Status);
        Assert.True(laterSameKind.ShouldApplyDomain);
        Assert.Equal(0, erFixture.Ledger.ReadinessBlockingCount);

        var brFixture = Fixture.Create(clOrdId: 2);
        ApplyPrepared(brFixture.Ledger, brFixture, brFixture.Frame);
        var businessReject = BusinessReject(
            "F1", 11, 2, refSeqNum: 77, inboundSeqNum: 90);
        brFixture.Ledger.ApplyBusinessReject(businessReject);
        var duplicate = brFixture.Ledger.ApplyBusinessReject(
            businessReject with { PossibleResend = true });
        Assert.Equal(InboundVenueEvidenceApplyStatus.Duplicate, duplicate.Status);
        Assert.Equal(0, brFixture.Ledger.ReadinessBlockingCount);
        Assert.Contains(
            brFixture.Ledger.CaptureSnapshot().InboundEvidence,
            evidence => evidence.Kind == InboundVenueEvidenceKind.BusinessReject
                && evidence.PossibleResend);
    }

    [Fact]
    public void LiveRouter_ContradictoryEvidenceIsOrderSymmetricAndDomainSuppressed()
    {
        var notAppliedFirst = Fixture.Create();
        ApplyPrepared(notAppliedFirst.Ledger, notAppliedFirst, notAppliedFirst.Frame);
        var notAppliedOrder = AddPendingOrder(notAppliedFirst.ClOrdId);
        var notAppliedOwnership = Ownership(notAppliedOrder);
        var notAppliedClient = new MockEntryPointClient();
        using (var router = CreateRouter(
                   notAppliedClient,
                   notAppliedOwnership,
                   notAppliedOrder,
                   notAppliedFirst.Ledger))
        {
            notAppliedClient.EmitNotApplied(new NotAppliedEnvelope(
                "F1", 11, 2, 77, 1, T0.AddMinutes(1)));
            notAppliedClient.EmitExecutionReport(ExactNewEnvelope(
                notAppliedFirst.ClOrdId,
                inboundSeqNum: 90));
        }
        Assert.Equal(OrderStatus.PendingNew, notAppliedOrder.Status);
        Assert.Equal(1, notAppliedFirst.Ledger.ReadinessBlockingCount);

        var rejectFirst = Fixture.Create(clOrdId: 2);
        ApplyPrepared(rejectFirst.Ledger, rejectFirst, rejectFirst.Frame);
        var rejectedOrder = AddPendingOrder(rejectFirst.ClOrdId);
        var rejectedOwnership = Ownership(rejectedOrder);
        var rejectClient = new MockEntryPointClient();
        using (var router = CreateRouter(
                   rejectClient,
                   rejectedOwnership,
                   rejectedOrder,
                   rejectFirst.Ledger))
        {
            rejectClient.EmitBusinessReject(new BusinessRejectEnvelope(
                "F1", 77, 3, "structural reject", 90, T0, 11, 2));
            rejectClient.EmitExecutionReport(ExactNewEnvelope(
                rejectFirst.ClOrdId,
                inboundSeqNum: 91));
        }
        Assert.Equal(OrderStatus.PendingNew, rejectedOrder.Status);
        AssertTerminalConflict(rejectFirst, "BusinessReject");

        var erFirst = Fixture.Create(clOrdId: 3);
        ApplyPrepared(erFirst.Ledger, erFirst, erFirst.Frame);
        var acceptedOrder = AddPendingOrder(erFirst.ClOrdId);
        var acceptedOwnership = Ownership(acceptedOrder);
        var erClient = new MockEntryPointClient();
        using (var router = CreateRouter(
                   erClient,
                   acceptedOwnership,
                   acceptedOrder,
                   erFirst.Ledger))
        {
            erClient.EmitExecutionReport(ExactNewEnvelope(
                erFirst.ClOrdId,
                inboundSeqNum: 90));
            erClient.EmitBusinessReject(new BusinessRejectEnvelope(
                "F1", 77, 3, "structural reject", 91, T0, 11, 2));
        }
        Assert.Equal(OrderStatus.Working, acceptedOrder.Status);
        AssertTerminalConflict(erFirst, "ExecutionReport");
    }

    [Theory]
    [InlineData("NotApplied")]
    [InlineData("BusinessReject")]
    public void SnapshotRestart_NegativeEvidenceThenExecutionReportRemainsConflicting(
        string firstEvidence)
    {
        var writer = Fixture.Create();
        ApplyPrepared(writer.Ledger, writer, writer.Frame);
        if (firstEvidence == "NotApplied")
            writer.Ledger.ApplyNotApplied(ExactNotApplied());
        else
            writer.Ledger.ApplyBusinessReject(
                BusinessReject("F1", 11, 2, refSeqNum: 77, inboundSeqNum: 90));
        var restored = RestoreLedger(writer);
        var order = AddPendingOrder(writer.ClOrdId);
        var ownership = Ownership(order);
        var orders = new WorkingOrderBook();
        Assert.True(orders.TryAdd(order));
        var replayer = new EventReplayer(
            orders,
            ownership,
            new KillSwitchService(),
            new SymbolHaltService(),
            new SessionPhaseService(),
            new ExecutionReportProcessor(
                ownership,
                orders,
                new PositionKeeper(),
                new NoOpExecutionEventSink(),
                new NoOpMarginProvider(),
                NullLogger<ExecutionReportProcessor>.Instance),
            new AlgoBook(),
            new ClOrdIdPrefixRegistry(),
            new AlgoIdRegistry(),
            outboundLedger: restored);
        var acknowledgement = Acknowledgement(
            writer,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2);

        replayer.Apply(acknowledgement);
        replayer.Apply(acknowledgement with { PossibleResend = true });

        Assert.Equal(OrderStatus.PendingNew, order.Status);
        Assert.Equal(1, restored.ReadinessBlockingCount);
        var mutation = Assert.Single(restored.SnapshotMutations());
        Assert.Equal(
            OutboundAmbiguityReason.ConflictingVenueEvidence,
            Assert.Single(mutation.Attempts).AmbiguityReason);
        Assert.Equal(
            firstEvidence == "BusinessReject"
                ? OutboundMutationState.VenueAcknowledged
                : OutboundMutationState.Ambiguous,
            mutation.State);
    }

    [Theory]
    [InlineData("NotApplied")]
    [InlineData("BusinessReject")]
    public void SnapshotRestart_ExecutionReportThenNegativeEvidenceClosesReadiness(
        string laterEvidence)
    {
        var writer = Fixture.Create();
        ApplyPrepared(writer.Ledger, writer, writer.Frame);
        writer.Ledger.ApplyVenueAcknowledgement(Acknowledgement(
            writer,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2));
        var restored = RestoreLedger(writer);

        var result = laterEvidence == "NotApplied"
            ? restored.ApplyNotApplied(ExactNotApplied())
            : restored.ApplyBusinessReject(
                BusinessReject("F1", 11, 2, refSeqNum: 77, inboundSeqNum: 91));

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedConflicting, result.Status);
        Assert.Equal(1, restored.ReadinessBlockingCount);
        var mutation = Assert.Single(restored.SnapshotMutations());
        Assert.Equal("ExecutionReport", mutation.Resolution?.EvidenceKind);
        Assert.Equal(
            OutboundAmbiguityReason.ConflictingVenueEvidence,
            Assert.Single(mutation.Attempts).AmbiguityReason);
        Assert.Equal(OutboundMutationState.VenueAcknowledged, mutation.State);

        var restoredAgain = RestoreLedger(restored, writer.Protector);
        Assert.Equal(1, restoredAgain.ReadinessBlockingCount);
        var restoredMutation = Assert.Single(restoredAgain.SnapshotMutations());
        Assert.True(restoredMutation.RequiresReconciliation);
        Assert.Equal(
            OutboundAmbiguityReason.ConflictingVenueEvidence,
            Assert.Single(restoredMutation.Attempts).AmbiguityReason);
    }

    [Fact]
    public void ExecutionReport_DuplicatePossResendAndConflictingSameIdentityAreMonotonic()
    {
        var fixture = Fixture.Create();
        ApplyPrepared(fixture.Ledger, fixture, fixture.Frame);
        var acknowledgement = Acknowledgement(
            fixture,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2) with
        {
            InboundSeqNum = 92,
            PossibleResend = false,
        };

        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedMatched,
            fixture.Ledger.ApplyVenueAcknowledgement(acknowledgement).Status);
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.Duplicate,
            fixture.Ledger.ApplyVenueAcknowledgement(
                acknowledgement with { PossibleResend = true }).Status);
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedConflicting,
            fixture.Ledger.ApplyVenueAcknowledgement(
                acknowledgement with { CumulativeQuantity = 9 }).Status);

        AssertState(fixture, OutboundMutationState.VenueAcknowledged);
        Assert.Equal(2, fixture.Ledger.InboundEvidenceCount);
        Assert.Contains(
            fixture.Ledger.CaptureSnapshot().InboundEvidence,
            evidence => evidence.Disposition == InboundVenueEvidenceDisposition.Conflicting
                && evidence.PossibleResend);
    }

    [Theory]
    [InlineData("Rejected", OutboundMutationKind.New, 0UL, null, "")]
    [InlineData("Rejected", OutboundMutationKind.New, 0UL, "risk-a", "risk-b")]
    [InlineData("Canceled", OutboundMutationKind.Cancel, 50UL, "cancel-a", "cancel-b")]
    public void ExecutionReport_ChangedRejectReasonConflictsWhileExactPossResendDedupes(
        string execKind,
        OutboundMutationKind mutationKind,
        ulong originalClOrdId,
        string? firstReason,
        string? changedReason)
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved with
        {
            MutationKind = mutationKind,
            OriginalClOrdId = originalClOrdId == 0 ? null : originalClOrdId,
        });
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Frame);
        var first = Acknowledgement(
            fixture,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2) with
        {
            ExecKind = execKind,
            OrigClOrdId = originalClOrdId,
            RejectReason = firstReason,
        };

        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedMatched,
            fixture.Ledger.ApplyVenueAcknowledgement(first).Status);
        var duplicate = fixture.Ledger.ApplyVenueAcknowledgement(first with
        {
            PossibleResend = true,
            TimestampUtc = first.TimestampUtc.AddSeconds(1),
            BookTouch = new BookTouchSnapshot
            {
                BestBid = 29m,
                BestAsk = 31m,
                MidPrice = 30m,
                CapturedAtUtc = first.TimestampUtc.AddSeconds(1),
            },
        });
        var changed = fixture.Ledger.ApplyVenueAcknowledgement(first with
        {
            RejectReason = changedReason,
        });

        Assert.Equal(InboundVenueEvidenceApplyStatus.Duplicate, duplicate.Status);
        Assert.False(duplicate.ShouldApplyDomain);
        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedConflicting, changed.Status);
        Assert.False(changed.ShouldApplyDomain);
        AssertTerminalConflict(fixture, "ExecutionReport");
        Assert.Contains(
            fixture.Ledger.CaptureSnapshot().InboundEvidence,
            evidence => evidence.Kind == InboundVenueEvidenceKind.ExecutionReport
                && evidence.Disposition == InboundVenueEvidenceDisposition.Conflicting
                && evidence.PossibleResend);
    }

    [Fact]
    public void EventReplayer_ChangedRejectReasonDoesNotPublishSecondDomainEvent()
    {
        var fixture = Fixture.Create();
        var ownership = new OrderOwnershipMap();
        var orders = new WorkingOrderBook();
        var sink = new CapturingExecutionSink();
        var replayer = new EventReplayer(
            orders,
            ownership,
            new KillSwitchService(),
            new SymbolHaltService(),
            new SessionPhaseService(),
            new ExecutionReportProcessor(
                ownership,
                orders,
                new PositionKeeper(),
                sink,
                new NoOpMarginProvider(),
                NullLogger<ExecutionReportProcessor>.Instance),
            new AlgoBook(),
            new ClOrdIdPrefixRegistry(),
            new AlgoIdRegistry(),
            outboundLedger: fixture.Ledger);
        replayer.Apply(LegacySubmit(fixture.ClOrdId));
        replayer.Apply(fixture.Approved);
        replayer.Apply(fixture.Intent);
        replayer.Apply(fixture.Frame);
        var rejected = Acknowledgement(
            fixture,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2) with
        {
            ExecKind = "Rejected",
            RejectReason = "venue-risk-a",
        };

        replayer.Apply(rejected);
        replayer.Apply(rejected with { PossibleResend = true });
        replayer.Apply(rejected with { RejectReason = "venue-risk-b" });

        Assert.True(orders.TryGet(fixture.ClOrdId, out var order));
        Assert.NotNull(order);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        var execution = Assert.Single(sink.Events);
        Assert.Equal("venue-risk-a", execution.RejectReason);
        AssertTerminalConflict(fixture, "ExecutionReport");
    }

    [Fact]
    public void SnapshotRestart_RejectReasonDedupeAndConflictRemainStable()
    {
        var writer = Fixture.Create();
        ApplyPrepared(writer.Ledger, writer, writer.Frame);
        var rejected = Acknowledgement(
            writer,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2) with
        {
            ExecKind = "Rejected",
            RejectReason = "venue-risk-a",
        };
        writer.Ledger.ApplyVenueAcknowledgement(rejected);
        writer.Ledger.ApplyVenueAcknowledgement(rejected with
        {
            PossibleResend = true,
        });
        var restored = RestoreLedger(writer);

        var duplicate = restored.ApplyVenueAcknowledgement(rejected);
        var conflict = restored.ApplyVenueAcknowledgement(rejected with
        {
            RejectReason = "venue-risk-b",
        });

        Assert.Equal(InboundVenueEvidenceApplyStatus.Duplicate, duplicate.Status);
        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedConflicting, conflict.Status);
        Assert.False(conflict.ShouldApplyDomain);
        Assert.Equal(1, restored.ReadinessBlockingCount);
        var mutation = Assert.Single(restored.SnapshotMutations());
        Assert.Equal(
            OutboundAmbiguityReason.ConflictingVenueEvidence,
            Assert.Single(mutation.Attempts).AmbiguityReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecutionReportIdentityCollision_MarksBothMutationsInEitherOrder(
        bool reverse)
    {
        var (ledger, first, second) = PreparedMutationPair();
        var firstEr = Acknowledgement(
            first,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2);
        var secondEr = Acknowledgement(
            second,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2);
        var initial = reverse ? secondEr : firstEr;
        var colliding = reverse ? firstEr : secondEr;

        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedMatched,
            ledger.ApplyVenueAcknowledgement(initial).Status);
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.Duplicate,
            ledger.ApplyVenueAcknowledgement(
                initial with { PossibleResend = true }).Status);
        var conflict = ledger.ApplyVenueAcknowledgement(colliding);
        var collidingDuplicate = ledger.ApplyVenueAcknowledgement(colliding with
        {
            PossibleResend = true,
        });

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedConflicting, conflict.Status);
        Assert.False(conflict.ShouldApplyDomain);
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.Duplicate,
            collidingDuplicate.Status);
        var initialFixture = reverse ? second : first;
        var collidingFixture = reverse ? first : second;
        AssertState(
            ledger,
            initialFixture.MutationId,
            OutboundMutationState.VenueAcknowledged);
        AssertState(
            ledger,
            collidingFixture.MutationId,
            OutboundMutationState.Ambiguous);
        Assert.All(
            ledger.SnapshotMutations(),
            mutation =>
            {
                Assert.True(mutation.RequiresReconciliation);
                Assert.Equal(
                    OutboundAmbiguityReason.ConflictingVenueEvidence,
                    Assert.Single(mutation.Attempts).AmbiguityReason);
            });
        Assert.Equal(2, ledger.ReadinessBlockingCount);
        Assert.Equal(
            0,
            ledger.PurgeTerminalCorrelations(T0.AddYears(10)));
        Assert.All(
            ledger.CaptureSnapshot().InboundEvidence,
            evidence =>
            {
                Assert.Equal(
                    InboundVenueEvidenceDisposition.Conflicting,
                    evidence.Disposition);
                Assert.Equal(2, evidence.MatchedMutationIds.Count);
                Assert.True(evidence.PossibleResend);
            });
    }

    [Fact]
    public void ExecutionReportIdentityCollision_HandlesKnownAndUnknownBothOrders()
    {
        var knownFirst = Fixture.Create();
        ApplyPrepared(knownFirst.Ledger, knownFirst, knownFirst.Frame);
        var knownEr = Acknowledgement(
            knownFirst,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2);
        knownFirst.Ledger.ApplyVenueAcknowledgement(knownEr);
        var unknownAfter = knownFirst.Ledger.ApplyVenueAcknowledgement(
            knownEr with { ClOrdId = 999 });

        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedConflicting,
            unknownAfter.Status);
        AssertTerminalConflict(knownFirst, "ExecutionReport");
        Assert.All(
            knownFirst.Ledger.CaptureSnapshot().InboundEvidence,
            evidence =>
            {
                Assert.Equal(
                    InboundVenueEvidenceDisposition.Conflicting,
                    evidence.Disposition);
                Assert.Equal(
                    knownFirst.MutationId,
                    Assert.Single(evidence.MatchedMutationIds));
            });

        var unknownFirst = Fixture.Create(clOrdId: 2);
        ApplyPrepared(unknownFirst.Ledger, unknownFirst, unknownFirst.Frame);
        var exact = Acknowledgement(
            unknownFirst,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2);
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedUnmatched,
            unknownFirst.Ledger.ApplyVenueAcknowledgement(
                exact with { ClOrdId = 999 }).Status);
        var knownAfter = unknownFirst.Ledger.ApplyVenueAcknowledgement(exact);

        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedConflicting,
            knownAfter.Status);
        Assert.False(knownAfter.ShouldApplyDomain);
        AssertState(unknownFirst, OutboundMutationState.Ambiguous);
        Assert.Equal(1, unknownFirst.Ledger.ReadinessBlockingCount);
        Assert.All(
            unknownFirst.Ledger.CaptureSnapshot().InboundEvidence,
            evidence =>
            {
                Assert.Equal(
                    InboundVenueEvidenceDisposition.Conflicting,
                    evidence.Disposition);
                Assert.Equal(
                    unknownFirst.MutationId,
                    Assert.Single(evidence.MatchedMutationIds));
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BusinessRejectIdentityCollision_MarksBothMutationsInEitherOrder(
        bool reverse)
    {
        var (ledger, first, second) = PreparedMutationPair();
        var firstReject = BusinessReject(
            "F1", 11, 2, refSeqNum: 77, inboundSeqNum: 90);
        var secondReject = BusinessReject(
            "F1", 11, 2, refSeqNum: 78, inboundSeqNum: 90);
        var initial = reverse ? secondReject : firstReject;
        var colliding = reverse ? firstReject : secondReject;

        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedMatched,
            ledger.ApplyBusinessReject(initial).Status);
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.Duplicate,
            ledger.ApplyBusinessReject(initial with { PossibleResend = true }).Status);
        var conflict = ledger.ApplyBusinessReject(colliding);
        var collidingDuplicate = ledger.ApplyBusinessReject(colliding with
        {
            PossibleResend = true,
        });

        Assert.Equal(InboundVenueEvidenceApplyStatus.RecordedConflicting, conflict.Status);
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.Duplicate,
            collidingDuplicate.Status);
        Assert.Equal(2, ledger.ReadinessBlockingCount);
        Assert.All(
            ledger.SnapshotMutations(),
            mutation =>
            {
                Assert.True(mutation.RequiresReconciliation);
                Assert.Equal(
                    OutboundAmbiguityReason.ConflictingVenueEvidence,
                    Assert.Single(mutation.Attempts).AmbiguityReason);
            });
        Assert.All(
            ledger.CaptureSnapshot().InboundEvidence,
            evidence =>
            {
                Assert.Equal(
                    InboundVenueEvidenceDisposition.Conflicting,
                    evidence.Disposition);
                Assert.True(evidence.PossibleResend);
            });
    }

    [Fact]
    public void BusinessRejectIdentityCollision_HandlesKnownAndUnknownBothOrders()
    {
        var knownFirst = Fixture.Create();
        ApplyPrepared(knownFirst.Ledger, knownFirst, knownFirst.Frame);
        knownFirst.Ledger.ApplyBusinessReject(
            BusinessReject("F1", 11, 2, refSeqNum: 77, inboundSeqNum: 90));
        var unknownAfter = knownFirst.Ledger.ApplyBusinessReject(
            BusinessReject("F1", 11, 2, refSeqNum: 999, inboundSeqNum: 90));

        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedConflicting,
            unknownAfter.Status);
        AssertTerminalConflict(knownFirst, "BusinessReject");

        var unknownFirst = Fixture.Create(clOrdId: 2);
        ApplyPrepared(unknownFirst.Ledger, unknownFirst, unknownFirst.Frame);
        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedUnmatched,
            unknownFirst.Ledger.ApplyBusinessReject(
                BusinessReject("F1", 11, 2, refSeqNum: 999, inboundSeqNum: 90))
                .Status);
        var knownAfter = unknownFirst.Ledger.ApplyBusinessReject(
            BusinessReject("F1", 11, 2, refSeqNum: 77, inboundSeqNum: 90));

        Assert.Equal(
            InboundVenueEvidenceApplyStatus.RecordedConflicting,
            knownAfter.Status);
        AssertState(unknownFirst, OutboundMutationState.Ambiguous);
        Assert.Equal(1, unknownFirst.Ledger.ReadinessBlockingCount);
        Assert.All(
            unknownFirst.Ledger.CaptureSnapshot().InboundEvidence,
            evidence =>
            {
                Assert.Equal(
                    InboundVenueEvidenceDisposition.Conflicting,
                    evidence.Disposition);
                Assert.Equal(
                    unknownFirst.MutationId,
                    Assert.Single(evidence.MatchedMutationIds));
            });
    }

    [Fact]
    public void LiveAndReplay_IdentityCollisionPreservesFirstDomainEffectAndSuppressesSecond()
    {
        var (liveLedger, liveFirst, liveSecond) = PreparedMutationPair();
        var liveFirstOrder = AddPendingOrder(liveFirst.ClOrdId);
        var liveSecondOrder = AddPendingOrder(liveSecond.ClOrdId);
        var liveOrders = new WorkingOrderBook();
        Assert.True(liveOrders.TryAdd(liveFirstOrder));
        Assert.True(liveOrders.TryAdd(liveSecondOrder));
        var liveOwnership = new OrderOwnershipMap();
        liveOwnership.Register(liveFirst.ClOrdId, liveFirstOrder.Owner);
        liveOwnership.Register(liveSecond.ClOrdId, liveSecondOrder.Owner);
        var liveClient = new MockEntryPointClient();
        using (var router = new EntryPointExecutionReportRouter(
                   liveClient,
                   new ExecutionReportProcessor(
                       liveOwnership,
                       liveOrders,
                       new PositionKeeper(),
                       new NoOpExecutionEventSink(),
                       new NoOpMarginProvider(),
                       NullLogger<ExecutionReportProcessor>.Instance),
                   new EventDispatcher(new NullEventStore()),
                   liveOrders,
                   bookTop: null,
                   drain: null,
                   outboundLedger: liveLedger))
        {
            liveClient.EmitExecutionReport(ExactNewEnvelope(
                liveFirst.ClOrdId,
                inboundSeqNum: 90));
            liveClient.EmitExecutionReport(ExactNewEnvelope(
                liveFirst.ClOrdId,
                inboundSeqNum: 90) with
            {
                PossibleResend = true,
            });
            liveClient.EmitExecutionReport(ExactNewEnvelope(
                liveSecond.ClOrdId,
                inboundSeqNum: 90));
        }
        Assert.Equal(OrderStatus.Working, liveFirstOrder.Status);
        Assert.Equal(OrderStatus.PendingNew, liveSecondOrder.Status);
        Assert.Equal(2, liveLedger.ReadinessBlockingCount);

        var (replayLedger, replayFirst, replaySecond) = PreparedMutationPair();
        var replayFirstOrder = AddPendingOrder(replayFirst.ClOrdId);
        var replaySecondOrder = AddPendingOrder(replaySecond.ClOrdId);
        var replayOrders = new WorkingOrderBook();
        Assert.True(replayOrders.TryAdd(replayFirstOrder));
        Assert.True(replayOrders.TryAdd(replaySecondOrder));
        var replayOwnership = new OrderOwnershipMap();
        replayOwnership.Register(replayFirst.ClOrdId, replayFirstOrder.Owner);
        replayOwnership.Register(replaySecond.ClOrdId, replaySecondOrder.Owner);
        var replayer = new EventReplayer(
            replayOrders,
            replayOwnership,
            new KillSwitchService(),
            new SymbolHaltService(),
            new SessionPhaseService(),
            new ExecutionReportProcessor(
                replayOwnership,
                replayOrders,
                new PositionKeeper(),
                new NoOpExecutionEventSink(),
                new NoOpMarginProvider(),
                NullLogger<ExecutionReportProcessor>.Instance),
            new AlgoBook(),
            new ClOrdIdPrefixRegistry(),
            new AlgoIdRegistry(),
            outboundLedger: replayLedger);

        replayer.Apply(Acknowledgement(
            replaySecond,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2));
        replayer.Apply(Acknowledgement(
            replayFirst,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2));

        Assert.Equal(OrderStatus.PendingNew, replayFirstOrder.Status);
        Assert.Equal(OrderStatus.Working, replaySecondOrder.Status);
        Assert.Equal(2, replayLedger.ReadinessBlockingCount);
    }

    [Fact]
    public void SnapshotRestore_IdentityCollisionKeepsAllMutationsAndEvidenceConflicting()
    {
        var (ledger, first, second) = PreparedMutationPair();
        var firstEr = Acknowledgement(
            first,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2);
        ledger.ApplyVenueAcknowledgement(firstEr);
        ledger.ApplyVenueAcknowledgement(Acknowledgement(
            second,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2));

        var restored = RestoreLedger(ledger, first.Protector);
        var duplicate = restored.ApplyVenueAcknowledgement(firstEr with
        {
            PossibleResend = true,
        });

        Assert.Equal(InboundVenueEvidenceApplyStatus.Duplicate, duplicate.Status);
        Assert.Equal(2, restored.ReadinessBlockingCount);
        Assert.All(
            restored.SnapshotMutations(),
            mutation =>
            {
                Assert.True(mutation.RequiresReconciliation);
                Assert.Equal(
                    OutboundAmbiguityReason.ConflictingVenueEvidence,
                    Assert.Single(mutation.Attempts).AmbiguityReason);
            });
        Assert.All(
            restored.CaptureSnapshot().InboundEvidence,
            evidence =>
            {
                Assert.Equal(
                    InboundVenueEvidenceDisposition.Conflicting,
                    evidence.Disposition);
                Assert.Equal(2, evidence.MatchedMutationIds.Count);
            });
    }

    [Fact]
    public void EventReplayer_ConflictingSameIdentityDoesNotDoubleBookDomainState()
    {
        var fixture = Fixture.Create();
        var ownership = new OrderOwnershipMap();
        var orders = new WorkingOrderBook();
        var positions = new PositionKeeper();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var processor = new ExecutionReportProcessor(
            ownership,
            orders,
            positions,
            new NoOpExecutionEventSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        var replayer = new EventReplayer(
            orders,
            ownership,
            new KillSwitchService(),
            new SymbolHaltService(),
            new SessionPhaseService(),
            processor,
            new AlgoBook(),
            clOrdIds,
            new AlgoIdRegistry(),
            outboundLedger: fixture.Ledger);
        replayer.Apply(LegacySubmit(fixture.ClOrdId));
        replayer.Apply(fixture.Approved);
        replayer.Apply(fixture.Intent);
        replayer.Apply(fixture.Frame);
        var first = Acknowledgement(
            fixture,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2) with
        {
            ExecKind = "PartialFill",
            LeavesQuantity = 5,
            CumulativeQuantity = 5,
            LastQuantity = 5,
            LastPrice = 30m,
            InboundSeqNum = 92,
        };
        replayer.Apply(first);
        replayer.Apply(first with
        {
            ClOrdId = 999,
            OrigClOrdId = fixture.ClOrdId,
            LeavesQuantity = 0,
            CumulativeQuantity = 10,
            LastQuantity = 5,
            LastPrice = 31m,
        });

        Assert.True(orders.TryGet(fixture.ClOrdId, out var order));
        Assert.NotNull(order);
        Assert.Equal(5, order.CumulativeQuantity);
        Assert.Equal(
            5,
            Assert.Single(positions.ForEndClientAndFirm(
                "F1",
                new EndClientId("sensitive-owner"))).NetQuantity);
        Assert.Equal(
            1,
            Assert.Single(
                clOrdIds.Snapshot().Counters,
                counter => counter.EndClientId == "sensitive-owner").Counter);
        Assert.All(clOrdIds.Snapshot().Counters, counter => Assert.Equal(1, counter.Counter));
        Assert.Equal(1, fixture.Ledger.ReadinessBlockingCount);
    }

    [Fact]
    public void EvidenceSnapshot_RestoresUnmatchedDiagnosticsWithoutSensitivePayloads()
    {
        var ledger = new OutboundMutationLedger();
        for (ulong i = 1; i <= OutboundMutationLedger.MaxRetainedUnmatchedEvidence + 1UL; i++)
        {
            ledger.ApplyBusinessReject(new BusinessRejectReceivedEvent
            {
                FirmId = "F1",
                SessionId = 11,
                SessionVerId = 2,
                RefSeqNum = i,
                RejectReason = 3,
                Text = "account=SECRET investor=SECRET",
                SeqNum = i,
                SendingTime = T0,
                TimestampUtc = T0.AddTicks((long)i),
            });
        }

        Assert.Equal(
            OutboundMutationLedger.MaxRetainedUnmatchedEvidence,
            ledger.InboundEvidenceCount);
        var snapshot = ledger.CaptureSnapshot();
        var restored = new OutboundMutationLedger();
        restored.Restore(
            snapshot.Mutations,
            snapshot.Correlations,
            snapshot.InboundEvidence);

        var diagnostics = restored.GetInboundEvidenceDiagnostics(
            OutboundMutationLedger.MaxEvidenceDiagnostics);
        Assert.Equal(OutboundMutationLedger.MaxEvidenceDiagnostics, diagnostics.Count);
        var json = JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain("SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("account", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("investor", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommitBeforeApply_CrashWindowReplaysEvidenceDeterministically()
    {
        var fixture = Fixture.Create();
        ApplyPrepared(fixture.Ledger, fixture, fixture.Frame);
        var store = new CommitTrackingStore();
        var dispatcher = new EventDispatcher(store);
        var acknowledgement = Acknowledgement(
            fixture,
            firmId: "F1",
            sessionId: 11,
            sessionVerId: 2);

        Assert.Throws<SimulatedCrashException>(() =>
            dispatcher.DispatchCommitted(
                acknowledgement,
                () => throw new SimulatedCrashException()));
        Assert.True(store.Flushed);
        AssertState(fixture, OutboundMutationState.FramePrepared);

        var recovered = new OutboundMutationLedger(fixture.Protector);
        recovered.Apply(fixture.Approved);
        recovered.Apply(fixture.Intent);
        recovered.Apply(fixture.Frame);
        recovered.ApplyVenueAcknowledgement(
            Assert.IsType<ExecutionReportReceivedEvent>(Assert.Single(store.Events)));
        AssertState(recovered, fixture.MutationId, OutboundMutationState.VenueAcknowledged);
        Assert.Single(recovered.GetInboundEvidenceDiagnostics());
    }

    [Fact]
    public void C16_ExecutionReportReceivedButNotAdmitted_DrainsUntilRetransmission()
    {
        var fixture = Fixture.Create();
        ApplyPrepared(fixture.Ledger, fixture, fixture.Frame);
        var order = AddPendingOrder(fixture.ClOrdId);
        var ownership = Ownership(order);
        var orders = new WorkingOrderBook();
        Assert.True(orders.TryAdd(order));
        var processor = new ExecutionReportProcessor(
            ownership,
            orders,
            new PositionKeeper(),
            new NoOpExecutionEventSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        var drain = new TestDrain();
        var rejectedClient = new MockEntryPointClient();
        using (var rejectedRouter = new EntryPointExecutionReportRouter(
                   rejectedClient,
                   processor,
                   new EventDispatcher(new RejectingAdmissionStore()),
                   orders,
                   bookTop: null,
                   drain,
                   fixture.Ledger))
        {
            Assert.Throws<WalFaultedException>(() =>
                rejectedClient.EmitExecutionReport(
                    ExactNewEnvelope(fixture.ClOrdId, inboundSeqNum: 90)));
        }

        Assert.True(drain.IsDraining);
        Assert.Equal(OrderStatus.PendingNew, order.Status);
        Assert.Equal(0, fixture.Ledger.InboundEvidenceCount);
        AssertState(fixture, OutboundMutationState.FramePrepared);

        var retransmitClient = new MockEntryPointClient();
        using var retransmitRouter = new EntryPointExecutionReportRouter(
            retransmitClient,
            processor,
            new EventDispatcher(new CommitTrackingStore()),
            orders,
            bookTop: null,
            drain: null,
            fixture.Ledger);
        var retransmission = ExactNewEnvelope(
            fixture.ClOrdId,
            inboundSeqNum: 90) with
        {
            PossibleResend = true,
        };

        retransmitClient.EmitExecutionReport(retransmission);
        retransmitClient.EmitExecutionReport(retransmission);

        Assert.Equal(OrderStatus.Working, order.Status);
        AssertState(fixture, OutboundMutationState.VenueAcknowledged);
        Assert.Equal(1, fixture.Ledger.InboundEvidenceCount);
    }

    [Fact]
    public void Crypto_RotatesDecryptsHistoricalKeys_AndRejectsMissingWrongOrTamperedKeys()
    {
        var oldProtector = Protector(("old", 1, Key(1)), active: ("old", 1));
        var mutationId = new OutboundMutationId(Guid.Parse(
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var sensitive = Sensitive();
        var command = CryptoCommand();
        OutboundSensitiveFieldRef[] fieldRefs = [OutboundSensitiveFieldRef.EndClientId];
        var envelope = oldProtector.Encrypt(
            mutationId, "F1", command, fieldRefs, sensitive);

        var rotated = Protector(
            ("old", 1, Key(1)),
            ("new", 2, Key(2)),
            active: ("new", 2),
            stableReference: ("old", 1));
        Assert.Equal(sensitive.EndClientId,
            rotated.Decrypt(mutationId, "F1", command, fieldRefs, envelope).EndClientId);
        Assert.Equal("new",
            rotated.Encrypt(mutationId, "F1", command, fieldRefs, sensitive).KeyId);
        Assert.Equal(
            oldProtector.CreateStableEndClientRef("F1", sensitive.EndClientId),
            rotated.CreateStableEndClientRef("F1", sensitive.EndClientId));

        var missing = Protector(("new", 2, Key(2)), active: ("new", 2));
        var missingEx = Assert.Throws<OutboundCommandEnvelopeException>(() =>
            missing.Decrypt(mutationId, "F1", command, fieldRefs, envelope));
        Assert.Equal(OutboundSensitivePayloadAvailability.MissingHistoricalKey,
            missingEx.Availability);

        var wrong = Protector(("old", 1, Key(9)), active: ("old", 1));
        var wrongEx = Assert.Throws<OutboundCommandEnvelopeException>(() =>
            wrong.Decrypt(mutationId, "F1", command, fieldRefs, envelope));
        Assert.Equal(OutboundSensitivePayloadAvailability.AuthenticationFailed,
            wrongEx.Availability);

        var ciphertext = Convert.FromBase64String(envelope.CiphertextBase64);
        ciphertext[0] ^= 0x80;
        var tampered = new EncryptedOutboundCommandEnvelope
        {
            KeyId = envelope.KeyId,
            KeyVersion = envelope.KeyVersion,
            AlgorithmVersion = envelope.AlgorithmVersion,
            NonceBase64 = envelope.NonceBase64,
            CiphertextBase64 = Convert.ToBase64String(ciphertext),
            AuthenticationTagBase64 = envelope.AuthenticationTagBase64,
        };
        Assert.Throws<OutboundCommandEnvelopeException>(() =>
            oldProtector.Decrypt(mutationId, "F1", command, fieldRefs, tampered));
    }

    [Fact]
    public void Crypto_UsesUniqueNonces_AndStableOpaqueEndClientReferences()
    {
        var protector = Protector(("active", 1, Key(1)), active: ("active", 1));
        var mutationA = new OutboundMutationId(Guid.NewGuid());
        var mutationB = new OutboundMutationId(Guid.NewGuid());
        var command = CryptoCommand();
        OutboundSensitiveFieldRef[] fieldRefs = [OutboundSensitiveFieldRef.EndClientId];
        var a = protector.Encrypt(mutationA, "F1", command, fieldRefs, Sensitive());
        var b = protector.Encrypt(mutationB, "F1", command, fieldRefs, Sensitive());

        Assert.NotEqual(a.NonceBase64, b.NonceBase64);
        var reference = protector.CreateStableEndClientRef("F1", Sensitive().EndClientId);
        Assert.Equal(reference,
            protector.CreateStableEndClientRef("F1", Sensitive().EndClientId));
        Assert.DoesNotContain(Sensitive().EndClientId, reference, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingHistoricalKey_PreservesMetadataAndFailsReadinessClosed()
    {
        var writer = Fixture.Create();
        writer.Ledger.Apply(writer.Approved);
        writer.Ledger.Apply(writer.Intent);
        var snapshot = writer.Ledger.SnapshotMutations();

        var missingProtector = Protector(("other", 1, Key(8)), active: ("other", 1));
        var restored = new OutboundMutationLedger(missingProtector);
        restored.Restore(snapshot, writer.Ledger.SnapshotCorrelations());

        Assert.True(restored.TryGet(writer.MutationId, out var mutation));
        Assert.Equal(OutboundSensitivePayloadAvailability.MissingHistoricalKey,
            mutation!.SensitivePayloadAvailability);
        Assert.True(mutation.RequiresReconciliation);
        Assert.Equal(1, restored.ReadinessBlockingCount);
        Assert.Single(mutation.Attempts);
    }

    [Fact]
    public void CanonicalPlaintextTamper_CannotBypassAuthenticatedEnvelope()
    {
        var fixture = Fixture.Create();
        var alteredCommand = fixture.Approved.Approval.CanonicalCommandNonSensitive with
        {
            Quantity = 999,
        };
        var alteredApproval = fixture.Approved.Approval with
        {
            CanonicalCommandNonSensitive = alteredCommand,
            StoredCommandIntegritySha256 =
                AeadOutboundCommandProtector.ComputeIntegritySha256(
                    alteredCommand,
                    fixture.Approved.Approval.SensitiveFieldRefs,
                    fixture.Approved.Approval.SensitiveCommandEnvelope),
        };
        var ledger = new OutboundMutationLedger(fixture.Protector);
        ledger.Apply(fixture.Approved with { Approval = alteredApproval });

        Assert.True(ledger.TryGet(fixture.MutationId, out var mutation));
        Assert.Equal(OutboundSensitivePayloadAvailability.AuthenticationFailed,
            mutation!.SensitivePayloadAvailability);
        Assert.True(mutation.RequiresReconciliation);
    }

    [Fact]
    public void LegacySnapshotWithoutEnvelope_RemainsReadableAndImportsPendingState()
    {
        const string legacyJson =
            """
            {
              "seq": 9,
              "createdAtUtc": "2026-07-18T01:02:03Z",
              "workingOrders": [{
                "clOrdId": 1,
                "endClientId": "legacy-customer",
                "symbol": "PETR4",
                "securityId": 123,
                "side": "Buy",
                "type": "Limit",
                "quantity": 10,
                "price": 30,
                "leavesQuantity": 10,
                "cumulativeQuantity": 0,
                "status": "PendingNew",
                "firmId": "F1"
              }],
              "ownership": [{ "clOrdId": 1, "endClientId": "legacy-customer" }],
              "pendingCancels": [{ "originalClOrdId": 1, "cancelClOrdId": 2 }],
              "pendingReplacements": [{
                "originalClOrdId": 1,
                "newClOrdId": 3,
                "ownerEndClientId": "legacy-customer",
                "symbol": "PETR4",
                "securityId": 123,
                "side": "Buy",
                "type": "Limit",
                "newQuantity": 20,
                "newPrice": 31,
                "firmId": "F1",
                "createdAtUtc": "2026-07-18T01:02:03Z",
                "ambiguousMarginHeld": true,
                "ambiguousAtUtc": "2026-07-18T01:02:04Z",
                "newRemainingNotional": 620
              }]
            }
            """;
        var snapshot = JsonSerializer.Deserialize<PlatformSnapshot>(
            legacyJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(snapshot);
        Assert.Null(snapshot.OutboundLedger);

        var ledger = new OutboundMutationLedger();
        var snapshotter = NewSnapshotter(
            ledger,
            new PendingReplacementRegistry(),
            new PendingCancelRegistry());
        snapshotter.Restore(snapshot);

        Assert.Equal(3, ledger.Count);
        Assert.Contains(ledger.SnapshotMutations(),
            m => m.State == OutboundMutationState.LegacyUnknown);
        Assert.Contains(ledger.SnapshotMutations(),
            m => m.State == OutboundMutationState.LegacyUnknownCancel);
        Assert.Contains(ledger.SnapshotMutations(),
            m => m.State == OutboundMutationState.Ambiguous);
        Assert.Equal(3, ledger.ReadinessBlockingCount);
        Assert.True(ledger.TryGetActiveForOriginal("F1", 1, out var activeCancel));
        Assert.Equal(OutboundMutationKind.Cancel, activeCancel!.Kind);
        ledger.ImportLegacyProvenUnsent(
            2,
            OutboundMutationKind.Cancel,
            1,
            T0.AddSeconds(1),
            OutboundProvenUnsentEvidence.LegacyWave1CancelPreSend);
        Assert.True(ledger.TryGetActiveForOriginal("F1", 1, out var activeReplace));
        Assert.Equal(OutboundMutationKind.Replace, activeReplace!.Kind);
    }

    [Fact]
    public void VersionedSnapshot_CapturesAndRestoresLedgerInsideAppliedPrefixEnvelope()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);
        var snapshotter = NewSnapshotter(
            fixture.Ledger,
            new PendingReplacementRegistry(),
            new PendingCancelRegistry());

        var snapshot = StateSnapshotter.Project(
            snapshotter.CaptureRaw(2, Guid.NewGuid()));
        Assert.Single(snapshot.OutboundLedger!.Mutations);

        var restoredLedger = new OutboundMutationLedger(fixture.Protector);
        NewSnapshotter(
            restoredLedger,
            new PendingReplacementRegistry(),
            new PendingCancelRegistry()).Restore(snapshot);

        Assert.Equal(
            JsonSerializer.Serialize(fixture.Ledger.SnapshotMutations()),
            JsonSerializer.Serialize(restoredLedger.SnapshotMutations()));
    }

    [Fact]
    public void CompletedLegacyMigration_IsSnapshotDurableAndDoesNotReimportPendingRows()
    {
        var ledger = new OutboundMutationLedger();
        ledger.CompleteLegacyMigration();
        var snapshotter = NewSnapshotter(
            ledger,
            new PendingReplacementRegistry(),
            new PendingCancelRegistry());
        var captured = StateSnapshotter.Project(
            snapshotter.CaptureRaw(1, Guid.NewGuid()));
        Assert.True(captured.OutboundLedger!.LegacyMigrationCompleted);

        captured.WorkingOrders.Add(new OrderSnapshot(
            ClOrdId: 1,
            EndClientId: "legacy-customer",
            Symbol: "PETR4",
            SecurityId: 123,
            Side: "Buy",
            Type: "Limit",
            Quantity: 10,
            Price: 30m,
            LeavesQuantity: 10,
            CumulativeQuantity: 0,
            Status: "PendingNew",
            FirmId: "F1"));
        var restored = new OutboundMutationLedger();
        NewSnapshotter(
            restored,
            new PendingReplacementRegistry(),
            new PendingCancelRegistry()).Restore(captured);

        Assert.True(restored.LegacyMigrationCompleted);
        Assert.Empty(restored.SnapshotMutations());
    }

    [Fact]
    public void SerializationAndDiagnostics_NeverExposeSensitivePlaintext()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);
        var snapshot = new PlatformSnapshot
        {
            Seq = 2,
            FormatVersion = PlatformSnapshot.CurrentFormatVersion,
            WalGeneration = Guid.NewGuid(),
            OutboundLedger = new OutboundLedgerSnapshot
            {
                Version = OutboundLedgerSnapshot.CurrentVersion,
                Mutations = fixture.Ledger.SnapshotMutations().ToList(),
                CorrelationTombstones = fixture.Ledger.SnapshotCorrelations().ToList(),
            },
        };

        var eventJson = JsonSerializer.Serialize<WalEvent>(
            fixture.Approved, WalEventJsonContext.Default.WalEvent);
        var snapshotJson = JsonSerializer.Serialize(snapshot,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var diagnosticsJson = JsonSerializer.Serialize(
            fixture.Ledger.GetDiagnostics(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var metricDimensionsJson = JsonSerializer.Serialize(
            fixture.Ledger.GetMetricDimensions(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var combined = eventJson + snapshotJson + diagnosticsJson + metricDimensionsJson
            + fixture.Approved.Approval.SensitiveCommandEnvelope
            + Sensitive();

        foreach (var secret in SensitiveValues())
            Assert.DoesNotContain(secret, combined, StringComparison.Ordinal);
        Assert.Contains("\"keyId\":\"key-a\"", eventJson, StringComparison.Ordinal);
        Assert.Contains("\"keyVersion\":1", eventJson, StringComparison.Ordinal);
    }

    [Fact]
    public void FutureCommandOrEnvelopeVersions_RemainPreservedAndReadinessClosed()
    {
        var fixture = Fixture.Create();
        var futureCommand = fixture.Approved.Approval.CanonicalCommandNonSensitive with
        {
            Version = OutboundCanonicalCommand.CurrentVersion + 1,
        };
        var futureApproval = OutboundApprovalFactory.Create(
            fixture.MutationId,
            "F1",
            futureCommand,
            Sensitive(),
            [OutboundSensitiveFieldRef.EndClientId],
            fixture.Protector,
            T0);
        var ledger = new OutboundMutationLedger(fixture.Protector);
        ledger.Apply(fixture.Approved with { Approval = futureApproval });
        Assert.True(ledger.TryGet(fixture.MutationId, out var future));
        Assert.Equal(OutboundSensitivePayloadAvailability.UnsupportedVersion,
            future!.SensitivePayloadAvailability);
        Assert.Equal(1, ledger.ReadinessBlockingCount);

        var snapshotter = NewSnapshotter(
            new OutboundMutationLedger(),
            new PendingReplacementRegistry(),
            new PendingCancelRegistry());
        Assert.Throws<OutboundLedgerRecoveryException>(() =>
            snapshotter.Restore(new PlatformSnapshot
            {
                OutboundLedger = new OutboundLedgerSnapshot
                {
                    Version = OutboundLedgerSnapshot.CurrentVersion + 1,
                },
            }));
    }

    [Fact]
    public void LogCapture_ContainsOnlyCountsAndNeverCustomerValues()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Frame);
        fixture.Ledger.ClassifyRecoveredAttempts(
            new ProcessEpochId(Guid.NewGuid()), T0.AddMinutes(1));
        var logger = new CapturingLogger<ColdStartLifecycleGuard>();
        var drain = new TestDrain();
        var guard = new ColdStartLifecycleGuard(
            new PendingCancelRegistry(),
            new PendingReplacementRegistry(),
            drain,
            logger,
            fixture.Ledger);

        Assert.Equal(1, guard.Apply());
        Assert.True(drain.IsDraining);
        var logs = string.Join('\n', logger.Messages);
        foreach (var secret in SensitiveValues())
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
    }

    [Fact]
    public void WalEvents_RoundTripThroughSourceGeneratedContext()
    {
        var fixture = Fixture.Create();
        WalEvent[] events =
        [
            fixture.Approved,
            fixture.Intent,
            fixture.Frame,
            fixture.Write,
            fixture.Unsent,
            new OutboundOperatorResolvedEvent
            {
                MutationId = fixture.MutationId,
                Decision = OutboundOperatorDecision.LeaveAmbiguous,
                EvidenceType = OutboundOperatorEvidenceType.ManualAnnotation,
                EvidenceDigest = new string('a', 64),
                OperatorRef = "operator-17",
                ResolvedAtUtc = T0.AddMinutes(1),
                TimestampUtc = T0.AddMinutes(1),
            },
        ];

        foreach (var evt in events)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(
                evt, WalEventJsonContext.Default.WalEvent);
            var restored = JsonSerializer.Deserialize(
                json, WalEventJsonContext.Default.WalEvent);
            Assert.NotNull(restored);
            Assert.Equal(evt.GetType(), restored.GetType());
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse(json),
                JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(
                    restored, WalEventJsonContext.Default.WalEvent))));
        }
    }

    [Fact]
    public void EventReplayer_AdvancesWatermarkForEveryBurnedAttempt()
    {
        var fixture = Fixture.Create();
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var ownership = new OrderOwnershipMap();
        var orders = new WorkingOrderBook();
        var processor = new ExecutionReportProcessor(
            ownership,
            orders,
            new PositionKeeper(),
            new NoOpExecutionEventSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        var replayer = new EventReplayer(
            orders,
            ownership,
            new KillSwitchService(),
            new SymbolHaltService(),
            new SessionPhaseService(),
            processor,
            new AlgoBook(),
            clOrdIds,
            new AlgoIdRegistry(),
            outboundLedger: fixture.Ledger);

        replayer.Apply(fixture.Approved);
        replayer.Apply(fixture.Intent);

        Assert.Equal(2UL, clOrdIds.Generate(new EndClientId(Sensitive().EndClientId)));
    }

    [Theory]
    [InlineData(OutboundMutationKind.New)]
    [InlineData(OutboundMutationKind.Cancel)]
    [InlineData(OutboundMutationKind.Replace)]
    public async Task Restart_ApprovalOnlyCommittedTail_AdvancesPrimaryWatermark(
        OutboundMutationKind kind)
    {
        var fixture = Fixture.Create();
        var approved = fixture.Approved with
        {
            MutationKind = kind,
            OriginalClOrdId = kind == OutboundMutationKind.New ? null : 99UL,
        };
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "outbound-approval-restart",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new PersistenceOptions
        {
            DataDirectory = root,
            FirmId = "test",
            ChannelCapacity = 16,
            GroupCommitMaxRecords = 1,
            GroupCommitWindow = TimeSpan.Zero,
            SegmentMaxBytes = 4096,
            IndexEveryNRecords = 1,
            IndexEveryNBytes = 128,
            FsyncOnFlush = false,
            LegacyWalStartupMode = LegacyWalStartupMode.ControlledCleanShutdown,
        };
        try
        {
            await using (var store = new FileEventStore(
                options, NullLogger<FileEventStore>.Instance))
            {
                var seq = store.Append(approved);
                await store.FlushThroughAsync(seq);
            }

            var ledger = new OutboundMutationLedger(fixture.Protector);
            var clOrdIds = new ClOrdIdPrefixRegistry();
            var replayer = NewReplayer(ledger, clOrdIds);
            await using var reopened = new FileEventStore(
                options, NullLogger<FileEventStore>.Instance);
            await foreach (var (_, evt) in reopened.ReadFromAsync(0))
                replayer.Apply(evt);

            var mutation = Assert.Single(ledger.SnapshotMutations());
            Assert.Equal(OutboundMutationState.ApprovedToSend, mutation.State);
            Assert.Empty(mutation.Attempts);
            Assert.Equal(
                2UL,
                clOrdIds.Generate(new EndClientId(Sensitive().EndClientId)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PersistenceRecovery_WriteWithoutFrame_RequiresReconciliation()
    {
        var fixture = Fixture.Create();
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "outbound-missing-frame-restart",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new PersistenceOptions
        {
            DataDirectory = root,
            FirmId = "test",
            ChannelCapacity = 16,
            GroupCommitMaxRecords = 1,
            GroupCommitWindow = TimeSpan.Zero,
            SegmentMaxBytes = 4096,
            IndexEveryNRecords = 1,
            IndexEveryNBytes = 128,
            FsyncOnFlush = false,
            LegacyWalStartupMode = LegacyWalStartupMode.ControlledCleanShutdown,
        };
        try
        {
            await using (var store = new FileEventStore(
                options, NullLogger<FileEventStore>.Instance))
            {
                store.Append(fixture.Approved);
                store.Append(fixture.Intent);
                var writeSeq = store.Append(fixture.Write);
                await store.FlushThroughAsync(writeSeq);
            }

            var ledger = new OutboundMutationLedger(fixture.Protector);
            var clOrdIds = new ClOrdIdPrefixRegistry();
            var snapshotter = NewSnapshotter(
                ledger,
                clOrdIds,
                new PendingReplacementRegistry(),
                new PendingCancelRegistry());
            var replayer = NewReplayer(ledger, clOrdIds);
            await using var reopened = new FileEventStore(
                options, NullLogger<FileEventStore>.Instance);
            var recovery = new PersistenceRecovery(
                reopened,
                snapshotter,
                replayer,
                new SnapshotStore(root, "test"),
                NullLogger<PersistenceRecovery>.Instance);

            await recovery.RunAsync();

            var mutation = Assert.Single(ledger.SnapshotMutations());
            Assert.Equal(OutboundMutationState.Ambiguous, mutation.State);
            Assert.True(mutation.RequiresReconciliation);
            Assert.Equal(1, ledger.ReadinessBlockingCount);
            var attempt = Assert.Single(mutation.Attempts);
            Assert.Null(attempt.FramePrepared);
            Assert.Equal(fixture.Write.CompletedAtUtc, attempt.TransportWriteCompletedAtUtc);
            Assert.Equal(
                OutboundAmbiguityReason.MissingFramePreparedEvidence,
                attempt.AmbiguityReason);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ApprovalReplay_WithMissingHistoricalKey_BlocksReadiness()
    {
        var writer = Fixture.Create();
        var missingProtector = Protector(
            ("other", 1, Key(9)), active: ("other", 1));
        var ledger = new OutboundMutationLedger(missingProtector);
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var replayer = NewReplayer(ledger, clOrdIds);

        replayer.Apply(writer.Approved);

        var mutation = Assert.Single(ledger.SnapshotMutations());
        Assert.Equal(
            OutboundSensitivePayloadAvailability.MissingHistoricalKey,
            mutation.SensitivePayloadAvailability);
        Assert.True(mutation.RequiresReconciliation);
        Assert.Equal(1, ledger.ReadinessBlockingCount);
        var drain = new TestDrain();
        var guard = new ColdStartLifecycleGuard(
            new PendingCancelRegistry(),
            new PendingReplacementRegistry(),
            drain,
            new CapturingLogger<ColdStartLifecycleGuard>(),
            ledger);
        Assert.Equal(1, guard.Apply());
        Assert.True(drain.IsDraining);
    }

    [Fact]
    public void MissingKey_VenueAcknowledgementTerminalisesEvidenceButKeepsReadinessBlocked()
    {
        var writer = Fixture.Create();
        var ledger = new OutboundMutationLedger(Protector(
            ("other", 1, Key(9)), active: ("other", 1)));
        var replayer = NewReplayer(ledger, new ClOrdIdPrefixRegistry());
        replayer.Apply(writer.Approved);
        replayer.Apply(writer.Intent);
        replayer.Apply(writer.Frame);
        replayer.Apply(Acknowledgement(
            writer, firmId: "F1", sessionId: 11, sessionVerId: 2));

        var terminal = Assert.Single(ledger.SnapshotMutations());
        Assert.Equal(OutboundMutationState.VenueAcknowledged, terminal.State);
        Assert.Equal(
            OutboundSensitivePayloadAvailability.MissingHistoricalKey,
            terminal.SensitivePayloadAvailability);
        Assert.True(terminal.RequiresReconciliation);
        Assert.Equal(1, ledger.ReadinessBlockingCount);
        Assert.Equal(0, ledger.PurgeTerminalCorrelations(T0.AddYears(10)));
        Assert.Single(ledger.SnapshotMutations());
        AssertKeyRestorationClearsPayloadBlocker(ledger, writer);
    }

    [Fact]
    public void WrongKey_BusinessRejectTerminalisesEvidenceButKeepsReadinessBlocked()
    {
        var writer = Fixture.Create();
        var ledger = new OutboundMutationLedger(Protector(
            ("key-a", 1, Key(9)), active: ("key-a", 1)));
        var replayer = NewReplayer(ledger, new ClOrdIdPrefixRegistry());
        replayer.Apply(writer.Approved);
        replayer.Apply(writer.Intent);
        replayer.Apply(writer.Frame);
        replayer.Apply(new BusinessRejectReceivedEvent
        {
            FirmId = "F1",
            SessionId = 11,
            SessionVerId = 2,
            RefSeqNum = 77,
            RejectReason = 3,
            SeqNum = 90,
            SendingTime = T0,
            TimestampUtc = T0.AddMinutes(1),
        });

        var terminal = Assert.Single(ledger.SnapshotMutations());
        Assert.Equal(OutboundMutationState.VenueAcknowledged, terminal.State);
        Assert.Equal(
            OutboundSensitivePayloadAvailability.AuthenticationFailed,
            terminal.SensitivePayloadAvailability);
        Assert.True(terminal.RequiresReconciliation);
        Assert.Equal(1, ledger.ReadinessBlockingCount);
        AssertKeyRestorationClearsPayloadBlocker(ledger, writer);
    }

    [Fact]
    public void MissingKey_OperatorTerminalisationKeepsReadinessBlocked()
    {
        var writer = Fixture.Create();
        var ledger = new OutboundMutationLedger(Protector(
            ("other", 1, Key(9)), active: ("other", 1)));
        var replayer = NewReplayer(ledger, new ClOrdIdPrefixRegistry());
        replayer.Apply(writer.Approved);
        replayer.Apply(writer.Intent);
        replayer.Apply(writer.Unsent);
        replayer.Apply(new OutboundOperatorResolvedEvent
        {
            MutationId = writer.MutationId,
            Decision = OutboundOperatorDecision.VenueAbsent,
            EvidenceType = OutboundOperatorEvidenceType.OfficialExtract,
            EvidenceDigest = new string('c', 64),
            OperatorRef = "operator-17",
            ResolvedAtUtc = T0.AddMinutes(1),
            TimestampUtc = T0.AddMinutes(1),
        });

        var terminal = Assert.Single(ledger.SnapshotMutations());
        Assert.Equal(OutboundMutationState.OperatorResolved, terminal.State);
        Assert.True(terminal.RequiresReconciliation);
        Assert.Equal(1, ledger.ReadinessBlockingCount);
        AssertKeyRestorationClearsPayloadBlocker(ledger, writer);
    }

    [Fact]
    public void ReconciliationRequiredEvent_ReplayDurablyFlagsProvenUnsentMutation()
    {
        var writer = Fixture.Create();
        var ledger = new OutboundMutationLedger(writer.Protector);
        var replayer = NewReplayer(ledger, new ClOrdIdPrefixRegistry());
        replayer.Apply(writer.Approved);
        replayer.Apply(writer.Intent);
        replayer.Apply(writer.Unsent);
        var flaggedAt = T0.AddMinutes(1);

        replayer.Apply(new OutboundReconciliationRequiredEvent
        {
            MutationId = writer.MutationId,
            Reason = "AlgoRepegAttemptCapExhausted",
            TimestampUtc = flaggedAt,
        });

        var mutation = Assert.Single(ledger.SnapshotMutations());
        Assert.Equal(OutboundMutationState.ProvenUnsent, mutation.State);
        Assert.True(mutation.RequiresReconciliation);
        Assert.True(mutation.ExplicitlyRequiresReconciliation);
        Assert.Equal(flaggedAt, mutation.StateChangedAtUtc);
    }

    [Fact]
    public void ReconciliationRequiredEvent_SnapshotRestorePreservesExplicitFlag()
    {
        var writer = Fixture.Create();
        writer.Ledger.Apply(writer.Approved);
        writer.Ledger.Apply(writer.Intent);
        writer.Ledger.Apply(writer.Unsent);
        writer.Ledger.Apply(new OutboundReconciliationRequiredEvent
        {
            MutationId = writer.MutationId,
            Reason = "AlgoRepegAttemptCapExhausted",
            TimestampUtc = T0.AddMinutes(1),
        });

        var restored = RestoreLedger(writer);

        var mutation = Assert.Single(restored.SnapshotMutations());
        Assert.Equal(OutboundMutationState.ProvenUnsent, mutation.State);
        Assert.Equal(
            OutboundSensitivePayloadAvailability.Available,
            mutation.SensitivePayloadAvailability);
        Assert.True(mutation.RequiresReconciliation);
        Assert.True(mutation.ExplicitlyRequiresReconciliation);
    }

    [Fact]
    public void SnapshotRestore_RepairsPrimaryAndRetryAttemptWatermarksIdempotently()
    {
        var fixture = Fixture.Create();
        fixture.Ledger.Apply(fixture.Approved);
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Unsent);
        fixture.Ledger.Apply(fixture.Intent with
        {
            AttemptId = new OutboundAttemptId(Guid.Parse(
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
            AttemptNo = 2,
            ClOrdId = 2,
            IntentPreparedAtUtc = T0.AddSeconds(6),
            TimestampUtc = T0.AddSeconds(6),
        });
        var capture = fixture.Ledger.CaptureSnapshot();
        var snapshot = new PlatformSnapshot
        {
            Seq = 4,
            FormatVersion = PlatformSnapshot.CurrentFormatVersion,
            WalGeneration = Guid.NewGuid(),
            OutboundLedger = new OutboundLedgerSnapshot
            {
                Version = OutboundLedgerSnapshot.CurrentVersion,
                LegacyMigrationCompleted = true,
                Mutations = capture.Mutations.ToList(),
                CorrelationTombstones = capture.Correlations.ToList(),
                InboundEvidence = capture.InboundEvidence.ToList(),
            },
            ClOrdIds = new ClOrdIdRegistrySnapshot(),
        };
        var restoredLedger = new OutboundMutationLedger(fixture.Protector);
        var restoredClOrdIds = new ClOrdIdPrefixRegistry();
        var snapshotter = NewSnapshotter(
            restoredLedger,
            restoredClOrdIds,
            new PendingReplacementRegistry(),
            new PendingCancelRegistry());

        snapshotter.Restore(snapshot);
        snapshotter.Restore(snapshot);

        Assert.Equal(
            3UL,
            restoredClOrdIds.Generate(
                new EndClientId(Sensitive().EndClientId)));
    }

    [Fact]
    public void SnapshotRestore_AcceptsV1LedgerOnlyWhenInboundEvidenceIsEmpty()
    {
        var ledger = new OutboundMutationLedger();
        var snapshotter = NewSnapshotter(
            ledger,
            new ClOrdIdPrefixRegistry(),
            new PendingReplacementRegistry(),
            new PendingCancelRegistry());

        snapshotter.Restore(new PlatformSnapshot
        {
            OutboundLedger = new OutboundLedgerSnapshot
            {
                Version = OutboundLedgerSnapshot.LegacyVersionWithoutInboundEvidence,
            },
        });

        Assert.Equal(0, ledger.Count);
        Assert.Throws<OutboundLedgerRecoveryException>(() =>
            snapshotter.Restore(new PlatformSnapshot
            {
                OutboundLedger = new OutboundLedgerSnapshot
                {
                    Version = OutboundLedgerSnapshot.LegacyVersionWithoutInboundEvidence,
                    InboundEvidence =
                    [
                        new InboundVenueEvidenceSnapshot
                        {
                            EvidenceId = new string('a', 64),
                            Kind = InboundVenueEvidenceKind.ExecutionReport,
                            Disposition = InboundVenueEvidenceDisposition.Unmatched,
                            FirmId = "F1",
                            ObservedAtUtc = T0,
                        },
                    ],
                },
            }));
    }

    [Fact]
    public void EventReplayer_FirmMismatchCannotTerminaliseLedgerAfterProcessorRejectsIt()
    {
        var fixture = Fixture.Create();
        var ownership = new OrderOwnershipMap();
        var orders = new WorkingOrderBook();
        var processor = new ExecutionReportProcessor(
            ownership,
            orders,
            new PositionKeeper(),
            new NoOpExecutionEventSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        var replayer = new EventReplayer(
            orders,
            ownership,
            new KillSwitchService(),
            new SymbolHaltService(),
            new SessionPhaseService(),
            processor,
            new AlgoBook(),
            new ClOrdIdPrefixRegistry(),
            new AlgoIdRegistry(),
            outboundLedger: fixture.Ledger);

        replayer.Apply(fixture.Approved);
        replayer.Apply(fixture.Intent);
        replayer.Apply(fixture.Frame);
        replayer.Apply(Acknowledgement(
            fixture, firmId: "OTHER", sessionId: 11, sessionVerId: 2));

        AssertState(fixture, OutboundMutationState.Ambiguous);
        Assert.Equal(1, fixture.Ledger.ReadinessBlockingCount);
        Assert.Null(fixture.Ledger.SnapshotMutations()[0].Resolution);
    }

    [Fact]
    public void Property_RestoredPrefixPlusTail_EqualsFullCommittedPrefix()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var random = new Random(seed);
            var fixture = Fixture.Create(clOrdId: 1);
            var events = BuildValidHistory(fixture, random.Next(0, 3));
            var cut = random.Next(0, events.Count + 1);

            var prefixLedger = new OutboundMutationLedger(fixture.Protector);
            foreach (var evt in events.Take(cut))
                Apply(prefixLedger, evt);
            var restored = new OutboundMutationLedger(fixture.Protector);
            restored.Restore(
                prefixLedger.SnapshotMutations(),
                prefixLedger.SnapshotCorrelations());
            foreach (var evt in events.Skip(cut))
                Apply(restored, evt);

            var full = new OutboundMutationLedger(fixture.Protector);
            foreach (var evt in events)
                Apply(full, evt);

            Assert.Equal(
                JsonSerializer.Serialize(full.SnapshotMutations()),
                JsonSerializer.Serialize(restored.SnapshotMutations()));
            Assert.Equal(
                JsonSerializer.Serialize(full.SnapshotCorrelations()),
                JsonSerializer.Serialize(restored.SnapshotCorrelations()));
        }
    }

    [Fact]
    public async Task ConcurrentSnapshotCapture_AlwaysRestoresACommittedLedgerPrefix()
    {
        var fixture = Fixture.Create();
        WalEvent[] events =
        [
            fixture.Approved,
            fixture.Intent,
            fixture.Frame,
            fixture.Write,
        ];
        var captures = new List<string>();
        var captureTask = Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                var snapshot = fixture.Ledger.CaptureSnapshot();
                var restored = new OutboundMutationLedger(fixture.Protector);
                restored.Restore(snapshot.Mutations, snapshot.Correlations);
                lock (captures)
                    captures.Add(JsonSerializer.Serialize(restored.SnapshotMutations()));
            }
        });
        foreach (var evt in events)
        {
            Apply(fixture.Ledger, evt);
            await Task.Yield();
        }
        await captureTask;

        var validPrefixes = new HashSet<string>(StringComparer.Ordinal);
        var candidate = new OutboundMutationLedger(fixture.Protector);
        validPrefixes.Add(JsonSerializer.Serialize(candidate.SnapshotMutations()));
        foreach (var evt in events)
        {
            Apply(candidate, evt);
            validPrefixes.Add(JsonSerializer.Serialize(candidate.SnapshotMutations()));
        }
        Assert.All(captures, capture => Assert.Contains(capture, validPrefixes));
    }

    private static List<WalEvent> BuildValidHistory(Fixture fixture, int terminalMode)
    {
        var events = new List<WalEvent>
        {
            fixture.Approved,
            fixture.Intent,
        };
        if (terminalMode == 0)
        {
            events.Add(fixture.Unsent);
            return events;
        }
        events.Add(fixture.Frame);
        if (terminalMode == 2)
            events.Add(fixture.Write);
        return events;
    }

    private static void Apply(OutboundMutationLedger ledger, WalEvent evt)
    {
        switch (evt)
        {
            case OutboundApprovedEvent approved: ledger.Apply(approved); break;
            case OutboundAttemptIntentPreparedEvent intent: ledger.Apply(intent); break;
            case OutboundFramePreparedEvent frame: ledger.Apply(frame); break;
            case OutboundTransportWriteCompletedEvent write: ledger.Apply(write); break;
            case OutboundProvenUnsentEvent unsent: ledger.Apply(unsent); break;
            case OutboundOperatorResolvedEvent resolved: ledger.Apply(resolved); break;
            default: throw new InvalidOperationException();
        }
    }

    private static void AssertState(Fixture fixture, OutboundMutationState expected)
    {
        Assert.True(fixture.Ledger.TryGet(fixture.MutationId, out var mutation));
        Assert.Equal(expected, mutation!.State);
    }

    private static ExecutionReportReceivedEvent Acknowledgement(
        Fixture fixture,
        string firmId,
        ulong sessionId,
        uint sessionVerId,
        ulong? clOrdId = null) => new()
        {
            ClOrdId = clOrdId ?? fixture.ClOrdId,
            ExecKind = "New",
            LeavesQuantity = 10,
            CumulativeQuantity = 0,
            LastQuantity = 0,
            LastPrice = 0,
            Synthetic = false,
            FirmId = firmId,
            SessionId = sessionId,
            SessionVerId = sessionVerId,
            InboundSeqNum = 90,
            TimestampUtc = T0.AddMinutes(2),
        };

    private static ExecutionReportReceivedEvent OldEr(
        ulong clOrdId,
        string execKind,
        ulong origClOrdId = 0)
    {
        var json =
            $$"""
            {
              "kind": "er.received",
              "clOrdId": {{clOrdId}},
              "execKind": "{{execKind}}",
              "leavesQuantity": 0,
              "cumulativeQuantity": 0,
              "lastQuantity": 0,
              "lastPrice": 0,
              "synthetic": false,
              "origClOrdId": {{origClOrdId}},
              "timestampUtc": "2026-07-18T01:02:03Z"
            }
            """;
        var evt = JsonSerializer.Deserialize<WalEvent>(
            json, WalEventJsonContext.Default.WalEvent);
        var er = Assert.IsType<ExecutionReportReceivedEvent>(evt);
        Assert.Null(er.FirmId);
        return er;
    }

    private static OutboundMutationLedger LegacyReplaceLedger()
    {
        var ledger = new OutboundMutationLedger();
        ledger.ImportLegacyReplace(new OrderReplaceRequestedEvent
        {
            OriginalClOrdId = 1,
            NewClOrdId = 2,
            EndClientId = "sensitive-owner",
            FirmId = "F1",
            Symbol = "PETR4",
            SecurityId = 123,
            Side = "Buy",
            Type = "Limit",
            NewQuantity = 20,
            NewPrice = 31m,
            TimestampUtc = T0,
        });
        return ledger;
    }

    private static StateSnapshotter NewSnapshotter(
        OutboundMutationLedger ledger,
        PendingReplacementRegistry replacements,
        PendingCancelRegistry pendingCancels) =>
        NewSnapshotter(
            ledger,
            new ClOrdIdPrefixRegistry(),
            replacements,
            pendingCancels);

    private static StateSnapshotter NewSnapshotter(
        OutboundMutationLedger ledger,
        ClOrdIdPrefixRegistry clOrdIds,
        PendingReplacementRegistry replacements,
        PendingCancelRegistry pendingCancels) =>
        new(
            new WorkingOrderBook(),
            new PositionKeeper(),
            new KillSwitchService(),
            new SymbolHaltService(),
            new SessionPhaseService(),
            clOrdIds,
            new OrderOwnershipMap(),
            new AlgoBook(),
            new AlgoIdRegistry(),
            new CashLedger(),
            replacements: replacements,
            pendingCancels: pendingCancels,
            outboundLedger: ledger);

    private static EventReplayer NewReplayer(
        OutboundMutationLedger ledger,
        ClOrdIdPrefixRegistry clOrdIds)
    {
        var ownership = new OrderOwnershipMap();
        var orders = new WorkingOrderBook();
        var processor = new ExecutionReportProcessor(
            ownership,
            orders,
            new PositionKeeper(),
            new NoOpExecutionEventSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        return new EventReplayer(
            orders,
            ownership,
            new KillSwitchService(),
            new SymbolHaltService(),
            new SessionPhaseService(),
            processor,
            new AlgoBook(),
            clOrdIds,
            new AlgoIdRegistry(),
            outboundLedger: ledger);
    }

    private static void AssertKeyRestorationClearsPayloadBlocker(
        OutboundMutationLedger unavailableLedger,
        Fixture writer)
    {
        var capture = unavailableLedger.CaptureSnapshot();
        var restoredLedger = new OutboundMutationLedger(writer.Protector);
        var clOrdIds = new ClOrdIdPrefixRegistry();
        var snapshotter = NewSnapshotter(
            restoredLedger,
            clOrdIds,
            new PendingReplacementRegistry(),
            new PendingCancelRegistry());
        snapshotter.Restore(new PlatformSnapshot
        {
            Seq = 4,
            FormatVersion = PlatformSnapshot.CurrentFormatVersion,
            WalGeneration = Guid.NewGuid(),
            OutboundLedger = new OutboundLedgerSnapshot
            {
                Version = OutboundLedgerSnapshot.CurrentVersion,
                LegacyMigrationCompleted = true,
                Mutations = capture.Mutations.ToList(),
                CorrelationTombstones = capture.Correlations.ToList(),
                InboundEvidence = capture.InboundEvidence.ToList(),
            },
            ClOrdIds = new ClOrdIdRegistrySnapshot(),
        });

        var restored = Assert.Single(restoredLedger.SnapshotMutations());
        Assert.Equal(
            OutboundSensitivePayloadAvailability.Available,
            restored.SensitivePayloadAvailability);
        Assert.False(restored.RequiresReconciliation);
        Assert.Equal(0, restoredLedger.ReadinessBlockingCount);
        Assert.Equal(
            2UL,
            clOrdIds.Generate(new EndClientId(Sensitive().EndClientId)));
    }

    private static AeadOutboundCommandProtector Protector(
        (string Id, int Version, byte[] Key) key,
        (string Id, int Version) active) =>
        Protector([key], active);

    private static AeadOutboundCommandProtector Protector(
        (string Id, int Version, byte[] Key) first,
        (string Id, int Version, byte[] Key) second,
        (string Id, int Version) active,
        (string Id, int Version)? stableReference = null) =>
        Protector([first, second], active, stableReference);

    private static AeadOutboundCommandProtector Protector(
        IEnumerable<(string Id, int Version, byte[] Key)> keys,
        (string Id, int Version) active,
        (string Id, int Version)? stableReference = null)
    {
        return new AeadOutboundCommandProtector(
            new OutboundCommandProtectionOptions
            {
                ActiveKeyId = active.Id,
                ActiveKeyVersion = active.Version,
                StableReferenceKeyId = stableReference?.Id ?? active.Id,
                StableReferenceKeyVersion = stableReference?.Version ?? active.Version,
                Keys = keys.Select(k => new OutboundCommandProtectionKeyOptions
                {
                    KeyId = k.Id,
                    Version = k.Version,
                    KeyBase64 = Convert.ToBase64String(k.Key),
                }).ToList(),
            },
            new IncrementingNonceSource());
    }

    private static byte[] Key(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static OutboundCanonicalCommand CryptoCommand() => new()
    {
        ClOrdId = 1,
        SecurityId = 123,
        Symbol = "PETR4",
        Side = "Buy",
        OrderType = "Limit",
        Quantity = 10,
        Price = 30m,
    };

    private static SensitiveOutboundCommand Sensitive() => new()
    {
        Account = "ACC-639-SECRET",
        InvestorId = "INVESTOR-639-SECRET",
        EndClientId = "CUSTOMER-639-SECRET",
        CustomerIdentifier = "DOCUMENT-639-SECRET",
        TradingSubAccount = "SUBACCOUNT-639-SECRET",
    };

    private static OrderSubmittedEvent LegacySubmit(ulong clOrdId) => new()
    {
        ClOrdId = clOrdId,
        EndClientId = "sensitive-owner",
        FirmId = "F1",
        Symbol = "PETR4",
        SecurityId = 123,
        Side = "Buy",
        Type = "Limit",
        Quantity = 10,
        Price = 30m,
        TimestampUtc = T0,
    };

    private static string[] SensitiveValues() =>
    [
        Sensitive().Account!,
        Sensitive().InvestorId!,
        Sensitive().EndClientId,
        Sensitive().CustomerIdentifier!,
        Sensitive().TradingSubAccount!,
    ];

    private sealed class IncrementingNonceSource : IOutboundNonceSource
    {
        private int _counter;

        public void Fill(Span<byte> nonce)
        {
            nonce.Clear();
            BitConverter.TryWriteBytes(nonce[^4..], Interlocked.Increment(ref _counter));
        }
    }

    private sealed class TestDrain : IDrainController
    {
        public bool IsDraining { get; private set; }
        public string? DrainReason { get; private set; }
        public void BeginDrain(string reason)
        {
            IsDraining = true;
            DrainReason = reason;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class CapturingExecutionSink : IExecutionEventSink
    {
        public List<ExecutionEvent> Events { get; } = new();
        public void Publish(ExecutionEvent ev) => Events.Add(ev);
    }

    private static void ApplyPrepared(
        OutboundMutationLedger ledger,
        Fixture fixture,
        OutboundFramePreparedEvent frame,
        string? approvedFirm = null)
    {
        ledger.Apply(approvedFirm is null
            ? fixture.Approved
            : fixture.Approved with { FirmId = approvedFirm });
        ledger.Apply(fixture.Intent);
        ledger.Apply(frame);
    }

    private static BusinessRejectReceivedEvent BusinessReject(
        string firmId,
        ulong sessionId,
        uint sessionVerId,
        ulong refSeqNum,
        ulong inboundSeqNum) =>
        new()
        {
            FirmId = firmId,
            SessionId = sessionId,
            SessionVerId = sessionVerId,
            RefSeqNum = refSeqNum,
            RejectReason = 3,
            SeqNum = inboundSeqNum,
            SendingTime = T0,
            TimestampUtc = T0,
        };

    private static NotAppliedReceivedEvent ExactNotApplied() =>
        new()
        {
            FirmId = "F1",
            SessionId = 11,
            SessionVerId = 2,
            FromSeqNo = 77,
            Count = 1,
            ObservedAtUtc = T0.AddMinutes(1),
            TimestampUtc = T0.AddMinutes(1),
        };

    private static NotAppliedEnvelope ExactNotAppliedEnvelope() =>
        new("F1", 11, 2, 77, 1, T0.AddMinutes(1));

    private static BusinessRejectEnvelope ExactBusinessRejectEnvelope() =>
        new(
            "F1",
            RefSeqNum: 77,
            RejectReason: 3,
            Text: "structural reject",
            SeqNum: 90,
            SendingTime: T0,
            SessionId: 11,
            SessionVerId: 2);

    private static Order AddPendingOrder(ulong clOrdId) =>
        new(
            clOrdId,
            new EndClientId("sensitive-owner"),
            "PETR4",
            123,
            OrderSide.Buy,
            OrderType.Limit,
            10,
            30m,
            "F1");

    private static OrderOwnershipMap Ownership(Order order)
    {
        var ownership = new OrderOwnershipMap();
        ownership.Register(order.ClOrdId, order.Owner);
        return ownership;
    }

    private static EntryPointExecutionReportRouter CreateRouter(
        MockEntryPointClient client,
        OrderOwnershipMap ownership,
        Order order,
        OutboundMutationLedger ledger)
    {
        var orders = new WorkingOrderBook();
        Assert.True(orders.TryAdd(order));
        var processor = new ExecutionReportProcessor(
            ownership,
            orders,
            new PositionKeeper(),
            new NoOpExecutionEventSink(),
            new NoOpMarginProvider(),
            NullLogger<ExecutionReportProcessor>.Instance);
        return new EntryPointExecutionReportRouter(
            client,
            processor,
            new EventDispatcher(new NullEventStore()),
            orders,
            bookTop: null,
            drain: null,
            outboundLedger: ledger);
    }

    private static ExecutionReportEnvelope ExactNewEnvelope(
        ulong clOrdId,
        ulong inboundSeqNum) =>
        new(
            clOrdId,
            EpExecType.New,
            LeavesQuantity: 10,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0,
            RejectReason: null,
            FirmId: "F1",
            SessionId: 11,
            SessionVerId: 2,
            InboundSeqNum: inboundSeqNum,
            SendingTime: T0);

    private static OutboundMutationLedger RestoreLedger(Fixture writer)
        => RestoreLedger(writer.Ledger, writer.Protector);

    private static OutboundMutationLedger RestoreLedger(
        OutboundMutationLedger source,
        AeadOutboundCommandProtector protector)
    {
        var capture = source.CaptureSnapshot();
        var restored = new OutboundMutationLedger(protector);
        restored.Restore(
            capture.Mutations,
            capture.Correlations,
            capture.InboundEvidence);
        return restored;
    }

    private static (
        OutboundMutationLedger Ledger,
        Fixture First,
        Fixture Second) PreparedMutationPair()
    {
        var first = Fixture.Create();
        var second = Fixture.Create(clOrdId: 2);
        var ledger = new OutboundMutationLedger(first.Protector);
        ApplyPrepared(ledger, first, first.Frame);
        ApplyPrepared(ledger, second, second.Frame with
        {
            OutboundSeqNum = 78,
        });
        return (ledger, first, second);
    }

    private static void AssertTerminalConflict(
        Fixture fixture,
        string expectedEvidenceKind)
    {
        AssertState(fixture, OutboundMutationState.VenueAcknowledged);
        var mutation = Assert.Single(fixture.Ledger.SnapshotMutations());
        Assert.Equal(expectedEvidenceKind, mutation.Resolution?.EvidenceKind);
        Assert.Equal(
            OutboundAmbiguityReason.ConflictingVenueEvidence,
            Assert.Single(mutation.Attempts).AmbiguityReason);
        Assert.True(mutation.RequiresReconciliation);
        Assert.Equal(1, fixture.Ledger.ReadinessBlockingCount);
    }

    private static void AssertState(
        OutboundMutationLedger ledger,
        OutboundMutationId mutationId,
        OutboundMutationState expected)
    {
        Assert.True(ledger.TryGet(mutationId, out var mutation));
        Assert.Equal(expected, mutation!.State);
    }

    private sealed class CommitTrackingStore : IEventStore
    {
        private long _seq;
        public List<WalEvent> Events { get; } = new();
        public bool Flushed { get; private set; }
        public long CurrentSeq => _seq;
        public long Append(WalEvent evt)
        {
            Events.Add(evt);
            return ++_seq;
        }
        public long Append(WalEvent evt, ReadOnlyMemory<byte> payload) => Append(evt);
        public ValueTask FlushAsync(CancellationToken ct = default)
        {
            Flushed = true;
            return ValueTask.CompletedTask;
        }
        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            for (var i = 0; i < Events.Count; i++)
            {
                if (i + 1 > sinceSeqExclusive)
                    yield return (i + 1, Events[i]);
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RejectingAdmissionStore : IEventStore
    {
        public long CurrentSeq => 0;
        public long LastCommittedSeq => 0;
        public long Append(WalEvent evt) => throw Failure();
        public long Append(WalEvent evt, ReadOnlyMemory<byte> payload) =>
            throw Failure();
        public ValueTask FlushAsync(CancellationToken ct = default) =>
            ValueTask.FromException(Failure());
        public ValueTask FlushThroughAsync(
            long seq,
            CancellationToken ct = default) =>
            ValueTask.FromException(Failure());
        public async IAsyncEnumerable<(long Seq, WalEvent Event)> ReadFromAsync(
            long sinceSeqExclusive,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static WalFaultedException Failure() =>
            new("inbound admission rejected", new IOException("injected"));
    }

    [Fact]
    public void AlgoOriginIdentity_IsDurableUniqueAndBlocksUnresolvedScheduling()
    {
        var origin = new AlgoOutboundOriginIdentity(
            9001,
            AlgoOutboundActionKind.NewChild,
            4);
        var first = Fixture.Create(7001);
        var approved = first.Approved with
        {
            Origin = OutboundMutationOrigin.Algo,
            AlgoOriginIdentity = origin,
        };
        first.Ledger.Apply(approved);

        Assert.True(first.Ledger.HasBlockingAlgoMutation(approved.FirmId, origin.ParentAlgoId));
        Assert.True(first.Ledger.TryGetByAlgoOrigin(approved.FirmId, origin, out var stored));
        Assert.Equal(approved.MutationId, stored!.MutationId);

        var snapshot = first.Ledger.CaptureSnapshot();
        var restored = new OutboundMutationLedger(first.Protector);
        restored.Restore(
            snapshot.Mutations,
            snapshot.Correlations,
            snapshot.InboundEvidence);
        Assert.True(restored.TryGetByAlgoOrigin(approved.FirmId, origin, out var recovered));
        Assert.Equal(origin, recovered!.AlgoOriginIdentity);

        var second = Fixture.Create(7002);
        var duplicate = second.Approved with
        {
            Origin = OutboundMutationOrigin.Algo,
            AlgoOriginIdentity = origin,
        };
        Assert.Throws<InvalidOperationException>(() => restored.Apply(duplicate));
        Assert.Single(restored.GetAlgoMutations(approved.FirmId, origin.ParentAlgoId));

        var otherFirm = duplicate with { FirmId = "OTHER" };
        restored.Apply(otherFirm);
        Assert.True(restored.TryGetByAlgoOrigin("OTHER", origin, out var otherStored));
        Assert.Equal(otherFirm.MutationId, otherStored!.MutationId);
    }

    [Fact]
    public void AlgoProvenUnsent_RemainsBlockedUntilExplicitDecision()
    {
        var fixture = Fixture.Create(7101);
        var origin = new AlgoOutboundOriginIdentity(
            9002,
            AlgoOutboundActionKind.Repeg,
            1);
        fixture.Ledger.Apply(fixture.Approved with
        {
            Origin = OutboundMutationOrigin.Algo,
            AlgoOriginIdentity = origin,
        });
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Unsent);

        Assert.True(fixture.Ledger.HasBlockingAlgoMutation(
            fixture.Approved.FirmId,
            origin.ParentAlgoId));
        Assert.Equal(
            OutboundMutationState.ProvenUnsent,
            Assert.Single(fixture.Ledger.GetAlgoMutations(
                fixture.Approved.FirmId,
                origin.ParentAlgoId)).State);
        Assert.False(fixture.Ledger.HasBlockingAlgoMutationExcept(
            fixture.Approved.FirmId,
            origin.ParentAlgoId,
            fixture.MutationId));
    }

    [Fact]
    public void AlgoTerminalMutationRequiringReconciliation_RemainsBlocking()
    {
        var fixture = Fixture.Create(7102);
        var origin = new AlgoOutboundOriginIdentity(
            9003,
            AlgoOutboundActionKind.CancelChild,
            1);
        fixture.Ledger.Apply(fixture.Approved with
        {
            Origin = OutboundMutationOrigin.Algo,
            AlgoOriginIdentity = origin,
        });
        fixture.Ledger.Apply(fixture.Intent);
        fixture.Ledger.Apply(fixture.Frame);
        fixture.Ledger.Apply(fixture.Write);

        var snapshot = Assert.Single(fixture.Ledger.CaptureSnapshot().Mutations);
        var restored = new OutboundMutationLedger(fixture.Protector);
        restored.Restore(
            [
                   snapshot with
                   {
                       State = OutboundMutationState.VenueAcknowledged,
                       Attempts =
                       [
                           snapshot.Attempts[0] with
                           {
                               AmbiguityReason =
                                   OutboundAmbiguityReason.ConflictingVenueEvidence,
                           },
                       ],
                   },
            ],
            Array.Empty<OutboundCorrelationTombstone>(),
            Array.Empty<InboundVenueEvidenceSnapshot>());

        Assert.True(restored.HasBlockingAlgoMutation(
            fixture.Approved.FirmId,
            origin.ParentAlgoId));
    }

    private sealed class SimulatedCrashException : Exception;

    private sealed class Fixture
    {
        public required AeadOutboundCommandProtector Protector { get; init; }
        public required OutboundMutationLedger Ledger { get; init; }
        public required OutboundMutationId MutationId { get; init; }
        public required OutboundAttemptId AttemptId { get; init; }
        public required ulong ClOrdId { get; init; }
        public required OutboundApprovedEvent Approved { get; init; }
        public required OutboundAttemptIntentPreparedEvent Intent { get; init; }
        public required OutboundFramePreparedEvent Frame { get; init; }
        public required OutboundTransportWriteCompletedEvent Write { get; init; }
        public required OutboundProvenUnsentEvent Unsent { get; init; }

        public static Fixture Create(ulong clOrdId = 1)
        {
            var protector = OutboundMutationLedgerTests.Protector(
                ("key-a", 1, Key(1)), active: ("key-a", 1));
            var mutationId = new OutboundMutationId(Guid.Parse(
                $"{clOrdId:x8}-1111-2222-3333-444444444444"));
            var attemptId = new OutboundAttemptId(Guid.Parse(
                $"{clOrdId:x8}-aaaa-bbbb-cccc-dddddddddddd"));
            var command = new OutboundCanonicalCommand
            {
                ClOrdId = clOrdId,
                SecurityId = 123,
                Symbol = "PETR4",
                Side = "Buy",
                OrderType = "Limit",
                Quantity = 10,
                Price = 30m,
            };
            var approval = OutboundApprovalFactory.Create(
                mutationId,
                "F1",
                command,
                Sensitive(),
                [
                    OutboundSensitiveFieldRef.Account,
                    OutboundSensitiveFieldRef.InvestorId,
                    OutboundSensitiveFieldRef.EndClientId,
                    OutboundSensitiveFieldRef.CustomerIdentifier,
                    OutboundSensitiveFieldRef.TradingSubAccount,
                ],
                protector,
                T0.AddSeconds(1),
                riskDecisionRef: "risk-17",
                marginReservationRef: "margin-17");
            var approved = new OutboundApprovedEvent
            {
                MutationId = mutationId,
                MutationKind = OutboundMutationKind.New,
                FirmId = "F1",
                EndClientRef = protector.CreateStableEndClientRef(
                    "F1", Sensitive().EndClientId),
                Origin = OutboundMutationOrigin.Rest,
                PrimaryClOrdId = clOrdId,
                RecordedAtUtc = T0,
                Approval = approval,
                TimestampUtc = T0.AddSeconds(1),
            };
            return new Fixture
            {
                Protector = protector,
                Ledger = new OutboundMutationLedger(protector),
                MutationId = mutationId,
                AttemptId = attemptId,
                ClOrdId = clOrdId,
                Approved = approved,
                Intent = new OutboundAttemptIntentPreparedEvent
                {
                    MutationId = mutationId,
                    AttemptId = attemptId,
                    AttemptNo = 1,
                    ClOrdId = clOrdId,
                    ProcessEpochId = new ProcessEpochId(Guid.Parse(
                        "dddddddd-dddd-dddd-dddd-dddddddddddd")),
                    IntentPreparedAtUtc = T0.AddSeconds(2),
                    TimestampUtc = T0.AddSeconds(2),
                },
                Frame = new OutboundFramePreparedEvent
                {
                    MutationId = mutationId,
                    AttemptId = attemptId,
                    FirmId = "F1",
                    SessionId = 11,
                    SessionVerId = 2,
                    OutboundSeqNum = 77,
                    EncodedFrameSha256 = new string('f', 64),
                    PreparedAtUtc = T0.AddSeconds(3),
                    TimestampUtc = T0.AddSeconds(3),
                },
                Write = new OutboundTransportWriteCompletedEvent
                {
                    MutationId = mutationId,
                    AttemptId = attemptId,
                    CompletedAtUtc = T0.AddSeconds(4),
                    GatewayReceiptVersion = 1,
                    TimestampUtc = T0.AddSeconds(4),
                },
                Unsent = new OutboundProvenUnsentEvent
                {
                    MutationId = mutationId,
                    AttemptId = attemptId,
                    Evidence = OutboundProvenUnsentEvidence.TypedPreFrameFailure,
                    TimestampUtc = T0.AddSeconds(5),
                },
            };
        }
    }
}
