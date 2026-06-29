using System.Diagnostics.Metrics;
using B3.Trading.Application.Observability;

namespace B3.Trading.EntryPointListener.Hosting;

/// <summary>
/// OpenTelemetry-compatible metric instruments for the inbound FIXP listener.
/// Reuses the <c>B3.Trading</c> meter from <see cref="MetricsRegistry"/>.
/// </summary>
public static class FixpListenerMetrics
{
    private static readonly Meter Meter = MetricsRegistry.Meter;

    /// <summary>Currently established sessions.</summary>
    public static readonly UpDownCounter<int> SessionsActive =
        Meter.CreateUpDownCounter<int>("entrypoint_listener.sessions_active");

    /// <summary>Negotiate outcomes. Tag: outcome (ok, reject:&lt;code&gt;).</summary>
    public static readonly Counter<long> NegotiateTotal =
        Meter.CreateCounter<long>("entrypoint_listener.negotiate_total");

    /// <summary>Inbound orders. Tags: kind (new, cancel), outcome (accepted).</summary>
    public static readonly Counter<long> OrdersInTotal =
        Meter.CreateCounter<long>("entrypoint_listener.orders_in_total");

    /// <summary>Outbound execution reports routed to bots.</summary>
    public static readonly Counter<long> ErOutTotal =
        Meter.CreateCounter<long>("entrypoint_listener.er_out_total");

    /// <summary>Count of messages currently buffered across all credentials.</summary>
    public static readonly UpDownCounter<long> ErOutboundBuffered =
        Meter.CreateUpDownCounter<long>("entrypoint_listener.er_outbound_buffered");

    /// <summary>Messages dropped due to buffer overflow.</summary>
    public static readonly Counter<long> ErOutboundDroppedTotal =
        Meter.CreateCounter<long>("entrypoint_listener.er_outbound_dropped_total");

    /// <summary>Retransmit request outcomes. Tag: outcome (replay, reject).</summary>
    public static readonly Counter<long> RetransmitRequestsTotal =
        Meter.CreateCounter<long>("entrypoint_listener.retransmit_requests_total");

    /// <summary>1 when the FIXP listener is enabled at boot.</summary>
    public static readonly UpDownCounter<int> Enabled =
        Meter.CreateUpDownCounter<int>("entrypoint_listener.enabled");

    /// <summary>Successful TLS handshakes.</summary>
    public static readonly Counter<long> TlsHandshakeCompleted =
        Meter.CreateCounter<long>("fixp.handshake.tls.completed.total");

    /// <summary>
    /// TLS handshake duration in ms (#533). Recorded for both successful and
    /// failed/rejected handshakes (tag: outcome=ok|tls|mtls), so a public
    /// dashboard can chart latency and a flood shows up as p99 climbing
    /// toward <c>Tls:HandshakeTimeout</c>.
    /// </summary>
    public static readonly Histogram<double> TlsHandshakeDurationMs =
        Meter.CreateHistogram<double>("fixp.handshake.tls.duration_ms");

    /// <summary>Rejected connections. Tag: reason.</summary>
    public static readonly Counter<long> ConnectionsRejected =
        Meter.CreateCounter<long>("fixp.connections.rejected.total");

    /// <summary>
    /// Client-certificate (mTLS) validation outcomes during the TLS
    /// handshake (RFC user-bot-fixp-mtls-v0 §6). Tag: outcome
    /// (<c>ok</c>, <c>absent</c>, <c>reject:&lt;reason&gt;</c>). Lets
    /// <c>Optional</c>-mode adoption be measured (watch <c>absent</c> fall to
    /// zero) before flipping to <c>Required</c>.
    /// </summary>
    public static readonly Counter<long> MtlsClientCertsTotal =
        Meter.CreateCounter<long>("entrypoint_listener.mtls_client_certs_total");
}
