using B3.Trading.Application.Observability;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging;
using Up = B3.EntryPoint.Client;
using UpModels = B3.EntryPoint.Client.Models;

namespace B3.Trading.Infrastructure;

/// <summary>
/// Real <see cref="IExchangeGateway"/> + <see cref="IEntryPointClient"/>
/// adapter wrapping a single upstream <c>B3.EntryPoint.Client.EntryPointClient</c>
/// (one instance per <see cref="FirmConfig"/>).
///
/// <para>
/// Outbound: translates domain commands into upstream
/// <c>NewOrderRequest/CancelOrderRequest/ReplaceOrderRequest</c>.
/// </para>
/// <para>
/// Inbound: a background <c>Task</c> consumes the upstream
/// <c>IAsyncEnumerable&lt;EntryPointEvent&gt;</c> and translates each
/// subtype into our internal <see cref="ExecutionReportEnvelope"/>,
/// raising <see cref="IEntryPointClient.ExecutionReportReceived"/>. Aggregated
/// across firms by <see cref="FirmGatewayRegistry"/>.
/// </para>
/// </summary>
public sealed class B3EntryPointClientGateway : IExchangeGateway, IEntryPointClient, IAsyncDisposable
{
    private readonly Up.EntryPointClient _client;
    private readonly string _firmId;
    private readonly ILogger<B3EntryPointClientGateway> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private readonly TimeSpan _initialReconnectDelay;
    private readonly TimeSpan _maxReconnectDelay;
    private Task? _eventLoop;
    private uint _currentSessionVerId;
    private int _connectedState; // 0 = disconnected, 1 = connected (matches UpDownCounter increments)
    private int _reconnectingState; // 0 = idle, 1 = reconnect loop active (observable gauge)
    private ulong _lastInboundSeqNum;
    private volatile bool _disposed;

    public B3EntryPointClientGateway(
        Up.EntryPointClient client,
        string firmId,
        uint initialSessionVerId,
        ILogger<B3EntryPointClientGateway> logger,
        TimeSpan? initialReconnectDelay = null,
        TimeSpan? maxReconnectDelay = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _firmId = firmId ?? throw new ArgumentNullException(nameof(firmId));
        _logger = logger;
        _currentSessionVerId = initialSessionVerId;
        _initialReconnectDelay = initialReconnectDelay ?? TimeSpan.FromSeconds(1);
        _maxReconnectDelay = maxReconnectDelay ?? TimeSpan.FromSeconds(30);
        _client.Terminated += OnTerminated;
        MetricsRegistry.RecordSessionVerId(_firmId, _currentSessionVerId);
        // Pull-based observable gauges: read SDK state + our reconnect flag on
        // every scrape rather than pushing on every transition. Avoids both
        // a stale dashboard and a dropped-update race against fast Terminated
        // → Reconnecting → Established cycles.
        MetricsRegistry.RegisterSessionStateSource(_firmId,
            () => FixpStateGaugeProjector.Project(_client.State));
        MetricsRegistry.RegisterReconnectingSource(_firmId,
            () => Volatile.Read(ref _reconnectingState));
    }

    public string FirmId => _firmId;

    public event Action<ExecutionReportEnvelope>? ExecutionReportReceived;

