using B3.Trading.Application.Observability;
using B3.Trading.Application.Risk;
using B3.Trading.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Up = B3.EntryPoint.Client;
using UpModels = B3.EntryPoint.Client.Models;

using B3.Trading.Application;

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
    private readonly TimeSpan _gracefulTerminateTimeout;
    private readonly TimeProvider _clock;
    private readonly OrderEntryLatencyProbe _latencyProbe;
    private Task? _eventLoop;
    private uint _currentSessionVerId;
    private int _connectedState; // 0 = disconnected, 1 = connected (matches UpDownCounter increments)
    private int _reconnectingState; // 0 = idle, 1 = reconnect loop active (observable gauge)
    private ulong _lastInboundSeqNum;
    private volatile bool _disposed;

    // Slice 2 of #132. Captured by the SDK's InboundGapAtReconnect handler
    // (fires synchronously inside ReconnectAsync, just before the call returns)
    // and consumed once on the very next post-reconnect call to the reactor.
    // Single-writer (SDK inbound thread inside ReconnectAsync) / single-reader
    // (reconnect loop, immediately after ReconnectAsync returns), separated by
    // the await — no lock needed for the bool/struct fields, but we Volatile.Read
    // / Write to defend against compiler reordering across the await barrier.
    private int _lastReconnectHadGap;
    private ulong _lastGapFromSeq;
    private uint _lastGapCount;
    private ulong _lastGapPriorSessionVerId;
    private string? _lastTerminationCode;
    private readonly IVenueDisconnectReactor? _reactor;
    // #433 P1. Optional resolver for the venue-side STP instruction
    // stamped on every outbound NewOrderRequest / ReplaceOrderRequest.
    // Optional (rather than required) so legacy tests that construct
    // the gateway directly stay green — a null resolver collapses to
    // SelfTradePreventionInstruction.None and the wire behavior
    // matches pre-#433.
    private readonly IOptionsMonitor<RiskOptions>? _riskOptions;

    public B3EntryPointClientGateway(
        Up.EntryPointClient client,
        string firmId,
        uint initialSessionVerId,
        ILogger<B3EntryPointClientGateway> logger,
        TimeSpan? initialReconnectDelay = null,
        TimeSpan? maxReconnectDelay = null,
        TimeProvider? clock = null,
        OrderEntryLatencyProbe? latencyProbe = null,
        IVenueDisconnectReactor? venueDisconnectReactor = null,
        TimeSpan? gracefulTerminateTimeout = null,
        IOptionsMonitor<RiskOptions>? riskOptions = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _firmId = firmId ?? throw new ArgumentNullException(nameof(firmId));
        _logger = logger;
        _currentSessionVerId = initialSessionVerId;
        _initialReconnectDelay = initialReconnectDelay ?? TimeSpan.FromSeconds(1);
        _maxReconnectDelay = maxReconnectDelay ?? TimeSpan.FromSeconds(30);
        _gracefulTerminateTimeout = gracefulTerminateTimeout ?? TimeSpan.FromSeconds(2);
        _clock = clock ?? TimeProvider.System;
        _latencyProbe = latencyProbe ?? new OrderEntryLatencyProbe(_clock);
        _reactor = venueDisconnectReactor;
        _riskOptions = riskOptions;
        _client.Terminated += OnTerminated;
        _client.InboundGapAtReconnect += OnInboundGapAtReconnect;
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

    private void OnInboundGapAtReconnect(object? sender, Up.InboundGapAtReconnectEventArgs e)
    {
        // Capture for the post-reconnect reactor invocation; the SDK fires
        // this exactly once per ReconnectAsync, synchronously inside that call,
        // BEFORE it returns to ReconnectLoopAsync. No lock needed — see field
        // declarations.
        _lastGapFromSeq = e.FromSeqNo;
        _lastGapCount = e.Count;
        _lastGapPriorSessionVerId = e.PriorSessionVerId;
        Volatile.Write(ref _lastReconnectHadGap, 1);
        _logger.LogWarning(
            "EntryPoint inbound gap at reconnect for firm {Firm}: priorVer={PriorVer} from={From} count={Count}",
            _firmId, e.PriorSessionVerId, e.FromSeqNo, e.Count);
    }

    public string FirmId => _firmId;

    /// <summary>
    /// Lower-snake-case tag of the live SDK <c>FixpClientState</c> (e.g.
    /// <c>established</c>, <c>terminated</c>). Mirrors the
    /// <c>trading.entrypoint.session_state</c> metric and is the canonical
    /// per-firm status read by the <c>/admin/firms</c> endpoint.
    /// </summary>
    public string SessionStateTag =>
        FixpStateGaugeProjector.Project(_client.State).Single(r => r.Value == 1).Key;

    /// <summary>Last <c>SessionVerId</c> the gateway has tried to use (in-memory; persisted via SDK's <c>SessionStateStore</c>).</summary>
    public uint CurrentSessionVerId => Volatile.Read(ref _currentSessionVerId);

    /// <summary>True when the auto-reconnect loop is currently running for this firm.</summary>
    public bool IsReconnecting => Volatile.Read(ref _reconnectingState) == 1;

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

        var req = BuildNewOrderRequest(order, ResolveStpInstruction(order));

        return SendAsync(req.ClOrdID.Value, OrderEntryLatencyProbe.OpSubmit,
            ct => _client.SubmitAsync(req, ct), cancellationToken);
    }

    /// <summary>
    /// Q3.4 (#284). Extracted from <see cref="SubmitAsync"/> so the
    /// outbound <c>NewOrderRequest</c> mapping can be pinned by unit
    /// tests without standing up a real <c>EntryPointClient</c>. Pure
    /// function: every field is read from the domain
    /// <see cref="Order"/> exactly once and there are no side effects.
    ///
    /// <para>
    /// #433 P1. <paramref name="stpInstruction"/> is the venue-side
    /// self-trade-prevention instruction (SDK 0.15.0+). Resolved by
    /// the caller via <see cref="RiskLimitsResolver.ResolveSelfTradePreventionMode"/>
    /// and mapped through <see cref="MapStpInstruction"/>; defaults
    /// to <see cref="UpModels.SelfTradePreventionInstruction.None"/>
    /// on the legacy overload below so existing tests stay green.
    /// </para>
    /// </summary>
    internal static UpModels.NewOrderRequest BuildNewOrderRequest(Order order)
        => BuildNewOrderRequest(order, UpModels.SelfTradePreventionInstruction.None);

    internal static UpModels.NewOrderRequest BuildNewOrderRequest(
        Order order, UpModels.SelfTradePreventionInstruction stpInstruction)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new UpModels.NewOrderRequest
        {
            ClOrdID = new UpModels.ClOrdID(order.ClOrdId),
            SecurityId = order.SecurityId,
            Side = order.Side == OrderSide.Buy ? UpModels.Side.Buy : UpModels.Side.Sell,
            OrderType = MapOrderType(order.Type),
            Price = order.Price,
            StopPrice = order.StopPrice,
            OrderQty = (ulong)order.Quantity,
            TimeInForce = MapTimeInForce(order.TimeInForce),
            ExpireDate = order.GoodTillDate,
            // Q3.4 (#284). Native iceberg / reserve display qty maps
            // straight to the SDK's MaxFloor (FIX standard for "max
            // visible qty"). Domain.Order's ctor already validated
            // 0 < DisplayQty <= Quantity, so the cast is safe.
            // TODO(#298): DisplayResetPolicy is NOT plumbed to the SDK —
            // B3.EntryPoint.Client 0.15.0 NewOrderRequest exposes no
            // refresh-policy flag. To avoid the venue silently defaulting
            // to Always (which would break the OnPartialFill / Never
            // contracts), the REST + risk validation boundary REJECTS
            // any policy other than Always (see #297 pass-1 fix).
            // Therefore only Always-policy icebergs can reach this point,
            // so MaxFloor alone faithfully expresses the trader's intent.
            // When B3.EntryPoint.Client exposes a refresh-policy field,
            // wire order.DisplayResetPolicy here and drop the validation
            // guard in OrdersEndpoints.cs + OrderSubmissionService.cs.
            MaxFloor = order.DisplayQty is { } dq ? (ulong)dq : (ulong?)null,
            // #433 P1. Venue-side STP. Defense-in-depth pair with
            // SelfTradePreventionCheck — see SelfTradePreventionMode
            // doc comment for the rationale. The instruction value is
            // resolved by the caller (gateway SubmitAsync) so this
            // static stays pure.
            SelfTradePreventionInstruction = stpInstruction,
        };
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

        return SendAsync(newClOrdId, OrderEntryLatencyProbe.OpCancel,
            ct => _client.CancelAsync(req, ct), cancellationToken);
    }

    public Task CancelReplaceAsync(
        Order original,
        ulong newClOrdId,
        long newQuantity,
        decimal? newPrice,
        TimeInForce? requestedTimeInForce,
        decimal? requestedStopPrice,
        DateTimeOffset? requestedGoodTillDate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (newClOrdId == 0)
            throw new ArgumentOutOfRangeException(nameof(newClOrdId), "Replace ClOrdID must be non-zero.");
        if (newQuantity < 0)
            throw new ArgumentOutOfRangeException(nameof(newQuantity));

        // Q1.1 (#253). Merge requested overrides with the original's
        // values so the venue sees a coherent ReplaceOrderRequest. The
        // domain helper enforces the invariants (StopPrice required iff
        // Stop*; GoodTillDate required iff TIF==GTD; auto-clear when
        // TIF moves away from GTD). Any violation throws ArgumentException,
        // which the caller (OrderModifyService) treats as a gateway-side
        // failure and rolls back the same way it does any other replace
        // dispatch failure.
        var (effTif, effStop, effGtd) = Order.MergeReplacementOptionals(
            original.Type, original.TimeInForce, original.StopPrice, original.GoodTillDate,
            requestedTimeInForce, requestedStopPrice, requestedGoodTillDate);

        var req = new UpModels.ReplaceOrderRequest
        {
            ClOrdID = new UpModels.ClOrdID(newClOrdId),
            OrigClOrdID = new UpModels.ClOrdID(original.ClOrdId),
            SecurityId = original.SecurityId,
            Side = original.Side == OrderSide.Buy ? UpModels.Side.Buy : UpModels.Side.Sell,
            OrderType = MapOrderType(original.Type),
            Price = newPrice,
            StopPrice = effStop,
            OrderQty = (ulong)newQuantity,
            TimeInForce = MapTimeInForce(effTif),
            ExpireDate = effGtd,
            // Q3.4 (#284). The modify pipeline does not currently
            // expose a way to alter the iceberg DisplayQty / policy,
            // so the replace inherits the original's visible portion
            // (clamped to newQuantity by HydrateReplacement when the
            // new order qty would otherwise be < DisplayQty). Same
            // SDK caveat as SubmitAsync: only MaxFloor is wired;
            // DisplayResetPolicy ride-along is gated behind the
            // REST/risk validation that only accepts Always today
            // (see #298), so passing MaxFloor alone is faithful.
            MaxFloor = original.DisplayQty is { } odq
                ? (ulong)Math.Min(odq, newQuantity)
                : (ulong?)null,
            // #433 P1. Venue-side STP carried on the replace too —
            // a modified order is a fresh book entry from the
            // matching engine's perspective so the instruction must
            // re-accompany it. Resolution scope is the original
            // order's (owner, firm, symbol) — modify never changes
            // those three.
            SelfTradePreventionInstruction = ResolveStpInstruction(original),
        };

        return SendAsync(newClOrdId, OrderEntryLatencyProbe.OpReplace,
            ct => _client.ReplaceAsync(req, ct), cancellationToken);
    }

    /// <summary>
    /// Common send pipeline: register the pending latency probe BEFORE the
    /// SDK await (the matching ER can race ahead of the await on a fast
    /// wire, so a post-await registration would lose samples), record the
    /// SDK call duration on success, and forget the pending entry on
    /// synchronous failure (no ER will follow). Failed-call duration is
    /// intentionally not histogrammed — failures are tracked by
    /// <see cref="MetricsRegistry.OrdersGatewayFailed"/>; mixing failure
    /// timings would corrupt the p99 of successful sends.
    /// </summary>
    private async Task SendAsync(ulong clOrdId, string op, Func<CancellationToken, Task> sdkCall, CancellationToken ct)
    {
        var start = _clock.GetTimestamp();
        _latencyProbe.OnSubmitted(clOrdId, _firmId, op);
        try
        {
            await sdkCall(ct).ConfigureAwait(false);
        }
        catch
        {
            _latencyProbe.Forget(clOrdId);
            throw;
        }

        MetricsRegistry.OrderEntryCallMs.Record(
            _clock.GetElapsedTime(start).TotalMilliseconds,
            new KeyValuePair<string, object?>("firm", _firmId),
            new KeyValuePair<string, object?>("op", op));
    }

    /// <summary>
    /// Q1.1 (#253). Domain → SDK <see cref="UpModels.OrderType"/> mapping
    /// covering the full B3 order surface exposed at v0. Internal so the
    /// gateway tests can pin the table without going through reflection
    /// on the SDK's private encoder.
    /// </summary>
    internal static UpModels.OrderType MapOrderType(OrderType type) => type switch
    {
        OrderType.Limit => UpModels.OrderType.Limit,
        OrderType.Market => UpModels.OrderType.Market,
        OrderType.StopLoss => UpModels.OrderType.StopLoss,
        OrderType.StopLimit => UpModels.OrderType.StopLimit,
        OrderType.MarketWithLeftover => UpModels.OrderType.MarketWithLeftoverAsLimit,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unmapped Domain.OrderType."),
    };

    /// <summary>
    /// Q1.1 (#253). Domain → SDK <see cref="UpModels.TimeInForce"/> mapping
    /// covering all 7 v0 values.
    /// </summary>
    internal static UpModels.TimeInForce MapTimeInForce(TimeInForce tif) => tif switch
    {
        TimeInForce.Day => UpModels.TimeInForce.Day,
        TimeInForce.IOC => UpModels.TimeInForce.ImmediateOrCancel,
        TimeInForce.FOK => UpModels.TimeInForce.FillOrKill,
        TimeInForce.GTC => UpModels.TimeInForce.GoodTillCancel,
        TimeInForce.GTD => UpModels.TimeInForce.GoodTillDate,
        TimeInForce.AtClose => UpModels.TimeInForce.AtTheClose,
        TimeInForce.GoodForAuction => UpModels.TimeInForce.GoodForAuction,
        _ => throw new ArgumentOutOfRangeException(nameof(tif), tif, "Unmapped Domain.TimeInForce."),
    };

    /// <summary>
    /// #433 P1. Application → SDK STP-instruction mapping. Total over
    /// every <see cref="SelfTradePreventionMode"/> value; throws on
    /// unmapped values so adding a future variant without updating
    /// this table fails loudly instead of silently sending
    /// <c>None</c> on the wire.
    /// </summary>
    internal static UpModels.SelfTradePreventionInstruction MapStpInstruction(
        SelfTradePreventionMode mode) => mode switch
        {
            SelfTradePreventionMode.None => UpModels.SelfTradePreventionInstruction.None,
            SelfTradePreventionMode.CancelAggressorOrder => UpModels.SelfTradePreventionInstruction.CancelAggressorOrder,
            SelfTradePreventionMode.CancelRestingOrder => UpModels.SelfTradePreventionInstruction.CancelRestingOrder,
            SelfTradePreventionMode.CancelBothOrders => UpModels.SelfTradePreventionInstruction.CancelBothOrders,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unmapped Application.SelfTradePreventionMode."),
        };

    /// <summary>
    /// #433 P1. Resolve the STP instruction to stamp on an outbound
    /// <c>NewOrderRequest</c> / <c>ReplaceOrderRequest</c> for a
    /// given order. Falls back to
    /// <see cref="UpModels.SelfTradePreventionInstruction.None"/>
    /// when no <see cref="RiskOptions"/> monitor is wired (test
    /// gateways that don't care about the field stay green); the
    /// production composition root always passes one.
    /// </summary>
    private UpModels.SelfTradePreventionInstruction ResolveStpInstruction(Order order)
    {
        if (_riskOptions is null)
            return UpModels.SelfTradePreventionInstruction.None;
        var mode = RiskLimitsResolver.ResolveSelfTradePreventionMode(
            _riskOptions.CurrentValue, order.Owner.Value, order.FirmId, order.Symbol);
        return MapStpInstruction(mode);
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
                    envelope = Translate(ev, _firmId);
                }
                catch (Exception ex)
                {
                    MetricsRegistry.EntryPointTranslationErrors.Add(1, FirmTag());
                    _logger.LogError(ex, "Failed to translate upstream event {Event} for firm {Firm}", ev.GetType().Name, _firmId);
                    continue;
                }

                if (envelope is null)
                    continue;

                // Record latency before subscriber fan-out so a misbehaving
                // subscriber can't lose the sample.
                _latencyProbe.OnExecutionReport(envelope.ClOrdId);

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
    ///
    /// <para>
    /// PR #317 P1. <paramref name="firmId"/> is stamped onto the resulting
    /// <see cref="ExecutionReportEnvelope.FirmId"/> so the downstream
    /// processor can detect (and reject) an ER that arrived on the wrong
    /// firm gateway. The gateway calls the firm-aware overload from its
    /// event loop; the parameterless overload (kept for pure translation
    /// tests) yields a null <c>FirmId</c> and skips the cross-firm check,
    /// matching pre-#317 behaviour.
    /// </para>
    /// </summary>
    internal static ExecutionReportEnvelope? Translate(UpModels.EntryPointEvent ev) => Translate(ev, firmId: null);

    internal static ExecutionReportEnvelope? Translate(UpModels.EntryPointEvent ev, string? firmId) => ev switch
    {
        UpModels.OrderAccepted a => new ExecutionReportEnvelope(
            ClOrdId: a.ClOrdID.Value,
            ExecType: EpExecType.New,
            LeavesQuantity: (long)(a.LeavesQty ?? 0UL),
            CumulativeQuantity: (long)(a.CumQty ?? 0UL),
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            FirmId: firmId),

        UpModels.OrderTrade t => new ExecutionReportEnvelope(
            ClOrdId: t.ClOrdID.Value,
            ExecType: t.OrderStatus == UpModels.OrderStatus.Filled ? EpExecType.Fill : EpExecType.PartialFill,
            LeavesQuantity: (long)(t.LeavesQty ?? 0UL),
            CumulativeQuantity: (long)(t.CumQty ?? 0UL),
            LastQuantity: (long)t.LastQty,
            LastPrice: t.LastPx,
            RejectReason: null,
            FirmId: firmId),

        UpModels.OrderCancelled c => new ExecutionReportEnvelope(
            ClOrdId: c.ClOrdID.Value,
            ExecType: EpExecType.Canceled,
            LeavesQuantity: 0,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: c.RestatementReason?.ToString(),
            OrigClOrdId: c.OrigClOrdID?.Value ?? 0UL,
            FirmId: firmId),

        UpModels.OrderModified m => new ExecutionReportEnvelope(
            ClOrdId: m.ClOrdID.Value,
            ExecType: EpExecType.Replaced,
            LeavesQuantity: (long)(m.LeavesQty ?? 0UL),
            CumulativeQuantity: (long)(m.CumQty ?? 0UL),
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: null,
            // OrigClOrdID is a non-nullable ClOrdID struct in the SDK,
            // but a venue that sends 0 on the wire would surface as
            // Value == 0 here. The processor treats OrigClOrdId == 0
            // as "missing" and falls back to OrderOwnershipMap's
            // new→orig link populated by RegisterReplaceLink (slice 1
            // of #122).
            OrigClOrdId: m.OrigClOrdID.Value,
            FirmId: firmId),

        UpModels.OrderRejected r => new ExecutionReportEnvelope(
            ClOrdId: r.ClOrdID.Value,
            ExecType: EpExecType.Rejected,
            LeavesQuantity: 0,
            CumulativeQuantity: 0,
            LastQuantity: 0,
            LastPrice: 0m,
            RejectReason: r.Reason ?? $"reject_code={r.RejectCode}",
            FirmId: firmId),

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

        // Capture the peer terminate code for the post-reconnect reactor
        // (slice 2 of #132). Stored before kicking the reconnect loop so
        // the loop sees the latest value when it consults it on success.
        Volatile.Write(ref _lastTerminationCode, e.Code.ToString());

        // Detach from the inbound thread so the event-loop can drain
        // cleanly; the reconnect loop owns its own lifecycle.
        _ = Task.Run(() => ReconnectLoopAsync(_shutdownCts.Token));
    }

    /// <summary>
    /// Singleflight reconnect loop. Uses the SDK v0.14.4 (#173) spec-
    /// canonical <c>EstablishReuseThenNegotiate</c> mode: the client first
    /// tries an <c>Establish</c> against the prior <c>SessionVerID</c>
    /// (reattach — peer keeps our working set + retransmit window) and
    /// only on a recoverable <c>EstablishmentReject</c> falls back to a
    /// fresh <c>Negotiate</c> with a strictly-greater <c>SessionVerID</c>
    /// supplied by the selector (renegotiate — peer's per-session state
    /// for this firm is discarded). The new SDK overload returns a
    /// <see cref="Up.ReconnectOutcome"/> whose <c>Kind</c> tells us which
    /// branch was taken; we mirror its <c>SessionVerId</c> into
    /// <see cref="_currentSessionVerId"/> so reattached cycles do NOT
    /// trigger downstream session-rolled bookkeeping (see PR #420 /
    /// <see cref="SnapshotService"/>). Exponential backoff with jitter
    /// guards the retry; exits cleanly on shutdown CT. On success the
    /// inbound event-loop is restarted via <see cref="StartEventLoop"/>
    /// so ER replay (FIXP retransmit) lands on the existing
    /// <c>ExecutionReportReceived</c> subscribers.
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
                MetricsRegistry.EntryPointReconnectAttempts.Add(1,
                    new KeyValuePair<string, object?>("firm", _firmId),
                    new KeyValuePair<string, object?>("attempt", attempt));
                var priorVerId = Volatile.Read(ref _currentSessionVerId);
                try
                {
                    // Selector is invoked at most once per ReconnectAsync —
                    // only when the SDK falls back to Negotiate after a
                    // recoverable Establish-reject. Reattach path skips it
                    // entirely, preserving the SessionVerID.
                    var outcome = await _client.ReconnectAsync(
                        Up.ReconnectMode.EstablishReuseThenNegotiate,
                        BumpSessionVerIdSelector,
                        ct).ConfigureAwait(false);
                    _currentSessionVerId = outcome.SessionVerId;
                    MetricsRegistry.EntryPointReconnectSucceeded.Add(1,
                        new KeyValuePair<string, object?>("firm", _firmId),
                        new KeyValuePair<string, object?>("kind", ReconnectKindTag(outcome.Kind)));
                    _logger.LogInformation(
                        "EntryPoint reconnect ok for firm {Firm} on attempt {N} (kind={Kind} sessionVerId={Ver} priorVerId={Prior} retransmitWindowReady={RetransmitReady}).",
                        _firmId, attempt, outcome.Kind, outcome.SessionVerId, priorVerId, outcome.RetransmitWindowReady);
                    OnConnected();
                    NotifyVenueDisconnectReactor();
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (OverflowException)
                {
                    // Selector overflowed — uint32 SessionVerId would wrap.
                    // Operator intervention required; give up.
                    _logger.LogError("SessionVerId overflow for firm {Firm}; giving up.", _firmId);
                    return;
                }
                catch (Exception ex)
                {
                    MetricsRegistry.EntryPointReconnectFailed.Add(1,
                        new KeyValuePair<string, object?>("firm", _firmId),
                        new KeyValuePair<string, object?>("reason", ex.GetType().Name));
                    _logger.LogWarning(ex, "EntryPoint reconnect failed for firm {Firm} (attempt {N}, priorSessionVerId={Prior}); will retry.",
                        _firmId, attempt, priorVerId);
                    // Defensive bump: if the failure happened past Negotiate
                    // the SDK has already burned the next value, so reuse
                    // would race with the peer's strict-monotonic guard.
                    // Cheap to skip a few. Reattach path doesn't burn the
                    // current value (Establish is idempotent on SessionVerID)
                    // so the +1 is safe either way.
                    try { _currentSessionVerId = checked(priorVerId + 1); }
                    catch (OverflowException)
                    {
                        _logger.LogError("SessionVerId overflow for firm {Firm}; giving up.", _firmId);
                        return;
                    }
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

    /// <summary>
    /// SessionVerID selector for the SDK v0.14.4 reconnect API. Invoked
    /// only on the renegotiate fallback (Reattach skips it). Returns
    /// <c>prior + 1</c>; an <see cref="OverflowException"/> escapes back
    /// to <see cref="ReconnectLoopAsync"/> which short-circuits the loop.
    /// </summary>
    private static uint BumpSessionVerIdSelector(uint prior) => checked(prior + 1);

    private static string ReconnectKindTag(Up.ReconnectKind kind) => kind switch
    {
        Up.ReconnectKind.Reattached => "reattached",
        Up.ReconnectKind.Renegotiated => "renegotiated",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Slice 2 of #132. After a successful reconnect, drains the
    /// captured gap / termination signals into a single
    /// <see cref="ReconnectOutcome"/> and hands it to the configured
    /// <see cref="IVenueDisconnectReactor"/>. Reactor errors are
    /// swallowed and logged: a reactor crash MUST NOT bring down the
    /// gateway. Resets the captured state regardless of reactor
    /// presence so a subsequent reconnect cycle starts clean.
    /// </summary>
    private void NotifyVenueDisconnectReactor()
    {
        var hadGap = Interlocked.Exchange(ref _lastReconnectHadGap, 0) == 1;
        var fromSeq = hadGap ? (ulong?)_lastGapFromSeq : null;
        var count = hadGap ? (uint?)_lastGapCount : null;
        var priorVer = hadGap ? (ulong?)_lastGapPriorSessionVerId : null;
        var priorCode = Interlocked.Exchange(ref _lastTerminationCode, null);

        if (_reactor is null) return;

        try
        {
            _reactor.OnPeerReconnected(_firmId,
                new ReconnectOutcome(hadGap, fromSeq, count, priorVer, priorCode));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "VenueDisconnectReactor threw for firm {Firm}; suppressed to keep gateway alive.", _firmId);
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

        // Graceful FIXP Terminate(Finished) before tearing down the SDK so the
        // peer flushes our session promptly and our next boot doesn't trip
        // DUPLICATE_SESSION_CONNECTION. The SDK's own DisposeAsync also sends
        // Terminate(Finished) internally as a last-resort fallback, but it
        // swallows IOException silently and doesn't emit the
        // `entrypoint.terminate` activity + counter that the public
        // TerminateAsync surfaces — so we call it explicitly here for both
        // observability (one log line per firm shutdown) and protocol-level
        // determinism (bounded timeout so a stuck peer can't hang shutdown).
        //
        // OnTerminated is intentionally still attached at this point: the SDK
        // raises Terminated(InitiatedByClient=true) from inside TerminateAsync,
        // and we want the existing handler to record the metric / log line
        // (the _disposed guard on line 527 ensures it skips the reconnect loop).
        if (Volatile.Read(ref _connectedState) == 1)
        {
            try
            {
                using var cts = new CancellationTokenSource(_gracefulTerminateTimeout);
                await _client.TerminateAsync(Up.TerminationCode.Finished, cts.Token).ConfigureAwait(false);
                _logger.LogInformation(
                    "Sent graceful FIXP Terminate(Finished) for firm {Firm} on shutdown.", _firmId);
            }
            catch (InvalidOperationException)
            {
                // SDK throws this if the session was torn down between our
                // connected-state check and the call (race with peer Terminate).
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Graceful FIXP Terminate failed for firm {Firm}; proceeding with dispose.", _firmId);
            }
        }

        _client.Terminated -= OnTerminated;
        _client.InboundGapAtReconnect -= OnInboundGapAtReconnect;
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
