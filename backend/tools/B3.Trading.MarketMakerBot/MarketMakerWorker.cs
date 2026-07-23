using B3.EntryPoint.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private readonly MarketDataFeed _marketData;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MarketMakerWorker> _log;
    private long _nextClOrdId;
    private EntryPointClient? _client;
    // RFC #703 book-driven quoting: OnBookOrderChanged runs synchronously
    // on MarketDataFeed's receive callback, so it can't itself await a
    // CancelAsync — it just signals the symbol here and returns. A
    // per-symbol pending flag coalesces a burst of deltas for the same
    // symbol into a single queued signal (mirrors the SDK's own
    // BackPressurePolicy.DropOldest intent) instead of unboundedly
    // queuing one entry per delta.
    private readonly Channel<string> _bookSignals = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, byte> _pendingBookSignals = new(StringComparer.Ordinal);

    public MarketMakerWorker(IOptions<MarketMakerBotOptions> options, OrderTracker tracker,
        MarketPriceTracker priceTracker, MarketDataFeed marketData, ILoggerFactory loggerFactory,
        ILogger<MarketMakerWorker> log)
    {
        _options = options.Value;
        _tracker = tracker;
        _priceTracker = priceTracker;
        _marketData = marketData;
        _loggerFactory = loggerFactory;
        _log = log;
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
        try
        {
            _log.LogInformation("[mm] connecting to {Endpoint} session={Session} verId={VerId}",
                _options.Endpoint, _options.SessionId, resolvedVerId);
            await _client.ConnectAsync(stoppingToken);
            _log.LogInformation("[mm] connected; instruments={Count} reconcile={Interval}",
                _options.Instruments.Count, _options.ReconcileInterval);

            // Market data is best-effort and never blocks FIXP quoting —
            // see MarketDataFeed's doc comment for why.
            await _marketData.StartAsync(_options.MarketData, _options.Instruments, _loggerFactory, stoppingToken);

            // Prime the book: one resting bid + ask per instrument before
            // anything else runs, so a fresh boot doesn't leave a window
            // with zero MM depth.
            foreach (var instr in _options.Instruments)
            {
                await QuoteSideAsync(_client, instr, isBuy: true, stoppingToken);
                await QuoteSideAsync(_client, instr, isBuy: false, stoppingToken);
            }

            var receive = ReceiveLoopAsync(_client, stoppingToken);
            var reconcile = ReconcileLoopAsync(_client, stoppingToken);
            var bookReaction = BookReactionLoopAsync(_client, stoppingToken);
            await Task.WhenAny(receive, reconcile, bookReaction);
            // Surface the failing task's exception (if any).
            await Task.WhenAll(receive, reconcile, bookReaction);
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
            _bookSignals.Writer.TryComplete();
            try { await _client.DisposeAsync(); } catch { /* ignore */ }
            await _marketData.DisposeAsync();
        }
    }

    private async Task ReceiveLoopAsync(EntryPointClient client, CancellationToken ct)
    {
        await foreach (var ev in client.Events(ct).ConfigureAwait(false))
        {
            try
            {
                await HandleEventAsync(client, ev, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "[mm] failed to handle event {Event}", ev.GetType().Name);
            }
        }
    }

    private async Task HandleEventAsync(EntryPointClient client, UpModels.EntryPointEvent ev, CancellationToken ct)
    {
        switch (ev)
        {
            case UpModels.OrderAccepted a:
                _tracker.SetOrderId(a.ClOrdID.Value, a.OrderId);
                _tracker.OnAccepted(a.ClOrdID.Value, (long)(a.LeavesQty ?? 0UL));
                break;
            case UpModels.OrderTrade t:
                {
                    _tracker.SetOrderId(t.ClOrdID.Value, t.OrderId);
                    var known = _tracker.TryGet(t.ClOrdID.Value, out var o);
                    var symbol = known ? o.Symbol : "?";
                    MarketMakerMetrics.Fills.Add(1, new KeyValuePair<string, object?>("symbol", symbol));
                    _tracker.OnTrade(t.ClOrdID.Value, (long)(t.LeavesQty ?? 0UL));
                    // Fully filled → immediately re-quote the same side so
                    // our side of the book never goes empty. Partial fills
                    // stay resting (still working at the same price).
                    if (known && (t.LeavesQty ?? 0UL) == 0UL)
                        await RequoteAsync(client, o.Symbol, o.IsBuy, ct);
                    break;
                }
            case UpModels.OrderCancelled c:
                {
                    MarketMakerMetrics.Cancelled.Add(1);
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
                    var targetClOrdId = c.OrigClOrdID?.Value
                        ?? (_tracker.TryResolveCancelAttempt(c.ClOrdID.Value, out var linked) ? linked : c.ClOrdID.Value);
                    var known = _tracker.TryGet(targetClOrdId, out var o);
                    _tracker.OnTerminal(targetClOrdId);
                    if (known) await RequoteAsync(client, o.Symbol, o.IsBuy, ct);
                    break;
                }
            case UpModels.OrderRejected r:
                {
                    // A reject of a bot-generated cancel request (see
                    // CancelStaleOrdersAsync) has no OrigClOrdID field to
                    // fall back on like OrderCancelled does, so it's
                    // otherwise indistinguishable from a rejected NEW
                    // order submit. Resolve it via the correlation table
                    // and deliberately do NOT free the original order's
                    // reservation: if it's still genuinely resting,
                    // closing it here would let the next reconcile tick
                    // submit a duplicate order alongside it — the exact
                    // venue-flooding failure mode RFC #703 exists to
                    // prevent. Worst case if this really was a miss-fill
                    // (order already gone at the venue): that side stays
                    // marked "open" — blocking further quoting on it —
                    // until the bot restarts, at which point
                    // cancel-on-disconnect and a fresh OrderTracker clear
                    // the stuck state. A stuck side is an acceptable
                    // trade-off against a duplicated resting order.
                    if (_tracker.TryResolveCancelAttempt(r.ClOrdID.Value, out var origClOrdId))
                    {
                        // Clear the pending-cancel marker (NOT the order
                        // itself — see rationale above) so the next
                        // reconcile tick is free to retry the cancel
                        // instead of treating one as permanently
                        // outstanding.
                        _tracker.ClearPendingCancel(origClOrdId);
                        var stuckKnown = _tracker.TryGet(origClOrdId, out var stuck);
                        var stuckSymbol = stuckKnown ? stuck.Symbol : "?";
                        MarketMakerMetrics.StaleCancelRejected.Add(1,
                            new KeyValuePair<string, object?>("symbol", stuckSymbol));
                        _log.LogWarning(
                            "[mm] stale-order cancel rejected for clordid={ClOrdId} reason={Reason}; leaving tracker state unchanged (see RFC #703)",
                            origClOrdId, r.Reason);
                        break;
                    }
                    var known = _tracker.TryGet(r.ClOrdID.Value, out var o);
                    var symbol = known ? o.Symbol : "?";
                    MarketMakerMetrics.Rejects.Add(1, new KeyValuePair<string, object?>("symbol", symbol));
                    _tracker.OnTerminal(r.ClOrdID.Value);
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
                _tracker.SetOrderId(m.ClOrdID.Value, m.OrderId);
                _tracker.OnAccepted(m.ClOrdID.Value, (long)(m.LeavesQty ?? 0UL));
                break;
        }
    }

    private async Task RequoteAsync(EntryPointClient client, string symbol, bool isBuy, CancellationToken ct)
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
    private async Task ReconcileLoopAsync(EntryPointClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_options.ReconcileInterval, ct); }
            catch (OperationCanceledException) { return; }

            await CancelStaleOrdersAsync(client, ct);
            // RFC #703 book-driven quoting makes cancel/resubmit cycles
            // common rather than rare, so tracker housekeeping now runs
            // every reconcile tick too — see OrderTracker.PruneClosed's
            // doc comment for why this became necessary.
            _tracker.PruneClosed(_options.MaxOrderAge, _tracker.UtcNow);

            foreach (var instr in _options.Instruments)
            {
                if (_priceTracker.IsDelisted(instr.Symbol)) continue;
                if (!_tracker.HasOpenSide(instr.Symbol, isBuy: true))
                    await QuoteSideAsync(client, instr, isBuy: true, ct);
                if (!_tracker.HasOpenSide(instr.Symbol, isBuy: false))
                    await QuoteSideAsync(client, instr, isBuy: false, ct);
            }
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
    /// the original tracked order so the reject still resolves and frees
    /// the stale reservation, instead of retrying identically forever.
    /// </summary>
    private async Task CancelStaleOrdersAsync(EntryPointClient client, CancellationToken ct)
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
            if (await SubmitCancelAsync(client, o, instr, MarketMakerMetrics.StaleCancelSubmitFailed, ct))
            {
                MarketMakerMetrics.StaleOrdersCancelled.Add(1,
                    new KeyValuePair<string, object?>("symbol", o.Symbol));
                _log.LogWarning(
                    "[mm] cancelled stale order clordid={ClOrdId} symbol={Symbol} side={Side} age={Age} (miss-fill guard)",
                    o.ClOrdId, o.Symbol, o.IsBuy ? "buy" : "sell", _tracker.UtcNow - o.SubmittedAtUtc);
            }
        }
    }

    /// <summary>
    /// RFC #703 book-driven quoting: <see cref="MarketDataFeed.BookOrderChanged"/>
    /// signals a symbol here — it can't itself await a cancel since it
    /// runs synchronously on the market-data callback. Coalesced per
    /// symbol by <see cref="_pendingBookSignals"/> so a burst of deltas
    /// for the same symbol only queues one reaction.
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
        if (_pendingBookSignals.TryAdd(symbol, 0))
            _bookSignals.Writer.TryWrite(symbol);
    }

    private async Task BookReactionLoopAsync(EntryPointClient client, CancellationToken ct)
    {
        try
        {
            await foreach (var symbol in _bookSignals.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                _pendingBookSignals.TryRemove(symbol, out _);
                try { await ReactToBookChangeAsync(client, symbol, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[mm] book-reaction failed for {Symbol}", symbol);
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
    /// </summary>
    private async Task ReactToBookChangeAsync(EntryPointClient client, string symbol, CancellationToken ct)
    {
        var instr = FindInstrument(symbol);
        if (instr is null || _priceTracker.IsDelisted(symbol)) return;
        var refPrice = _priceTracker.TryGetReferencePrice(symbol, out var live) ? live : instr.RefPrice;
        var now = _tracker.UtcNow;
        var maxDeviation = instr.TickSize * _options.RequoteDeviationTicks;

        foreach (var isBuy in new[] { true, false })
        {
            if (!_tracker.TryGetActiveSideOrder(symbol, isBuy, out var resting)) continue;
            if (resting.PendingCancelClOrdId is not null) continue;
            // Throttled from the last CANCEL ATTEMPT, not from the
            // order's own submission time: a synchronously-failed or
            // rejected cancel frees PendingCancelClOrdId immediately (see
            // SubmitCancelAsync / HandleEventAsync's OrderRejected case),
            // and SubmittedAtUtc would otherwise already be far in the
            // past for a long-resting order — letting every subsequent
            // book delta retry the cancel with no real throttle at all,
            // the exact venue-flooding shape RFC #703 exists to prevent.
            var lastActivity = resting.LastCancelAttemptAtUtc ?? resting.SubmittedAtUtc;
            if (now - lastActivity < _options.MinRequoteInterval) continue;

            var target = QuoteCalculator.ComputeQuotePrice(instr, isBuy, refPrice);
            if (target <= 0m) continue;
            if (Math.Abs(resting.Price - target) <= maxDeviation) continue;

            if (await SubmitCancelAsync(client, resting, instr, MarketMakerMetrics.BookDrivenRequoteSubmitFailed, ct))
            {
                MarketMakerMetrics.BookDrivenRequotes.Add(1,
                    new KeyValuePair<string, object?>("symbol", symbol),
                    new KeyValuePair<string, object?>("side", isBuy ? "buy" : "sell"));
                _log.LogInformation(
                    "[mm] book-driven requote: cancelling clordid={ClOrdId} symbol={Symbol} side={Side} resting={Resting} target={Target}",
                    resting.ClOrdId, symbol, isBuy ? "buy" : "sell", resting.Price, target);
            }
        }
    }

    /// <summary>
    /// Shared cancel-submit path for both the staleness guard and the
    /// book-driven reactive requote: atomically registers the
    /// cancel-attempt correlation BEFORE the SDK await (see <see
    /// cref="OrderTracker.TryRegisterCancelAttempt"/>'s doc comment on
    /// why atomicity matters here specifically — the staleness guard and
    /// the reactive path run on separate concurrent loops and could
    /// otherwise both target the same order), sends the request, and on
    /// synchronous failure clears the pending-cancel marker it just set
    /// so the order isn't permanently hidden from future guards. Returns
    /// whether the cancel was accepted for transmission (false also when
    /// a cancel was already outstanding for this order from the OTHER
    /// path); callers add their own reason-specific success metric/log.
    /// </summary>
    private async Task<bool> SubmitCancelAsync(EntryPointClient client, TrackedOrder o, InstrumentConfig instr,
        System.Diagnostics.Metrics.Counter<long> submitFailedMetric, CancellationToken ct)
    {
        var cancelClOrdId = (ulong)Interlocked.Increment(ref _nextClOrdId);
        if (!_tracker.TryRegisterCancelAttempt(cancelClOrdId, o.ClOrdId))
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
            submitFailedMetric.Add(1, new KeyValuePair<string, object?>("symbol", o.Symbol));
            _log.LogWarning(ex, "[mm] failed to cancel clordid={ClOrdId} symbol={Symbol}", o.ClOrdId, o.Symbol);
            return false;
        }
    }

    private async Task QuoteSideAsync(EntryPointClient client, InstrumentConfig instr, bool isBuy, CancellationToken ct)
    {
        if (_priceTracker.IsDelisted(instr.Symbol)) return;
        // RFC #703 client-side safety cap (defense in depth against the
        // failure mode in pedrosakuma/B3MatchingPlatform#567): stop adding
        // NEW resting orders once the bot's own tracked open-order count
        // hits the configured ceiling. Existing resting orders are left
        // alone — this only throttles growth, it never panic-cancels.
        var openCount = _tracker.OpenCount();
        if (openCount >= _options.MaxOpenOrders)
        {
            MarketMakerMetrics.SafetyCapHits.Add(1,
                new KeyValuePair<string, object?>("symbol", instr.Symbol));
            _log.LogWarning(
                "[mm] safety cap hit: {OpenCount} open orders >= MaxOpenOrders={MaxOpenOrders}; skipping quote for {Symbol} side={Side}",
                openCount, _options.MaxOpenOrders, instr.Symbol, isBuy ? "buy" : "sell");
            return;
        }
        var refPrice = _priceTracker.TryGetReferencePrice(instr.Symbol, out var live) ? live : instr.RefPrice;
        var price = QuoteCalculator.ComputeQuotePrice(instr, isBuy, refPrice);
        var quantity = QuoteCalculator.QuoteQuantity(instr);
        if (price <= 0m) return; // pathological config; skip silently.

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
        // A SymbolDelisted event can still land between the check above
        // and here; re-checking right before submit shrinks that window.
        // A residual race (delisted arriving during the SubmitAsync
        // await itself) is accepted for this sandbox tool — worst case
        // is one resting order on an already-halted symbol, which the
        // reconcile loop's IsDelisted check then leaves alone (it won't
        // re-quote it, but also won't cancel the stray one automatically).
        if (_priceTracker.IsDelisted(instr.Symbol))
        {
            _tracker.OnTerminal(clOrdId);
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
            await client.SubmitAsync(req, ct);
            MarketMakerMetrics.OrdersSubmitted.Add(1,
                new KeyValuePair<string, object?>("symbol", instr.Symbol),
                new KeyValuePair<string, object?>("side", isBuy ? "buy" : "sell"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _tracker.OnTerminal(clOrdId);
            MarketMakerMetrics.OrdersSubmitFailed.Add(1,
                new KeyValuePair<string, object?>("symbol", instr.Symbol));
            _log.LogWarning(ex, "[mm] quote submit failed for {Symbol} side={Side} clordid={ClOrdId}",
                instr.Symbol, isBuy ? "buy" : "sell", clOrdId);
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
}
