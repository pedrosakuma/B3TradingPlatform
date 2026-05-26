# Metrics

The trading-host emits OpenTelemetry metrics + traces opt-in via the
standard `OTEL_EXPORTER_OTLP_ENDPOINT` environment variable. When that
variable is unset, the OTel SDK is **not registered at all** — no
exporter, no periodic pump, no warnings — so dev loops and unit tests pay
zero overhead.

## Activation

Minimum env to turn it on:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
OTEL_EXPORTER_OTLP_PROTOCOL=grpc        # default; can also be http/protobuf
```

All other `OTEL_*` standard envs (`OTEL_RESOURCE_ATTRIBUTES`,
`OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_METRIC_EXPORT_INTERVAL`,
`OTEL_TRACES_SAMPLER`, etc.) are honoured by the SDK directly — we don't
shadow them in code.

## Resource attributes

Every signal carries:

| Attribute | Value |
|---|---|
| `service.name` | `b3-trading-host` (const, used by Grafana datasources) |
| `service.version` | Assembly version (`1.0.0.0` today) |
| `deployment.environment` | `ASPNETCORE_ENVIRONMENT` (`Development`, `Docker`, `Production`, ...) |

Operators can layer more via `OTEL_RESOURCE_ATTRIBUTES=foo=bar,baz=qux`.

## Application meter (`B3.Trading`)

All app-layer instruments live on a single `Meter("B3.Trading", "1.0.0")`
declared in
[`MetricsRegistry.cs`](../backend/src/B3.Trading.Application/Observability/MetricsRegistry.cs).
One `AddMeter(MetricsRegistry.Meter.Name)` call wires the lot.

| OTel name | Type | Tags |
|---|---|---|
| `trading.orders.submitted` | Counter | `symbol`, `side`, `source` (manual/algo), `firmId`, `security_type` (equity/option/unknown) |
| `trading.orders.rejected_by_risk` | Counter | `check` |
| `trading.orders.gateway_failed` | Counter | (none) |
| `trading.orders.cancel_requested` | Counter | (none) |
| `trading.er.received` | Counter | `type` (Fill, Canceled, Rejected, ...) |
| `trading.kill_switch.toggled` | Counter | `state` (on/off) |
| `trading.wal.appended` | Counter | (none) |
| `trading.wal.backpressure` | Counter | `call_site` |
| `trading.wal.segments_rotated` | Counter | (none) |
| `trading.snapshots.taken` | Counter | (none) |
| `trading.snapshots.failed` | Counter | (none) |
| `trading.snapshots.duration_ms` | Histogram | (none) |
| `trading.recovery.events_replayed` | Counter | (none) |
| `trading.ws.connections.active` | UpDownCounter | (none) |
| `trading.ws.messages.sent` | Counter | `topic` |
| `trading.drain.rejections` | Counter | `route` |
| `trading.entrypoint.connected` | UpDownCounter | `firm` |
| `trading.entrypoint.events_received` | Counter | `firm`, `type` |
| `trading.entrypoint.reconnect_attempts` | Counter | `firm` |
| `trading.entrypoint.translation_errors` | Counter | `firm` |
| `trading.entrypoint.business_rejects` | Counter | `firm`, `code` |
| `trading.entrypoint.terminated` | Counter | `firm`, `cause` |
| `trading.marketdata.subscribe_errors` | Counter | `symbol`, `reason` |
| `trading.risk.refprice.lookups` | Counter | `source` (live/fallback/missing) |
| `trading.risk.refprice.staleness_seconds` | Observable Gauge | `symbol` |
| `trading.risk.collar.bypassed_no_reference` | Counter | `symbol` |
| `trading.risk.rolling_notional.bypassed_no_reference` | Counter | `symbol` |
| `trading.risk.rolling_notional.active_buckets` | Observable Gauge | `scope` (end_client/firm) |
| `trading.risk.order_rate.active_buckets` | Observable Gauge | `scope` (end_client/firm) |
| `trading.algo.signals_consumed` | Counter | `kind` (created/cancel_requested/child_execution_observed) |
| `trading.algo.signals_dropped` | Counter | `kind` |
| `trading.algo.children_submitted` | Counter | `type` (iceberg/twap) |
| `trading.algo.twap.slice_fire_jitter` | Histogram (ms) | (none) |
| `trading.algo.scheduler.tick_duration` | Histogram (ms) | (none) |
| `trading.algo.signal_queue_depth` | UpDownCounter | (none) |
| `trading.options.zero_price_orders_submitted` | Counter | `symbol`, `side`, `firmId`, `put_call` |

The `trading.risk.*` series back the **B3 Trading — Risk** Grafana
dashboard (`docker/observability/grafana/dashboards/risk.json`); the
v2 risk pipeline is documented in
[`docs/rfcs/pre-trade-risk-v2.md`](rfcs/pre-trade-risk-v2.md).

The `trading.algo.*` series (plus the `source` tag on
`trading.orders.submitted`) back the **B3 Trading — Algo** Grafana
dashboard (`docker/observability/grafana/dashboards/algo.json`); the
v0 algo pipeline is documented in
[`docs/rfcs/algo-orders-v0.md`](rfcs/algo-orders-v0.md). Per RFC §7
C1, `parentAlgoId` is intentionally **not** a metric tag (cardinality
would explode); per-algo drill-down lives in structured logs and
traces.

## Option-specific surveillance (OPT-F / #488)

`trading.orders.submitted.security_type` lets dashboards split equity
vs option flow (and surface "unknown" buckets that signal a symbol
missing from the directory). The `security_type` value is derived from
`SymbolDirectory.TryGetSpec(...).SecurityType` at submit time; when
the directory is not injected (most unit tests) the tag is `unknown`.

`trading.options.zero_price_orders_submitted` is the dedicated
surveillance signal for the OPT-C (#485) cabinet / worthless-OTM
closeout flow — orders that travel as `Limit Price=0` on the OPT
channel. A small steady stream is normal end-of-cycle hygiene; a
sudden spike on one `(symbol, firmId, put_call)` tuple is the
compliance alert (off-market levelling, wash-out, mis-keyed price).

## Auto-instrumentation also exported

Wired alongside the application meter so a single OTLP pipeline carries
everything:

- **ASP.NET Core**: `http.server.request.duration`,
  `http.server.active_requests`, `kestrel.active_connections`,
  `kestrel.connection.duration`, `kestrel.queued_connections`,
  `aspnetcore.routing.match_attempts`,
  `aspnetcore.authentication.authenticate.duration`,
  `aspnetcore.memory_pool.{rented,pooled,allocated,evicted}`.
- **.NET runtime**: `dotnet.gc.{collections,heap.total_allocated,...}`,
  `dotnet.thread_pool.*`, `dotnet.exceptions`, `dotnet.assembly.count`,
  `dotnet.process.{cpu,memory}`, `dotnet.jit.*`.

## Traces

- AspNetCore instrumentation only.
- Health probes (`/live`, `/ready`, `/health`) are filtered at the source
  — they flood the trace stream and never carry useful diagnostic
  signal.

## Prometheus naming

We export OTLP, **not** the Prometheus exposition format. The PR 7-2c
otel-collector translates with the standard rules:

- Dots become underscores: `trading.orders.submitted` →
  `trading_orders_submitted`.
- Counters get a `_total` suffix appended:
  `trading_orders_submitted_total`.
- Histograms become `<name>_bucket` / `_sum` / `_count` triples.
- UpDownCounters keep their natural name (no `_total`).

That mapping lives in the collector config, not in the host, so we keep
exactly one wire format here.

## Smoke test

Quick local verification with a debug-output collector — proves SDK
activation, resource attributes, and that at least one application
counter exports:

```bash
# 1. Start a collector that prints whatever it receives
cat > /tmp/otel-debug.yaml <<'EOF'
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
exporters:
  debug:
    verbosity: detailed
