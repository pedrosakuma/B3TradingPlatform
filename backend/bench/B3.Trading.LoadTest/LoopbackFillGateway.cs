using B3.Trading.Application;
using B3.Trading.Domain;

namespace B3.Trading.LoadTest;

/// <summary>
/// Synthetic <see cref="IExchangeGateway"/> that closes the loop: every
/// accepted submit triggers an immediate <see cref="ExecKind.Fill"/>
/// execution-report fed back through the real
/// <see cref="ExecutionReportProcessor"/> via the real
/// <see cref="Application.Persistence.EventDispatcher"/>. This makes the
/// platform exercise the **complete** REST → WAL → ER → publish pipeline
/// the RFC §7.2 harness is supposed to time, without spinning up an
/// EntryPoint matching simulator process.
///
/// <para>
/// The ER is dispatched from a thread-pool task (matching the
/// production gateway, which receives ERs from the EntryPoint client's
/// background reader) so the producer's submit call completes before
/// the matching ER is observed at the sink. Latency captured at
/// <see cref="LatencyCapturingSink"/> therefore reflects the real
/// dispatcher contention path.
/// </para>
/// </summary>
public sealed class LoopbackFillGateway : IExchangeGateway
{
    // Resolved post-construction to break the DI cycle (the processor
    // depends on the sink which depends on this gateway's wiring).
    private ExecutionReportProcessor? _processor;
    private Application.Persistence.EventDispatcher? _dispatcher;

    public long ErsApplied;
    public long ErDispatchFailures;

    public void Bind(ExecutionReportProcessor processor, Application.Persistence.EventDispatcher dispatcher)
    {
        _processor = processor;
        _dispatcher = dispatcher;
    }

    public Task SubmitAsync(Order order, CancellationToken cancellationToken)
    {
        // Schedule the ER asynchronously so the submit call returns before
        // the matching publish — same shape as the production EntryPoint
        // gateway, where ERs arrive on the client's reader thread.
        _ = Task.Run(() => DispatchFill(order), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CancelReplaceAsync(
        Order original, ulong newClOrdId, long newQuantity, decimal? newPrice,
        TimeInForce? requestedTimeInForce, decimal? requestedStopPrice, DateTimeOffset? requestedGoodTillDate,
        CancellationToken cancellationToken) => Task.CompletedTask;

    private void DispatchFill(Order order)
    {
        var processor = _processor;
        var dispatcher = _dispatcher;
        if (processor is null || dispatcher is null) return;

        try
        {
            var qty = order.Quantity;
            var px = order.Price ?? 1m;
            dispatcher.Dispatch(
                new Application.Persistence.ExecutionReportReceivedEvent
                {
                    ClOrdId = order.ClOrdId,
                    ExecKind = ExecKind.Fill.ToString(),
                    LeavesQuantity = 0,
                    CumulativeQuantity = qty,
                    LastQuantity = qty,
                    LastPrice = px,
                    Synthetic = false,
                    OrigClOrdId = 0,
                },
                () => processor.Apply(order.ClOrdId, ExecKind.Fill,
                    leaves: 0, cumQty: qty, lastQty: qty, lastPx: px,
                    rejectReason: null, origClOrdId: 0));
            Interlocked.Increment(ref ErsApplied);
        }
        catch (Exception)
        {
            // WAL backpressure or transient — record and move on; the
            // load-test report surfaces the count as a "lost ERs"
            // diagnostic so the operator can dial the rate down.
            Interlocked.Increment(ref ErDispatchFailures);
        }
    }
}
