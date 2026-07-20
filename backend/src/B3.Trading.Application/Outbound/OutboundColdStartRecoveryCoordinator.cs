using B3.Trading.Application.Persistence;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Application.Outbound;

public sealed record OutboundColdStartRecoveryResult(
    int ApprovedWithoutAttempt,
    int ProvenUnsent,
    int Ambiguous,
    int ReadinessBlocking);

public sealed class OutboundColdStartRecoveryCoordinator
{
    private readonly OutboundMutationLedger _ledger;
    private readonly OutboundProcessEpoch _epoch;
    private readonly EventDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboundColdStartRecoveryCoordinator> _logger;

    public OutboundColdStartRecoveryCoordinator(
        OutboundMutationLedger ledger,
        OutboundProcessEpoch epoch,
        EventDispatcher dispatcher,
        ILogger<OutboundColdStartRecoveryCoordinator> logger,
        TimeProvider? clock = null)
    {
        _ledger = ledger;
        _epoch = epoch;
        _dispatcher = dispatcher;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public Task<OutboundColdStartRecoveryResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var approved = _ledger.SnapshotMutations().Count(static mutation =>
            mutation.State == OutboundMutationState.ApprovedToSend
            && mutation.Attempts.Count == 0);
        var classifications = _ledger.PlanRecoveredAttempts(_epoch.Id);
        var provenUnsent = 0;
        var ambiguous = 0;

        foreach (var classification in classifications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (classification.Disposition == RecoveredOutboundAttemptDisposition.ProvenUnsent)
            {
                var evt = new OutboundProvenUnsentEvent
                {
                    MutationId = classification.MutationId,
                    AttemptId = classification.AttemptId,
                    Evidence = classification.ProvenUnsentEvidence
                        ?? throw new InvalidOperationException("Recovered proven-unsent classification lacks evidence."),
                    TimestampUtc = _clock.GetUtcNow(),
                };
                _dispatcher.DispatchCommitted(
                    evt,
                    () => _ledger.Apply(evt),
                    cancellationToken);
                provenUnsent++;
                continue;
            }

            _ledger.MarkAmbiguous(
                classification.MutationId,
                classification.AttemptId,
                classification.AmbiguityReason
                    ?? throw new InvalidOperationException("Recovered ambiguous classification lacks a reason."),
                _clock.GetUtcNow());
            ambiguous++;
        }

        var blocking = _ledger.ReadinessBlockingCount;
        _logger.LogInformation(
            "Outbound cold-start recovery classified approvedWithoutAttempt={Approved}, provenUnsent={ProvenUnsent}, ambiguous={Ambiguous}, readinessBlocking={Blocking}.",
            approved,
            provenUnsent,
            ambiguous,
            blocking);
        return Task.FromResult(new OutboundColdStartRecoveryResult(
            approved,
            provenUnsent,
            ambiguous,
            blocking));
    }
}
