using B3.EntryPoint.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        };

        _client = new EntryPointClient(clientOpts);
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
            await Task.WhenAny(receive, reconcile);
            // Surface the failing task's exception (if any).
            await Task.WhenAll(receive, reconcile);
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "[mm] fatal error in main loop");
            throw;
        }
        finally
        {
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
                _tracker.OnAccepted(a.ClOrdID.Value, (long)(a.LeavesQty ?? 0UL));
                break;
            case UpModels.OrderTrade t:
                {
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
                    var known = _tracker.TryGet(c.ClOrdID.Value, out var o);
                    _tracker.OnTerminal(c.ClOrdID.Value);
                    if (known) await RequoteAsync(client, o.Symbol, o.IsBuy, ct);
                    break;
                }
            case UpModels.OrderRejected r:
                {
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

    private async Task QuoteSideAsync(EntryPointClient client, InstrumentConfig instr, bool isBuy, CancellationToken ct)
    {
        if (_priceTracker.IsDelisted(instr.Symbol)) return;
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
