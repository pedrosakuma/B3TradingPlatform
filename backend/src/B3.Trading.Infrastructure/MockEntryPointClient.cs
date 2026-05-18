using System.Collections.Concurrent;

namespace B3.Trading.Infrastructure;

/// <summary>
/// In-memory mock of <see cref="IEntryPointClient"/>. Records every outbound
/// request and lets tests (and the Host, until the real client lib is
/// wired) drive ExecutionReports manually via <see cref="EmitExecutionReport"/>.
/// </summary>
public sealed class MockEntryPointClient : IEntryPointClient
{
    private readonly ConcurrentQueue<NewOrderSingle> _newOrders = new();
    private readonly ConcurrentQueue<OrderCancelRequest> _cancels = new();
    private readonly ConcurrentQueue<OrderCancelReplaceRequest> _replaces = new();

    public IReadOnlyCollection<NewOrderSingle> SubmittedNewOrders => _newOrders;
    public IReadOnlyCollection<OrderCancelRequest> SubmittedCancels => _cancels;
    public IReadOnlyCollection<OrderCancelReplaceRequest> SubmittedReplaces => _replaces;

    /// <summary>
    /// Optional test hook: when non-null, every
    /// <see cref="SubmitCancelAsync"/> invokes it after recording the
    /// request and throws the returned exception (or completes
    /// normally if it returns null). Used by repeg-cancel-failure
    /// regression tests; left null in production composition.
    /// </summary>
    public Func<OrderCancelRequest, Exception?>? CancelFailureInjector { get; set; }

    /// <summary>
    /// Pass-5 review (#296) P2. Optional test hook: when non-null,
    /// every <see cref="SubmitCancelAsync"/> awaits the
    /// <see cref="Task"/> returned by the injector AFTER recording
    /// the request but BEFORE returning to the caller. Used by the
    /// Pegged fill-races-cancel regression tests to deterministically
    /// gate the engine's <c>CancelAsync</c> await: the test injects
    /// a Fill ER while the cancel is held, then releases the gate
    /// and asserts the post-condition the in-engine guard guarantees
    /// (no orphan replacement child, audit pair balanced, no stuck
    /// state). Left null in production composition.
    /// </summary>
    public Func<OrderCancelRequest, Task>? CancelDelayInjector { get; set; }

    public event Action<ExecutionReportEnvelope>? ExecutionReportReceived;

    public Task SubmitNewOrderAsync(NewOrderSingle request, CancellationToken cancellationToken)
    {
        _newOrders.Enqueue(request);
        return Task.CompletedTask;
    }

    public Task SubmitCancelAsync(OrderCancelRequest request, CancellationToken cancellationToken)
    {
        _cancels.Enqueue(request);
        var injector = CancelFailureInjector;
        if (injector is not null)
        {
            var ex = injector(request);
            if (ex is not null) return Task.FromException(ex);
        }
        var delay = CancelDelayInjector;
        if (delay is not null)
        {
            // Defer to the test-supplied gate; surface the gate's
            // task to the engine's await so the caller pauses inside
            // CancelAsync until the test releases the TCS.
            return delay(request);
        }
        return Task.CompletedTask;
    }

    public Task SubmitCancelReplaceAsync(OrderCancelReplaceRequest request, CancellationToken cancellationToken)
    {
        _replaces.Enqueue(request);
        var injector = ReplaceFailureInjector;
        if (injector is not null)
        {
            var ex = injector(request);
            if (ex is not null) return Task.FromException(ex);
        }
        var delay = ReplaceDelayInjector;
        if (delay is not null)
        {
            return delay(request);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pass-1 review (#299) P1-B. Optional test hook: when non-null,
    /// every <see cref="SubmitCancelReplaceAsync"/> invokes it after
    /// recording the request and throws the returned exception (or
    /// completes normally if it returns null). Mirrors
    /// <see cref="CancelFailureInjector"/>; used by the algo-modify
    /// send-failure regression test to simulate an ambiguous send
    /// (the request was recorded — and may have been accepted by the
    /// venue — but the SDK reports failure to the caller).
    /// </summary>
    public Func<OrderCancelReplaceRequest, Exception?>? ReplaceFailureInjector { get; set; }

    /// <summary>
    /// #300 retrofit. Mirror of <see cref="CancelDelayInjector"/> for
    /// the cancel-replace path: when non-null, the gateway awaits the
    /// supplied <see cref="Task"/> AFTER enqueueing the request and
    /// AFTER the failure-injector check, BEFORE returning to the
    /// caller. Used by the Pegged fill-races-replace regression test
    /// to deterministically gate the engine's
    /// <c>CancelReplaceAsync</c> await: the test injects a Fill ER on
    /// the OLD child while the replace is held, then releases the
    /// gate and asserts the post-condition (no orphan replacement
    /// child, audit pair balanced, replacement intent + margin
    /// reservation released). Left null in production composition.
    /// </summary>
    public Func<OrderCancelReplaceRequest, Task>? ReplaceDelayInjector { get; set; }

    /// <summary>
    /// Test/host hook to push an ER through the exchange-side event.
    /// </summary>
    public void EmitExecutionReport(ExecutionReportEnvelope er) =>
        ExecutionReportReceived?.Invoke(er);
}
