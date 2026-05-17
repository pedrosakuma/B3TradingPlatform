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
        return Task.CompletedTask;
    }

    public Task SubmitCancelReplaceAsync(OrderCancelReplaceRequest request, CancellationToken cancellationToken)
    {
        _replaces.Enqueue(request);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Test/host hook to push an ER through the exchange-side event.
    /// </summary>
    public void EmitExecutionReport(ExecutionReportEnvelope er) =>
        ExecutionReportReceived?.Invoke(er);
}