service:
  pipelines:
    metrics: { receivers: [otlp], exporters: [debug] }
    traces:  { receivers: [otlp], exporters: [debug] }
EOF

docker run -d --rm --name otelcol-test -p 4317:4317 \
  -v /tmp/otel-debug.yaml:/etc/otelcol-contrib/config.yaml \
  otel/opentelemetry-collector-contrib:0.119.0

# 2. Run the host pointing at it (any non-default signing key works)
cd backend/src/B3.Trading.Host
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 \
OTEL_EXPORTER_OTLP_PROTOCOL=grpc \
OTEL_METRIC_EXPORT_INTERVAL=2000 \
ASPNETCORE_URLS=http://localhost:5050 \
Trading__Auth__SigningKey="dev-only-test-key-32-bytes-min-length____padding!" \
  dotnet run --no-build

# 3. Hit a counter (in another shell)
TOKEN=$(curl -sX POST http://localhost:5050/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"alice","password":"wonderland"}' | jq -r .token)
curl -sX POST http://localhost:5050/orders/ \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"symbol":"PETR4","securityId":1,"side":"Buy","type":"Limit","quantity":100,"price":30.5}'

# 4. Watch it land in the collector
docker logs otelcol-test 2>&1 | grep "Name: trading."
#  -> Name: trading.orders.submitted
#  -> Name: trading.wal.appended
```

The full observability stack (collector + Prometheus + Grafana) ships
behind the `obs` compose profile in PR 7-2c.
