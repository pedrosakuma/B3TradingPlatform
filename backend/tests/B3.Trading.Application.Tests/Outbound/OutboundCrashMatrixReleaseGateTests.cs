using System.Reflection;
using B3.Trading.Application.Tests.Persistence;

namespace B3.Trading.Application.Tests.Outbound;

public sealed class OutboundCrashMatrixReleaseGateTests
{
    [Fact] public void C01_BeforeIntentAdmission_IsGated() => AssertMapped(1);
    [Fact] public void C02_UncommittedIntent_IsGated() => AssertMapped(2);
    [Fact] public void C03_CommittedIntentBeforeRisk_IsGated() => AssertMapped(3);
    [Fact] public void C04_RiskRejectBeforeCommit_IsGated() => AssertMapped(4);
    [Fact] public void C05_UncommittedApproval_IsGated() => AssertMapped(5);
    [Fact] public void C06_CommittedApprovalBeforeAttempt_IsGated() => AssertMapped(6);
    [Fact] public void C07_UncommittedAttemptIntent_IsGated() => AssertMapped(7);
    [Fact] public void C08_IntentOnlyDeadEpoch_IsGated() => AssertMapped(8);
    [Fact] public void C09_UncommittedFrameCallback_IsGated() => AssertMapped(9);
    [Fact] public void C10_CommittedFrameBeforeWrite_IsGated() => AssertMapped(10);
    [Fact] public void C11_TypedPreFrameFailure_IsGated() => AssertMapped(11);
    [Fact] public void C12_PostFrameWriteFailure_IsGated() => AssertMapped(12);
    [Fact] public void C13_WriteBeforeCompletionEvent_IsGated() => AssertMapped(13);
    [Fact] public void C14_UncommittedWriteCompletion_IsGated() => AssertMapped(14);
    [Fact] public void C15_CommittedWriteBeforeEr_IsGated() => AssertMapped(15);
    [Fact] public void C16_ErReceivedBeforeAdmission_IsGated() => AssertMapped(16);
    [Fact] public void C17_UncommittedErApply_IsGated() => AssertMapped(17);
    [Fact] public void C18_CommittedErBeforeApply_IsGated() => AssertMapped(18);
    [Fact] public void C19_ExactBusinessReject_IsGated() => AssertMapped(19);
    [Fact] public void C20_UnmatchedBusinessReject_IsGated() => AssertMapped(20);
    [Fact] public void C21_ExactNotApplied_IsGated() => AssertMapped(21);
    [Fact] public void C22_MultipleRowsRecoverIndependently_IsGated() => AssertMapped(22);
    [Fact] public void C23_RequiredWalFaultIsSticky_IsGated() => AssertMapped(23);
    [Fact] public void C24_SnapshotAheadOfMarker_IsGated() => AssertMapped(24);
    [Fact] public void C25_ExclusiveHostFenceUnavailable_IsGated() => AssertMapped(25);

    [Fact]
    public void CrashMatrix_ContainsEveryRowExactlyOnce()
    {
        Assert.Equal(25, Mappings.Count);
        Assert.Equal(Enumerable.Range(1, 25), Mappings.Keys.Order());
    }

