# Public auth-surface alerting & dashboard (go-public, #533)

Portable Prometheus rules + a Grafana dashboard for the **user-bot FIXP
listener** once it is exposed on the public internet (epic #527, mTLS #528,
connection-caps #529). These watch the surface a hostile internet can
actually reach: TLS/mTLS handshakes, Negotiate auth, connection caps, and
per-credential order flow.

> The executable v1 rules are auto-loaded from
> `docker/observability/prometheus/rules/v1/b3-trading.rules.yml` and tested
> by `promtool` in CI. This page explains their thresholds and response.
> The dashboard JSON lives at
> `docker/observability/grafana/dashboards/public-auth.json` and is
> auto-provisioned.

Metric names below use the Prometheus form (meter dots → underscores,
counters keep `_total`). See `docs/METRICS.md` for the catalog.

---

## 1. Prometheus alerting rules

```yaml
groups:
  - name: public-auth-surface-v0
    interval: 30s
    rules:
      # 1.1 Sustained Negotiate auth-rejection rate — a credential-stuffing
      # or brute-force probe. A non-zero baseline is normal (typos); a
      # sustained spike on the public surface is an attack signal.
      - alert: PublicAuthNegotiateRejectSpike
        expr: sum(rate(entrypoint_listener_negotiate_total{outcome=~"reject:.*"}[5m])) > 1
        for: 5m
        labels:
          severity: page
          subsystem: fixp-listener
          surface: public
        annotations:
          summary: "FIXP Negotiate rejections sustained >1/s for 5m"
          description: |
            entrypoint_listener.negotiate_total{outcome=reject:*} > 1/s for
            5 minutes on {{ $labels.instance }}. Credential-stuffing /
            brute-force against the public user-bot port is likely. Confirm
            the source IPs in the rejected-connection logs and tighten
            ConnectionCaps.DeniedIps / AllowedIps if a single origin
            dominates. See docs/operations/fixp-listener.md.

      # 1.2 Connection caps / rate-limit kicking in — DoS or misconfig.
      # ConnectionsRejected reasons: tls/mtls/accept_rate_limit/
      # ip_blocked/max_connections. Any sustained rate means a real client
      # is being turned away (or a flood is being absorbed).
      - alert: PublicConnectionsRejectedSpike
        expr: sum by (reason) (rate(fixp_connections_rejected_total[5m])) > 5
        for: 5m
        labels:
          severity: page
          subsystem: fixp-listener
          surface: public
        annotations:
          summary: "FIXP connections rejected >5/s ({{ $labels.reason }}) for 5m"
          description: |
            fixp.connections.rejected{reason={{ $labels.reason }}} > 5/s for
            5 minutes. accept_rate_limit / max_connections = the global or
            per-IP caps are saturated (flood, or caps set too low for real
            load); ip_blocked = a denied IP is hammering; tls/mtls =
            handshake failures (bad/expired client certs or a scanner).

      # 1.3 mTLS client-cert rejections — expired/revoked/untrusted certs.
      - alert: PublicMtlsClientCertRejectSpike
        expr: sum(rate(entrypoint_listener_mtls_client_certs_total{outcome=~"reject:.*"}[5m])) > 0.5
        for: 5m
        labels:
          severity: ticket
          subsystem: fixp-listener
          surface: public
        annotations:
          summary: "mTLS client-cert rejections sustained for 5m"
          description: |
            entrypoint_listener.mtls_client_certs_total{outcome=reject:*}
            sustained >0.5/s. Likely an expired/rotated/untrusted bot cert
            or a probe presenting bad certs. Cross-check the CA trust bundle
            and per-credential cert pins (#540).

      # 1.4 Handshake latency climbing toward the timeout — TLS DoS.
      - alert: PublicTlsHandshakeLatencyHigh
        expr: histogram_quantile(0.99, sum by (le) (rate(fixp_handshake_tls_duration_ms_bucket[5m]))) > 2000
        for: 10m
        labels:
          severity: ticket
          subsystem: fixp-listener
          surface: public
        annotations:
          summary: "p99 TLS handshake latency > 2s for 10m"
          description: |
            p99 of fixp.handshake.tls.duration_ms > 2000ms for 10m. The
            handshake path is saturating toward Tls:HandshakeTimeout (5s
            default). Resource pressure or a slow-loris-style flood; verify
            CPU and ConnectionCaps headroom.

      # 1.5 Outbound ER buffer overflow — bots can't keep up / are gone.
      - alert: PublicErOutboundDropping
        expr: rate(entrypoint_listener_er_outbound_dropped_total[5m]) > 0
        for: 5m
        labels:
          severity: ticket
          subsystem: fixp-listener
          surface: public
        annotations:
          summary: "FIXP outbound ER buffer dropping for 5m"
          description: |
            entrypoint_listener.er_outbound_dropped_total > 0/s sustained:
            a bot's outbound buffer overflowed (slow/stalled consumer). The
            bot must reconnect-and-replay; persistent drops mean a wedged
            credential. Identify it and disconnect/throttle.

      # 1.6 Listener disabled / scrape gone — public port may be down.
      - alert: PublicFixpListenerDown
        expr: max(entrypoint_listener_enabled) < 1 or absent(entrypoint_listener_enabled)
        for: 2m
        labels:
          severity: page
          subsystem: fixp-listener
          surface: public
        annotations:
          summary: "FIXP listener disabled or not reporting"
          description: |
            entrypoint_listener.enabled is 0 or absent for 2m — the public
            user-bot port is down or the trading-host stopped scraping.
```

## 2. Dashboard

`public-auth.json` charts: active sessions, Negotiate outcomes, rejected
connections by reason, mTLS cert outcomes, TLS handshake p50/p99 latency,
and per-credential order intake. Use it as the on-call landing page when
any rule above fires.
