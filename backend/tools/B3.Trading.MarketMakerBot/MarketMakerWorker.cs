using B3.EntryPoint.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Threading.Channels;
using B3.EntryPoint.Client.Fixp;
using UpModels = B3.EntryPoint.Client.Models;
using UpState = B3.EntryPoint.Client.State;

namespace B3.Trading.MarketMakerBot;

/// <summary>
/// The bot's main loop. Single FIXP session against matching-platform,
/// behaving as a co-located two-sided market maker: on connect it
/// submits one resting bid + one resting ask per configured
/// instrument, then re-quotes IMMEDIATELY whenever a side's order
/// terminates (fill, cancel, or reject) — driven by the FIXP event
/// stream (<see cref="ReceiveLoopAsync"/>), not by a polling tick.
/// A low-frequency <see cref="ReconcileLoopAsync"/> is a defensive
/// safety net only (catches missed events / post-reconnect gaps); it is
/// NOT the primary quoting path.
/// </summary>
internal sealed class MarketMakerWorker : BackgroundService
{
    private readonly MarketMakerBotOptions _options;
    private readonly OrderTracker _tracker;
    private readonly MarketPriceTracker _priceTracker;
    private readonly VolatilitySpreadEstimator _volatilitySpread;
    private readonly MarketMakerPnlLedger _pnlLedger;
    private readonly MarketMakerOrderLifecycle _orderLifecycle;
    private readonly MarketMakerMetrics _metrics;
    private readonly MarketDataFeed _marketData;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MarketMakerWorker> _log;
    private readonly TimeProvider _clock;
    private long _nextClOrdId;
    private IEntryPointClient? _client;
    // Pricing-context changes are coalesced per configured symbol. The
    // bounded channel can contain at most one entry per symbol, while the
    // reason map carries attribution for the eventual cancel attempt.
    private readonly Channel<string> _pricingContextSignals;
    private readonly ConcurrentDictionary<string, CancelReason> _pendingPricingContextSignals =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancelReason> _dirtyPricingContextSignals =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pricingContextFailureRetries =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ulong, StartupCleanupAwaiter>
        _massActionReports = new();
    private readonly HashSet<string> _configuredSymbols;
    private readonly ConcurrentDictionary<string, FeedAvailabilityObservation> _feedAvailability =
        new(StringComparer.Ordinal);

