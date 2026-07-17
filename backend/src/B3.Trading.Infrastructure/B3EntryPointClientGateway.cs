using B3.Trading.Application.Investor;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Routing;
using B3.Trading.Application.Risk;
using B3.Trading.Application.SubAccount;
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
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly TimeSpan _initialReconnectDelay;
    private readonly TimeSpan _maxReconnectDelay;
    private readonly TimeSpan _gracefulTerminateTimeout;
    private readonly bool _terminateOnShutdown;
    private readonly TimeProvider _clock;
    private readonly OrderEntryLatencyProbe _latencyProbe;
    private Task? _eventLoop;
    private Task? _reconnectLoop;
    private uint _currentSessionVerId;
    private int _connectedState; // 0 = disconnected, 1 = connected (matches UpDownCounter increments)
    private int _reconnectingState; // 0 = idle, 1 = reconnect loop active (observable gauge)
    private ulong _lastInboundSeqNum;
    private int _reconnectScheduled;
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
    // #471. Optional mapper from domain SubAccountId (string) to the
    // numeric uint? carried in NewOrderRequest.TradingSubAccount /
    // ReplaceOrderRequest.TradingSubAccount (SDK 0.15.0+). Null →
    // no mapper wired → orders never stamp the field (legacy wire
    // behavior, matches the pre-#471 contract). Production
    // composition root always wires DeterministicSubAccountWireIdMapper.
    private readonly ISubAccountWireIdMapper? _subAccountWireIdMapper;
    // #458. Optional resolver for the CBLC Account number (ulong?)
    // carried in NewOrderRequest.Account / ReplaceOrderRequest.Account.
    // Default = NullVenueAccountResolver (always null) — wire field
    // stays omitted and post-trade allocation continues via the
    // broker's out-of-band matching. Operators that have configured
    // a real mapping (lookup table, broker handshake) swap in a real
    // impl at the composition root.
    private readonly IVenueAccountResolver? _venueAccountResolver;
    // #472. Optional resolver for the opaque InvestorId (Prefix,
    // Document) carried in NewOrderRequest.InvestorId /
    // ReplaceOrderRequest.InvestorId. Default = NullInvestorIdResolver
    // (always null) — wire field stays omitted (pre-#472 behavior).
    // The platform deliberately treats InvestorId as opaque (NOT
    // CPF/CNPJ); operators wire a real resolver that hits their
    // broker-issued registry, keeping PII off the wire and out of the
    // WAL (LGPD posture — see IInvestorIdResolver doc).
    private readonly IInvestorIdResolver? _investorIdResolver;
    // #473. Optional resolver for the RoutingInstruction stamped on
    // outbound NewOrderRequest / ReplaceOrderRequest. Default = null
    // → resolver helper short-circuits to null → wire field stays
    // omitted (pre-#473 behavior). Pre-trade gating happens at the
    // caller (OrderSubmissionService / OrderModifyService) via
    // RoutingInstructionAllowedCheck; the gateway re-resolves here
    // to keep the BuildNewOrderRequest path SDK-self-contained, and
    // resolvers MUST be deterministic per-Order so the two
    // resolutions agree (see IRoutingInstructionResolver doc).
    private readonly IRoutingInstructionResolver? _routingInstructionResolver;
    // #512. Reads the SDK's EFFECTIVE SessionVerId after ConnectAsync
    // returns. The SDK may have fallen back from Establish-reuse to a
    // freshly negotiated session with a bumped verId (recoverable reuse
    // reject); that mutation lands on the EntryPointClientOptions instance
    // the composition root passed to the client, so the provider closes
    // over the same instance. Null → no post-connect roll detection
    // (legacy behaviour, matches direct-construction tests).
    private readonly Func<uint>? _effectiveSessionVerIdProvider;
    // #512. Hands a detected connect-time session roll to the Application
    // layer to reap un-acked PendingNew BEFORE the event loop / first
    // snapshot. Null → detection still syncs CurrentSessionVerId but
    // performs no reap.
    private readonly IConnectSessionRollReactor? _connectSessionRollReactor;
    private readonly Func<Up.ReconnectMode, Func<uint, uint>, CancellationToken, Task<Up.ReconnectOutcome>>? _reconnectAsyncOverride;
    private readonly Func<CancellationToken, Task>? _connectAsyncOverride;
    private readonly Func<CancellationToken, IAsyncEnumerable<UpModels.EntryPointEvent>>? _eventStreamOverride;
    private readonly Action? _connectedTestHook;
    // #565. The upstream SDK dials whatever `IPEndPoint` is on the
    // `EntryPointClientOptions` instance it was constructed with — it never
    // re-resolves the configured hostname itself (see
    // B3.EntryPoint.Client.EntryPointClient.ReconnectAsync, which reads
    // `_options.Endpoint.Address` verbatim). On Kubernetes the peer's pod IP
    // changes across a failover/reschedule, so without re-resolving DNS
    // before each attempt the reconnect loop keeps dialing a dead IP
    // forever. The composition root closes over the shared
    // `EntryPointClientOptions` and the original `host:port` string and
    // hands us this callback to refresh `Endpoint` in place immediately
    // before every `ReconnectAsync` attempt. Async + cancellable (rather
    // than a blocking `Action`) so a slow/hung resolver during exactly the
    // network incident this feature targets can't starve the thread pool
    // or ignore shutdown; the composition root uses
    // `Dns.GetHostAddressesAsync` under a bounded timeout tied to `ct`.
    // Null → legacy behaviour (matches direct-construction tests and Mock
    // mode, where there is no DNS to re-resolve).
    private readonly Func<CancellationToken, Task>? _reResolveEndpoint;

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
        IOptionsMonitor<RiskOptions>? riskOptions = null,
        ISubAccountWireIdMapper? subAccountWireIdMapper = null,
        IVenueAccountResolver? venueAccountResolver = null,
        IInvestorIdResolver? investorIdResolver = null,
        IRoutingInstructionResolver? routingInstructionResolver = null,
        bool terminateOnShutdown = true,
        Func<uint>? effectiveSessionVerIdProvider = null,
        IConnectSessionRollReactor? connectSessionRollReactor = null,
        Func<CancellationToken, Task>? reResolveEndpoint = null,
        Func<Up.ReconnectMode, Func<uint, uint>, CancellationToken, Task<Up.ReconnectOutcome>>? reconnectAsyncOverride = null,
        Action? connectedTestHook = null,
        Func<CancellationToken, Task>? connectAsyncOverride = null,
        Func<CancellationToken, IAsyncEnumerable<UpModels.EntryPointEvent>>? eventStreamOverride = null)
    {
        _terminateOnShutdown = terminateOnShutdown;
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
        _subAccountWireIdMapper = subAccountWireIdMapper;
        _venueAccountResolver = venueAccountResolver;
        _investorIdResolver = investorIdResolver;
        _routingInstructionResolver = routingInstructionResolver;
        _effectiveSessionVerIdProvider = effectiveSessionVerIdProvider;
        _connectSessionRollReactor = connectSessionRollReactor;
        _reResolveEndpoint = reResolveEndpoint;
        _reconnectAsyncOverride = reconnectAsyncOverride;
        _connectedTestHook = connectedTestHook;
        _connectAsyncOverride = connectAsyncOverride;
        _eventStreamOverride = eventStreamOverride;
        _client.Terminated += OnTerminated;
        _client.InboundGapAtReconnect += OnInboundGapAtReconnect;
        MetricsRegistry.RecordSessionVerId(_firmId, _currentSessionVerId);
        // Pull-based observable gauges: read SDK state + our reconnect flag on
        // every scrape rather than pushing on every transition. Avoids both
        // a stale dashboard and a dropped-update race against fast Terminated
        // → Reconnecting → Established cycles.
        MetricsRegistry.RegisterSessionStateSource(_firmId,
            () => FixpStateGaugeProjector.Project(OperationalState));
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
        FixpStateGaugeProjector.Project(OperationalState).Single(r => r.Value == 1).Key;

    private Up.Fixp.FixpClientState OperationalState =>
        Volatile.Read(ref _connectedState) == 1
            ? _client.State
            : Up.Fixp.FixpClientState.Disconnected;

    /// <summary>Last <c>SessionVerId</c> the gateway has tried to use (in-memory; persisted via SDK's <c>SessionStateStore</c>).</summary>
    public uint CurrentSessionVerId => Volatile.Read(ref _currentSessionVerId);

    /// <summary>True when the auto-reconnect loop is currently running for this firm.</summary>
    public bool IsReconnecting => Volatile.Read(ref _reconnectingState) == 1;

    public event Action<ExecutionReportEnvelope>? ExecutionReportReceived;
    public event Action<BusinessRejectEnvelope>? BusinessRejectReceived;

    /// <summary>
    /// Establish the FIXP session and start the inbound event loop. Idempotent;
    /// safe to call from a hosted-service <c>StartAsync</c>.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Volatile.Read(ref _connectedState) == 1
                && _eventLoop is { IsCompleted: false })
                return;

            if (_reResolveEndpoint is not null)
                await _reResolveEndpoint(cancellationToken).ConfigureAwait(false);

            if (_connectAsyncOverride is not null)
                await _connectAsyncOverride(cancellationToken).ConfigureAwait(false);
            else
                await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            ReconcileConnectSessionRoll();
            OnConnected();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// #512. Detect a deferred connect-time session-version bump and
    /// reconcile it BEFORE the event loop / first snapshot starts.
    ///
    /// <para>
    /// The #512 happy path resumes the persisted session via Establish-reuse
    /// (verId unchanged) — this is a no-op. But when the venue rejects the
    /// reuse with a recoverable reject, the SDK falls back to Negotiate with
    /// a bumped verId inside the same <c>ConnectAsync</c>. The boot-time
    /// #380/#504 baseline reconcile already ran against the OLD verId before
    /// app start, so it never sees this bump. We read the SDK's effective
    /// verId, sync <see cref="CurrentSessionVerId"/> (so the next snapshot
    /// records the new baseline), and hand the roll to the Application reactor
    /// to reap un-acked PendingNew. Mirrors the reconnect path which already
    /// syncs the verId from the reconnect outcome.
    /// </para>
    /// </summary>
    internal void ReconcileConnectSessionRoll()
    {
        var prior = Volatile.Read(ref _currentSessionVerId);
        if (!TryReadEffectiveSessionVerId("connect", out var effective))
            return;

        if (effective <= prior) return;

        // Order matters for the cross-restart backstop. The boot reconcile
        // re-detects a roll only while the snapshot baseline still records
        // the OLD verId. So we must REAP first and publish the bumped verId
        // only after the reap succeeds: a snapshot interleaving between the
        // reap (orders already cancelled under the dispatcher lock) and the
        // verId publish records old-baseline + cancelled-orders — safe. If
        // we published the verId first, a snapshot could seal
        // baseline=bumped with the ghosts still present, and a crash before
        // the next snapshot would make next-restart boot reconcile miss the
        // roll (storedVerId == currentVerId) — the ghosts would survive
        // permanently. Same reasoning is why a reactor failure must NOT
        // publish the bumped verId: leaving the baseline old keeps the
        // backstop armed.
        try
        {
            _connectSessionRollReactor?.OnSessionRolled(_firmId, prior, effective);
        }
        catch (Exception ex)
        {
            // A reactor failure must never abort the connect — the session
            // is established. Leave _currentSessionVerId at the old baseline
            // so the next-restart boot reconcile still re-detects the roll
            // and reaps the ghosts.
            _logger.LogError(ex,
                "Connect session-roll reactor failed for firm {Firm} (from={From} to={To}); leaving SessionVerId baseline at {Prior} to keep the restart backstop armed.",
                _firmId, prior, effective, prior);
            return;
        }

        Volatile.Write(ref _currentSessionVerId, effective);
        MetricsRegistry.RecordSessionVerId(_firmId, effective);
    }

    /// <summary>
    /// #380 / #503. Reconcile a live-reconnect session-version transition.
    /// Called by <see cref="ReconnectLoopAsync"/> after a successful reconnect,
    /// before <see cref="OnConnected"/>.
    ///
    /// <para>
    /// Any reconnect outcome whose effective <c>SessionVerId</c> advanced past
    /// the gateway's last confirmed baseline is treated as a confirmed session
    /// roll. The common path is <see cref="Up.ReconnectKind.Renegotiated"/>
    /// (venue rejected Establish-reuse and forced a fresh Negotiate), but a
    /// previous failed renegotiate can also leave the SDK's local retry state on
    /// the bumped verId and allow a later <see cref="Up.ReconnectKind.Reattached"/>
    /// to that already-rolled session. In both cases the venue discarded the old
    /// working set, so un-acked PendingNew cannot exist and surviving
    /// Working / PartiallyFilled orders are ghosts that FIXP retransmission
    /// cannot resurrect. We hand the roll to the Application reactor (reap
    /// PendingNew + flag Working/PartiallyFilled stale) and publish the bumped
    /// verId only AFTER the reactor succeeds. A reactor failure leaves
    /// <see cref="CurrentSessionVerId"/> at the old baseline so the next
    /// restart's boot reconcile re-detects the roll — identical backstop
    /// reasoning to <see cref="ReconcileConnectSessionRoll"/>.
    /// </para>
    ///
    /// <para>
    /// On a <see cref="Up.ReconnectKind.Reattached"/> outcome the venue kept
    /// our working set (verId preserved); we simply mirror the SDK's verId and
    /// do no reconciliation. The conditional inbound-gap / peer-terminate
    /// staleness for that case is handled separately by
    /// <see cref="NotifyVenueDisconnectReactor"/>.
    /// </para>
    /// </summary>
    internal void ReconcileReconnectSessionRoll(Up.ReconnectKind kind, uint priorVerId, uint effectiveVerId)
    {
        var confirmedRoll = effectiveVerId > priorVerId;
        if (!confirmedRoll)
        {
            // Reattach (or a defensive no-advance Renegotiate): mirror the
            // SDK's verId. No ghosts — venue kept our set.
            Volatile.Write(ref _currentSessionVerId, effectiveVerId);
            MetricsRegistry.RecordSessionVerId(_firmId, effectiveVerId);
            return;
        }

        try
        {
            _connectSessionRollReactor?.OnSessionRolled(_firmId, priorVerId, effectiveVerId);
        }
        catch (Exception ex)
        {
            // A reactor failure must never abort the reconnect — the session
            // is established. Leave _currentSessionVerId at the old baseline
            // so the next-restart boot reconcile still re-detects the roll.
            _logger.LogError(ex,
                "Reconnect session-roll reactor failed for firm {Firm} (from={From} to={To}); leaving SessionVerId baseline at {Prior} to keep the restart backstop armed.",
                _firmId, priorVerId, effectiveVerId, priorVerId);
            return;
        }

        Volatile.Write(ref _currentSessionVerId, effectiveVerId);
        MetricsRegistry.RecordSessionVerId(_firmId, effectiveVerId);
    }

    private void OnConnected(bool startEventLoop = true)
    {
        if (Interlocked.Exchange(ref _connectedState, 1) == 0)
            MetricsRegistry.EntryPointConnected.Add(1, FirmTag());
        MetricsRegistry.RecordSessionVerId(_firmId, _currentSessionVerId);
        if (_connectedTestHook is not null)
        {
            _connectedTestHook();
            return;
        }
        if (startEventLoop)
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

        var req = BuildNewOrderRequest(order, ResolveStpInstruction(order), ResolveTradingSubAccount(order), ResolveVenueAccount(order), ResolveInvestorId(order), ResolveRoutingInstruction(order));

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
        => BuildNewOrderRequest(order, UpModels.SelfTradePreventionInstruction.None, tradingSubAccount: null, venueAccount: null, investorId: null, routingInstruction: null);

    internal static UpModels.NewOrderRequest BuildNewOrderRequest(
        Order order, UpModels.SelfTradePreventionInstruction stpInstruction)
        => BuildNewOrderRequest(order, stpInstruction, tradingSubAccount: null, venueAccount: null, investorId: null, routingInstruction: null);

    internal static UpModels.NewOrderRequest BuildNewOrderRequest(
        Order order,
        UpModels.SelfTradePreventionInstruction stpInstruction,
        uint? tradingSubAccount)
        => BuildNewOrderRequest(order, stpInstruction, tradingSubAccount, venueAccount: null, investorId: null, routingInstruction: null);

    internal static UpModels.NewOrderRequest BuildNewOrderRequest(
        Order order,
        UpModels.SelfTradePreventionInstruction stpInstruction,
        uint? tradingSubAccount,
        ulong? venueAccount)
        => BuildNewOrderRequest(order, stpInstruction, tradingSubAccount, venueAccount, investorId: null, routingInstruction: null);

    internal static UpModels.NewOrderRequest BuildNewOrderRequest(
        Order order,
        UpModels.SelfTradePreventionInstruction stpInstruction,
        uint? tradingSubAccount,
        ulong? venueAccount,
        InvestorIdentity? investorId)
        => BuildNewOrderRequest(order, stpInstruction, tradingSubAccount, venueAccount, investorId, routingInstruction: null);

    internal static UpModels.NewOrderRequest BuildNewOrderRequest(
        Order order,
        UpModels.SelfTradePreventionInstruction stpInstruction,
        uint? tradingSubAccount,
        ulong? venueAccount,
        InvestorIdentity? investorId,
        RoutingInstruction? routingInstruction)
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
            // #457. Native MinQty (FIX). When the trader sets a minimum
            // execution quantity, the venue must fill at least this many
            // contracts at submit time or reject the order. Domain.Order's
            // ctor already validated 0 < MinQty <= Quantity so the cast
            // to ulong? is safe.
            MinQty = order.MinQty is { } mq ? (ulong)mq : (ulong?)null,
            // #433 P1. Venue-side STP. Defense-in-depth pair with
            // SelfTradePreventionCheck — see SelfTradePreventionMode
            // doc comment for the rationale. The instruction value is
            // resolved by the caller (gateway SubmitAsync) so this
            // static stays pure.
            SelfTradePreventionInstruction = stpInstruction,
            // #471. Numeric wire id for the order's sub-account, when
            // the order carries one. Null = no sub-account on the
            // wire (SDK omits the field entirely) — matches the
            // master-bucket semantics of Order.SubAccountId == null.
            TradingSubAccount = tradingSubAccount,
            // #458. CBLC Account number for clearing/allocation, when
            // an operator has wired a real IVenueAccountResolver. Null
            // = no mapping known → wire field stays omitted and the
            // broker's out-of-band post-trade allocation continues to
            // apply (pre-#458 wire behavior).
            Account = venueAccount,
            // #472. Opaque InvestorId (Prefix, Document) when an
            // operator has wired a real IInvestorIdResolver. Null =
            // wire field omitted (pre-#472 behavior). Translated from
            // the domain InvestorIdentity record to the SDK struct at
            // this boundary so Application/Domain stay SDK-free.
            InvestorId = investorId is { } id
                ? new UpModels.InvestorId { Prefix = id.Prefix, Document = id.Document }
                : (UpModels.InvestorId?)null,
            // #473. Routing instruction stamped when an operator has
            // wired a real IRoutingInstructionResolver AND the
            // resolved scope's RiskLimits.AllowedRoutingInstructions
            // permits the value (gating happens upstream in
            // RoutingInstructionAllowedCheck). Null = wire field
            // omitted (pre-#473 behavior).
            RoutingInstruction = MapRoutingInstructionToWire(routingInstruction),
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

        var req = BuildReplaceOrderRequest(original, newClOrdId, newQuantity, newPrice,
            requestedTimeInForce, requestedStopPrice, requestedGoodTillDate,
            ResolveStpInstruction(original), ResolveTradingSubAccount(original),
            ResolveVenueAccount(original), ResolveInvestorId(original),
            ResolveRoutingInstruction(original));

        return SendAsync(newClOrdId, OrderEntryLatencyProbe.OpReplace,
            ct => _client.ReplaceAsync(req, ct), cancellationToken);
    }

    /// <summary>
    /// #457. Extracted from <see cref="CancelReplaceAsync"/> so the outbound
    /// <c>ReplaceOrderRequest</c> mapping (including the FIX MinQty / MaxFloor
    /// inheritance + clamp) can be pinned by unit tests without standing up a
    /// real <c>EntryPointClient</c>. Pure function: every field is read from
    /// the original domain <see cref="Order"/> and the modify-call arguments,
    /// with no side effects.
    /// </summary>
    internal static UpModels.ReplaceOrderRequest BuildReplaceOrderRequest(
        Order original,
        ulong newClOrdId,
        long newQuantity,
        decimal? newPrice,
        TimeInForce? requestedTimeInForce,
        decimal? requestedStopPrice,
        DateTimeOffset? requestedGoodTillDate,
        UpModels.SelfTradePreventionInstruction selfTradePreventionInstruction = UpModels.SelfTradePreventionInstruction.None,
        uint? tradingSubAccount = null,
        ulong? account = null,
        InvestorIdentity? investorId = null,
        RoutingInstruction? routingInstruction = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        var (effTif, effStop, effGtd) = Order.MergeReplacementOptionals(
            original.Type, original.TimeInForce, original.StopPrice, original.GoodTillDate,
            requestedTimeInForce, requestedStopPrice, requestedGoodTillDate);

        return new UpModels.ReplaceOrderRequest
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
            // #457. Replace inherits the original's MinQty (clamped to
            // newQuantity if the new order qty would otherwise be below
            // MinQty), mirroring the DisplayQty handling above and
            // Order.HydrateReplacement. A future modify-pipeline slice
            // can plumb explicit MinQty overrides.
            MinQty = original.MinQty is { } omq
                ? (ulong)Math.Min(omq, newQuantity)
                : (ulong?)null,
            // #433 P1. Venue-side STP carried on the replace too —
            // a modified order is a fresh book entry from the
            // matching engine's perspective so the instruction must
            // re-accompany it. Resolution scope is the original
            // order's (owner, firm, symbol) — modify never changes
            // those three.
            SelfTradePreventionInstruction = selfTradePreventionInstruction,
            // #471. Same rationale as STP above: from the matching
            // engine's perspective a replace is a fresh order, so the
            // TradingSubAccount wire id must re-accompany it. The
            // domain sub-account on the original Order is preserved
            // through every modify path (HydrateReplacement copies it
            // verbatim), so resolving from `original` is correct.
            TradingSubAccount = tradingSubAccount,
            // #458. Same rationale: replace is a fresh book entry; the
            // CBLC Account must re-accompany it. Resolving from
            // `original` is correct because owner/firm/sub-account
            // (the typical inputs to the resolver) are immutable
            // across a modify.
            Account = account,
            // #472. Same rationale: replace is a fresh book entry;
            // the InvestorId must re-accompany it. Resolving from
            // `original` is correct because owner/firm (the typical
            // inputs to the resolver) are immutable across a modify.
            InvestorId = MapInvestorIdToWire(investorId),
            // #473. Same rationale: replace is a fresh book entry,
            // routing intent must re-accompany it. Resolved off the
            // original since owner/firm (the typical inputs) are
            // immutable across modify; pre-trade whitelist gating
            // was already applied by OrderModifyService.
            RoutingInstruction = MapRoutingInstructionToWire(routingInstruction),
        };
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

    /// <summary>
    /// #471. Resolve the numeric <c>TradingSubAccount</c> stamped on
    /// an outbound <c>NewOrderRequest</c> / <c>ReplaceOrderRequest</c>.
    /// Returns <c>null</c> when no <see cref="ISubAccountWireIdMapper"/>
    /// is wired (legacy gateways that don't care about the field stay
    /// green) or when the order itself has no sub-account (master
    /// bucket — wire field stays omitted). The production composition
    /// root always wires <see cref="DeterministicSubAccountWireIdMapper"/>.
    /// </summary>
    private uint? ResolveTradingSubAccount(Order order)
    {
        if (_subAccountWireIdMapper is null) return null;
        return _subAccountWireIdMapper.TryMap(order.FirmId, order.SubAccountId);
    }

    /// <summary>
    /// #458. Resolve the CBLC <c>Account</c> stamped on an outbound
    /// <c>NewOrderRequest</c> / <c>ReplaceOrderRequest</c>. Returns
    /// <c>null</c> when no <see cref="IVenueAccountResolver"/> is
    /// wired (legacy gateways stay green) or when the wired resolver
    /// has no mapping for the order — in both cases the wire field
    /// stays omitted and post-trade allocation continues via the
    /// broker's out-of-band matching.
    /// </summary>
    private ulong? ResolveVenueAccount(Order order)
    {
        if (_venueAccountResolver is null) return null;
        return _venueAccountResolver.TryResolve(order);
    }

    /// <summary>
    /// #472. Resolve the opaque <see cref="InvestorIdentity"/>
    /// stamped on an outbound <c>NewOrderRequest</c> /
    /// <c>ReplaceOrderRequest</c>. Returns <c>null</c> when no
    /// <see cref="IInvestorIdResolver"/> is wired (legacy gateways
    /// stay green) or when the wired resolver has no mapping for the
    /// order — in both cases the wire field stays omitted.
    /// </summary>
    private InvestorIdentity? ResolveInvestorId(Order order)
    {
        if (_investorIdResolver is null) return null;
        return _investorIdResolver.TryResolve(order);
    }

    /// <summary>
    /// Translate the domain <see cref="InvestorIdentity"/> to the SDK
    /// wire struct. Kept private so the SDK type never leaks past the
    /// gateway boundary.
    /// </summary>
    private static UpModels.InvestorId? MapInvestorIdToWire(InvestorIdentity? id)
        => id is { } v
            ? new UpModels.InvestorId { Prefix = v.Prefix, Document = v.Document }
            : (UpModels.InvestorId?)null;

    /// <summary>
    /// #473. Resolve the <see cref="RoutingInstruction"/> stamped on
    /// an outbound <c>NewOrderRequest</c> / <c>ReplaceOrderRequest</c>.
    /// Returns <c>null</c> when no
    /// <see cref="IRoutingInstructionResolver"/> is wired (legacy
    /// gateways stay green) or when the wired resolver has no
    /// instruction for the order. Pre-trade whitelist gating is the
    /// caller's responsibility — the gateway trusts what the resolver
    /// returns here.
    /// </summary>
    private RoutingInstruction? ResolveRoutingInstruction(Order order)
    {
        if (_routingInstructionResolver is null) return null;
        return _routingInstructionResolver.TryResolve(order);
    }

    /// <summary>
    /// Translate domain <see cref="RoutingInstruction"/> → SDK wire
    /// enum. Kept private so the SDK type never leaks past the
    /// gateway boundary.
    /// </summary>
    private static UpModels.RoutingInstruction? MapRoutingInstructionToWire(RoutingInstruction? value)
        => value switch
        {
            RoutingInstruction.RetailLiquidityTaker => UpModels.RoutingInstruction.RetailLiquidityTaker,
            RoutingInstruction.WaivedPriority => UpModels.RoutingInstruction.WaivedPriority,
            RoutingInstruction.BrokerOnly => UpModels.RoutingInstruction.BrokerOnly,
            RoutingInstruction.BrokerOnlyRemoval => UpModels.RoutingInstruction.BrokerOnlyRemoval,
            null => null,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown RoutingInstruction"),
        };

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
        var recover = true;
        try
        {
            var events = _eventStreamOverride is not null
                ? _eventStreamOverride(ct)
                : _client.Events(ct);
            await foreach (var ev in events.ConfigureAwait(false))
            {
                MetricsRegistry.EntryPointEventsReceived.Add(1,
                    new KeyValuePair<string, object?>("firm", _firmId),
                    new KeyValuePair<string, object?>("event_type", ev.GetType().Name));

                // Defensive gap detection on top of the SDK's own
                // IRetransmitRequestHandler. The SDK normally hides gaps via
                // automatic retransmit; if a gap nevertheless surfaces here,
                // the metric flags it for ops and the ER processor's
                // idempotency (#16) makes any subsequent replay safe.
                var gapObservation = FixpGapDetector.Observe(ev.SeqNum, ref _lastInboundSeqNum);
                switch (gapObservation)
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

                // BusinessReject lacks the ER processor's ClOrdID-keyed
                // idempotency path, so a retransmitted duplicate must stop here
                // rather than being re-counted / re-routed downstream.
                if (gapObservation == GapObservation.Duplicate && ev is UpModels.BusinessReject)
                    continue;

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

                // #432 — BusinessReject has no ClOrdID anchor and therefore
                // no ExecutionReportEnvelope, but it must NOT be silently
                // discarded: it indicates a structural rejection (malformed
                // payload / unknown SecurityID / outside trading hours) for
                // which the order request never produced an ER. Route via
                // its own event channel + metric so the downstream router
                // can persist it to the WAL for operator audit.
                if (ev is UpModels.BusinessReject br)
                {
                    InvokeBusinessRejectSubscribers(
                        BusinessRejectReceived,
                        new BusinessRejectEnvelope(
                            FirmId: _firmId,
                            RefSeqNum: br.RefSeqNum,
                            RejectReason: br.RejectReason,
                            Text: br.Text,
                            SeqNum: br.SeqNum,
                            SendingTime: br.SendingTime),
                        _firmId,
                        _logger);
                    continue;
                }

                if (envelope is null)
                    continue;

                // Record latency before subscriber fan-out so a misbehaving
                // subscriber can't lose the sample.
                _latencyProbe.OnExecutionReport(envelope.ClOrdId);

                InvokeExecutionReportSubscribers(
                    ExecutionReportReceived, envelope, _firmId, _logger);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
            recover = false;
        }
        catch (Exception ex) when (ex is WalBackpressureException or WalFaultedException)
        {
            recover = false;
            _logger.LogCritical(ex,
                "EntryPoint event loop for firm {Firm} stopped because an inbound event could not be persisted; order ingress is draining.",
                _firmId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EntryPoint event loop for firm {Firm} terminated unexpectedly.", _firmId);
        }
        finally
        {
            if (recover && !_disposed && !_shutdownCts.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "EntryPoint event loop for firm {Firm} stopped while the gateway was expected to be operational; scheduling recovery.",
                    _firmId);
                MarkDisconnected();
                ScheduleReconnect();
            }
        }
    }

    internal static void InvokeExecutionReportSubscribers(
        Action<ExecutionReportEnvelope>? subscribers,
        ExecutionReportEnvelope envelope,
        string firmId,
        ILogger<B3EntryPointClientGateway> logger)
    {
        try
        {
            subscribers?.Invoke(envelope);
        }
        catch (Exception ex) when (ex is not WalBackpressureException and not WalFaultedException)
        {
            logger.LogError(ex, "ER subscriber threw for firm {Firm}; continuing.", firmId);
        }
    }

    internal static void InvokeBusinessRejectSubscribers(
        Action<BusinessRejectEnvelope>? subscribers,
        BusinessRejectEnvelope envelope,
        string firmId,
        ILogger<B3EntryPointClientGateway> logger)
    {
        try
        {
            subscribers?.Invoke(envelope);
        }
        catch (Exception ex) when (ex is not WalBackpressureException and not WalFaultedException)
        {
            logger.LogError(ex, "BusinessReject subscriber threw for firm {Firm}; continuing.", firmId);
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

        UpModels.BusinessReject br => null, // surfaced on the dedicated BusinessReject channel
        _ => null,
    };

    private void OnTerminated(object? sender, Up.TerminatedEventArgs e)
    {
        MetricsRegistry.EntryPointTerminated.Add(1,
            new KeyValuePair<string, object?>("firm", _firmId),
            new KeyValuePair<string, object?>("code", e.Code.ToString()),
            new KeyValuePair<string, object?>("initiated_by_client", e.InitiatedByClient));
        MarkDisconnected();
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
        ScheduleReconnect();
    }

    private void MarkDisconnected()
    {
        if (Interlocked.Exchange(ref _connectedState, 0) == 1)
            MetricsRegistry.EntryPointConnected.Add(-1, FirmTag());
    }

    private void ScheduleReconnect()
    {
        if (_disposed || _shutdownCts.IsCancellationRequested)
            return;
        if (Interlocked.CompareExchange(ref _reconnectScheduled, 1, 0) != 0)
            return;

        _reconnectLoop = Task.Run(async () =>
        {
            try
            {
                await ReconnectLoopAsync(_shutdownCts.Token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectScheduled, 0);
            }

            if (Volatile.Read(ref _connectedState) == 1
                && _connectedTestHook is null
                && !_disposed
                && !_shutdownCts.IsCancellationRequested)
                StartEventLoop();
        });
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
    /// guards the retry; exits cleanly on shutdown CT. Immediately before
    /// each <c>ReconnectAsync</c> attempt, <see cref="_reResolveEndpoint"/>
    /// (if wired) re-resolves the peer hostname so a matching-side pod IP
    /// change (Kubernetes failover/reschedule) is picked up without a
    /// trading-host restart (#565). On success the
    /// inbound event-loop is restarted via <see cref="StartEventLoop"/>
    /// so ER replay (FIXP retransmit) lands on the existing
    /// <c>ExecutionReportReceived</c> subscribers.
    /// </summary>
    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
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
                    // #565. Re-resolve the peer hostname immediately before
                    // dialing so a matching-side pod IP change (Kubernetes
                    // failover/reschedule) is picked up on THIS attempt
                    // rather than requiring a trading-host restart. Resolver
                    // failures/timeouts reuse the last-known endpoint after
                    // the first successful resolution. Before that first
                    // resolution they propagate into this retry/backoff loop.
                    if (_reResolveEndpoint is not null)
                        await _reResolveEndpoint(ct).ConfigureAwait(false);

                    // Selector is invoked at most once per ReconnectAsync —
                    // only when the SDK falls back to Negotiate after a
                    // recoverable Establish-reject. Reattach path skips it
                    // entirely, preserving the SessionVerID.
                    var outcome = await ReconnectAsyncCore(
                        Up.ReconnectMode.EstablishReuseThenNegotiate,
                        BumpSessionVerIdSelector,
                        ct).ConfigureAwait(false);
                    MetricsRegistry.EntryPointReconnectSucceeded.Add(1,
                        new KeyValuePair<string, object?>("firm", _firmId),
                        new KeyValuePair<string, object?>("kind", ReconnectKindTag(outcome.Kind)));
                    _logger.LogInformation(
                        "EntryPoint reconnect ok for firm {Firm} on attempt {N} (kind={Kind} sessionVerId={Ver} priorVerId={Prior} retransmitWindowReady={RetransmitReady}).",
                        _firmId, attempt, outcome.Kind, outcome.SessionVerId, priorVerId, outcome.RetransmitWindowReady);
                    // Reconcile the session-version transition BEFORE restarting
                    // the event loop (so the reap/stale lands ahead of ER replay)
                    // and, on a confirmed roll, BEFORE publishing the bumped
                    // verId (so a snapshot can't seal the new baseline with
                    // ghosts still present — same backstop reasoning as the
                    // connect path).
                    ReconcileReconnectSessionRoll(outcome.Kind, priorVerId, outcome.SessionVerId);
                    OnConnected(startEventLoop: false);
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
                    // Keep our published baseline pinned to the last CONFIRMED
                    // venue sessionVerId until a reconnect actually succeeds.
                    // The SDK mutates its own local SessionVerId while probing
                    // reuse→negotiate fallbacks, but a failed attempt does not
                    // prove the venue accepted that value. Advancing our mirror
                    // here can make the eventual Renegotiated success look like
                    // prior==effective and skip stale-on-session-roll
                    // reconciliation (#516). The SDK retains whatever local
                    // retry state it needs internally; _currentSessionVerId is
                    // only advanced on confirmed success paths.
                    var delay = ComputeBackoff(attempt);
                    try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectingState, 0);
            _connectionLock.Release();
        }
    }

    internal Task ReconnectLoopForTestsAsync(CancellationToken ct)
        => ReconnectLoopAsync(ct);

    private Task<Up.ReconnectOutcome> ReconnectAsyncCore(
        Up.ReconnectMode mode,
        Func<uint, uint> selector,
        CancellationToken ct)
        => _reconnectAsyncOverride is not null
            ? _reconnectAsyncOverride(mode, selector, ct)
            : _client.ReconnectAsync(mode, selector, ct);

    /// <summary>
    /// SessionVerID selector for the SDK v0.14.4 reconnect API. Invoked
    /// only on the renegotiate fallback (Reattach skips it). Returns
    /// <c>prior + 1</c>; an <see cref="OverflowException"/> escapes back
    /// to <see cref="ReconnectLoopAsync"/> which short-circuits the loop.
    /// </summary>
    private static uint BumpSessionVerIdSelector(uint prior) => checked(prior + 1);

    private bool TryReadEffectiveSessionVerId(string phase, out uint effective)
    {
        effective = 0;
        if (_effectiveSessionVerIdProvider is null)
            return false;

        try
        {
            effective = _effectiveSessionVerIdProvider();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read effective SessionVerId after {Phase} for firm {Firm}; leaving current baseline unchanged.",
                phase, _firmId);
            return false;
        }
    }

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
        if (_reconnectLoop is not null)
        {
            try { await _reconnectLoop.ConfigureAwait(false); } catch { /* reconnect loop handles failures */ }
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
        // #512 cold-start resume: when the gateway is configured to SUSPEND on
        // shutdown (terminateOnShutdown=false), we deliberately send NO
        // Terminate(Finished). A Terminate(Finished) tells the venue the
        // session is permanently finished and evicts session-scoped working
        // order ownership — exactly the bug behind #507/#512. Suspending (clean
        // TCP close, no Terminate) lets the next boot reattach via Establish-
        // reuse and keep its resting orders. The SDK's own DisposeAsync Terminate
        // is independently gated by EntryPointClientOptions.TerminateOnDispose.
        if (_terminateOnShutdown && Volatile.Read(ref _connectedState) == 1)
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
        _connectionLock.Dispose();
        MarkDisconnected();
    }

    // BusinessReject, also exposed for instrumentation hookup if a future
    // correlation map is added (RefSeqNum → ClOrdID).
    internal static void RecordBusinessReject(string firmId, UpModels.BusinessReject br) =>
        RecordBusinessReject(firmId, br.RejectReason);

    internal static void RecordBusinessReject(string firmId, int rejectReason)
    {
        MetricsRegistry.EntryPointBusinessRejects.Add(1,
            new KeyValuePair<string, object?>("firm", firmId),
            new KeyValuePair<string, object?>("reason", rejectReason));
    }
}