    /// <summary>
    /// Establish the FIXP session and start the inbound event loop. Idempotent;
    /// safe to call from a hosted-service <c>StartAsync</c>.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        OnConnected();
    }

    private void OnConnected()
    {
        if (Interlocked.Exchange(ref _connectedState, 1) == 0)
            MetricsRegistry.EntryPointConnected.Add(1, FirmTag());
        MetricsRegistry.RecordSessionVerId(_firmId, _currentSessionVerId);
        StartEventLoop();
    }

    /// <summary>
    /// Spawn the inbound event-loop task. Re-enters after a successful
    /// reconnect — the previous task completed when the underlying
    /// <c>Events()</c> enumeration drained on Terminate, so a fresh
    /// <c>Task.Run</c> is required (the original <c>??=</c> was a bug:
    /// once completed, the loop would never restart and ER replay after
    /// reconnect would land in /dev/null).
    /// </summary>
    private void StartEventLoop()
    {
        if (_eventLoop is { IsCompleted: false }) return;
        _eventLoop = Task.Run(() => RunEventLoopAsync(_shutdownCts.Token));
    }

    public Task SubmitAsync(Order order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.Quantity < 0)
            throw new ArgumentOutOfRangeException(nameof(order), "Quantity cannot be negative when submitted upstream.");

        var req = new UpModels.NewOrderRequest
        {
            ClOrdID = new UpModels.ClOrdID(order.ClOrdId),
            SecurityId = order.SecurityId,
            Side = order.Side == OrderSide.Buy ? UpModels.Side.Buy : UpModels.Side.Sell,
            OrderType = order.Type == OrderType.Limit ? UpModels.OrderType.Limit : UpModels.OrderType.Market,
            Price = order.Price,
            OrderQty = (ulong)order.Quantity,
            TimeInForce = UpModels.TimeInForce.Day,
        };

        return _client.SubmitAsync(req, cancellationToken);
    }

    public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (newClOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(newClOrdId), "Cancel ClOrdID must be non-zero.");

        var req = new UpModels.CancelOrderRequest
        {
            ClOrdID = new UpModels.ClOrdID(newClOrdId),
            OrigClOrdID = new UpModels.ClOrdID(order.ClOrdId),
            SecurityId = order.SecurityId,
            Side = order.Side == OrderSide.Buy ? UpModels.Side.Buy : UpModels.Side.Sell,
        };

        return _client.CancelAsync(req, cancellationToken);
    }

    public Task CancelReplaceAsync(Order original, ulong newClOrdId, long newQuantity, decimal? newPrice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (newClOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(newClOrdId), "Replace ClOrdID must be non-zero.");
        if (newQuantity < 0)
            throw new ArgumentOutOfRangeException(nameof(newQuantity));

        var req = new UpModels.ReplaceOrderRequest
        {
            ClOrdID = new UpModels.ClOrdID(newClOrdId),
            OrigClOrdID = new UpModels.ClOrdID(original.ClOrdId),
            SecurityId = original.SecurityId,
            Side = original.Side == OrderSide.Buy ? UpModels.Side.Buy : UpModels.Side.Sell,
            OrderType = original.Type == OrderType.Limit ? UpModels.OrderType.Limit : UpModels.OrderType.Market,
            Price = newPrice,
            OrderQty = (ulong)newQuantity,
            TimeInForce = UpModels.TimeInForce.Day,
        };

        return _client.ReplaceAsync(req, cancellationToken);
    }

    // The IEntryPointClient submit-side surface is unused on the real adapter
    // (OrdersEndpoints calls IExchangeGateway directly). We implement it to
    // satisfy the interface so the registry can expose a single
    // IEntryPointClient seam to the existing router.
    public Task SubmitNewOrderAsync(NewOrderSingle request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Use IExchangeGateway.SubmitAsync.");
    public Task SubmitCancelAsync(OrderCancelRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Use IExchangeGateway.CancelAsync.");
    public Task SubmitCancelReplaceAsync(OrderCancelReplaceRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Use IExchangeGateway.CancelReplaceAsync.");

    private async Task RunEventLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var ev in _client.Events(ct).ConfigureAwait(false))
            {
                MetricsRegistry.EntryPointEventsReceived.Add(1,
                    new KeyValuePair<string, object?>("firm", _firmId),
                    new KeyValuePair<string, object?>("event_type", ev.GetType().Name));

                // Defensive gap detection on top of the SDK's own
                // IRetransmitRequestHandler. The SDK normally hides gaps via
                // automatic retransmit; if a gap nevertheless surfaces here,
                // the metric flags it for ops and the ER processor's
                // idempotency (#16) makes any subsequent replay safe.
                switch (FixpGapDetector.Observe(ev.SeqNum, ref _lastInboundSeqNum))
                {
                    case GapObservation.Gap:
                        MetricsRegistry.EntryPointGapDetected.Add(1, FirmTag());
                        _logger.LogWarning("Inbound seqnum gap on firm {Firm}: got {Got} after {Last}; SDK retransmit should follow.",
                            _firmId, ev.SeqNum, _lastInboundSeqNum);
                        break;
                    case GapObservation.Duplicate:
                        MetricsRegistry.EntryPointDuplicateInbound.Add(1, FirmTag());
                        // Don't continue — let translation + idempotent ER
                        // processor drop it. Duplicates are expected during
                        // FIXP retransmit.
                        break;
                }

                ExecutionReportEnvelope? envelope;
                try
                {
                    envelope = Translate(ev);
                }
                catch (Exception ex)
                {
                    MetricsRegistry.EntryPointTranslationErrors.Add(1, FirmTag());
                    _logger.LogError(ex, "Failed to translate upstream event {Event} for firm {Firm}", ev.GetType().Name, _firmId);
                    continue;
                }

                if (envelope is null)
                    continue;

                try
                {
                    ExecutionReportReceived?.Invoke(envelope);
                }
                catch (Exception ex)
                {
                    // Subscriber raised — never let it kill the loop, otherwise
                    // we'd silently stop translating ERs for this firm.
                    _logger.LogError(ex, "ER subscriber threw for firm {Firm}; continuing.", _firmId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EntryPoint event loop for firm {Firm} terminated unexpectedly.", _firmId);
        }
    }

    /// <summary>
    /// Translates an upstream <c>EntryPointEvent</c> subtype into our internal
    /// envelope. Returns <c>null</c> when the event has no in-domain
    /// representation (e.g. <c>BusinessReject</c>, which lacks a ClOrdID and
    /// is handled via metrics + log only).
    /// </summary>
    internal static ExecutionReportEnvelope? Translate(UpModels.EntryPointEvent ev) => ev switch
    {
        UpModels.OrderAccepted a => new ExecutionReportEnvelope(
            ClOrdId: a.ClOrdID.Value,
            ExecType: EpExecType.New,
            LeavesQuantity: (long)(a.LeavesQty ?? 0UL),
            CumulativeQuantity: (long)(a.CumQty ?? 0UL),
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null),

        UpModels.OrderTrade t => new ExecutionReportEnvelope(
            ClOrdId: t.ClOrdID.Value,
            ExecType: t.OrderStatus == UpModels.OrderStatus.Filled ? EpExecType.Fill : EpExecType.PartialFill,
            LeavesQuantity: (long)(t.LeavesQty ?? 0UL),
            CumulativeQuantity: (long)(t.CumQty ?? 0UL),
            LastQuantity: (long)t.LastQty,
            LastPrice: t.LastPx,
            RejectReason: null),

        UpModels.OrderCancelled c => new ExecutionReportEnvelope(
            ClOrdId: c.ClOrdID.Value,
            ExecType: EpExecType.Canceled,
            LeavesQuantity: 0,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: c.RestatementReason?.ToString(),
            OrigClOrdId: c.OrigClOrdID?.Value ?? 0UL),

        UpModels.OrderModified m => new ExecutionReportEnvelope(
            ClOrdId: m.ClOrdID.Value,
            ExecType: EpExecType.Replaced,
            LeavesQuantity: (long)(m.LeavesQty ?? 0UL),
            CumulativeQuantity: (long)(m.CumQty ?? 0UL),
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            OrigClOrdId: m.OrigClOrdID.Value),

        UpModels.OrderRejected r => new ExecutionReportEnvelope(
            ClOrdId: r.ClOrdID.Value,
            ExecType: EpExecType.Rejected,
            LeavesQuantity: 0,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: r.Reason ?? $"reject_code={r.RejectCode}"),

        UpModels.BusinessReject br => null, // surfaced as metric only — no ClOrdID to anchor an envelope to
        _ => null,
    };

    private void OnTerminated(object? sender, Up.TerminatedEventArgs e)
    {
        MetricsRegistry.EntryPointTerminated.Add(1,
            new KeyValuePair<string, object?>("firm", _firmId),
            new KeyValuePair<string, object?>("code", e.Code.ToString()),
            new KeyValuePair<string, object?>("initiated_by_client", e.InitiatedByClient));
        if (Interlocked.Exchange(ref _connectedState, 0) == 1)
            MetricsRegistry.EntryPointConnected.Add(-1, FirmTag());
        _logger.LogWarning("EntryPoint session terminated for firm {Firm}: code={Code} reason={Reason} byClient={ByClient}",
            _firmId, e.Code, e.Reason, e.InitiatedByClient);

        // Don't fight a graceful shutdown or a client-initiated terminate
        // (e.g. our own DisposeAsync sending Terminate). Peer-initiated
        // terminations are the only ones that warrant a reconnect attempt.
        if (e.InitiatedByClient || _disposed || _shutdownCts.IsCancellationRequested) return;

        // Detach from the inbound thread so the event-loop can drain
        // cleanly; the reconnect loop owns its own lifecycle.
        _ = Task.Run(() => ReconnectLoopAsync(_shutdownCts.Token));
    }

    /// <summary>
    /// Singleflight reconnect loop. Bumps <c>SessionVerId</c> on every
    /// attempt (the gateway requires strict-greater) and applies
    /// exponential backoff with jitter. Exits cleanly on shutdown CT.
    /// On success the inbound event-loop is restarted via
    /// <see cref="StartEventLoop"/> so ER replay (FIXP retransmit) lands
    /// on the existing <c>ExecutionReportReceived</c> subscribers.
    /// </summary>
    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        if (!await _reconnectLock.WaitAsync(0, ct).ConfigureAwait(false))
            return; // already reconnecting
        Interlocked.Exchange(ref _reconnectingState, 1);
        try
        {
            var attempt = 0;
            while (!ct.IsCancellationRequested && !_disposed)
            {
                attempt++;
                uint nextVerId;
                try { nextVerId = checked(_currentSessionVerId + 1); }
                catch (OverflowException)
                {
                    _logger.LogError("SessionVerId overflow for firm {Firm}; giving up.", _firmId);
                    return;
                }
                MetricsRegistry.EntryPointReconnectAttempts.Add(1,
                    new KeyValuePair<string, object?>("firm", _firmId),
                    new KeyValuePair<string, object?>("attempt", attempt));
                try
                {
                    await _client.ReconnectAsync(nextVerId, ct).ConfigureAwait(false);
                    _currentSessionVerId = nextVerId;
                    MetricsRegistry.EntryPointReconnectSucceeded.Add(1, FirmTag());
                    _logger.LogInformation("EntryPoint reconnect ok for firm {Firm} on attempt {N} (sessionVerId={Ver}).",
                        _firmId, attempt, nextVerId);
                    OnConnected();
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    MetricsRegistry.EntryPointReconnectFailed.Add(1,
                        new KeyValuePair<string, object?>("firm", _firmId),
                        new KeyValuePair<string, object?>("reason", ex.GetType().Name));
                    _logger.LogWarning(ex, "EntryPoint reconnect failed for firm {Firm} (attempt {N}, sessionVerId={Ver}); will retry.",
                        _firmId, attempt, nextVerId);
                    // Bump the in-memory version even on failure: if the
                    // failure was anything past Negotiate, the gateway has
                    // already burned the value. Cheap to skip a few.
                    _currentSessionVerId = nextVerId;
                    var delay = ComputeBackoff(attempt);
                    try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectingState, 0);
            _reconnectLock.Release();
        }
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        var basisMs = Math.Min(_maxReconnectDelay.TotalMilliseconds,
            _initialReconnectDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 16)));
        var jitterMs = Random.Shared.NextDouble() * 0.25 * basisMs;
        return TimeSpan.FromMilliseconds(basisMs + jitterMs);
    }

    private KeyValuePair<string, object?> FirmTag() => new("firm", _firmId);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try { _shutdownCts.Cancel(); } catch { /* ignore */ }
        // Stop emitting per-firm gauges before tearing down the SDK so the
        // observable callbacks don't race with _client disposal.
        MetricsRegistry.UnregisterSessionStateSource(_firmId);
        MetricsRegistry.UnregisterReconnectingSource(_firmId);
        if (_eventLoop is not null)
        {
            try { await _eventLoop.ConfigureAwait(false); } catch { /* event loop swallows, but be defensive */ }
        }
        _client.Terminated -= OnTerminated;
        await _client.DisposeAsync().ConfigureAwait(false);
        _shutdownCts.Dispose();
        _reconnectLock.Dispose();
        if (Interlocked.Exchange(ref _connectedState, 0) == 1)
            MetricsRegistry.EntryPointConnected.Add(-1, FirmTag());
    }

    // BusinessReject, also exposed for instrumentation hookup if a future
    // correlation map is added (RefSeqNum → ClOrdID).
    internal static void RecordBusinessReject(string firmId, UpModels.BusinessReject br)
    {
        MetricsRegistry.EntryPointBusinessRejects.Add(1,
            new KeyValuePair<string, object?>("firm", firmId),
            new KeyValuePair<string, object?>("reason", br.RejectReason));
    }
}
