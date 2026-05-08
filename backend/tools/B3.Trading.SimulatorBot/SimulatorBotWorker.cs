using B3.EntryPoint.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Up = B3.EntryPoint.Client;
using UpModels = B3.EntryPoint.Client.Models;
using UpState = B3.EntryPoint.Client.State;

namespace B3.Trading.SimulatorBot;

/// <summary>
/// The bot's main loop. Single FIXP session against matching-platform;
/// concurrent submit + receive paths separated by the SDK's own
/// channels:
/// <list type="bullet">
///   <item><see cref="ReceiveLoopAsync"/> drains <c>_client.Events()</c>
///         and feeds the <see cref="OrderTracker"/>.</item>
///   <item><see cref="SubmitLoopAsync"/> ticks on
///         <see cref="SimulatorBotOptions.TickInterval"/>; each tick walks
///         the configured instrument list and submits at most one new
///         order per instrument (subject to the in-flight cap), then
///         emits auto-cancels for stale open orders.</item>
/// </list>
/// </summary>
internal sealed class SimulatorBotWorker : BackgroundService
{
    private readonly SimulatorBotOptions _options;
    private readonly OrderTracker _tracker;
    private readonly ILogger<SimulatorBotWorker> _log;
    private readonly Random _rng;
    private long _nextClOrdId;
    private EntryPointClient? _client;

