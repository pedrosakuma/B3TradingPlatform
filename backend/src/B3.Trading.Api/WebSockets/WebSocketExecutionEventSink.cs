using System.Threading.Channels;
using B3.Trading.Application;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Api.WebSockets;

/// <summary>
/// Real <see cref="IExecutionEventSink"/> backed by the WebSocket
/// <see cref="SubscriptionManager"/>. Routes a single
/// <see cref="ExecutionEvent"/> to all impacted channels for the owner.
///
/// <para>
/// RFC §5.2 (F2). The sink is channel-backed: both the
/// <see cref="EventDispatcher"/> fan-out path
/// (<see cref="IExecutionFanOutSink.Enqueue"/>, written UNDER the
/// dispatcher lock) and the synthetic out-of-WAL publishes
/// (<see cref="IExecutionEventSink.Publish"/>, used by
/// <c>OrderStalenessService</c> and the <c>EntryPointExecutionReportRouter</c>
/// WAL-backpressure fallback) end up on the SAME bounded
/// <see cref="Channel{T}"/>. A single background drain consumes the
/// channel and runs the expensive subscriber-walk + DTO build OFF the
/// dispatcher lock, while preserving WAL-append order for events that
/// arrived via the dispatcher (RFC §4.1, §5.2 ordering note).
/// </para>
///
/// <para>
/// Per-sink overflow policy (RFC §6.3): bounded at
/// <see cref="ChannelCapacity"/> with <c>FullMode = DropOldest</c> and
/// an item-dropped callback that bumps
/// <see cref="MetricsRegistry.WsHubFanOutDropped"/>. A drop indicates
/// the WS publish thread cannot keep up; subscribers detect the gap
/// via existing reconnect-and-replay (the WS client sees a missed
/// frame at the connection layer or via a stale orders.me snapshot
/// and refetches state).
/// </para>
/// </summary>
public sealed class WebSocketExecutionEventSink : IExecutionEventSink, IExecutionFanOutSink, IHostedService, IAsyncDisposable
{
    /// <summary>
    /// 64 K events per RFC §5.2. At a sustained 50 K ER/s with a drain
    /// thread that publishes in tens of microseconds per event, the
    /// queue depth is zero in steady state; the cap exists only to
    /// bound memory under a stuck-drain scenario.
    /// </summary>
    public const int ChannelCapacity = 65_536;

    private readonly SubscriptionManager _subs;
    private readonly WorkingOrderBook _orders;
    private readonly PositionKeeper _positions;
    private readonly PnlKeeper? _pnl;
    private readonly Application.Risk.IReferencePrice? _refPrice;
    private readonly ILogger<WebSocketExecutionEventSink>? _logger;
    private readonly Channel<ExecutionEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _drainTask;

    public WebSocketExecutionEventSink(
        SubscriptionManager subs,
        WorkingOrderBook orders,
        PositionKeeper positions,
        PnlKeeper? pnl = null,
        Application.Risk.IReferencePrice? refPrice = null,
        ILogger<WebSocketExecutionEventSink>? logger = null)
    {
        _subs = subs;
        _orders = orders;
        _positions = positions;
        _pnl = pnl;
        _refPrice = refPrice;
        _logger = logger;
        _channel = Channel.CreateBounded<ExecutionEvent>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            },
            itemDropped: static _ => MetricsRegistry.WsHubFanOutDropped.Add(1));
    }

    /// <inheritdoc />
    public ExecutionFanOutTargets Target => ExecutionFanOutTargets.WsHub;

    /// <inheritdoc />
    public void Publish(ExecutionEvent ev) => _channel.Writer.TryWrite(ev);

    /// <inheritdoc />
    public void Enqueue(long seq, ExecutionEvent ev) => _channel.Writer.TryWrite(ev);

    private int _stopped;

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
                            "ws hub fan-out publish failed for owner={Owner} clOrdId={ClOrdId}",
                            ev.Owner.Value, ev.ClOrdId);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private void PublishCore(ExecutionEvent ev)
    {
        if (_subs.CountFor(ev.Owner) == 0)
            return;

        // executions.me — every ER becomes an execution event.
        _subs.Publish(ev.Owner, Channels.ExecutionsMe, ev.ToDto());

        // orders.me — current order state after mutation.
        if (_orders.TryGet(ev.ClOrdId, out var order) && order is not null)
            _subs.Publish(ev.Owner, Channels.OrdersMe, order.ToDto());

        // positions.me — only fills affect positions.
        if (ev.Kind is ExecKind.Fill or ExecKind.PartialFill && ev.LastQuantity > 0)
        {
            var position = _positions.GetOrCreate(ev.Owner, ev.Symbol);
            _subs.Publish(ev.Owner, Channels.PositionsMe, position.ToDto());

            // Q2.4 (#271). pnl.me — fills move both the realized
            // bucket (via PnlKeeper) and unrealized (via the new
            // avg-cost basis). Re-project the whole snapshot so
            // subscribers can swap it in atomically. Note we project
            // OFF the dispatcher path (this drain is async and
            // ordered with positions.me — guaranteeing pnl reflects
            // the same fill the position update reports).
            if (_pnl is not null && _refPrice is not null)
            {
                var pnlSnap = PnlProjection.Build(ev.Owner, _pnl, _positions, _refPrice);
                _subs.Publish(ev.Owner, Channels.PnlMe, pnlSnap);
            }
        }
    }
}
