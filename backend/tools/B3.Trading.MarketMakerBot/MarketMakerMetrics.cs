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

    /// <summary>RFC #703 client-side safety cap: incremented every time a
    /// quote is skipped because <see cref="OrderTracker.OpenCount"/> is at
    /// or above <see cref="MarketMakerBotOptions.MaxOpenOrders"/>. Should
    /// normally stay at zero.</summary>
    public static readonly Counter<long> SafetyCapHits =
        Meter.CreateCounter<long>("bot.orders.safety_cap_hit");
}