    public MarketMakerWorker(IOptions<MarketMakerBotOptions> options, OrderTracker tracker,
        MarketPriceTracker priceTracker, VolatilitySpreadEstimator volatilitySpread,
        MarketMakerPnlLedger pnlLedger, MarketMakerMetrics metrics,
        MarketDataFeed marketData, ILoggerFactory loggerFactory, ILogger<MarketMakerWorker> log,
        TimeProvider? clock = null)
    {
        _options = options.Value;
        _tracker = tracker;
        _priceTracker = priceTracker;
        _volatilitySpread = volatilitySpread;
        _pnlLedger = pnlLedger;
        _orderLifecycle = new MarketMakerOrderLifecycle(tracker, pnlLedger);
        _metrics = metrics;
        _marketData = marketData;
        _loggerFactory = loggerFactory;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _configuredSymbols = new HashSet<string>(
            _options.Instruments.Select(instrument => instrument.Symbol),
            StringComparer.Ordinal);
        _pricingContextSignals = Channel.CreateBounded<string>(new BoundedChannelOptions(
            Math.Max(1, _configuredSymbols.Count))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        // Time-of-day high bits + monotonic low bits give unique ClOrdIDs
        // across restarts within the same SessionVerId. The SDK's
        // FileSessionStateStore handles SessionVerId itself, but ClOrdID
        // uniqueness is ours to defend.
        _nextClOrdId = (long)(((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) << 20);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_options.StateDirectory);
        var stateStore = new UpState.FileSessionStateStore(_options.StateDirectory);
        uint? persisted = null;
        try
        {
            var snap = await stateStore.LoadAsync(stoppingToken);
            if (snap is not null) persisted = snap.SessionVerId;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mm] failed to load persisted SessionState; falling back to configured SessionVerId.");
        }
        var resolvedVerId = persisted is { } p
            ? (_options.SessionVerId > checked(p + 1) ? _options.SessionVerId : checked(p + 1))
            : _options.SessionVerId;

        var ep = EndpointParser.Parse(_options.Endpoint);
        var addrs = System.Net.Dns.GetHostAddresses(ep.Host);
        if (addrs.Length == 0)
            throw new InvalidOperationException($"Could not resolve bot endpoint host '{ep.Host}'.");
        var ipEndpoint = new System.Net.IPEndPoint(addrs[0], ep.Port);
        var clientOpts = new EntryPointClientOptions
        {
            Endpoint = ipEndpoint,
            SessionId = _options.SessionId,
            SessionVerId = resolvedVerId,
            EnteringFirm = _options.EnteringFirm,
            Credentials = EntryPointClientOptions.AccessKey(_options.AccessKey),
            SenderLocation = _options.SenderLocation,
            EnteringTrader = _options.EnteringTrader,
            SessionStateStore = stateStore,
            Logger = _log,
            // RFC #703: the bot never explicitly cancels its own resting
            // orders — it relies entirely on this session attribute to
            // keep the venue's book from accumulating orphaned orders
            // across an abrupt disconnect (crash, pod restart, network
            // blip) or a graceful shutdown/terminate.
            // CancelOnDisconnectType is marked evaluation-only (B3EP_COD)
            // in SDK 0.17.0; deliberately opting in here as it's the only
            // available server-enforced backstop pending stabilization.
#pragma warning disable B3EP_COD
            CancelOnDisconnect = CancelOnDisconnectType.CancelOnDisconnectOrTerminate,
#pragma warning restore B3EP_COD
        };

        _client = new EntryPointClient(clientOpts);
        _marketData.BookOrderChanged += OnBookOrderChanged;
        _marketData.SymbolAvailabilityChanged += OnSymbolAvailabilityChanged;
        _marketData.VolatilitySpreadChanged += OnVolatilitySpreadChanged;
        _marketData.ConnectionEligibilityChanged += OnMarketDataConnectionEligibilityChanged;
        try
        {
            _log.LogInformation("[mm] connecting to {Endpoint} session={Session} verId={VerId}",
                _options.Endpoint, _options.SessionId, resolvedVerId);
            await _client.ConnectAsync(stoppingToken);
            _log.LogInformation("[mm] connected; instruments={Count} reconcile={Interval}",
                _options.Instruments.Count, _options.ReconcileInterval);

            var receive = await PrepareConnectedSessionAsync(_client, stoppingToken);
            var reconcile = ReconcileLoopAsync(_client, stoppingToken);
            var pricingReaction = PricingContextReactionLoopAsync(_client, stoppingToken);
            await Task.WhenAny(receive, reconcile, pricingReaction);
            // Surface the failing task's exception (if any).
            await Task.WhenAll(receive, reconcile, pricingReaction);
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "[mm] fatal error in main loop");
            throw;
        }
        finally
        {
            _marketData.BookOrderChanged -= OnBookOrderChanged;
            _marketData.SymbolAvailabilityChanged -= OnSymbolAvailabilityChanged;
            _marketData.VolatilitySpreadChanged -= OnVolatilitySpreadChanged;
            _marketData.ConnectionEligibilityChanged -= OnMarketDataConnectionEligibilityChanged;
            _pricingContextSignals.Writer.TryComplete();
            try { await _client.DisposeAsync(); } catch { /* ignore */ }
            await _marketData.DisposeAsync();
        }
    }

    internal async Task<Task> PrepareConnectedSessionAsync(IEntryPointClient client, CancellationToken ct)
    {
        // Start draining the FIXP event stream before requesting cleanup. A
        // legacy session can produce up to the venue's 100k cancellation ER
        // cap, and the authoritative mass-action report itself arrives here.
        var receive = ReceiveLoopAsync(client, ct);

        await CleanupLegacySessionOrdersAsync(client, receive, ct);
        await Task.Yield();
        await ThrowIfReceiveLoopStoppedAsync(receive);

        // Market data and all normal quoting deliberately start only after
        // the venue has accepted the session-scoped cleanup.
        await AwaitWhileReceiveLoopActiveAsync(
            operationToken => _marketData.StartAsync(
                _options.MarketData,
                _options.Instruments,
                _loggerFactory,
                operationToken),
            receive,
            ct);

        foreach (var instr in _options.Instruments)
        {
            await AwaitWhileReceiveLoopActiveAsync(
                operationToken => QuoteSideAsync(client, instr, isBuy: true, operationToken),
                receive,
                ct);
            await AwaitWhileReceiveLoopActiveAsync(
                operationToken => QuoteSideAsync(client, instr, isBuy: false, operationToken),
                receive,
                ct);
        }

        await ThrowIfReceiveLoopStoppedAsync(receive);
        return receive;
    }

    private async Task CleanupLegacySessionOrdersAsync(
        IEntryPointClient client,
        Task receiveLoop,
        CancellationToken ct)
    {
        var keepAlive = client.KeepAlive
            ?? throw new InvalidOperationException(
                "FIXP keep-alive scheduler is unavailable after session establishment.");
        var outboundSeqNum = await AwaitNextOutboundSequenceAsync(keepAlive, receiveLoop, ct);
        var clOrdId = (ulong)Interlocked.Increment(ref _nextClOrdId);
        var request = new UpModels.MassActionRequest
        {
            ClOrdID = new UpModels.ClOrdID(clOrdId),
            ActionType = UpModels.MassActionType.CancelOrders,
            Scope = UpModels.MassActionScope.AllOrdersForATradingSession,
        };
        var cleanupAwaiter = new StartupCleanupAwaiter(outboundSeqNum);
        if (!_massActionReports.TryAdd(clOrdId, cleanupAwaiter))
            throw new InvalidOperationException($"Duplicate startup cleanup ClOrdID {clOrdId}.");
        EventHandler<SequenceFrameEventArgs> peerSequenceHandler =
            (_, args) => cleanupAwaiter.ObservePeerSequence(args.NextSeqNo);
        keepAlive.SequenceFrameReceived += peerSequenceHandler;

        try
        {
            _log.LogInformation(
                "[mm] cancelling legacy orders for FIXP session before starting market data or quoting clordid={ClOrdId} outboundSeqNum={OutboundSeqNum}",
                clOrdId,
                outboundSeqNum);

            // SDK 0.17.0 returns a transport-level synthetic MassActionReport,
            // while the real venue report is published as MassActionExecuted.
            // Matching writes ACCEPTED before attempting EnqueueMassCancel and
            // follows it with BusinessReject(SystemBusy) on enqueue/WAL failure.
            // Require a sequence fence beyond ACCEPTED so that follow-up cannot
            // race normal quoting.
            var requestTask = client.MassActionAsync(request, ct);
            var cleanup = CompleteCleanupAsync(requestTask, cleanupAwaiter.Completion);
            var timeout = Task.Delay(_options.StartupCleanupTimeout, _clock, ct);
            await Task.WhenAny(cleanup, receiveLoop, timeout);

            if (receiveLoop.IsCompleted)
            {
                await ThrowIfReceiveLoopStoppedAsync(receiveLoop);
            }

            if (cleanup.IsCompleted)
            {
                var report = await cleanup;
                await ThrowIfReceiveLoopStoppedAsync(receiveLoop);
                _log.LogInformation(
                    "[mm] startup legacy-order cleanup completed reportId={ReportId} clordid={ClOrdId}",
                    report.MassActionReportId,
                    clOrdId);
                return;
            }

            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"Startup mass-action completion fence did not arrive within {_options.StartupCleanupTimeout}.");
        }
        finally
        {
            keepAlive.SequenceFrameReceived -= peerSequenceHandler;
            _massActionReports.TryRemove(clOrdId, out _);
        }

        static async Task<UpModels.MassActionExecuted> CompleteCleanupAsync(
            Task<UpModels.MassActionReport> requestTask,
            Task<UpModels.MassActionExecuted> reportTask)
        {
            var sendResult = await requestTask;
            if (sendResult.Response != UpModels.MassActionResponse.Accepted)
            {
                throw new InvalidOperationException(
                    $"Startup mass action was rejected: {sendResult.RejectReason?.ToString() ?? sendResult.Reason ?? "unknown"}.");
            }

            var report = await reportTask;
            if (report.Response != UpModels.MassActionResponse.Accepted)
            {
                throw new InvalidOperationException(
                    $"Startup mass action was rejected: {report.RejectReason?.ToString() ?? "unknown"}.");
            }

            return report;
        }
    }

    private async Task<ulong> AwaitNextOutboundSequenceAsync(
        IKeepAliveScheduler keepAlive,
        Task receiveLoop,
        CancellationToken ct)
    {
        var source = new TaskCompletionSource<ulong>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<SequenceFrameEventArgs> handler =
            (_, args) => source.TrySetResult(args.NextSeqNo);
        keepAlive.SequenceFrameSent += handler;
        try
        {
            var timeout = Task.Delay(_options.CancelAckTimeout, _clock, ct);
            await Task.WhenAny(source.Task, receiveLoop, timeout);
            if (receiveLoop.IsCompleted)
                await ThrowIfReceiveLoopStoppedAsync(receiveLoop);
            if (source.Task.IsCompleted)
                return await source.Task;
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"FIXP outbound sequence fence did not arrive within {_options.CancelAckTimeout}.");
        }
        finally
        {
            keepAlive.SequenceFrameSent -= handler;
        }
    }

    private static async Task AwaitWhileReceiveLoopActiveAsync(
        Func<CancellationToken, Task> operationFactory,
        Task receiveLoop,
        CancellationToken ct)
    {
        await Task.Yield();
        await ThrowIfReceiveLoopStoppedAsync(receiveLoop);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var operation = operationFactory(operationCts.Token);
        await Task.WhenAny(operation, receiveLoop);
        if (receiveLoop.IsCompleted)
        {
            operationCts.Cancel();
            try { await operation; }
            catch (OperationCanceledException) when (operationCts.IsCancellationRequested) { }
            await ThrowIfReceiveLoopStoppedAsync(receiveLoop);
        }
        await operation;
        await ThrowIfReceiveLoopStoppedAsync(receiveLoop);
    }

    private static async Task ThrowIfReceiveLoopStoppedAsync(Task receiveLoop)
    {
        if (!receiveLoop.IsCompleted) return;
        await receiveLoop;
        throw new InvalidOperationException("FIXP event stream ended during market-maker startup.");
    }

    private async Task ReceiveLoopAsync(IEntryPointClient client, CancellationToken ct)
    {
        await using var events = client.Events(ct).GetAsyncEnumerator(ct);
        ulong lastProcessedSeqNum = 0;
        while (true)
        {
            var moveNext = events.MoveNextAsync().AsTask();
            if (!moveNext.IsCompleted)
            {
                // Give an immediately-following stream completion/fault a
                // chance to settle before treating this pending MoveNext as
                // proof that the receive loop remains live past the event.
                await Task.Yield();
                if (!moveNext.IsCompleted)
                {
                    foreach (var pendingCleanup in _massActionReports.Values)
                        pendingCleanup.ObserveReceivePoll(lastProcessedSeqNum);
                }
            }
            if (!await moveNext.ConfigureAwait(false))
                return;

            var ev = events.Current;
            try
            {
                await HandleEventAsync(client, ev, ct);
                lastProcessedSeqNum = ev.SeqNum;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "[mm] failed to handle event {Event}", ev.GetType().Name);
            }
        }
    }

    /// <summary>
    /// internal (not private) + <c>IEntryPointClient</c> parameter: this
    /// is the seam <c>MarketMakerWorkerTests</c> drives directly with a
    /// fake client to deterministically exercise the accept/fill/reject
    /// event interplay — see #709. It's how the #707 duplicate-order
    /// regression (a null LeavesQty on OrderAccepted misread as a fill)
    /// is now covered by a fast unit test instead of only a live Docker
    /// soak test.
    /// </summary>
    internal async Task HandleEventAsync(IEntryPointClient client, UpModels.EntryPointEvent ev, CancellationToken ct)
    {
        switch (ev)
        {
            case UpModels.MassActionExecuted massAction:
                if (_massActionReports.TryGetValue(massAction.ClOrdID.Value, out var cleanupAwaiter))
                    cleanupAwaiter.ObserveMassActionReport(massAction);
                break;
            case UpModels.BusinessReject businessReject:
                foreach (var pendingCleanup in _massActionReports.Values)
                    pendingCleanup.ObserveBusinessReject(businessReject);
                break;
            case UpModels.OrderAccepted a:
                // LeavesQty is not guaranteed to be populated on this ER
                // shape — confirmed empirically the venue omits it
                // entirely on the New ack — so it must never be defaulted
                // to zero via `??` (see OrderTracker.OnAccepted's doc
                // comment for the duplicate-order bug this caused).
                _orderLifecycle.Synchronize(() =>
                {
                    _tracker.SetOrderId(a.ClOrdID.Value, a.OrderId);
                    _tracker.OnAccepted(a.ClOrdID.Value, a.LeavesQty is { } aLeaves ? (long)aLeaves : null);
                });
                break;
            case UpModels.OrderTrade t:
                {
                    var isFilled = t.OrderStatus == UpModels.OrderStatus.Filled;
                    var transition = _orderLifecycle.Synchronize(() =>
                    {
                        var known = _tracker.TryGet(t.ClOrdID.Value, out var order);
                        FillApplyResult? fillResult = null;
                        if (known)
                        {
                            _tracker.SetOrderId(t.ClOrdID.Value, t.OrderId);
                            fillResult = _pnlLedger.Apply(new OwnFill(
                                t.ClOrdID.Value,
                                t.TradeId,
                                order.Symbol,
                                order.IsBuy,
                                order.Quantity,
                                t.LastPx,
                                t.LastQty,
                                t.CumQty,
                                t.LeavesQty,
                                isFilled,
                                t.OrderStatus is UpModels.OrderStatus.PartiallyFilled or UpModels.OrderStatus.Filled));
                        }

                        _tracker.OnTrade(
                            t.ClOrdID.Value,
                            isFilled,
                            t.LeavesQty is { } tLeaves ? (long)tLeaves : null);
                        if (isFilled)
                            _pnlLedger.MarkTerminal(t.ClOrdID.Value);
                        return (
                            Known: known,
                            Symbol: known ? order.Symbol : null,
                            IsBuy: known && order.IsBuy,
                            FillResult: fillResult);
                    });

                    _metrics.RecordFillReceived(transition.Symbol);
                    if (transition.FillResult is { } fillResult)
                    {
                        _metrics.RecordFillResult(transition.Symbol!, fillResult);
                        LogFillResult(t, transition.Symbol!, fillResult);
                        if (fillResult.Status == FillApplyStatus.Applied &&
                            FindInstrument(transition.Symbol!)?.InventorySkew.Enabled == true)
                        {
                            SignalPricingContextChanged(transition.Symbol!, CancelReason.InventoryStrategy);
                        }
                    }
                    else
                    {
                        _metrics.RecordUnknownOrderFill();
                        _log.LogWarning(
                            "[mm-pnl] ignored fill for unknown order clordid={ClOrdId} orderId={OrderId} tradeId={TradeId} lastQty={LastQty} cumQty={CumQty}",
                            t.ClOrdID.Value, t.OrderId, t.TradeId, t.LastQty, t.CumQty);
                    }
                    // The authoritative "is this order done" signal is
                    // OrderStatus (Filled vs PartiallyFilled), not
                    // LeavesQty — same rationale as OrderAccepted above;
                    // an absent/null LeavesQty on a partial fill must not
                    // be misread as "fully filled".
                    // Fully filled → immediately re-quote the same side so
                    // our side of the book never goes empty. Partial fills
                    // stay resting (still working at the same price).
                    if (transition.Known && isFilled)
                        await RequoteAsync(client, transition.Symbol!, transition.IsBuy, ct);
                    break;
                }
            case UpModels.OrderCancelled c:
                {
                    _metrics.RecordCancelled();
                    // OrigClOrdID is the original resting order's id; it's
                    // only set when the cancel was in response to an
                    // explicit CancelOrderRequest (ours or another
                    // session's) — a venue-initiated spontaneous cancel
                    // (e.g. Day expiry) reports ClOrdID as the order's own
                    // id with no OrigClOrdID. Prefer OrigClOrdID, but the
                    // upstream gateway is also known to sometimes drop it
                    // on cancel acks entirely (see this repo's own
                    // ExecutionReportProcessorTests.Cancel_WithMissingOrigClOrdId_ResolvesViaCancelLink
                    // for the trading-host side of the same class of bug)
                    // — fall back to our own cancel-attempt correlation
                    // table before finally assuming ClOrdID IS the
                    // original id (the spontaneous-cancel case).
                    var transition = _orderLifecycle.Synchronize(() =>
                    {
                        var targetClOrdId = c.OrigClOrdID?.Value
                            ?? (_tracker.TryResolveCancelAttempt(c.ClOrdID.Value, out var linked)
                                ? linked
                                : c.ClOrdID.Value);
                        var known = _tracker.TryGet(targetClOrdId, out var order);
                        _tracker.OnTerminal(targetClOrdId);
                        _tracker.ForgetCancelAttempt(c.ClOrdID.Value);
                        _pnlLedger.MarkTerminal(targetClOrdId);
                        return (
                            Known: known,
                            Symbol: known ? order.Symbol : null,
                            IsBuy: known && order.IsBuy);
                    });
                    if (transition.Known)
                    {
                        await RequoteAsync(client, transition.Symbol!, transition.IsBuy, ct);
                        _pricingContextFailureRetries.TryRemove(transition.Symbol!, out _);
                        if (TryTakeDirtyPricingContext(transition.Symbol!, out var dirtyReason))
                            SignalPricingContextChanged(transition.Symbol!, dirtyReason);
                    }
                    break;
                }
            case UpModels.OrderRejected r:
                {
                    // A reject of a bot-generated cancel request (see
                    // CancelStaleOrdersAsync / ReactToBookChangeAsync) has
                    // no OrigClOrdID field to fall back on like
                    // OrderCancelled does, so it's otherwise
                    // indistinguishable from a rejected NEW order submit.
                    // Resolve it via the correlation table and
                    // deliberately do NOT free the original order's
                    // reservation: if it's still genuinely resting,
                    // closing it here would let the next reconcile tick
                    // submit a duplicate order alongside it — the exact
                    // venue-flooding failure mode RFC #703 exists to
                    // prevent. If the ER itself is lost, CancelAckTimeout
                    // expires only the matching pending marker and allows
                    // another guarded cancel attempt without ever freeing
                    // the original side reservation prematurely.
                    var cancelReject = _orderLifecycle.Synchronize(() =>
                    {
                        if (!_tracker.TryResolveCancelAttempt(
                                r.ClOrdID.Value,
                                out var origClOrdId,
                                out var cancelReason))
                        {
                            return (
                                Matched: false,
                                OrigClOrdId: 0UL,
                                CancelReason: default(CancelReason),
                                StuckSymbol: "?");
                        }

                        // Clear the pending-cancel marker (NOT the order
                        // itself — see rationale above) so the next
                        // reconcile tick / book delta is free to retry
                        // the cancel instead of treating one as
                        // permanently outstanding.
                        _tracker.ClearPendingCancelIfMatches(origClOrdId, r.ClOrdID.Value);
                        _tracker.ForgetCancelAttempt(r.ClOrdID.Value);
                        var stuckKnown = _tracker.TryGet(origClOrdId, out var stuck);
                        var stuckSymbol = stuckKnown ? stuck.Symbol : "?";
                        return (
                            Matched: true,
                            OrigClOrdId: origClOrdId,
                            CancelReason: cancelReason,
                            StuckSymbol: stuckSymbol);
                    });
                    if (cancelReject.Matched)
                    {
                        // Attribute the reject to the trigger that raised
                        // the shared cancel request. Existing stale-order
                        // and price-drift metrics retain their historical
                        // names while future strategy/feed reasons can use
                        // the same correlation seam.
                        if (cancelReject.CancelReason == CancelReason.PriceDrift)
                        {
                            _metrics.RecordBookDrivenRequoteCancelRejected(cancelReject.StuckSymbol);
                            _log.LogInformation(
                                "[mm] book-driven requote cancel rejected for clordid={ClOrdId} reason={Reason}; leaving tracker state unchanged (see RFC #703)",
                                cancelReject.OrigClOrdId, r.Reason);
                        }
                        else if (cancelReject.CancelReason == CancelReason.StaleOrder)
                        {
                            _metrics.RecordStaleCancelRejected(cancelReject.StuckSymbol);
                            _log.LogWarning(
                                "[mm] stale-order cancel rejected for clordid={ClOrdId} reason={Reason}; leaving tracker state unchanged (see RFC #703)",
                                cancelReject.OrigClOrdId, r.Reason);
                        }
                        else if (cancelReject.CancelReason == CancelReason.FeedUnavailable)
                        {
                            _metrics.RecordFeedCancelRejected(cancelReject.StuckSymbol);
                            _log.LogWarning(
                                "[mm-feed] feed-unavailable cancel rejected for clordid={ClOrdId} symbol={Symbol} reason={Reason}; retry remains guarded",
                                cancelReject.OrigClOrdId, cancelReject.StuckSymbol, r.Reason);
                        }
                        else
                        {
                            _log.LogWarning(
                                "[mm] cancel rejected for clordid={ClOrdId} trigger={CancelReason} reason={Reason}; leaving tracker state unchanged (see RFC #703)",
                                cancelReject.OrigClOrdId, cancelReject.CancelReason, r.Reason);
                        }
                        var retryReason = TryTakeDirtyPricingContext(
                            cancelReject.StuckSymbol,
                            out var dirtyReason)
                            ? dirtyReason
                            : cancelReject.CancelReason;
                        if (IsPricingContextReason(retryReason))
                            RetryPricingContextChanged(cancelReject.StuckSymbol, retryReason);
                        break;
                    }
                    var rejection = _orderLifecycle.Synchronize(() =>
                    {
                        var known = _tracker.TryGet(r.ClOrdID.Value, out var order);
                        var symbol = known ? order.Symbol : null;
                        _tracker.OnTerminal(r.ClOrdID.Value);
                        _pnlLedger.MarkTerminal(r.ClOrdID.Value);
                        return (Known: known, Symbol: symbol);
                    });
                    _metrics.RecordRejected(rejection.Symbol);
                    // Deliberately do NOT re-quote immediately here: an
                    // instrument-level reject (bad config, halt, risk
                    // limit) would otherwise repeat identically forever,
                    // flooding the session with reject→submit→reject
                    // churn. The low-frequency ReconcileLoopAsync is the
                    // right place to retry a rejected side — it naturally
                    // rate-limits retries to ReconcileInterval.
                    break;
                }
            case UpModels.OrderModified m:
                // The bot never sends a ReplaceOrderRequest (it only ever
                // submits New/Cancel), so ANY OrderModified/ExecType=Replaced
                // it receives is, by construction, unsolicited from its own
                // perspective — the venue restating the order for reasons
                // the bot didn't ask for and doesn't model (e.g. a priority
                // timestamp reset). Mirroring B3.Trading.Application's own
                // hard-learned lesson for the same ExecType (see
                // ExecutionReportProcessor's ExecKind.Replaced case, #122:
                // "unsolicited Replaced ER ... leave the original alone"),
                // this must NOT feed LeavesQty into OnAccepted/Close: the
                // SDK gives no documented guarantee that LeavesQty is
                // present (as opposed to null-defaulted-to-zero via `??`)
                // on this particular ER shape, and wrongly treating an
                // absent/stale value as "fully filled" here closes the
                // tracker's view of a still-genuinely-resting order —
                // freeing its (symbol, side) reservation without the venue
                // ever actually cancelling it, so the next reconcile tick
                // adds a duplicate resting order alongside the original
                // (see pedrosakuma/B3EntryPointClient#228). Only the
                // venue OrderId denormalization is safe to apply
                // unconditionally.
                _orderLifecycle.Synchronize(() => _tracker.SetOrderId(m.ClOrdID.Value, m.OrderId));
                break;
        }

    }

    private async Task RequoteAsync(IEntryPointClient client, string symbol, bool isBuy, CancellationToken ct)
    {
        var instr = FindInstrument(symbol);
        if (instr is null) return;
        await QuoteSideAsync(client, instr, isBuy, ct);
    }

    /// <summary>
    /// Defensive safety net only — periodically verifies every configured
    /// (instrument, side) still has a resting order and re-quotes any gap
    /// (e.g. a dropped event, or a reconnect where in-flight orders from
    /// before the gap are gone). The event-driven path in
    /// <see cref="HandleEventAsync"/> is what keeps quotes fresh under
    /// normal operation; this loop should rarely find anything to do.
    /// </summary>
    private async Task ReconcileLoopAsync(IEntryPointClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_options.ReconcileInterval, _clock, ct); }
            catch (OperationCanceledException) { return; }

            await ReconcileOnceAsync(client, ct);
        }
    }

    internal async Task ReconcileOnceAsync(IEntryPointClient client, CancellationToken ct)
    {
        ExpirePendingCancelAcknowledgements();
        await CancelStaleOrdersAsync(client, ct);
        foreach (var change in _volatilitySpread.Refresh())
        {
            _log.LogInformation(
                "[mm-volatility] effective spread changed during window refresh symbol={Symbol} estimateTicks={MoveEstimateTicks} samples={SampleCount} ready={Ready} connected={Connected} previousAdditionalTicks={PreviousAdditionalTicks} additionalTicks={AdditionalTicks}",
                change.Symbol,
                change.Current.MoveEstimateTicks,
                change.Current.SampleCount,
                change.Current.IsReady,
                change.Current.IsConnected,
                change.PreviousAdditionalSpreadTicks,
                change.Current.AdditionalSpreadTicks);
            SignalPricingContextChanged(change.Symbol, CancelReason.VolatilityStrategy);
        }
        _orderLifecycle.Prune(_options.MaxOrderAge);

        foreach (var instr in _options.Instruments)
        {
            if (_priceTracker.IsDelisted(instr.Symbol))
                continue;
            if (_options.MarketData.FeedLossPolicy == FeedLossPolicy.PauseAndCancel)
            {
                var availability = ObserveFeedAvailability(instr.Symbol);
                if (!availability.IsEligible)
                {
                    SignalPricingContextChanged(instr.Symbol, CancelReason.FeedUnavailable);
                    continue;
                }
            }

            if (!_tracker.HasOpenSide(instr.Symbol, isBuy: true))
                await QuoteSideAsync(client, instr, isBuy: true, ct);
            if (!_tracker.HasOpenSide(instr.Symbol, isBuy: false))
                await QuoteSideAsync(client, instr, isBuy: false, ct);
        }
    }

    private void ExpirePendingCancelAcknowledgements()
    {
        var expired = _tracker.ExpirePendingCancelAttempts(
            _options.CancelAckTimeout,
            _tracker.UtcNow);
        foreach (var attempt in expired)
        {
            _metrics.RecordCancelAcknowledgementExpired(attempt.Symbol, attempt.Reason);
            _log.LogWarning(
                "[mm] cancel acknowledgement expired cancelClOrdId={CancelClOrdId} origClOrdId={OrigClOrdId} symbol={Symbol} side={Side} trigger={CancelReason} age={Age}; guarded retry enabled",
                attempt.CancelClOrdId,
                attempt.OrigClOrdId,
                attempt.Symbol,
                attempt.IsBuy ? "buy" : "sell",
                attempt.Reason,
                attempt.ExpiredAtUtc - attempt.AttemptedAtUtc);
            if (IsPricingContextReason(attempt.Reason))
                SignalPricingContextChanged(attempt.Symbol, attempt.Reason);
        }
    }

    /// <summary>
    /// RFC #703 miss-fill guard: the SDK has no order-status query, so we
    /// can't ask the venue "is this still really open" — instead, any
    /// order the tracker still considers open past
    /// <see cref="MarketMakerBotOptions.MaxOrderAge"/> is explicitly
    /// cancelled. If it genuinely was still resting, the venue's
    /// <c>OrderCancelled</c> ER closes it and <see cref="HandleEventAsync"/>
    /// re-quotes the side normally. If the bot had silently missed its
    /// terminal event earlier (a "miss-fill"), the venue rejects the
    /// cancel of an unknown/already-terminal order via <c>OrderRejected</c>
    /// keyed on the CANCEL request's own (freshly-generated) ClOrdID —
    /// <see cref="OrderTracker.RegisterCancelAttempt"/> aliases that id to
    /// the original tracked order so the reject still resolves without
    /// freeing a potentially-resting reservation; bounded retries continue
    /// through the normal cancel guard.
    /// internal (not private) so <c>MarketMakerWorkerTests</c> can drive
    /// it directly with a <see cref="FakeEntryPointClient"/>-equivalent
    /// and an <see cref="OrderTracker"/> constructed with a fake
    /// <see cref="TimeProvider"/> — see #711's follow-up.
    /// </summary>
    internal async Task CancelStaleOrdersAsync(IEntryPointClient client, CancellationToken ct)
    {
        var stale = _tracker.FindStale(_options.MaxOrderAge, _tracker.UtcNow);
        foreach (var o in stale)
        {
            var instr = FindInstrument(o.Symbol);
            if (instr is null)
            {
                // Instrument config was removed/renamed since the order was
                // submitted; there is no valid SecurityId to cancel with.
                _log.LogWarning(
                    "[mm] cannot build stale-order cancel for clordid={ClOrdId}: unknown instrument {Symbol}",
                    o.ClOrdId, o.Symbol);
                continue;
            }
            if (await SubmitCancelAsync(
                    client,
                    o,
                    instr,
                    CancelReason.StaleOrder,
                    _metrics.RecordStaleCancelSubmitFailed,
                    ct))
            {
                _metrics.RecordStaleOrderCancelled(o.Symbol);
                _log.LogWarning(
                    "[mm] cancelled stale order clordid={ClOrdId} symbol={Symbol} side={Side} age={Age} (miss-fill guard)",
                    o.ClOrdId, o.Symbol, o.IsBuy ? "buy" : "sell", _tracker.UtcNow - o.SubmittedAtUtc);
            }
        }
    }

    /// <summary>
    /// RFC #703 book-driven quoting: <see cref="MarketDataFeed.BookOrderChanged"/>
    /// can't await a cancel from its synchronous callback, so it signals
    /// the shared pricing-context reaction path after filtering self-orders.
    /// </summary>
    private void OnBookOrderChanged(string symbol, ulong orderId)
    {
        // Self-order filter: a delta the bot's OWN resting order caused
        // (its own submit/cancel/fill landing in the book) must not
        // trigger a reactive requote of itself — see
        // OrderTracker.IsOwnOrder's doc comment. This is inherently
        // best-effort, NOT a hard guarantee: FIXP (order acks) and
        // market-data (book deltas) are two independent feeds with no
        // shared sequencing, so a fast MD callback can observe our own
        // OrderAdded/OrderDeleted before OrderTracker.SetOrderId has
        // learned the OrderId (on submit) or before it's forgotten it
        // (on Close, just after cancel/fill) — in both windows
        // IsOwnOrder(orderId) misses and the delta is (harmlessly)
        // treated as external. ReactToBookChangeAsync only ever cancels
        // a side once its resting price has genuinely drifted past
        // RequoteDeviationTicks from a fresh target, so a spuriously
        // "external" self-delta just causes one extra no-op evaluation,
        // never an incorrect cancel.
        if (_tracker.IsOwnOrder(orderId)) return;
        SignalPricingContextChanged(symbol, CancelReason.PriceDrift);
    }

    internal void OnSymbolAvailabilityChanged(string symbol) =>
        SignalPricingContextChanged(symbol, CancelReason.FeedUnavailable);

    internal void OnVolatilitySpreadChanged(string symbol) =>
        SignalPricingContextChanged(symbol, CancelReason.VolatilityStrategy);

    internal void OnMarketDataConnectionEligibilityChanged()
    {
        foreach (var symbol in _configuredSymbols)
            SignalPricingContextChanged(symbol, CancelReason.FeedUnavailable);
    }

    /// <summary>
    /// Coalesces a pricing-context change for one configured symbol. A signal
    /// arriving while that symbol is already queued is folded into the queued
    /// reaction; one arriving after dequeue schedules exactly one follow-up.
    /// </summary>
    internal bool SignalPricingContextChanged(string symbol, CancelReason reason)
    {
        if (!_configuredSymbols.Contains(symbol)) return false;
        _pricingContextFailureRetries.TryRemove(symbol, out _);
        return EnqueuePricingContextChanged(symbol, reason);
    }

    private bool EnqueuePricingContextChanged(string symbol, CancelReason reason)
    {
        while (true)
        {
            if (_pendingPricingContextSignals.TryAdd(symbol, reason))
            {
                if (_pricingContextSignals.Writer.TryWrite(symbol))
                    return true;
                _pendingPricingContextSignals.TryRemove(symbol, out _);
                return false;
            }

            if (!_pendingPricingContextSignals.TryGetValue(symbol, out var current))
                continue;
            var merged = MergePricingContextReason(current, reason);
            if (merged == current)
                return false;
            if (_pendingPricingContextSignals.TryUpdate(symbol, merged, current))
                return false;
        }
    }

    private bool RetryPricingContextChanged(string symbol, CancelReason reason)
    {
        if (!_configuredSymbols.Contains(symbol) ||
            !_pricingContextFailureRetries.TryAdd(symbol, 0))
        {
            return false;
        }

        if (reason == CancelReason.FeedUnavailable)
            _metrics.RecordFeedCancelRetry(symbol);
        return EnqueuePricingContextChanged(symbol, reason);
    }

    private void MarkPricingContextDirty(string symbol, CancelReason reason) =>
        _dirtyPricingContextSignals.AddOrUpdate(
            symbol,
            reason,
            (_, current) => MergePricingContextReason(current, reason));

    private bool TryTakeDirtyPricingContext(string symbol, out CancelReason reason) =>
        _dirtyPricingContextSignals.TryRemove(symbol, out reason);

    private static bool IsPricingContextReason(CancelReason reason) =>
        reason is CancelReason.PriceDrift or CancelReason.InventoryStrategy or
            CancelReason.VolatilityStrategy or CancelReason.FeedUnavailable;

    private static CancelReason MergePricingContextReason(CancelReason current, CancelReason incoming) =>
        PricingContextPriority(incoming) > PricingContextPriority(current) ? incoming : current;

    private static int PricingContextPriority(CancelReason reason) => reason switch
    {
        CancelReason.FeedUnavailable => 3,
        CancelReason.VolatilityStrategy => 2,
        CancelReason.InventoryStrategy => 2,
        CancelReason.PriceDrift => 1,
        _ => 0,
    };

    internal async Task PricingContextReactionLoopAsync(IEntryPointClient client, CancellationToken ct)
    {
        try
        {
            await foreach (var symbol in _pricingContextSignals.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!_pendingPricingContextSignals.TryRemove(symbol, out var reason))
                    continue;
                try { await ReactToPricingContextChangeAsync(client, symbol, reason, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogError(ex,
                        "[mm] pricing-context reaction failed for {Symbol} trigger={CancelReason}",
                        symbol, reason);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    /// <summary>
    /// RFC #703 book-driven quoting: reacts to a market-data book delta
    /// NOT caused by the bot's own resting order (filtered in <see
    /// cref="OnBookOrderChanged"/>) by comparing each side's currently
    /// resting price against a freshly-computed target and cancelling it
    /// if it has drifted past <see cref="MarketMakerBotOptions.RequoteDeviationTicks"/>.
    /// Deliberately does NOT resubmit here — the existing
    /// <c>OrderCancelled</c> path in <see cref="HandleEventAsync"/>
    /// re-quotes the side once the cancel is acked, reusing the same
    /// submit/reservation machinery as every other requote trigger
    /// instead of a bespoke cancel-then-immediately-resubmit race. A side
    /// with no resting order (nothing to react with; the reconcile loop
    /// owns filling that gap), one already being cancelled, or one still
    /// within <see cref="MarketMakerBotOptions.MinRequoteInterval"/> of
    /// its own submission is left alone — the last of these throttles a
    /// burst of book updates from repeatedly cancelling a quote that
    /// hasn't even settled yet, the same venue-flooding shape RFC #703
    /// exists to prevent.
    /// internal (not private) so <c>MarketMakerWorkerTests</c> can drive
    /// it directly — see #711's follow-up.
    /// </summary>
    internal Task ReactToBookChangeAsync(IEntryPointClient client, string symbol, CancellationToken ct) =>
        ReactToPricingContextChangeAsync(client, symbol, CancelReason.PriceDrift, ct);

    internal async Task ReactToPricingContextChangeAsync(
        IEntryPointClient client,
        string symbol,
        CancelReason reason,
        CancellationToken ct)
    {
        var instr = FindInstrument(symbol);
        if (instr is null) return;
        var now = _tracker.UtcNow;
        var maxDeviation = reason is CancelReason.InventoryStrategy or CancelReason.VolatilityStrategy
            ? 0m
            : instr.TickSize * _options.RequoteDeviationTicks;
        TimeSpan? retryAfter = null;

        foreach (var isBuy in new[] { true, false })
        {
            var decision = BuildQuoteDecision(instr, isBuy);
            if (!_tracker.TryGetActiveSideOrder(symbol, isBuy, out var resting))
            {
                if (decision.ShouldQuote)
                    await QuoteSideAsync(client, instr, isBuy, ct);
                continue;
            }
            if (resting.PendingCancelClOrdId is not null)
            {
                MarkPricingContextDirty(symbol, reason);
                continue;
            }
            var isSuppressed = !decision.ShouldQuote || decision.Price is null;
            var target = decision.Price;
            if (!isSuppressed && Math.Abs(resting.Price - target!.Value) <= maxDeviation) continue;

            // Fast-path skip only — the authoritative throttle check runs
            // INSIDE SubmitCancelAsync's atomic registration below (see
            // OrderTracker.TryRegisterCancelAttempt), since this snapshot
            // read is unsynchronized and could be stale by the time we
            // actually try to register the attempt.
            var lastActivity = resting.LastCancelAttemptAtUtc ?? resting.SubmittedAtUtc;
            var elapsed = now - lastActivity;
            if (elapsed < _options.MinRequoteInterval)
            {
                if (reason == CancelReason.InventoryStrategy ||
                    reason == CancelReason.VolatilityStrategy ||
                    reason == CancelReason.FeedUnavailable ||
                    isSuppressed)
                {
                    var remaining = _options.MinRequoteInterval - elapsed;
                    retryAfter = retryAfter is null || remaining < retryAfter
                        ? remaining
                        : retryAfter;
                }
                continue;
            }

            if (await SubmitCancelAsync(
                    client,
                    resting,
                    instr,
                    reason,
                    reason == CancelReason.PriceDrift
                        ? _metrics.RecordBookDrivenRequoteSubmitFailed
                        : reason == CancelReason.FeedUnavailable
                            ? _metrics.RecordFeedCancelSubmitFailed
                            : static _ => { },
                    ct,
                    _options.MinRequoteInterval))
            {
                if (reason == CancelReason.PriceDrift)
                {
                    _metrics.RecordBookDrivenRequote(symbol, isBuy);
                    _log.LogInformation(
                        "[mm] book-driven requote: cancelling clordid={ClOrdId} symbol={Symbol} side={Side} resting={Resting} target={Target}",
                        resting.ClOrdId, symbol, isBuy ? "buy" : "sell", resting.Price, target);
                }
                else if (isSuppressed)
                {
                    if (reason == CancelReason.FeedUnavailable)
                        _metrics.RecordFeedCancel(symbol, isBuy);
                    _log.LogWarning(
                        "[mm] pricing-context suppression: cancelling clordid={ClOrdId} symbol={Symbol} side={Side} trigger={CancelReason} suppression={SuppressionReason}",
                        resting.ClOrdId, symbol, isBuy ? "buy" : "sell", reason, decision.SuppressionReason);
                }
                else
                {
                    _log.LogInformation(
                        "[mm] pricing-context requote: cancelling clordid={ClOrdId} symbol={Symbol} side={Side} trigger={CancelReason} resting={Resting} target={Target}",
                        resting.ClOrdId, symbol, isBuy ? "buy" : "sell", reason, resting.Price, target);
                }
            }
        }

        if (retryAfter is { } delay)
        {
            if (reason == CancelReason.FeedUnavailable)
                _metrics.RecordFeedCancelRetry(symbol);
            await Task.Delay(delay, _clock, ct);
            EnqueuePricingContextChanged(symbol, reason);
        }
    }

    /// <summary>
    /// Shared cancel-submit path for the staleness guard and all reactive
    /// pricing-context requotes: atomically registers the
    /// cancel-attempt correlation BEFORE the SDK await (see <see
    /// cref="OrderTracker.TryRegisterCancelAttempt"/>'s doc comment on
    /// why atomicity matters here specifically — the staleness guard and
    /// pricing-context reaction run on separate concurrent loops and could
    /// otherwise both target the same order), sends the request, and on
    /// synchronous failure clears the pending-cancel marker (and its now
    /// -abandoned correlation row) it just set so the order isn't
    /// permanently hidden from future guards. <paramref name="minIntervalSinceLastAttempt"/>,
    /// when given, is enforced INSIDE the same atomic registration rather
    /// than only by a separate pre-call check in the caller, so a
    /// concurrent register/clear on another thread can't leave the
    /// caller's own throttle decision stale by the time this actually
    /// commits. <paramref name="reason"/> is stamped onto the
    /// correlation row so a later OrderRejected can be attributed to the
    /// right trigger (see <see cref="OrderTracker.TryResolveCancelAttempt(ulong, out ulong, out CancelReason)"/>
    /// and <see cref="HandleEventAsync"/>'s OrderRejected case) instead of
    /// always being reported as a stale-order cancel reject now that both
    /// triggers share this same submit path. Returns whether the cancel
    /// was accepted for transmission (false also when a cancel was
    /// already outstanding for this order from the OTHER path, the order
    /// had already closed, or the interval hadn't elapsed); callers add
    /// their own reason-specific success metric/log.
    /// </summary>
    private async Task<bool> SubmitCancelAsync(IEntryPointClient client, TrackedOrder o, InstrumentConfig instr,
        CancelReason reason, Action<string> recordSubmitFailed, CancellationToken ct,
        TimeSpan? minIntervalSinceLastAttempt = null)
    {
        var cancelClOrdId = (ulong)Interlocked.Increment(ref _nextClOrdId);
        if (!_tracker.TryRegisterCancelAttempt(cancelClOrdId, o.ClOrdId, minIntervalSinceLastAttempt, reason))
            return false;
        var req = new UpModels.CancelOrderRequest
        {
            ClOrdID = new UpModels.ClOrdID(cancelClOrdId),
            OrigClOrdID = new UpModels.ClOrdID(o.ClOrdId),
            SecurityId = instr.SecurityId,
            Side = o.IsBuy ? UpModels.Side.Buy : UpModels.Side.Sell,
        };
        try
        {
            await client.CancelAsync(req, ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The request never reached (or was never acknowledged by)
            // the venue, so no ER will ever arrive to clear
            // PendingCancelClOrdId via ClearPendingCancel/Close(). Guarded
            // so we don't clear a DIFFERENT, later attempt that may have
            // already been registered for this order by the time this
            // catch runs.
            _tracker.ClearPendingCancelIfMatches(o.ClOrdId, cancelClOrdId);
            recordSubmitFailed(o.Symbol);
            _log.LogWarning(ex, "[mm] failed to cancel clordid={ClOrdId} symbol={Symbol}", o.ClOrdId, o.Symbol);
            if (IsPricingContextReason(reason))
                RetryPricingContextChanged(o.Symbol, reason);
            return false;
        }
    }

    internal async Task QuoteSideAsync(IEntryPointClient client, InstrumentConfig instr, bool isBuy, CancellationToken ct)
    {
        var decision = BuildQuoteDecision(instr, isBuy);
        if (!decision.ShouldQuote || decision.Price is not { } price)
        {
            RecordSuppressedDecision(instr.Symbol, isBuy, decision);
            return;
        }
        // RFC #703 client-side safety cap (defense in depth against the
        // failure mode in pedrosakuma/B3MatchingPlatform#567): stop adding
        // NEW resting orders once the bot's own tracked open-order count
        // hits the configured ceiling. Existing resting orders are left
        // alone — this only throttles growth, it never panic-cancels.
        var openCount = _tracker.OpenCount();
        if (openCount >= _options.MaxOpenOrders)
        {
            _metrics.RecordSafetyCapHit(instr.Symbol);
            _log.LogWarning(
                "[mm] safety cap hit: {OpenCount} open orders >= MaxOpenOrders={MaxOpenOrders}; skipping quote for {Symbol} side={Side}",
                openCount, _options.MaxOpenOrders, instr.Symbol, isBuy ? "buy" : "sell");
            return;
        }
        var quantity = QuoteCalculator.QuoteQuantity(instr);

        var clOrdId = (ulong)Interlocked.Increment(ref _nextClOrdId);
        // Atomic check-and-reserve: if another caller (the event-driven
        // requote path or the reconcile safety net) already reserved this
        // (symbol, side) between our HasOpenSide check and now, this
        // returns false and we skip — preventing two resting orders on
        // the same side. Register BEFORE the SDK await — the matching ER
        // can race ahead of the await on a fast wire (mirrors
        // trading-host's pattern).
        if (!_tracker.TryRegisterSubmit(clOrdId, instr.Symbol, price, quantity, isBuy))
            return;
        if (!IsSubmitEligible(instr.Symbol))
        {
            _orderLifecycle.Synchronize(() => _tracker.OnTerminal(clOrdId));
            return;
        }
        var req = new UpModels.NewOrderRequest
        {
            ClOrdID = new UpModels.ClOrdID(clOrdId),
            SecurityId = instr.SecurityId,
            Side = isBuy ? UpModels.Side.Buy : UpModels.Side.Sell,
            OrderType = UpModels.OrderType.Limit,
            Price = price,
            OrderQty = (ulong)quantity,
            TimeInForce = UpModels.TimeInForce.Day,
        };
        try
        {
            if (!IsSubmitEligible(instr.Symbol))
            {
                _orderLifecycle.Synchronize(() => _tracker.OnTerminal(clOrdId));
                return;
            }
            await client.SubmitAsync(req, ct);
            _metrics.RecordOrderSubmitted(instr.Symbol, isBuy);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _orderLifecycle.Synchronize(() => _tracker.OnTerminal(clOrdId));
            _metrics.RecordOrderSubmitFailed(instr.Symbol);
            _log.LogWarning(ex, "[mm] quote submit failed for {Symbol} side={Side} clordid={ClOrdId}",
                instr.Symbol, isBuy ? "buy" : "sell", clOrdId);
        }
    }

    /// <summary>
    /// The single worker-side context builder for both initial/replacement
    /// submits and reactive drift comparisons. Inventory comes only from the
    /// process-local P&amp;L ledger; dynamic spread comes only from valid
    /// market-data trades through <see cref="VolatilitySpreadEstimator"/>.
    /// </summary>
    internal QuoteDecision BuildQuoteDecision(InstrumentConfig instrument, bool isBuy)
    {
        var strict = _options.MarketData.FeedLossPolicy == FeedLossPolicy.PauseAndCancel;
        var availability = strict ? ObserveFeedAvailability(instrument.Symbol) : default;
        decimal liveReference;
        bool hasLiveReference;
        if (strict)
        {
            if (availability.IsEligible &&
                availability.LastValidMark is { Price: > 0m } strictMark)
            {
                hasLiveReference = true;
                liveReference = strictMark.Price;
            }
            else
            {
                hasLiveReference = false;
                liveReference = default;
            }
        }
        else
        {
            hasLiveReference = _priceTracker.TryGetReferencePrice(
                instrument.Symbol,
                out liveReference);
        }
        var referencePrice = hasLiveReference ? liveReference : instrument.RefPrice;
        var volatilitySpread = _volatilitySpread.GetSnapshot(instrument.Symbol);
        var effectiveHalfSpreadTicks = checked(
            instrument.SpreadTicks + volatilitySpread.AdditionalSpreadTicks);
        var configuredHalfSpread = checked(instrument.SpreadTicks * instrument.TickSize);
        var effectiveHalfSpread = checked(effectiveHalfSpreadTicks * instrument.TickSize);
        var netQuantity = _pnlLedger.TryGetSnapshot(instrument.Symbol, out var position)
            ? position.Position
            : 0L;
        var inventorySkew = InventorySkewCalculator.Calculate(
            instrument.InventorySkew,
            netQuantity,
            instrument.LotSize,
            instrument.TickSize);
        return QuoteCalculator.Decide(new QuoteInputs(
            isBuy,
            referencePrice,
            hasLiveReference
                ? QuoteReferenceSource.LiveMarketData
                : QuoteReferenceSource.ConfiguredRefPrice,
            inventorySkew.MidShift,
            inventorySkew.SkewTicks,
            ConfiguredHalfSpread: configuredHalfSpread,
            EffectiveHalfSpread: effectiveHalfSpread,
            AdditionalHalfSpreadTicks: volatilitySpread.AdditionalSpreadTicks,
            instrument.TickSize,
            _priceTracker.IsDelisted(instrument.Symbol)
               ? QuoteSuppressionReason.InstrumentDelisted
               : strict && !availability.IsEligible
                   ? QuoteSuppressionReason.FeedUnavailable
                   : QuoteSuppressionReason.None));
    }

    private bool IsSubmitEligible(string symbol)
    {
        if (_priceTracker.IsDelisted(symbol))
            return false;
        return _options.MarketData.FeedLossPolicy != FeedLossPolicy.PauseAndCancel ||
            ObserveFeedAvailability(symbol).IsEligible;
    }

    private ReferenceAvailability ObserveFeedAvailability(string symbol)
    {
        var current = _priceTracker.GetAvailability(symbol, _options.MarketData.MaxReferenceAge);
        var observation = new FeedAvailabilityObservation(
            current.IsEligible,
            current.UnavailableReason,
            current.ConnectionEpoch);
        while (true)
        {
            if (!_feedAvailability.TryGetValue(symbol, out var previous))
            {
                if (!_feedAvailability.TryAdd(symbol, observation))
                    continue;
                PublishFeedAvailabilityTransition(symbol, current);
                return current;
            }
            if (previous == observation)
                return current;
            if (_feedAvailability.TryUpdate(symbol, observation, previous))
            {
                PublishFeedAvailabilityTransition(symbol, current);
                return current;
            }
        }
    }

    private void PublishFeedAvailabilityTransition(string symbol, ReferenceAvailability availability)
    {
        _metrics.RecordFeedAvailabilityTransition(
            symbol,
            availability.IsEligible,
            availability.UnavailableReason);
        _log.LogInformation(
            "[mm-feed] symbol availability changed symbol={Symbol} available={Available} reason={Reason} epoch={Epoch} age={ReferenceAge} source={ReferenceSource}",
            symbol,
            availability.IsEligible,
            availability.UnavailableReason,
            availability.ConnectionEpoch,
            availability.ReferenceAge,
            availability.LastValidMark?.Source);
    }

    private void RecordSuppressedDecision(string symbol, bool isBuy, QuoteDecision decision)
    {
        if (decision.SuppressionReason != QuoteSuppressionReason.FeedUnavailable)
            return;
        var availability = ObserveFeedAvailability(symbol);
        _metrics.RecordFeedSuppressedDecision(symbol, isBuy, availability.UnavailableReason);
        _log.LogInformation(
            "[mm-feed] quote decision suppressed symbol={Symbol} side={Side} reason={Reason} epoch={Epoch} age={ReferenceAge} source={ReferenceSource}",
            symbol,
            isBuy ? "buy" : "sell",
            availability.UnavailableReason,
            availability.ConnectionEpoch,
            availability.ReferenceAge,
            availability.LastValidMark?.Source);
    }

    private readonly record struct FeedAvailabilityObservation(
        bool IsEligible,
        FeedUnavailableReason Reason,
        long ConnectionEpoch);

    private sealed class StartupCleanupAwaiter
    {
        private readonly object _gate = new();
        private readonly ulong _outboundSeqNum;
        private readonly TaskCompletionSource<UpModels.MassActionExecuted> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private UpModels.MassActionExecuted? _acceptedReport;
        private ulong _highestPeerNextSeqNo;
        private ulong _receivePollAfterSeqNum;

        public StartupCleanupAwaiter(ulong outboundSeqNum) =>
            _outboundSeqNum = outboundSeqNum;

        public Task<UpModels.MassActionExecuted> Completion => _completion.Task;

        public void ObserveMassActionReport(UpModels.MassActionExecuted report)
        {
            if (report.Response != UpModels.MassActionResponse.Accepted)
            {
                _completion.TrySetException(new InvalidOperationException(
                    $"Startup mass action was rejected: {report.RejectReason?.ToString() ?? "unknown"}."));
                return;
            }

            lock (_gate)
            {
                _acceptedReport = report;
                TryCompleteLocked();
            }
        }

        public void ObserveBusinessReject(UpModels.BusinessReject reject)
        {
            if (reject.RefSeqNum != _outboundSeqNum) return;
            _completion.TrySetException(new InvalidOperationException(
                $"Startup mass action received BusinessReject reason={reject.RejectReason}: {reject.Text ?? "unknown"}."));
        }

        public void ObservePeerSequence(ulong nextSeqNo)
        {
            lock (_gate)
            {
                if (nextSeqNo > _highestPeerNextSeqNo)
                    _highestPeerNextSeqNo = nextSeqNo;
                TryCompleteLocked();
            }
        }

        public void ObserveReceivePoll(ulong afterSeqNum)
        {
            lock (_gate)
            {
                if (afterSeqNum > _receivePollAfterSeqNum)
                    _receivePollAfterSeqNum = afterSeqNum;
                TryCompleteLocked();
            }
        }

        private void TryCompleteLocked()
        {
            // Matching suppresses Sequence heartbeats while outbound business
            // traffic is active. Its next advertised app sequence therefore
            // fences the synchronous enqueue decision: a follow-up BMR would
            // consume one of the sequence numbers below NextSeqNo. Requiring
            // the receive loop to be polling after that range ensures every
            // such event has been handled before startup can proceed.
            if (_acceptedReport is { } accepted &&
                _highestPeerNextSeqNo > accepted.SeqNum &&
                _receivePollAfterSeqNum >= _highestPeerNextSeqNo - 1)
            {
                _completion.TrySetResult(accepted);
            }
        }
    }

    private InstrumentConfig? FindInstrument(string symbol)
    {
        foreach (var i in _options.Instruments)
            if (string.Equals(i.Symbol, symbol, StringComparison.Ordinal)) return i;
        return null;
    }

    internal static System.Net.DnsEndPoint ParseEndpoint(string endpoint) =>
        EndpointParser.Parse(endpoint);

    private void LogFillResult(UpModels.OrderTrade trade, string symbol, FillApplyResult result)
    {
        switch (result.Status)
        {
            case FillApplyStatus.Applied when result.QuantityMismatch:
                _log.LogWarning(
                    "[mm-pnl] fill delta mismatch symbol={Symbol} clordid={ClOrdId} tradeId={TradeId} lastQty={LastQty} cumQty={CumQty} bookedQuantity={BookedQuantity}",
                    symbol, trade.ClOrdID.Value, trade.TradeId, trade.LastQty, trade.CumQty,
                    result.BookedQuantity);
                break;
            case FillApplyStatus.Applied:
                break;
            case FillApplyStatus.Duplicate:
                _log.LogInformation(
                    "[mm-pnl] ignored duplicate fill symbol={Symbol} clordid={ClOrdId} tradeId={TradeId} cumQty={CumQty}",
                    symbol, trade.ClOrdID.Value, trade.TradeId, trade.CumQty);
                break;
            case FillApplyStatus.Invalid:
                _log.LogWarning(
                    "[mm-pnl] ignored invalid fill symbol={Symbol} clordid={ClOrdId} tradeId={TradeId} lastPx={LastPx} lastQty={LastQty} cumQty={CumQty} reason={Reason}",
                    symbol, trade.ClOrdID.Value, trade.TradeId, trade.LastPx, trade.LastQty, trade.CumQty,
                    result.Reason);
                break;
            case FillApplyStatus.Inconsistent:
                _log.LogWarning(
                    "[mm-pnl] ignored inconsistent fill symbol={Symbol} clordid={ClOrdId} tradeId={TradeId} lastQty={LastQty} cumQty={CumQty} leavesQty={LeavesQty} reason={Reason}",
                    symbol, trade.ClOrdID.Value, trade.TradeId, trade.LastQty, trade.CumQty, trade.LeavesQty,
                    result.Reason);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(result));
        }
    }
}
