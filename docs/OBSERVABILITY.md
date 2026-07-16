# Observability & Lifecycle

Phase 3 (partial) wires the platform to a generic Kubernetes-/Linux-host
shaped lifecycle and exposes a process-wide metric surface using
`System.Diagnostics.Metrics`. Mirrors the convention in
[`B3MarketDataPlatform`](https://github.com/pedrosakuma/B3MarketDataPlatform)
(`MetricsRegistry`, `/health`, `/ready`, `/live`).

The remaining Phase 3 items — ER replay on (re)connect, FIXP per-session
state machine, gap recovery — are deferred until the
`B3EntryPointClient` upstream lands.

## Probes

| Endpoint | Purpose | Status semantics |
| --- | --- | --- |
| `GET /live` | Liveness — is the process alive? | Always **200**. Used by orchestrator to decide whether to restart. |
| `GET /ready` | Readiness — can order ingress be accepted safely? | **200** only when not draining, identity and WAL are healthy, and required exchange sessions can accept orders. **503** in `Mode=Unavailable`. Used by load balancers to route requests. |
| `GET /health` | Rich JSON for humans/dashboards | Always **200**. Body contains `status`, `uptime`, `startedAt`, `persistence` and `identityDirectory` blocks. |

Sample `/health` body:

```json
{
  "status": "ready",
  "uptime": "00:01:42",
  "startedAt": "2026-05-01T16:00:00Z",
  "persistence": {
    "enabled": true,
    "firmId": "default",
    "dataDirectory": "data",
    "snapshotInterval": "00:05:00"
  },
  "identityDirectory": {
    "provider": "Sqlite",
    "ready": true,
    "path": "data/identity/users.db",
    "schemaVersion": 1,
    "reason": null,
    "busyTimeoutMilliseconds": 5000
  }
}
```

## Drain (graceful SIGTERM)

`DrainState` is a singleton flag flipped by `DrainHostedService` on
`IHostApplicationLifetime.ApplicationStopping` (i.e. `SIGTERM`,
`Ctrl+C`, or programmatic `IHostApplicationLifetime.StopApplication`).

While draining:

- `/ready` → **503** (LB stops sending new traffic).
- `POST /orders` → **503** with `{"error":"service draining"}`. New
  orders are refused; in-flight orders complete normally.
- The ER router and WAL keep running so replies for already-submitted
  orders flush out before the host exits.
- `SnapshotService` writes a final snapshot in its `finally` block.
- `FileEventStore.DisposeAsync` flushes any pending records past the
  channel sentinel before closing the file.

`/live` keeps returning 200 throughout — the process is healthy, just
refusing new work.

## Metrics

Single static `MetricsRegistry` in
`B3.Trading.Application.Observability`, meter name **`B3.Trading`**
(version `1.0.0`). No exporter wired in-process; the host machine /
sidecar is expected to attach an OTel collector or scrape endpoint.

| Instrument | Type | Tags |
| --- | --- | --- |
| `trading.orders.submitted` | counter | `symbol`, `side` |
| `trading.orders.rejected_by_risk` | counter | `reason` |
| `trading.orders.gateway_failed` | counter | — |
| `trading.orders.cancel_requested` | counter | — |
| `trading.er.received` | counter | `exec_type` |
| `trading.kill_switch.toggled` | counter | `scope`, `killed` |
| `trading.wal.appended` | counter | — |
| `trading.wal.backpressure` | counter | `call_site` |
| `trading.wal.segments_rotated` | counter | `reason` (`day` / `size`) |
| `trading.snapshots.taken` | counter | — |
| `trading.snapshots.failed` | counter | — |
| `trading.snapshots.duration_ms` | histogram | — |
| `trading.recovery.events_replayed` | counter | — |
| `trading.ws.connections.active` | up-down counter | — |
| `trading.ws.messages.sent` | counter | `channel` |
| `trading.drain.rejections` | counter | `route` |

### Scraping

Local exploration with
[`dotnet-counters`](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-counters):

```bash
dotnet counters monitor --process-id <pid> --counters B3.Trading
```

Production deployment: attach an OTel SDK + Prometheus / OTLP exporter
in the host. Wiring is intentionally not in `Program.cs` — this is a
deployment concern (Phase 7).

## Verification

Tests live in `backend/tests/B3.Trading.Api.Tests/Lifecycle/`:

- `Live_Always_Returns_Ok`
- `Ready_Returns_Ok_When_Not_Draining`
- `Health_Returns_Json_With_Persistence_Block`
- `Drain_Causes_Ready_To_Return_503_And_Health_Status_Draining`
- `Drain_Causes_Order_Submit_To_Return_503`
- `Order_Submit_Increments_OrdersSubmitted_Counter` (uses `MeterListener`
  to verify the instrument is registered under the expected name)

## Out of scope (this slice)

- ER replay on FIXP (re)connect — needs the upstream client.
- Per-session FIXP state machine and gap recovery — same.
- In-process Prometheus / OTLP exporter — deployment concern (Phase 7).
- Distributed tracing — not exercised yet; trivial to add when an OTel
  collector is in front of the process.
