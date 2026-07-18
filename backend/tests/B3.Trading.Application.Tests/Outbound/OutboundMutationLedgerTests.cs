using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using B3.Trading.Application.Lifecycle;
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
        Assert.Equal(0, intentOnly.Ledger.ReadinessBlockingCount);
        Assert.Equal(1, frame.Ledger.ReadinessBlockingCount);
        Assert.Equal(1, write.Ledger.ReadinessBlockingCount);
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
            OriginalClOrdId = 1,
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
            OriginalClOrdId: 1,
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
        Assert.Equal(OutboundMutationState.VenueAcknowledged, mutation.State);
        Assert.True(Assert.Single(ledger.SnapshotCorrelations()).Terminal);
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
