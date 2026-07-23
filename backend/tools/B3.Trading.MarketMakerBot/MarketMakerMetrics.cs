using System.Diagnostics.Metrics;

namespace B3.Trading.MarketMakerBot;

/// <summary>
/// <see cref="System.Diagnostics.Metrics"/> instruments emitted by the
/// bot. Visible to any host that registers the meter
/// <c>"B3.Trading.MarketMakerBot"</c> with an OpenTelemetry exporter.
/// MVP scope: log-shaped only — wiring an OTLP exporter is left to a
/// follow-up if/when the bot lands in observability dashboards.
/// </summary>
public static class MarketMakerMetrics
{
    public const string MeterName = "B3.Trading.MarketMakerBot";

    public static readonly Meter Meter = new(MeterName, "0.1.0");

    /// <summary>Orders accepted by the bot for transmission. Tagged
    /// <c>{symbol, side}</c>.</summary>
    public static readonly Counter<long> OrdersSubmitted =
        Meter.CreateCounter<long>("bot.orders.submitted");

    /// <summary>Outbound submit attempts that the SDK rejected synchronously
    /// (transport error, terminated session, etc).</summary>
    public static readonly Counter<long> OrdersSubmitFailed =
        Meter.CreateCounter<long>("bot.orders.submit_failed");

    /// <summary>Trades observed via OrderTrade events. Tagged <c>{symbol}</c>.</summary>
    public static readonly Counter<long> Fills =
        Meter.CreateCounter<long>("bot.fills.received");

    /// <summary>OrderRejected events received. Tagged <c>{symbol}</c>.</summary>
    public static readonly Counter<long> Rejects =
        Meter.CreateCounter<long>("bot.orders.rejected");

    /// <summary>OrderCancelled events received.</summary>
    public static readonly Counter<long> Cancelled =
        Meter.CreateCounter<long>("bot.orders.cancelled");

    /// <summary>RFC #703 miss-fill/staleness guard: orders explicitly
    /// cancelled by <c>MarketMakerWorker.CancelStaleOrdersAsync</c> for
    /// exceeding <see cref="MarketMakerBotOptions.MaxOrderAge"/>. Tagged
    /// <c>{symbol}</c>. Should normally stay at zero — a nonzero rate
    /// means the event-driven requote path is missing terminal events.</summary>
    public static readonly Counter<long> StaleOrdersCancelled =
        Meter.CreateCounter<long>("bot.orders.stale_cancelled");

    /// <summary>RFC #703 miss-fill/staleness guard: the venue rejected a
    /// stale-order cancel request (e.g. the order was already
    /// terminal/unknown, or a transient reject). The tracker deliberately
    /// leaves the original order's reservation untouched in this case —
    /// see <c>MarketMakerWorker.HandleEventAsync</c>'s OrderRejected case
    /// for the rationale. Tagged <c>{symbol}</c>.</summary>
    public static readonly Counter<long> StaleCancelRejected =
        Meter.CreateCounter<long>("bot.orders.stale_cancel_rejected");

    /// <summary>RFC #703 miss-fill/staleness guard: a stale-order cancel
    /// request failed synchronously (transport error, terminated session,
    /// etc) before any ER could arrive to resolve it. Tagged
    /// <c>{symbol}</c>.</summary>
    public static readonly Counter<long> StaleCancelSubmitFailed =
        Meter.CreateCounter<long>("bot.orders.stale_cancel_submit_failed");

    /// <summary>RFC #703 client-side safety cap: incremented every time a
    /// quote is skipped because <see cref="OrderTracker.OpenCount"/> is at
    /// or above <see cref="MarketMakerBotOptions.MaxOpenOrders"/>. Should
    /// normally stay at zero.</summary>
    public static readonly Counter<long> SafetyCapHits =
        Meter.CreateCounter<long>("bot.orders.safety_cap_hit");

    /// <summary>RFC #703 book-driven quoting: a market-data book delta not
    /// caused by the bot's own resting order (see
    /// <c>OrderTracker.IsOwnOrder</c>) revealed a side's resting price had
    /// drifted past <see cref="MarketMakerBotOptions.RequoteDeviationTicks"/>
    /// from the freshly-computed target, triggering a reactive
    /// cancel-and-requote instead of waiting for the resting order to
    /// terminate on its own. Tagged <c>{symbol, side}</c>.</summary>
    public static readonly Counter<long> BookDrivenRequotes =
        Meter.CreateCounter<long>("bot.orders.book_driven_requote");

    /// <summary>RFC #703 book-driven quoting: a reactive cancel triggered
    /// by <c>MarketMakerWorker.ReactToBookChangeAsync</c> failed
    /// synchronously before any ER could arrive to resolve it. Tagged
    /// <c>{symbol}</c>.</summary>
    public static readonly Counter<long> BookDrivenRequoteSubmitFailed =
        Meter.CreateCounter<long>("bot.orders.book_driven_requote_submit_failed");
}