    private static void AssertMapped(int row)
    {
        var mapping = Mappings[row];
        var method = mapping.Type.GetMethod(
            mapping.Method,
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var testAttribute = method!.GetCustomAttributes()
            .OfType<FactAttribute>()
            .SingleOrDefault();
        Assert.True(
            testAttribute is not null,
            $"{mapping.TestId} must remain an executable xUnit test.");
        Assert.Null(testAttribute!.Skip);
    }

    private static readonly IReadOnlyDictionary<int, Mapping> Mappings =
        new Dictionary<int, Mapping>
        {
            [1] = Map<RestOrderIdempotencyStoreTests>(
                nameof(RestOrderIdempotencyStoreTests.SnapshotRestore_PreservesReplayAndConflictSemantics)),
            [2] = Map<CommittedPrefixFileEventStoreTests>(
                "CrashBeforeMarkerPublication_DoesNotReplaySurvivor"),
            [3] = Map<DurableOrderSubmissionServiceTests>(
                nameof(DurableOrderSubmissionServiceTests.ApprovedSubmit_CommitsPendingApprovalIntentFrameAndWriteInOrder)),
            [4] = Map<DurableOrderSubmissionServiceTests>(
                nameof(DurableOrderSubmissionServiceTests.RiskReject_IsDurableBeforeApprovalAndNeverEntersGateway)),
            [5] = Map<DurableOrderSubmissionServiceTests>(
                nameof(DurableOrderSubmissionServiceTests.ApprovalAppendFailure_TerminalisesNoWriteBeforeMarginRelease)),
            [6] = Map<NewOrderOutboundCoordinatorTests>(
                nameof(NewOrderOutboundCoordinatorTests.RecoveryStart_EntersApprovedMutationExactlyOnce)),
            [7] = Map<CommittedPrefixFileEventStoreTests>(
                "CrashBeforeMarkerPublication_DoesNotReplaySurvivor"),
            [8] = Map<OutboundMutationLedgerTests>(
                nameof(OutboundMutationLedgerTests.ColdStartCoordinator_CommitsIntentOnlyProvenUnsent_AndDoesNotResendFramePrepared)),
            [9] = Map<NewOrderOutboundCoordinatorTests>(
                nameof(NewOrderOutboundCoordinatorTests.FramePersistenceFailure_PreventsWriteAndRequiresReconciliation)),
            [10] = Map<OutboundMutationLedgerTests>(
                nameof(OutboundMutationLedgerTests.ColdStartCoordinator_CommitsIntentOnlyProvenUnsent_AndDoesNotResendFramePrepared)),
            [11] = Map<NewOrderOutboundCoordinatorTests>(
                nameof(NewOrderOutboundCoordinatorTests.TypedPreFrameFailure_IsProvenUnsentAndRetainsMarginUntilDomainTerminalCommit)),
            [12] = Map<NewOrderOutboundCoordinatorTests>(
                nameof(NewOrderOutboundCoordinatorTests.ExceptionAfterFrame_IsAmbiguousAndDoesNotReleaseMargin)),
            [13] = Map<NewOrderOutboundCoordinatorTests>(
                nameof(NewOrderOutboundCoordinatorTests.ExceptionAfterFrame_IsAmbiguousAndDoesNotReleaseMargin)),
            [14] = Map<CommittedPrefixFileEventStoreTests>(
                "CrashBeforeMarkerPublication_DoesNotReplaySurvivor"),
            [15] = Map<OutboundMutationLedgerTests>(
                nameof(OutboundMutationLedgerTests.Recovery_IntentOnlyIsProvenUnsent_FrameAndWriteAreAmbiguous)),
            [16] = Map<OutboundMutationLedgerTests>(
                nameof(OutboundMutationLedgerTests.CommitBeforeApply_CrashWindowReplaysEvidenceDeterministically)),
            [17] = Map<OutboundMutationLedgerTests>(
                nameof(OutboundMutationLedgerTests.CommitBeforeApply_CrashWindowReplaysEvidenceDeterministically)),
            [18] = Map<OutboundMutationLedgerTests>(
                nameof(OutboundMutationLedgerTests.CommitBeforeApply_CrashWindowReplaysEvidenceDeterministically)),
            [19] = Map<OutboundMutationLedgerTests>(
                nameof(OutboundMutationLedgerTests.BusinessReject_CorrelatesOnlyExactFirmSessionVersionAndSequence)),
            [20] = Map<OutboundMutationLedgerTests>(
                nameof(OutboundMutationLedgerTests.BusinessReject_MissingIdentityRemainsUnmatchedAndDoesNotUseText)),
            [21] = Map<OutboundMutationLedgerTests>(
                nameof(OutboundMutationLedgerTests.NotApplied_UsesOverflowSafeHalfOpenRange_AndNeverAutoResends)),
            [22] = Map<OutboundMutationLedgerTests>(
                nameof(OutboundMutationLedgerTests.RecoveryGate_BlocksOnlyFirmsCapturedDuringColdClassification)),
            [23] = Map<CommittedPrefixFileEventStoreTests>(
                nameof(CommittedPrefixFileEventStoreTests.MarkerFault_IsStickyAndFailsEveryOutstandingFence)),
            [24] = Map<SnapshotCommittedPrefixTests>(
                nameof(SnapshotCommittedPrefixTests.Recovery_IgnoresOnDiskSnapshotAheadOfCommittedMarker)),
            [25] = Map<ActiveHostFenceTests>(
                nameof(ActiveHostFenceTests.SecondHostLoses_AndNextExclusiveAcquisitionAdvancesDurableEpoch)),
        };

    private static Mapping Map<T>(string method) => new(typeof(T), method);

    private sealed record Mapping(Type Type, string Method)
    {
        public string TestId => $"{Type.FullName}.{Method}";
    }
}
