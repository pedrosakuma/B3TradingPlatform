using System.Diagnostics.Metrics;

namespace B3.Trading.SimulatorBot;

/// <summary>
/// <see cref="System.Diagnostics.Metrics"/> instruments emitted by the
/// bot. Visible to any host that registers the meter
/// <c>"B3.Trading.SimulatorBot"</c> with an OpenTelemetry exporter.
/// MVP scope: log-shaped only — wiring an OTLP exporter is left to a
/// follow-up if/when the bot lands in observability dashboards.
/// </summary>
public static class SimulatorBotMetrics
{
    public const string MeterName = "B3.Trading.SimulatorBot";

    public static readonly Meter Meter = new(MeterName, "0.1.0");

    /// <summary>Orders accepted by the bot for transmission. Tagged
    /// <c>{symbol, side}</c>.</summary>
    public static readonly Counter<long> OrdersSubmitted =
        Meter.CreateCounter<long>("bot.orders.submitted");

    /// <summary>Outbound submit attempts that the SDK rejected synchronously
    /// (transport error, terminated session, etc).</summary>
    public static readonly Counter<long> OrdersSubmitFailed =
        Meter.CreateCounter<long>("bot.orders.submit_failed");

    /// <summary>Cancel requests sent. Tagged <c>{reason=auto|shutdown}</c>.</summary>
    public static readonly Counter<long> CancelsSent =
        Meter.CreateCounter<long>("bot.orders.cancels_sent");

    /// <summary>Trades observed via OrderTrade events. Tagged <c>{symbol}</c>.</summary>
    public static readonly Counter<long> Fills =
        Meter.CreateCounter<long>("bot.fills.received");

    /// <summary>OrderRejected events received. Tagged <c>{symbol}</c>.</summary>
    public static readonly Counter<long> Rejects =
        Meter.CreateCounter<long>("bot.orders.rejected");

    /// <summary>OrderCancelled events received.</summary>
    public static readonly Counter<long> Cancelled =
        Meter.CreateCounter<long>("bot.orders.cancelled");
}
