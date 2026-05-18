using System.Threading.Channels;
using B3.Trading.Application;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Api.WebSockets.DropCopy;

/// <summary>
/// Q4.6 (#306). Channel-backed drop-copy fan-out sink. Same shape as
/// <see cref="WebSocketExecutionEventSink"/>: both the dispatcher
/// <see cref="IExecutionFanOutSink.Enqueue"/> path (called UNDER the
/// dispatcher lock — must be non-blocking) and the synthetic
/// <see cref="IExecutionEventSink.Publish"/> path
/// (<c>OrderStalenessService</c> / WAL-backpressure fallback) funnel
/// into the same bounded <see cref="Channel{T}"/>. A single background
/// drain runs the firm-scoped subscriber walk + DTO build OFF the
/// dispatcher lock while preserving WAL-append order for events that
/// arrived via the dispatcher (RFC §4.1 / §5.2 ordering note).
///
/// <para><b>Per-sink overflow policy</b> is identical to the per-user
/// WS sink (bounded + DropOldest + metric). A drop indicates the
/// drain is stuck; drop-copy subscribers detect the gap via the same
/// reconnect-and-snapshot pattern as <c>orders.me</c> consumers.</para>
///
/// <para><b>Channel routing.</b> For every captured
/// <see cref="ExecutionEvent"/> the drain emits:
/// <list type="bullet">
///   <item><c>dropcopy.orders</c> — latest order DTO (when the order
///   is still in the book; venue events that pre-date the in-memory
///   order are silently skipped, same as <c>orders.me</c>).</item>
///   <item><c>dropcopy.fills</c> — <see cref="ExecutionDto"/> for
///   <see cref="ExecKind.Fill"/> / <see cref="ExecKind.PartialFill"/>
///   with <c>LastQuantity &gt; 0</c>.</item>
///   <item><c>dropcopy.cancels</c> — <see cref="ExecutionDto"/> for
///   <see cref="ExecKind.Canceled"/> (venue-cancelled or GTD-expired
///   — replaces and rejects show up only on the orders channel, as
///   order-state transitions, matching FIX drop-copy convention).
///   </item>
/// </list></para>
/// </summary>
public sealed class DropCopyExecutionEventSink : IExecutionFanOutSink, IExecutionEventSink, IHostedService, IAsyncDisposable
{
    /// <summary>Drain bound — same order of magnitude as <see cref="WebSocketExecutionEventSink.ChannelCapacity"/>.</summary>
    public const int ChannelCapacity = 65_536;

    private readonly DropCopyManager _manager;
    private readonly WorkingOrderBook _orders;
    private readonly ILogger<DropCopyExecutionEventSink>? _logger;
    private readonly Channel<ExecutionEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _drainTask;
    private int _stopped;

    public DropCopyExecutionEventSink(
        DropCopyManager manager,
        WorkingOrderBook orders,
        ILogger<DropCopyExecutionEventSink>? logger = null)
    {
        _manager = manager;
        _orders = orders;
        _logger = logger;
        _channel = Channel.CreateBounded<ExecutionEvent>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            },
            itemDropped: _ =>
            {
                // Pass-3 review (#323) P1: DropOldest hides the loss
                // from drop-copy subscribers because per-client seqs
                // are assigned downstream (after the sink) and snapshots
                // for fills/cancels are empty by design — a silent
                // discard would be unrecoverable. Fail-closed: mark
                // every active subscriber for resync. Clients reconnect
                // and pick up a fresh snapshot from a known state.
                MetricsRegistry.WsHubFanOutDropped.Add(1);
                try { _manager.DisconnectAllForResync("drop_copy_sink_overflow_resync_required"); }
                catch (Exception ex) { _logger?.LogWarning(ex, "drop-copy resync-disconnect on sink overflow failed"); }
            });
    }

    /// <inheritdoc />
    public ExecutionFanOutTargets Target => ExecutionFanOutTargets.DropCopy;

    /// <inheritdoc />
    public void Enqueue(long seq, ExecutionEvent ev) => _channel.Writer.TryWrite(ev);

    /// <inheritdoc />
    public void Publish(ExecutionEvent ev) => _channel.Writer.TryWrite(ev);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _drainTask = Task.Run(DrainAsync);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _cts.Cancel();
        _channel.Writer.TryComplete();
        if (_drainTask is not null)
        {
            try { await _drainTask.ConfigureAwait(false); } catch { /* drain stop is best-effort */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task DrainAsync()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var ev))
                {
                    try { PublishCore(ev); }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex,
                            "drop-copy fan-out publish failed for firmId={Firm} clOrdId={ClOrdId}",
                            ev.FirmId, ev.ClOrdId);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private void PublishCore(ExecutionEvent ev)
    {
        // No firm => no fan-out (legacy / pre-multi-firm events default-cased through tests).
        var firmId = ev.FirmId;
        if (string.IsNullOrEmpty(firmId)) return;

        // No out-of-lock SubscriberCount fast-path here: the per-firm
        // empty-set check happens INSIDE DropCopyManager.Publish (under
        // the per-firm lock). Reading _byFirm here without the lock
        // would re-introduce the Publish-vs-Add race (Q4.6 RFC §4.3):
        // a concurrent Add() could register a subscriber whose snapshot
        // has been enqueued but whose first live delta would then be
        // dropped because the unlocked fast-path saw zero subscribers.

        // orders.* — current order state after mutation (skip if the
        // order is no longer in the book, same fall-through as orders.me).
        if (_orders.TryGet(ev.ClOrdId, out var order) && order is not null)
            _manager.Publish(firmId, DropCopyManager.DropCopyChannels.Orders, order.ToDto());

        // fills.* — only economic fills.
        if (ev.Kind is ExecKind.Fill or ExecKind.PartialFill && ev.LastQuantity > 0)
            _manager.Publish(firmId, DropCopyManager.DropCopyChannels.Fills, ev.ToDto());

        // cancels.* — venue cancels (incl. GTD-expired). Replaces/rejects
        // are visible via the orders channel as state transitions.
        if (ev.Kind == ExecKind.Canceled)
            _manager.Publish(firmId, DropCopyManager.DropCopyChannels.Cancels, ev.ToDto());
    }
}
