using System.Reflection;
using B3.Trading.Application.Tests.Persistence;
using B3.Trading.Infrastructure.Persistence;

namespace B3.Trading.Application.Tests.Outbound;

public sealed class OutboundCrashMatrixReleaseGateTests
{
    [Fact] public Task C01_BeforeIntentAdmission_IsGated() => ExecuteMappedAsync(1);
    [Fact] public Task C02_UncommittedIntent_IsGated() => ExecuteMappedAsync(2);
    [Fact] public Task C03_CommittedIntentBeforeRisk_IsGated() => ExecuteMappedAsync(3);
    [Fact] public Task C04_RiskRejectBeforeCommit_IsGated() => ExecuteMappedAsync(4);
    [Fact] public Task C05_UncommittedApproval_IsGated() => ExecuteMappedAsync(5);
    [Fact] public Task C06_CommittedApprovalBeforeAttempt_IsGated() => ExecuteMappedAsync(6);
    [Fact] public Task C07_UncommittedAttemptIntent_IsGated() => ExecuteMappedAsync(7);
    [Fact] public Task C08_IntentOnlyDeadEpoch_IsGated() => ExecuteMappedAsync(8);
    [Fact] public Task C09_UncommittedFrameCallback_IsGated() => ExecuteMappedAsync(9);
    [Fact] public Task C10_CommittedFrameBeforeWrite_IsGated() => ExecuteMappedAsync(10);
    [Fact] public Task C11_TypedPreFrameFailure_IsGated() => ExecuteMappedAsync(11);
    [Fact] public Task C12_PostFrameWriteFailure_IsGated() => ExecuteMappedAsync(12);
    [Fact] public Task C13_WriteBeforeCompletionEvent_IsGated() => ExecuteMappedAsync(13);
    [Fact] public Task C14_UncommittedWriteCompletion_IsGated() => ExecuteMappedAsync(14);
    [Fact] public Task C15_CommittedWriteBeforeEr_IsGated() => ExecuteMappedAsync(15);
    [Fact] public Task C16_ErReceivedBeforeAdmission_IsGated() => ExecuteMappedAsync(16);
    [Fact] public Task C17_UncommittedErApply_IsGated() => ExecuteMappedAsync(17);
    [Fact] public Task C18_CommittedErBeforeApply_IsGated() => ExecuteMappedAsync(18);
    [Fact] public Task C19_ExactBusinessReject_IsGated() => ExecuteMappedAsync(19);
    [Fact] public Task C20_UnmatchedBusinessReject_IsGated() => ExecuteMappedAsync(20);
    [Fact] public Task C21_ExactNotApplied_IsGated() => ExecuteMappedAsync(21);
    [Fact] public Task C22_MultipleRowsRecoverIndependently_IsGated() => ExecuteMappedAsync(22);
    [Fact] public Task C23_RequiredWalFaultIsSticky_IsGated() => ExecuteMappedAsync(23);
    [Fact] public Task C24_SnapshotAheadOfMarker_IsGated() => ExecuteMappedAsync(24);
    [Fact] public Task C25_ExclusiveHostFenceUnavailable_IsGated() => ExecuteMappedAsync(25);

    [Fact]
    public void CrashMatrix_ContainsEveryRowExactlyOnce()
    {
        Assert.Equal(25, Mappings.Count);
        Assert.Equal(Enumerable.Range(1, 25), Mappings.Keys.Order());
    }

    private static async Task ExecuteMappedAsync(int row)
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

        object? instance = null;
        try
        {
            instance = method.IsStatic
                ? null
                : Activator.CreateInstance(mapping.Type);
            var result = method.Invoke(instance, mapping.Arguments.ToArray());
            if (result is Task task)
                await task.ConfigureAwait(false);
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(exception.InnerException)
                .Throw();
        }
        finally
        {
            if (instance is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (instance is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private static readonly IReadOnlyDictionary<int, Mapping> Mappings =
        new Dictionary<int, Mapping>
        {
            [1] = Map<DurableOrderSubmissionServiceTests>(
                nameof(DurableOrderSubmissionServiceTests.C01_CrashBeforeRecordedIntentAdmission_RetryReusesUncommittedClOrdId)),
            [2] = Map<CommittedPrefixFileEventStoreTests>(
                "CrashBeforeMarkerPublication_DoesNotReplaySurvivor",
                WalCommitBoundary.RecordAppended),
            [3] = Map<DurableOrderSubmissionServiceTests>(
                nameof(DurableOrderSubmissionServiceTests.C03_CrashAfterIntentCommitBeforeRisk_RestartsFailClosedWithoutPolicyVersion)),
            [4] = Map<DurableOrderSubmissionServiceTests>(
                nameof(DurableOrderSubmissionServiceTests.C04_CrashAfterRiskRejectBeforeCommit_ReevaluatesAsUnknownNotPriorReject)),
            [5] = Map<DurableOrderSubmissionServiceTests>(
                nameof(DurableOrderSubmissionServiceTests.C05_ApprovalAppendedButNotCommitted_RestartsPendingApprovalWithoutGatewayCall)),
            [6] = Map<NewOrderOutboundCoordinatorTests>(
                nameof(NewOrderOutboundCoordinatorTests.RecoveryStart_EntersApprovedMutationExactlyOnce)),
            [7] = Map<NewOrderOutboundCoordinatorTests>(
                nameof(NewOrderOutboundCoordinatorTests.C07_AttemptIntentAppendedButNotCommitted_RestartsApprovedWithoutGatewayEntry)),
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
                nameof(NewOrderOutboundCoordinatorTests.C13_WriteReturnsSuccessBeforeCompletionAdmission_RestartsAmbiguousFromFrame)),
            [14] = Map<CommittedPrefixFileEventStoreTests>(
                nameof(CommittedPrefixFileEventStoreTests.C14_WriteCompletionAppendedButNotCommitted_RestartsAmbiguousFromCommittedFrame)),
            [15] = Map<OutboundMutationLedgerTests>(
                nameof(OutboundMutationLedgerTests.Recovery_IntentOnlyIsProvenUnsent_FrameAndWriteAreAmbiguous)),
            [16] = Map<OutboundMutationLedgerTests>(
                nameof(OutboundMutationLedgerTests.C16_ExecutionReportReceivedButNotAdmitted_DrainsUntilRetransmission)),
            [17] = Map<CommittedPrefixFileEventStoreTests>(
                nameof(CommittedPrefixFileEventStoreTests.C17_ExecutionReportAppendedButNotCommitted_IsDiscardedAndRetransmitted)),
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

    private static Mapping Map<T>(string method, params object[] arguments) =>
        new(typeof(T), method, arguments);

    private sealed record Mapping(
        Type Type,
        string Method,
        IReadOnlyList<object> Arguments)
    {
        public string TestId => $"{Type.FullName}.{Method}";
    }
}
