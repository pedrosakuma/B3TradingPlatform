using B3.Trading.Application.MarketData;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Trading.Application.Risk;

/// <summary>
/// Bridges venue-originated trading-status changes
/// (<see cref="IMarketDataSubscriber.TradingStatusChanged"/>) into
/// <see cref="SymbolHaltService"/>, audited via
/// <see cref="SymbolHaltToggledEvent"/> with
/// <see cref="HaltOrigin.Venue"/>.
///
/// <para>
/// Stage A of #370: the SDK does not surface a dedicated halt event
/// yet (tracked in <c>B3MarketDataPlatform#40</c>), so the
/// <see cref="SdkMarketDataSubscriber"/> watches
/// <c>InfoSnapshot.TradingStatus</c> for changes and raises the
/// app-side event. This subscriber translates raw SBE
/// <c>SecurityTradingStatus</c> codes (PAUSE / OPEN / FORBIDDEN) into
/// halt/resume operations on the operator-independent venue origin
/// flag. Operator halts placed via <c>/admin/halts</c> are unaffected
/// by venue resumes (and vice versa) — see <see cref="HaltOrigin"/>.
/// </para>
///
/// <para>
/// Dispatched events go through <see cref="EventDispatcher"/> like
/// any other audited mutation, so the venue halt survives a process
/// restart and shows up in the WAL alongside operator halts. The
/// <c>"origin" = "venue"</c> tag on
/// <c>trading.symbol_halt.toggled</c> lets ops separate the two
/// streams in dashboards.
/// </para>
///
/// <para>
/// Codes other than <c>PAUSE</c>, <c>FORBIDDEN</c> and <c>OPEN</c>
/// are deliberately ignored here: <c>CLOSE</c>, <c>RESERVED</c> and
/// <c>FINAL_CLOSING_CALL</c> are scheduled session phases owned by
/// <c>SessionPhaseService</c>, not halts. Surfacing them as halts
/// would mis-attribute every pre-open to a regulatory action.
/// </para>
/// </summary>
public sealed class VenueHaltSubscriber : IHostedService, IAsyncDisposable
{
    private readonly IMarketDataSubscriber _subscriber;
    private readonly SymbolHaltService _haltService;
    private readonly EventDispatcher _dispatcher;
    private readonly ILogger<VenueHaltSubscriber>? _logger;
    private int _started;

    public VenueHaltSubscriber(
        IMarketDataSubscriber subscriber,
        SymbolHaltService haltService,
        EventDispatcher dispatcher,
        ILogger<VenueHaltSubscriber>? logger = null)
    {
        _subscriber = subscriber ?? throw new ArgumentNullException(nameof(subscriber));
        _haltService = haltService ?? throw new ArgumentNullException(nameof(haltService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe at most once even if the hosted service framework
        // calls Start twice (defensive; happens in some test rigs).
        if (System.Threading.Interlocked.Exchange(ref _started, 1) == 0)
            _subscriber.TradingStatusChanged += OnTradingStatusChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (System.Threading.Interlocked.Exchange(ref _started, 0) == 1)
            _subscriber.TradingStatusChanged -= OnTradingStatusChanged;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _started, 0) == 1)
            _subscriber.TradingStatusChanged -= OnTradingStatusChanged;
        return ValueTask.CompletedTask;
    }

    internal void OnTradingStatusChanged(MarketTradingStatusChange change)
    {
        var status = change.NewStatus;

        bool halted;
        if (SecurityTradingStatusCodes.IsHalt(status))
        {
            halted = true;
        }
        else if (SecurityTradingStatusCodes.IsOpen(status))
        {
            halted = false;
        }
        else
        {
            // Phase code (CLOSE / RESERVED / FINAL_CLOSING_CALL / …) —
            // not a halt; SessionPhaseService owns it.
            return;
        }

        // Suppress no-op transitions: if the venue origin already has
        // the desired state we do not touch the WAL. Halting twice
        // from the same origin would otherwise produce a stream of
        // identical SymbolHaltToggledEvent records in the audit log.
        var alreadyHaltedByVenue = _haltService.IsHaltedBy(change.Symbol, HaltOrigin.Venue);
        if (halted == alreadyHaltedByVenue) return;

        try
        {
            _dispatcher.Dispatch(
                new SymbolHaltToggledEvent
                {
                    Symbol = change.Symbol,
                    Halted = halted,
                    ActorUserId = null, // venue-originated, no operator
                    Origin = HaltOrigin.Venue,
                },
                () =>
                {
                    if (halted) _haltService.Halt(change.Symbol, HaltOrigin.Venue);
                    else _haltService.Resume(change.Symbol, HaltOrigin.Venue);
                });

            MetricsRegistry.SymbolHaltToggled.Add(1,
                new KeyValuePair<string, object?>("halted", halted),
                new KeyValuePair<string, object?>("origin", "venue"));

            _logger?.LogInformation(
                "Venue halt {Action} for {Symbol}: status {PreviousStatus} → {NewStatus}",
                halted ? "applied" : "cleared",
                change.Symbol,
                change.PreviousStatus,
                change.NewStatus);
        }
        catch (Exception ex)
        {
            // Never let a venue halt failure tear down the SDK reader
            // loop. The next status delta will retry; missing one halt
            // is worse than missing a hundred, but worse still is
            // losing the market-data feed entirely.
            _logger?.LogError(ex,
                "Failed to apply venue halt for {Symbol} (status {NewStatus})",
                change.Symbol, change.NewStatus);
        }
    }
}
