# Chaos drill — `scripts/chaos/`

Q4.15 (#315). Named failure scenarios against a running
docker-compose trading-host stack. Companion to
[`docs/operations/runbook-failover-recovery.md`](../../docs/operations/runbook-failover-recovery.md).

## Prerequisites

- Docker + `docker compose` (v2) on `PATH`.
- `curl` on `PATH`.
- The base+real compose stack reachable from the host (default URL
  `http://localhost:5000`). Required env vars:
  - `TRADING_AUTH_SIGNING_KEY` (≥ 32 chars).
  - `TRADING_SEED_PASSWORD_HASH` (PBKDF2 base64).
  - `TRADING_SEED_PASSWORD_SALT` (base64).
  - Same placeholders used by `.github/workflows/conformance.yml`.

You can either bring the stack up yourself:

```bash
docker compose -f docker/docker-compose.yml \
               -f docker/docker-compose.real.yml \
               up -d --wait trading-host
scripts/chaos/run-chaos-drill.sh --scenario host-kill
```

…or let the script do it:

```bash
scripts/chaos/run-chaos-drill.sh --up --scenario host-kill
```

## Scenarios

| `--scenario` | Action | Pass criterion |
|---|---|---|
| `host-kill` | SIGKILL `b3-trading-host`, restart. | Exchange sessions re-establish, WAL/snapshot state is monotonic, and a fresh real trade fills. |
| `marketdata-kill` | SIGKILL `b3-marketdata`, then restart. | Host stays live; exchange readiness returns and a fresh real trade fills after recovery. |
| `network-partition` | Disconnect trading-host from `b3-net`, then reconnect. | Exchange sessions re-establish, WAL/snapshot state is monotonic, and a fresh real trade fills. |

Each scenario writes pre/post state JSON snapshots to
`./chaos-artifacts/<scenario>-{pre,post}.json` (overridable via
`CHAOS_ARTIFACTS_DIR`).

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Scenario PASSED. |
| `1` | Scenario FAILED (state diverged, `/ready` did not return, WAL regressed). |
| `2` | Precondition failed (container not running, missing `docker`/`curl`). |
| `3` | Usage error. |

## Env overrides

| Variable | Default | Purpose |
|---|---|---|
| `TRADING_CONTAINER` | `b3-trading-host` | Container name. |
| `MARKETDATA_CONTAINER` | `b3-marketdata` | Container name. |
| `DOCKER_NETWORK` | `b3-net` | Compose network to disconnect from. |
| `TRADING_BASE_URL` | `http://localhost:5000` | REST base for `/health` + `/ready`. |
| `READY_TIMEOUT_S` | `60` | Per-scenario `/ready` wait budget. |
| `PARTITION_HOLD_S` | `10` | How long to hold the network partition. |
| `POST_KILL_WAIT_S` | `5` | Sleep between SIGKILL and restart. |
| `CHAOS_ARTIFACTS_DIR` | `./chaos-artifacts` | Where pre/post JSON snapshots land. |

## CI

The workflow [`.github/workflows/chaos-drill.yml`](../../.github/workflows/chaos-drill.yml)
runs a selected scenario on `workflow_dispatch`; nightly runs rotate through
all three supported scenarios. Container logs
are uploaded on failure. It is **not** gated on PR merges — chaos is
expensive.

## Related

- Runbook: [`docs/operations/runbook-failover-recovery.md`](../../docs/operations/runbook-failover-recovery.md).
- Persistence design: [`docs/PERSISTENCE.md`](../../docs/PERSISTENCE.md).
- Recovery driver: [`backend/src/B3.Trading.Infrastructure/Persistence/SnapshotService.cs`](../../backend/src/B3.Trading.Infrastructure/Persistence/SnapshotService.cs).
- Pure-.NET invariant test:
  `UngracefulStop_NoFlush_RecoversToLastFlushedSeq_NoTornWriteFalsePositives`
  in
  [`backend/tests/B3.Trading.Application.Tests/Persistence/RecoveryAndSnapshotTests.cs`](../../backend/tests/B3.Trading.Application.Tests/Persistence/RecoveryAndSnapshotTests.cs).
