# Perf hardening v0 — alert rules

Concrete Prometheus alert rules for the metrics, log signals and
config knobs catalogued in [`../RUNBOOK.md`](../RUNBOOK.md) §1.
Drop the `groups:` block below into your existing
`alerting_rules.yml` (or the equivalent file your AlertManager
deploy loads).

The trading-host scrape pipeline is documented in
[`../METRICS.md`](../METRICS.md): app-meter signals reach
Prometheus via the OTel collector's `prometheusexporter` on
`otel-collector:8889`. OTel meter names are translated to
Prometheus convention (dots → underscores; counters get a
`_total` suffix); the rules below already use the
post-translation names.

> **Translation notes.**
>
> - The repo currently has **no** committed alert rules — only the
>   scrape config in [`docker/observability/prometheus.yml`](../../docker/observability/prometheus.yml).
>   These rules are intentionally portable: drop them into any
>   Prometheus + AlertManager stack, or translate them to the
>   recording / alerting equivalent in your ops platform
>   (Datadog monitor, Grafana Cloud alert, etc.) — the
>   `expr:` / `for:` / labels are the contract.
> - The `.abandoned` rule is **log-derived** (Loki / equivalent),
>   not Prometheus — see §1.2 of the runbook for why. The example
>   below is LogQL; translate the selector to whatever log query
>   language your aggregator uses.

---

## 1. Prometheus alerting rules

```yaml
groups:
  - name: perf-hardening-v0
    interval: 30s
    rules:
      # 1.1 WS hub fan-out drops — see RUNBOOK §1.1 / PR #220.
      # Any sustained non-zero rate means real subscribers are
      # losing events and forced into reconnect-and-replay.
      - alert: WsHubFanOutDropping
        expr: rate(trading_dispatcher_ws_fanout_dropped_total[1m]) > 0
        for: 1m
        labels:
          severity: page
          subsystem: ws-hub
          rfc: perf-hardening-v0
          rfc_section: "5.2"
        annotations:
          summary: "WS hub publish→drain queue overflowed (DropOldest, 64K)"
          description: |
            trading.dispatcher.ws_fanout_dropped has been > 0/s for
            1 minute on {{ $labels.instance }}. The
            WebSocketExecutionEventSink's single drain thread is
            behind the dispatcher's publish rate, so all subscribed
            WS clients will see a gap and must reconnect-and-replay.
            See docs/RUNBOOK.md §1.1 for the triage sequence.
          runbook_url: "https://github.com/pedrosakuma/B3TradingPlatform/blob/main/docs/RUNBOOK.md#11-tradingdispatcherws_fanout_dropped-counter"

```

### 1.1 Config-drift detection (deferred)

Issue #231 also asks for info-level drift detection on
`OutboundDrainShutdownTimeout` (default `1s`) and
`GroupCommitMaxRecords` (default `512`). Implementing those as
**Prometheus alerts requires a `build-info`-style config gauge that
the trading-host does not emit today** — there is no
`trading_entrypoint_listener_outbound_drain_shutdown_timeout_seconds`
or `trading_persistence_group_commit_max_records` series in the
current scrape, so any rule of the form `expr: <metric> != <default>`
would silently never fire (no series → no samples → no alert).

Until those gauges are added (tracked as a follow-up to #231), the
recommended path is a **deploy-time** check rather than a runtime
alert: diff the rendered `appsettings.Production.json` (or the
equivalent K8s `ConfigMap` / Helm values) against the documented
defaults in CI, and fail the deploy on unexplained drift. Skeleton
rule for when the gauges land:

```yaml
# To be enabled once the trading-host emits build-info gauges
# for the perf-v0 config knobs (follow-up to #231).
#
# - alert: PerfV0OutboundDrainTimeoutDrift
#   expr: trading_entrypoint_listener_outbound_drain_shutdown_timeout_seconds != 1
#   for: 5m
#   labels:  { severity: info, subsystem: fixp-listener,  rfc_section: "5.3" }
# - alert: PerfV0GroupCommitMaxRecordsDrift
#   expr: trading_persistence_group_commit_max_records != 512
#   for: 5m
#   labels:  { severity: info, subsystem: persistence,    rfc_section: "4.2" }
```

## 2. Log-derived alert (`.abandoned`)

The drain-loop **abandoned** signal is a structured warning log,
not an OTel counter (see RUNBOOK §1.2). LogQL example for a
Loki-backed stack:

```logql
sum(count_over_time({app="b3-trading-host"}
    |= "fixp.outbound.drain.shutdown.abandoned" [5m])) > 0
```

Translate the selector to your aggregator. Recommended
AlertManager wiring:

| Field | Value |
|---|---|
| Severity | `page` |
| Subsystem | `fixp-listener` |
| For | immediate (any occurrence) |
| Runbook | [`../RUNBOOK.md#12-fixpoutbounddrainshutdownabandoned-warning-log`](../RUNBOOK.md#12-fixpoutbounddrainshutdownabandoned-warning-log) |

Do **not** alert on the sibling `fixp.outbound.drain.shutdown.timeout`
log line at page severity — that is the documented "deadline
elapsed" path bounded by `OutboundDrainShutdownTimeout` (§1.3) and
is recoverable by design.

## 3. Summary table

| Rule | Type | Severity | Source |
|---|---|---|---|
| `WsHubFanOutDropping` | Prometheus | page | PR #220 |
| Drain `.abandoned` | log-derived | page | PR #219 |
| `OutboundDrainShutdownTimeout` drift | deploy-time check (gauge TBD, follow-up to #231) | info | PR #219 |
| `GroupCommitMaxRecords` drift | deploy-time check (gauge TBD, follow-up to #231) | info | PR #214 |