    public SimulatorBotWorker(IOptions<SimulatorBotOptions> options, OrderTracker tracker,
        ILogger<SimulatorBotWorker> log)
    {
        _options = options.Value;
        _tracker = tracker;
        _log = log;
        _rng = _options.RandomSeed is { } seed ? new Random(seed) : new Random();
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
            _log.LogWarning(ex, "[bot] failed to load persisted SessionState; falling back to configured SessionVerId.");
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
            _log.LogInformation("[bot] connecting to {Endpoint} session={Session} verId={VerId}",
                _options.Endpoint, _options.SessionId, resolvedVerId);
            await _client.ConnectAsync(stoppingToken);
            _log.LogInformation("[bot] connected; instruments={Count} tick={Tick} cap={Cap}",
                _options.Instruments.Count, _options.TickInterval, _options.MaxInFlightPerSymbol);

            var receive = ReceiveLoopAsync(_client, stoppingToken);
            var submit = SubmitLoopAsync(_client, stoppingToken);
            await Task.WhenAny(receive, submit);
            // Surface the failing task's exception (if any).
            await Task.WhenAll(receive, submit);
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "[bot] fatal error in main loop");
            throw;
        }
        finally
        {
            try { await _client.DisposeAsync(); } catch { /* ignore */ }
        }
    }

    private async Task ReceiveLoopAsync(EntryPointClient client, CancellationToken ct)
    {
        await foreach (var ev in client.Events(ct).ConfigureAwait(false))
        {
            try
            {
                HandleEvent(ev);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[bot] failed to handle event {Event}", ev.GetType().Name);
            }
        }
    }

    private void HandleEvent(UpModels.EntryPointEvent ev)
    {
        switch (ev)
        {
            case UpModels.OrderAccepted a:
                _tracker.OnAccepted(a.ClOrdID.Value, (long)(a.LeavesQty ?? 0UL));
                break;
            case UpModels.OrderTrade t:
                {
                    var symbol = _tracker.TryGet(t.ClOrdID.Value, out var o) ? o.Symbol : "?";
                    SimulatorBotMetrics.Fills.Add(1,
                        new KeyValuePair<string, object?>("symbol", symbol));
                    _tracker.OnTrade(t.ClOrdID.Value, (long)(t.LeavesQty ?? 0UL));
                    break;
                }
            case UpModels.OrderCancelled c:
                SimulatorBotMetrics.Cancelled.Add(1);
                _tracker.OnTerminal(c.ClOrdID.Value);
                break;
            case UpModels.OrderRejected r:
                {
                    var symbol = _tracker.TryGet(r.ClOrdID.Value, out var o) ? o.Symbol : "?";
                    SimulatorBotMetrics.Rejects.Add(1,
                        new KeyValuePair<string, object?>("symbol", symbol));
                    _tracker.OnTerminal(r.ClOrdID.Value);
                    break;
                }
            case UpModels.OrderModified m:
                _tracker.OnAccepted(m.ClOrdID.Value, (long)(m.LeavesQty ?? 0UL));
                break;
        }
    }

    private async Task SubmitLoopAsync(EntryPointClient client, CancellationToken ct)
    {
        // First tick after a small delay so the receive loop is up.
        await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        while (!ct.IsCancellationRequested)
        {
            foreach (var instr in _options.Instruments)
            {
                var open = _tracker.InFlightCount(instr.Symbol);
                var draft = OrderPattern.Next(_rng, instr, _options.CrossProbability,
                    open, _options.MaxInFlightPerSymbol);
                if (draft is { } d)
                {
                    await TrySubmitAsync(client, d, ct);
                }
            }

            if (_options.AutoCancelAfter > TimeSpan.Zero)
            {
                foreach (var stale in _tracker.SnapshotStaleOpen(_options.AutoCancelAfter))
                {
                    await TryCancelAsync(client, stale, "auto", ct);
                }
            }

            try { await Task.Delay(_options.TickInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TrySubmitAsync(EntryPointClient client, OrderDraft d, CancellationToken ct)
    {
        var clOrdId = (ulong)Interlocked.Increment(ref _nextClOrdId);
        // Register BEFORE the SDK await — the matching ER can race ahead
        // of the await on a fast wire (mirrors trading-host's pattern).
        _tracker.RegisterSubmit(clOrdId, d.Symbol, d.Price, d.Quantity, d.IsBuy);
        var req = new UpModels.NewOrderRequest
        {
            ClOrdID = new UpModels.ClOrdID(clOrdId),
            SecurityId = d.SecurityId,
            Side = d.IsBuy ? UpModels.Side.Buy : UpModels.Side.Sell,
            OrderType = UpModels.OrderType.Limit,
            Price = d.Price,
            OrderQty = (ulong)d.Quantity,
            TimeInForce = UpModels.TimeInForce.Day,
        };
        try
        {
            await client.SubmitAsync(req, ct);
            SimulatorBotMetrics.OrdersSubmitted.Add(1,
                new KeyValuePair<string, object?>("symbol", d.Symbol),
                new KeyValuePair<string, object?>("side", d.IsBuy ? "buy" : "sell"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _tracker.OnTerminal(clOrdId);
            SimulatorBotMetrics.OrdersSubmitFailed.Add(1,
                new KeyValuePair<string, object?>("symbol", d.Symbol));
            _log.LogWarning(ex, "[bot] submit failed for {Symbol} clordid={ClOrdId}", d.Symbol, clOrdId);
        }
    }

    private async Task TryCancelAsync(EntryPointClient client, TrackedOrder order, string reason, CancellationToken ct)
    {
        var cancelClOrdId = (ulong)Interlocked.Increment(ref _nextClOrdId);
        var req = new UpModels.CancelOrderRequest
        {
            ClOrdID = new UpModels.ClOrdID(cancelClOrdId),
            OrigClOrdID = new UpModels.ClOrdID(order.ClOrdId),
            SecurityId = LookupSecurityId(order.Symbol),
            Side = order.IsBuy ? UpModels.Side.Buy : UpModels.Side.Sell,
        };
        try
        {
            await client.CancelAsync(req, ct);
            SimulatorBotMetrics.CancelsSent.Add(1,
                new KeyValuePair<string, object?>("reason", reason));
            // Don't mark the original as terminal here — wait for the
            // OrderCancelled ER. The cancel may yet be rejected.
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[bot] cancel failed for clordid={ClOrdId}", order.ClOrdId);
        }
    }

    private ulong LookupSecurityId(string symbol)
    {
        foreach (var i in _options.Instruments)
            if (string.Equals(i.Symbol, symbol, StringComparison.Ordinal)) return i.SecurityId;
        return 0UL;
    }

    internal static System.Net.DnsEndPoint ParseEndpoint(string endpoint) =>
        EndpointParser.Parse(endpoint);
}
