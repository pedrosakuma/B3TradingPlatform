# Failover, Recovery, Snapshot Replay & WAL Repair Runbook

> **Q4.15 (#315).** Operational runbook for the `trading-host` slice
> covering crash / hang / disk / partition scenarios, recovery flows
> (cold start, snapshot+WAL replay, snapshot-only, WAL repair) and the
> chaos drill that exercises a subset of them.
>
> The perf-hardening "what to watch in prod" guide is a **separate**
> document: [`../RUNBOOK.md`](../RUNBOOK.md). Read that first if you
> are paged for a metric alert. Read this one if the host is down,
> hanging, partitioned, or its on-disk state looks corrupt.
>
> **Q4.9 (active-passive matching-engine HA, #309) is not yet
> shipped.** Sections that depend on a passive replica or leader-lease
> eviction are explicitly marked `Pending #309 / Q4.9` — do not invent
> failover steps that the platform cannot perform today.

---

## 0. Quick reference

| Surface | Where to look |
|---|---|
| Health & drain state | `GET /health`, `GET /ready`, `GET /live` — [`backend/src/B3.Trading.Host/Lifecycle/HealthEndpoints.cs`](../../backend/src/B3.Trading.Host/Lifecycle/HealthEndpoints.cs) |
| Exchange (matching) status | `health.exchange.readyForOrders` (Real mode); `ExchangeStatus` aggregates per-firm session state |
| FIXP listener (inbound bots) | `health.entryPointListener.activeSessions` |
| WAL & snapshots on disk | `data/{firm}/wal/YYYY-MM-DD/*.log,*.idx`, `data/{firm}/snapshots/snap-*.json`, `data/{firm}/snapshots/latest.txt` |
| WAL backpressure metric | `trading_wal_backpressure_total` ([`backend/src/B3.Trading.Application/Observability/MetricsRegistry.cs`](../../backend/src/B3.Trading.Application/Observability/MetricsRegistry.cs)) |
| Snapshot metrics | `trading.snapshot.*` family (same file); two-phase capture in [`backend/src/B3.Trading.Application/StateSnapshotter.cs`](../../backend/src/B3.Trading.Application/StateSnapshotter.cs) |
| WS fan-out drop | `trading_dispatcher_ws_fanout_dropped_total` — see [`../RUNBOOK.md`](../RUNBOOK.md) §1.1 |
| Source-of-truth invariant | ER stream from B3 EntryPoint is canonical — [`../PERSISTENCE.md`](../PERSISTENCE.md) §"Source-of-truth invariant" |
| Recovery driver | [`backend/src/B3.Trading.Infrastructure/Persistence/SnapshotService.cs`](../../backend/src/B3.Trading.Infrastructure/Persistence/SnapshotService.cs) (`PersistenceRecovery`) |
| Venue session-roll recovery (reattach vs stale) | §1.12; `kind=Reattached`/`Renegotiated` reconnect logs; `trading.entrypoint.orders_auto_staled{reason=session_rolled}` |
| WAL framing & torn-write detection | [`backend/src/B3.Trading.Infrastructure/Persistence/SegmentReader.cs`](../../backend/src/B3.Trading.Infrastructure/Persistence/SegmentReader.cs), [`SegmentWriter.cs`](../../backend/src/B3.Trading.Infrastructure/Persistence/SegmentWriter.cs) |
| Chaos drill script | [`../../scripts/chaos/run-chaos-drill.sh`](../../scripts/chaos/run-chaos-drill.sh) |

> **Convention.** Every section below references symbols by relative
> file path so on-call can `grep -rn '<symbol>' backend/src/` without
> chasing line numbers that drift.

---

## 1. Failover scenarios

Each scenario follows the same shape: **Detect → Triage → Mitigate →
Verify**. "Verify" means the post-condition you must confirm before
clearing the page — do not declare the incident closed until those
checks pass.

### 1.1 Trading-host crash — graceful exit

**Detect.**
- `GET /live` connection refused / 404.
- Process exit code logged as 0 (clean shutdown initiated by
  `IHostApplicationLifetime` or SIGTERM).
- `DrainState.IsDraining` was true in the last `/health` body before
  the connection dropped (see
  [`backend/src/B3.Trading.Host/Lifecycle/HealthEndpoints.cs`](../../backend/src/B3.Trading.Host/Lifecycle/HealthEndpoints.cs)).

**Triage.**
1. Was a deploy or rolling restart in flight? Check CI / orchestrator.
2. Was a manual `docker compose stop trading-host` issued?
3. Did the host complete its drain (FIXP outbound channel flushed —
   `trading.fixp.outbound.drain.shutdown.abandoned` should be **zero**
   for this restart's window)? If it bumped, see §1.8.

**Mitigate.**
- Restart with `docker compose -f docker/docker-compose.yml up -d
  trading-host` (or the same overlay stack the operator was running).
- The host will execute `PersistenceRecovery.RunAsync` synchronously
  before binding `/ready`. **Do not** roll the data directory; warm
  recovery is the whole point.

**Verify.**
- `GET /ready` returns 200 within the host's normal warm-recovery
  window (snapshot load + WAL tail replay; minutes scale even for
  large WALs because the reader is sequential).
- `GET /health` body shows:
  - `persistence.firmId` matches the firm you expect.
  - `exchange.readyForOrders=true` (Real mode) once FIXP renegotiates.
- The next inbound ER from the EntryPoint is accepted without a
  "session out of order" log line (B3 replays on FIXP recovery — see
  [`../PERSISTENCE.md`](../PERSISTENCE.md) §"Source-of-truth invariant").
- `WorkingOrderBook` last-replayed seq equals the WAL's last
  `CurrentSeq` (visible in startup logs:
  `Persistence recovery: restored snapshot at seq=…`).

### 1.2 Trading-host crash — ungraceful exit

**Detect.**
- Process exit was non-zero, OOM-kill, `kill -9`, container OOM, host
  reboot, or kernel oops.
- No "draining" line in logs before EOF.
- `_drainLoop.WaitAsync` 250 ms catch in
  [`backend/src/B3.Trading.EntryPointListener/Hosting/FixpOutboundChannelWriter.cs`](../../backend/src/B3.Trading.EntryPointListener/Hosting/FixpOutboundChannelWriter.cs)
  may have logged "abandoned" with a non-zero counter.

**Triage.**
1. Capture container logs *before* restart (orchestrator scrollback
   has a finite retention window — `docker logs b3-trading-host
   --since 30m > incident-<ts>.log`).
2. Check `dmesg` / kubelet events for OOM-kill (most common cause).
3. The WAL may have a torn tail. This is **expected** under ungraceful
   exit — the recovery code is designed for it (see §2.4 below and
   `SegmentReader.LastValidEnd` in
   [`backend/src/B3.Trading.Infrastructure/Persistence/SegmentReader.cs`](../../backend/src/B3.Trading.Infrastructure/Persistence/SegmentReader.cs)).

**Mitigate.**
- Restart the container. `FileEventStore`'s constructor scans every
  segment, stops at the first torn record, and exposes `CurrentSeq`
  as the last-valid seq. `PersistenceRecovery` then loads the latest
  snapshot and replays only the tail.
- **Do not** delete or truncate WAL files unless `PersistenceRecovery`
  itself throws on startup — torn-tail truncation is automatic and
  safe. Go to §2.4 only if recovery throws.

**Verify.**
- Same checks as §1.1.
- Additionally: the next FIXP `Negotiate` succeeds and B3 replays any
  ERs whose seq exceeds the host's recovered max-seq. Confirm via the
  matching-platform side `/sessions` endpoint.

### 1.3 Trading-host hang (health endpoint stops responding)

**Detect.**
- `GET /live` either hangs > probe timeout or returns 200 while
  `GET /ready` and `GET /health` hang.
- Orchestrator liveness probe will eventually kill the pod; this
  section is for the window before that happens, or when liveness is
  disabled.

**Triage.**
1. Capture a thread dump if possible (`dotnet-dump`, `dotnet-stack`).
   The most common hang sites in this codebase are:
   - The WAL group-commit drain thread blocked on disk IO (check disk
     latency / `iostat`).
   - The dispatcher snapshot lock held by a long capture
     (`EventDispatcher.WithSnapshotLock` in
     [`backend/src/B3.Trading.Application/EventDispatcher.cs`](../../backend/src/B3.Trading.Application/EventDispatcher.cs)).
   - WS hub drain backed up — see [`../RUNBOOK.md`](../RUNBOOK.md) §1.1.
2. Sample `trading_wal_backpressure_total` over the last few minutes
   (Prom: `rate(...[1m])`). A sustained increase indicates the
   dispatcher cannot enqueue and is back-pressuring callers; the
   process is alive but unhealthy.

**Mitigate.**
- If the orchestrator has not already killed the pod, issue a hard
  `docker kill -s SIGKILL b3-trading-host` and treat as §1.2. A hung
  process cannot drain; SIGTERM will time out.
- Investigate the root cause **after** service is restored.

**Verify.**
- §1.2 verification checks.
- Additionally: confirm the post-restart `trading_wal_backpressure_total`
  rate returns to zero. If it doesn't, see §1.4 (disk) or escalate to
  a capacity review.

### 1.4 WAL disk full / IO degradation

**Detect.**
- `df` on the data volume mountpoint shows >90% used, or `iostat`
  shows sustained `await` >> normal baseline.
- `Append` calls into `FileEventStore` start throwing
  `WalBackpressureException` (channel saturated because the writer
  cannot drain) — counter
  `trading_wal_backpressure_total` rises.
- Algo engine logs include
  `reason=wal_backpressure` (see
  [`backend/src/B3.Trading.Application/Algo/AlgoEngine.cs`](../../backend/src/B3.Trading.Application/Algo/AlgoEngine.cs)).
- In the worst case, `IOException: No space left on device` in the
  background writer log.

**Triage.**
1. Check which file system: WAL volume or snapshot volume? Both live
   under `Trading:Persistence:DataDirectory` by default but operators
   may have split them on dedicated mounts.
2. EOD files (`data/{firm}/eod/*.json`) accumulate forever by design;
   they are the most common culprit.
3. Snapshot retention: only the latest snapshot is referenced from
   `latest.txt` but older `snap-*.json` files are not pruned by the
   host; an aggressive snapshot interval can pile up MiBs/day.

**Mitigate.**
- **Free disk first; correctness later.** The trading-host is
  fail-closed: once WAL appends fail, order submission also fails
  (the WAL is on the critical path). Old snapshot files older than
  the one referenced in `latest.txt` are safe to delete:
  ```bash
  # latest.txt is a PLAIN ASCII integer (the seq), NOT JSON.
  # The pointed-to snapshot file is snap-<seq, zero-padded to 12 digits>.json.
  SEQ=$(cat data/{firm}/snapshots/latest.txt | tr -d '[:space:]')
  KEEP=$(printf 'snap-%012d.json' "$SEQ")
  find data/{firm}/snapshots -name 'snap-*.json' ! -name "$KEEP" -delete
  ```
- EOD files older than your firm's regulatory retention can be
  archived off-volume.
- **Do not** delete `.log` / `.idx` files in `wal/` while the host is
  running. If you must, stop the host first; see §2.4 (WAL repair).

**Verify.**
- `df` shows free space > 20%.
- `trading_wal_backpressure_total` rate returns to zero.
- `Append` no longer throws (a fresh order submission via `POST /orders`
  succeeds end-to-end).

### 1.5 WAL backpressure storm (sustained without disk pressure)

**Detect.**
- `rate(trading_wal_backpressure_total[1m]) > 0` sustained for several
  minutes, **without** disk-level symptoms (§1.4).
- Producers in the algo path log `reason=wal_backpressure`.
- `GET /health` still returns ready.

**Triage.**
1. This is almost always a **producer storm** — replay burst from
   B3, snapshot capture holding the dispatcher lock too long, or a
   misbehaving bot fanning out modifications.
2. Inspect `EventDispatcher` queue depth via metrics; correlate with
   `trading.er.received` rate.
3. Check `PersistenceOptions.ChannelCapacity` / `GroupCommitMaxRecords`
   / `GroupCommitWindow` — defaults assume modest steady-state load
   (see [`backend/src/B3.Trading.Application/Persistence/PersistenceOptions.cs`](../../backend/src/B3.Trading.Application/Persistence/PersistenceOptions.cs)).

**Mitigate.**
- For burst causes: ride it out. The bounded channel is by design.
- For sustained: kill the offending bot session (FIXP listener admin
  surface — see [`fixp-listener.md`](fixp-listener.md)) and confirm
  the rate drops.
- For chronic: raise `ChannelCapacity` and ship a patch. Do **not**
  raise blindly — the cap also bounds worst-case in-memory queueing.

**Verify.**
- Rate returns to zero.
- No order rejections attributable to `wal_backpressure` in the last
  15 minutes (algo logs / `GET /orders/history`).

### 1.6 Snapshot capture stuck

**Detect.**
- `trading.snapshot.captured` counter flat for far longer than
  `Trading:Persistence:SnapshotInterval`.
- `trading.snapshot.failed` increases.
- Logs from `StateSnapshotter` / `TwoPhaseSnapshotCapture` (see
  [`backend/src/B3.Trading.Application/StateSnapshotter.cs`](../../backend/src/B3.Trading.Application/StateSnapshotter.cs))
  show an in-flight capture that never completes.

**Triage.**
1. Two-phase capture takes the dispatcher's snapshot lock only
   briefly to copy state, then serialises + fsyncs outside the lock
   (see PR for `TwoPhaseSnapshotCapture`). If the lock is held long,
   that's the dispatcher being slow; if the on-disk write is slow,
   that's IO.
2. Snapshots are a **derived cache** — the WAL is the source of
   truth ([`../PERSISTENCE.md`](../PERSISTENCE.md)). A skipped
   snapshot only lengthens the next cold-boot replay; it does not
   risk data loss.

**Mitigate.**
- Short-term: ignore. Recovery still works from snapshot-N + full
  WAL tail.
- If the IO is the cause: see §1.4.
- If the dispatcher lock is held by something else: thread dump and
  inspect `EventDispatcher.WithSnapshotLock` callers.

**Verify.**
- Next `SnapshotInterval` window produces a fresh `snap-*.json` and
  `latest.txt` updates.

### 1.7 Matching platform unavailable (Real mode)

**Detect.**
- `health.exchange.readyForOrders=false` while
  `entryPointListener.listening=true` (host is up; matching is gone).
- FIXP `Negotiate` / `Establish` errors in trading-host logs (see
  [`backend/src/B3.Trading.Infrastructure/EntryPoint/`](../../backend/src/B3.Trading.Infrastructure/EntryPoint/)).
- Heartbeat loss → session torn down → `ExchangeStatus` flips per-firm
  state away from `established`.

**Triage.**
1. Is matching-platform itself up? `curl
   http://matching-platform:8080/metrics` (or its mapped host port).
2. Is the network path healthy? See §1.9 for the partition case.
3. Bridge config drift: check
   [`docker/real/exchange-simulator.bridge.json`](../../docker/real/exchange-simulator.bridge.json)
   matches the trading-host's `Trading__Exchange__Firms__0__*` env.

**Mitigate.**
- Restart matching-platform if it crashed (`docker compose -f
  docker/docker-compose.yml up -d matching-platform`).
- Trading-host will auto-reconnect; **no** trading-host restart is
  needed.
- New `POST /orders` will return 502 BadGateway until the FIXP session
  re-establishes. This is the **honest no-broker** posture — orders
  are not silently queued.

**Verify.**
- `health.exchange.readyForOrders=true`.
- A fresh `POST /orders` succeeds and the ER round-trips.
- No duplicate clOrdIds were generated during the outage (the
  `ClOrdIdPrefixRegistry` is recovery-safe; replayed under
  `PersistenceRecovery`).

> **Q4.9 / #309 callout.** When the active-passive matching pair
> ships, this scenario should evolve to "primary unavailable → witness
> evicts lease → passive takes over → trading-host's FIXP target
> rotates". **Until then, this is a single-point-of-failure.**
> Pending #309 / Q4.9.

### 1.8 Market-data feed loss

**Detect.**
- `IReferencePrice` lookups stale; risk-layer collar checks may
  degrade.
- WS clients see no top-of-book updates.
- See the live `marketdata` container's `/sessions` / `/metrics`.

**Triage.**
1. Is the `marketdata` container up? `docker ps | grep b3-marketdata`.
2. Is matching-platform pushing UMDF unicast to it? Matching's UDP
   sink resolves the `marketdata` hostname at startup
   ([`docker/docker-compose.yml`](../../docker/docker-compose.yml)
   "depends_on marketdata" comment).
3. UMDF docs: cross-platform feed lives in
   [`pedrosakuma/B3MarketDataPlatform`](https://github.com/pedrosakuma/B3MarketDataPlatform);
   on this side, the consumer is `MarketDataWebSocketClient` (see
   [`backend/src/B3.Trading.Infrastructure/`](../../backend/src/B3.Trading.Infrastructure/)).

**Mitigate.**
- Restart `marketdata` container. Matching does not need to restart;
  it will push to the freshly-resolved IP after a short retry window.
- The exchange health surface should transition from healthy →
  **Degraded** (chaos drill `marketdata-kill` scenario asserts this
  contract; see §6.2).

**Verify.**
- Ref-prices update within a few seconds of a known trade on
  matching.
- `health` body's `exchange` block reflects the recovery.

### 1.9 User-bot FIXP listener overflow

**Detect.**
- Q3.3 metrics: rate-limit reject counter and buffer-full counter
  bump.
- See [`fixp-listener.md`](fixp-listener.md) for the per-session
  surface and admin levers.

**Triage.**
1. Which bot session is the offender? Listener metrics tag by session.
2. Is the rate-limit tuned correctly for that bot's expected load?

**Mitigate.**
- Per-session admin kill or rate-limit override (see
  [`fixp-listener.md`](fixp-listener.md)).
- For systemic load: raise per-session bucket size and ship.

**Verify.**
- Buffer-full counter rate returns to zero.
- No connected sessions show "abandoned" drains on the next graceful
  restart (`trading.fixp.outbound.drain.shutdown.abandoned`).

### 1.10 Network partition (trading-host ↔ matching ↔ marketdata)

**Detect.**
- All three of:
  - `health.exchange.readyForOrders=false`.
  - Matching-platform `/sessions` shows the host session torn.
  - `marketdata` updates stop flowing.
- ICMP / TCP-level probes between the containers fail (in compose
  parlance: the trading-host is off `b3-net`).

**Triage.**
1. Is this a real network event (cloud NIC, security group) or a
   compose-level disconnect (someone ran `docker network disconnect`)?
2. Snapshot the WAL last-seq on each side before mitigating — the
   chaos drill captures these into `/tmp`-style JSON for the
   post-drill diff (see §6).

**Mitigate.**
- Reconnect: `docker network connect b3-net b3-trading-host` (or the
  cloud-side fix).
- FIXP `Negotiate` will run; B3 replays any missed ERs.

**Verify.**
- `health.exchange.readyForOrders=true`.
- **No event loss.** Asserted as:
  - WAL `CurrentSeq` is monotonic across the partition.
  - No duplicate clOrdIds in the fill projection
    (`GET /fills/{id}/touch`).
- This is the exact invariant the `network-partition` chaos drill
  exercises (§6.2).

### 1.11 Self-trade prevention / risk-check storm

**Detect.**
- Sudden surge in `POST /orders` 4xx rate, dominated by STP or
  risk-gate rejects.
- `trading.reference_price.collar_no_bypass_counter` or related
  surfaces from
  [`backend/src/B3.Trading.Application/Observability/MetricsRegistry.cs`](../../backend/src/B3.Trading.Application/Observability/MetricsRegistry.cs).

**Triage.**
1. Is this one client/bot? Check the firm/owner tag distribution.
2. Is collar config sensible? See `Trading:Risk:*` options.
3. Is this an algo loop? Check `AlgoEngine` repeated modifications —
   the Q3 / Q4.x adoptions / retire-FIFO surface tracks this
   ([`AlgoEngine.cs`](../../backend/src/B3.Trading.Application/Algo/AlgoEngine.cs)).

**Mitigate.**
- Per-client kill switch: `POST /kill/end-client/{id}`.
- Per-firm kill switch: `POST /kill/firm/{id}`.
- Global: `POST /kill` (only as a last resort; surface to leadership).

**Verify.**
- Reject rate returns to baseline.
- Kill-switch state is durable across restart (replayed from WAL via
  `KillSwitchToggledEvent` — covered by
  [`backend/tests/B3.Trading.Application.Tests/Persistence/RecoveryAndSnapshotTests.cs`](../../backend/tests/B3.Trading.Application.Tests/Persistence/RecoveryAndSnapshotTests.cs)
  `Recovery_FromWalAlone_ReproducesOrdersOwnershipPositionsAndKillSwitch`).

### 1.12 Venue FIXP session roll — recoverable reattach vs stale-on-roll

**Background.** When the trading-host's FIXP order-entry session to the
venue drops and the gateway reconnects
([`B3EntryPointClientGateway.ReconnectLoopAsync`](../../backend/src/B3.Trading.Infrastructure/B3EntryPointClientGateway.cs)),
the SDK returns a `ReconnectKind` that determines whether working orders
are recoverable. There is **no** `MassStatusRequest` / order-status sweep
on the B3 EntryPoint binary wire (8.4.2) — it does not exist in the
protocol (`MessageType` enum has no status-query verb), so reconciliation
is **not** a back-office query. The two regimes are:

| Disconnect window | SDK `ReconnectKind` | What happened | Recovery |
|---|---|---|---|
| ≤ `SuspendedTimeoutMs` | `Reattached` | Venue kept the session **Suspended**; `SessionVerId` preserved | SDK auto Establish-reattach + `RetransmitRequest` replays the venue's per-session `RetransmitBuffer` (`PossResend=1`); our gateway consumes the replay and the **idempotent ER processor** (#16) dedupes it. **Working orders re-sync with no operator action.** |
| > `SuspendedTimeoutMs` | `Renegotiated` | Venue **reaped** the session; Establish-reuse **rejected**; fresh Negotiate with a **bumped `SessionVerId`** | Genuinely unrecoverable on the wire — the venue discarded its per-session state. The gateway reconciles via the session-roll reactor: un-acked `PendingNew` are reaped and surviving `Working`/`PartiallyFilled` orders are flagged **stale** (#380 / #515). |

`SuspendedTimeoutMs` remains a **venue-side / matching-platform** setting
(not a `B3.Trading.*` option), but the real-stack conformance overlay now
mounts a dedicated matching bridge config with a shorter value so this
boundary can be exercised deterministically in seconds during CI and local
real-stack runs.

Separately, a full matching-platform **process restart** is stricter than a
mere TCP partition: upstream issue
[`pedrosakuma/B3MatchingPlatform#405`](https://github.com/pedrosakuma/B3MatchingPlatform/issues/405)
tracks that FIXP session state still lives in matching's process memory
only, so `docker compose restart matching-platform` necessarily forces a
fresh Negotiate / bumped `SessionVerId` even when the venue book + WAL are
intact. Operators should therefore expect the same stale-on-roll behavior as
the `> SuspendedTimeoutMs` row above, but with the venue's resting book still
potentially alive underneath the advisory stale flags.

**Detect.**
- First classify by whether `RecordSessionVerId` advanced. Any reconnect that
  comes back on a higher effective `SessionVerId` is a real session roll even
  if the SDK labels the reconnect as `Reattached`; expect the same stale-on-roll
  behavior as an explicit `Renegotiated`.
- Non-advancing `Reattached` reconnects are the silent/self-healing case; look
  for `EntryPoint reconnect ok … kind=Reattached` in trading-host logs and a
  burst of duplicate-inbound metric (`EntryPointDuplicateInbound`, expected
  during retransmit).
- Advanced-session reconnects surface as either `kind=Renegotiated` or
  `kind=Reattached` plus a `RecordSessionVerId` jump, together with the
  auto-stale counters
  `trading.entrypoint.orders_auto_staled{reason=session_rolled}` /
  `trading.entrypoint.session_roll_stale_reconcile_failed`
  ([`MetricsRegistry.cs`](../../backend/src/B3.Trading.Application/Observability/MetricsRegistry.cs)).
- Operators see the affected orders carry `isStale=true` /
  `staleReason=session_rolled:{from}-{to}` in `GET /orders/` and
  `GET /orders/history`.

**Triage.**
1. Confirm whether `SessionVerId` advanced across the reconnect. A bumped
   effective version means the old working set is no longer authoritative,
   regardless of whether the log line says `Reattached` or `Renegotiated`.
2. On any advanced-version reconnect, the stale flag is **expected and correct**, not a
   bug: the venue reaped the session, so the platform cannot trust its
   working set without operator confirmation.
3. If `session_roll_stale_reconcile_failed` fired, the staling phase hit
   a WAL error mid-bulk and the **tail of the working set may be
   un-flagged** — reconcile those orders by hand (see Mitigate).

**Mitigate.**
- `Reattached`: nothing to do — confirm orders re-synced.
- `Renegotiated`: review the stale orders in the blotter; clear the flag
  once reconciled against the venue, via
  `POST /admin/firms/{firmId}/orders/{clOrdId}/clear-stale`
  ([`AdminEndpoints.cs`](../../backend/src/B3.Trading.Api/AdminEndpoints.cs)).
  A stale flag also **auto-clears** when a terminal ER arrives for the
  order.
- `session_roll_stale_reconcile_failed`: treat as a WAL incident (§1.4),
  and manually mark-stale any surviving working orders for the firm via
  `POST /admin/firms/{firmId}/orders/{clOrdId}/mark-stale`.

**Verify.**
- After `Reattached`: working orders present pre-disconnect are still
  live and not flagged stale; the platform should also accept a fresh
  order, surface it as `Working` in `GET /orders`, and let it execute to
  `Filled`.
- After `Renegotiated`: surviving `Working`/`PartiallyFilled` orders for
  the rolled firm are flagged stale; un-acked `PendingNew` are cancelled;
  fresh post-reconnect orders should still trade through to `Filled`
  even though the older survivors remain operator-review stale.
- The real-stack contract is covered end-to-end by
  [`backend/tests/B3.Trading.Conformance/Spec_FIXP_SessionRoll/SuspendedTimeoutBoundarySpecTests.cs`](../../backend/tests/B3.Trading.Conformance/Spec_FIXP_SessionRoll/SuspendedTimeoutBoundarySpecTests.cs)
  (requires the docker-compose real-conformance overlay, which mounts the
  docker CLI/socket into the conformance runner so the spec can
  disconnect/reconnect the matching-platform network leg, then proves
  both recovery paths still support a full post-reconnect order
  round-trip), and by
  [`backend/tests/B3.Trading.Conformance/Spec_FIXP_SessionRoll/MatchingPlatformRestartSpecTests.cs`](../../backend/tests/B3.Trading.Conformance/Spec_FIXP_SessionRoll/MatchingPlatformRestartSpecTests.cs)
  which restarts the matching-platform process itself, proves that the host
  takes the forced-`Renegotiated` stale path, and then contract-proves the
  venue book survived the restart by filling the pre-restart stale survivor
  after recovery before asserting a fresh post-restart trade round-trip.
- The boundary policy is unit-covered by
  [`backend/tests/B3.Trading.Application.Tests/GatewayConnectSessionRollTests.cs`](../../backend/tests/B3.Trading.Application.Tests/GatewayConnectSessionRollTests.cs)
  (Reattached → no reactor; Renegotiated → reap + stale) and
  [`ConnectSessionRollReactorTests.cs`](../../backend/tests/B3.Trading.Application.Tests/ConnectSessionRollReactorTests.cs).

> **Why no `OrderStatusRequest` reconciliation?** The B3 matching
> platform serves every recovery the FIXP protocol exposes
> (`RetransmitRequest`, `Establish` reattach, Cancel-on-Disconnect)
> end-to-end; out-of-band reconciliation (a status sweep) is an explicit
> anti-pattern, not a designed-in recovery path. The stale-on-roll
> heuristic is the correct fallback **only** for the genuinely
> unrecoverable case (session reaped past `SuspendedTimeoutMs`). See the
> upstream wire audit in
> [`pedrosakuma/B3EntryPointClient#193`](https://github.com/pedrosakuma/B3EntryPointClient/issues/193).

---

## 2. Recovery flows

### 2.1 Cold start from clean data dir

**When.** First boot, deliberate full reset, or after operator-grade
data corruption (after taking a backup).

**Procedure.**
1. Stop the host.
2. Move (do not delete) `data/{firm}/` aside:
   ```bash
   mv data/FIRM01 data/FIRM01.archived-$(date -u +%Y%m%dT%H%M%SZ)
   ```
3. Start the host. `PersistenceRecovery.RunAsync` sees no snapshot
   and an empty WAL; the in-memory state initialises empty.
4. The B3 EntryPoint will replay open orders on the first FIXP
   `Establish` — the source-of-truth invariant guarantees
   state convergence ([`../PERSISTENCE.md`](../PERSISTENCE.md)).

**Verify.**
- `GET /ready` returns 200 quickly.
- Open orders from B3 appear in `WorkingOrderBook` within the FIXP
  recovery window.
- Position seeds (if configured under `Trading:PositionSeed:*`) apply
  cleanly; the host logs which seeds were skipped because the
  position was already recovered from B3 (see
  [`backend/src/B3.Trading.Host/Composition/TradingHostStartup.cs`](../../backend/src/B3.Trading.Host/Composition/TradingHostStartup.cs)).

### 2.2 Warm restart from snapshot + WAL replay

**When.** Default restart path. Covers §1.1 and §1.2.

**Procedure.** Just restart the process. The driver lives in
[`PersistenceRecovery.RunAsync`](../../backend/src/B3.Trading.Infrastructure/Persistence/SnapshotService.cs):

1. `SnapshotStore.LoadLatest()` reads `data/{firm}/snapshots/latest.txt`
   and deserialises `snap-NNNN.json`.
2. `StateSnapshotter.Restore(snap)` materialises the in-memory world
   (book, positions, kill switch, halts, ownership, algos, cash
   ledger).
3. Audit-only pre-pass over `seq <= snap.Seq` for `AuditLogEvent` so
   the bounded audit ring rehydrates (the keeper is intentionally not
   in the snapshot envelope).
4. Q4.7 fill-projection pre-pass: same shape, repopulates
   `FillProjection` from `ExecutionReportReceivedEvent` history so
   `GET /fills/{id}/touch` works post-restart.
5. Main replay: `IEventStore.ReadFromAsync(snap.Seq + 1)` →
   `EventReplayer.Apply` for each event.
6. `/ready` flips to 200; FIXP `Negotiate` runs; B3 replays the post-
   crash tail.

**Snapshot + WAL invariant.** A snapshot at `seq=N` is durable on
disk **only after** every event with `seq <= N` is durable on disk
(two-phase capture — [`backend/tests/B3.Trading.Application.Tests/Persistence/TwoPhaseSnapshotCaptureTests.cs`](../../backend/tests/B3.Trading.Application.Tests/Persistence/TwoPhaseSnapshotCaptureTests.cs)).
Therefore replay from `snap.Seq + 1` cannot drop events.

**Verify.**
- Startup log includes:
  `Persistence recovery: restored snapshot at seq=N (M orders, K positions).`
- Followed by:
  `Persistence recovery: rehydrated <X> audit envelopes from pre-snapshot WAL prefix (cap=...).`
- Followed by:
  `Persistence recovery: rehydrated <Y> fill touch records from pre-snapshot WAL prefix.`
- No `WARN`/`ERROR` from `SegmentReader` about torn writes (a single
  torn-tail entry is *informational*; multiple per segment indicates
  real corruption).

### 2.3 Snapshot replay only (no WAL tail)

**When.** Disaster recovery from a transported snapshot file when the
WAL is unavailable (e.g. shipped a snapshot off-site for forensic
analysis, or restoring to a clean host).

**Procedure.**
1. Stop the host.
2. Drop the snapshot file into `data/{firm}/snapshots/` using the
   canonical filename `snap-<seq, zero-padded to 12 digits>.json`
   (e.g. `snap-000000012345.json` for seq=12345).
3. Write `data/{firm}/snapshots/latest.txt` pointing at it — the file
   is a **plain ASCII integer** (the seq, no newline-sensitive format,
   no JSON):
   ```bash
   echo -n 12345 > data/{firm}/snapshots/latest.txt
   ```
   Important: `SnapshotStore.LoadLatest` selects the file whose
   filename ends with the seq encoded in `latest.txt` (the canonical
   path); it **only** falls back to the highest-seq `snap-*.json` on
   disk when `latest.txt` is missing, unparseable, or points at a seq
   with no matching file. To be belt-and-braces during restore, both
   write `latest.txt` AND delete any snapshot files you do not want
   loaded.
4. Ensure `data/{firm}/wal/` is empty (or contains only seq <= N
   events that have already been folded into the snapshot).
5. Start the host.
6. `PersistenceRecovery` will load the snapshot; the audit and fill
   pre-passes will be no-ops because the WAL has no records to
   rehydrate from. **This is acceptable** — both surfaces are
   bounded caches.

**Event-sourced model caveat.** Per
[`../PERSISTENCE.md`](../PERSISTENCE.md), the WAL is the audit log +
boot accelerator and the snapshot is a derived cache. Snapshot-only
recovery loses local audit history before `snap.Seq` for surfaces not
in the snapshot envelope. For most regulatory questions the B3 ER
stream remains canonical and is replayed on FIXP recovery.

**Q4.7 pre-pass for additive ER fields.** Snapshots written by older
binaries lack the Q4.7 best-execution touch fields; the pre-pass
re-derives them from the WAL `ExecutionReportReceivedEvent` stream.
With snapshot-only recovery, expect `GET /fills/{id}/touch` to return
404 for pre-snapshot fills.

### 2.4 WAL repair: truncated / torn-write detection & manual recovery

> **No `wal-tool` binary ships today.** A future `B3.Trading.Tools.Wal`
> project is on the roadmap (tracking ticket TBD). The procedure
> below uses the existing `FileEventStore` + `SegmentReader` directly.

**Automatic torn-tail handling.** On every open, `FileEventStore`
constructs a `SegmentReader` per segment which iterates records,
verifying each `[u32 length][u32 crc32][payload]` frame. The first
record whose length runs past EOF or whose CRC fails triggers
`SegmentReader.LastValidEnd` — the byte offset of the last valid
framed record. The bytes past `LastValidEnd` are simply not read; **no
truncation is performed automatically**.

Note that **`SegmentReader` validates framing and CRC only** — it does
**not** look inside the payload. A frame with a complete `[length][crc][payload]`
that the JSON deserializer later rejects (e.g. missing `kind`
discriminator) passes `LastValidEnd` and surfaces later in
`FileEventStore.ReadFromAsync` as a hard exception, **not** as a
silent truncation. Those errors are corruption, not a torn tail, and
fall under the manual procedure below. `CurrentSeq` is incremented
per-record as the framed payloads are enumerated, so it reflects only
the records `SegmentReader` accepted *and* JSON-parsed successfully.

This handles the common case: a clean torn-tail after `kill -9` or
power loss. Tested by
[`backend/tests/B3.Trading.Application.Tests/Persistence/FileEventStoreTests.cs`](../../backend/tests/B3.Trading.Application.Tests/Persistence/FileEventStoreTests.cs)
`TornWrite_TruncatesAtLastValidRecordOnReopen` and
`CrcMismatch_StopsReplayAtCorruptRecord`, plus the new
`UngracefulStop_NoFlush_RecoversToLastFlushedSeq_NoTornWriteFalsePositives`
added under Q4.15.

**When the automatic path is not enough.** Recovery throws on
startup, OR a segment past the first one contains a torn record
(rare; only happens if a previous restart appended past a torn tail
without truncation, which the platform does not do — so this implies
external editing or disk corruption).

**Manual procedure** (operator-grade, with the host **stopped**):

1. **Stop the host.** `docker compose stop trading-host`. Repair on a
   live process is unsupported.
2. **Back up the WAL.**
   ```bash
   tar czf data-backup-$(date -u +%Y%m%dT%H%M%SZ).tar.gz data/{firm}/wal data/{firm}/snapshots
   ```
3. **Identify the bad segment.** Recovery's exception trace points to
   the segment file. Cross-check with:
   ```bash
   find data/{firm}/wal -name '*.log' -printf '%T@ %p\n' | sort -n | tail
   ```
4. **Scan to last-good-seq.** No `wal-tool` exists, so use a one-shot
   `dotnet run` of `SegmentReader` against the suspect `.log`. The
   `LastValidEnd` byte offset and the last successfully-read seq are
   the safe truncation point. (Until the tool ships, this requires
   spinning up a throwaway `Program.cs` referencing
   `B3.Trading.Infrastructure`; an operator with no .NET environment
   should escalate to engineering.)
5. **Truncate the segment.** `truncate -s <LastValidEnd>
   data/{firm}/wal/YYYY-MM-DD/NNN.log`. **Also** truncate or rebuild
   the matching `.idx` — the sparse index assumes byte offsets refer
   to valid records; an offset past the new EOF is poison.
6. **Drop all later segments in the same day-dir, AND all later
   day-dirs.** The WAL invariant is "no event past a torn write";
   keeping them would silently expose post-corruption state on next
   boot, defeating the entire CRC discipline.
7. **Drop snapshots strictly newer than the surviving tail.** A
   snapshot at `seq=N` requires every event `<= N` to be readable; if
   N is past your truncation point, delete the `snap-<NNNNNNNNNNNN>.json`
   file and either rewrite `snapshots/latest.txt` to point at an older
   surviving seq (the file is a **plain ASCII integer**, e.g. `12345`
   — written by `SnapshotStore.Write` as `snapshot.Seq.ToString(...)`,
   **not** JSON) or delete `latest.txt` entirely so `LoadLatest`
   selects whatever surviving snapshot is highest. Note:
   `SnapshotStore.LoadLatest` honours `latest.txt` when it parses to a
   seq that matches a file on disk, and only falls back to the
   highest-seq `snap-*.json` when the pointer is missing, unparseable,
   or unmatched — so during repair you must **both** update the
   pointer **and** delete any files newer than your truncation point.
8. **Restart.** B3 will replay the gap on FIXP `Establish`.
9. **Document** the truncation byte-offset, last-good-seq, and the
   chain of segments / snapshots dropped, in the incident record.

**Cross-references.**
- WAL framing & CRC rules: [`../PERSISTENCE.md`](../PERSISTENCE.md)
  §"Record framing".
- Torn-write detection tests:
  [`backend/tests/B3.Trading.Application.Tests/Persistence/FileEventStoreTests.cs`](../../backend/tests/B3.Trading.Application.Tests/Persistence/FileEventStoreTests.cs).
- Operator-grade truncate+replay test:
  `ReadFromAsync_OperatorRecoveryFromMissingKind_TruncateBadRecordAndReplay`
  in the same file.

---

## 3. Drain & shutdown semantics

The trading-host's shutdown sequence and FIXP outbound drain
guarantees are documented in [`../RUNBOOK.md`](../RUNBOOK.md) §1.2
(`trading.fixp.outbound.drain.shutdown.abandoned`) and the source of
truth is
[`backend/src/B3.Trading.EntryPointListener/Hosting/FixpOutboundChannelWriter.cs`](../../backend/src/B3.Trading.EntryPointListener/Hosting/FixpOutboundChannelWriter.cs).

**Operational summary** (do not duplicate the perf-runbook prose):

- `SIGTERM` flips `DrainState.IsDraining=true`. `/ready` returns 503
  immediately so load balancers stop routing.
- The dispatcher continues draining in-flight events.
- A graceful shutdown takes at most the FIXP outbound drain budget
  (250 ms catch in `FixpOutboundChannelWriter`); past that, the
  `abandoned` counter bumps and the process exits anyway.
- `SIGKILL` skips all of the above — handled by §1.2 / §2.2.

---

## 4. HA active-passive — Pending #309 / Q4.9

> **Status:** unshipped. This section documents *intended* behaviour
> for forward planning. Do **not** attempt to construct an HA pair
> from the current `docker-compose.yml` — the wiring does not exist.

**Intended shape.**

- A pair of matching engines (`matching-platform-primary`,
  `matching-platform-passive`) share a witness component
  (`matching-witness`) holding a leader lease.
- The trading-host's FIXP target is the *current leader*. On lease
  loss, the witness evicts the primary; the passive promotes and
  rotates the leader endpoint. The trading-host's
  `Trading__Exchange__Firms__0__Endpoint` becomes a logical name that
  resolves via the witness.
- Failover RTO target: **single-digit seconds** for the FIXP
  re-establish. Order acceptance pauses (502 BadGateway) for that
  window — same fail-closed posture as §1.7 today.
- RPO target: **zero events** — both engines replicate WAL via an
  ordered, durable channel before ack; the passive's state is
  byte-identical to the primary's at any acked seq.

**Trading-host responsibilities** (already partially in place):

- Tolerate a FIXP `Establish` against a different remote IP without
  losing client correlation (the `ClOrdIdPrefixRegistry` is durable
  and survives session rotation).
- Surface "leader transition" as a distinct `/health.exchange` state
  rather than just `readyForOrders=false`.
- Run the witness-aware reconnect loop with bounded exponential
  backoff.

**Open questions for #309 / Q4.9.**

- Witness implementation: shared-disk lease vs. external coordinator
  (etcd/Consul). RFC pending.
- Whether the trading-host itself needs an active-passive pair (the
  current Phase-6 design treats it as recoverable from WAL + ER
  replay, which suggests "no" — one host per firm is sufficient if
  recovery is fast enough).
- Drop-copy WS feed (Q4.6, #306) needs to survive leader rotation
  cleanly. Current implementation re-emits on reconnect; should be
  validated under HA conditions.

Until #309 lands, the **matching platform is a single point of
failure** and the only mitigation for its loss is §1.7 +
fail-closed-on-order-submit.

---

## 5. Where to find things (greppable index)

| Concept | File |
|---|---|
| `PersistenceRecovery` | [`backend/src/B3.Trading.Infrastructure/Persistence/SnapshotService.cs`](../../backend/src/B3.Trading.Infrastructure/Persistence/SnapshotService.cs) |
| `FileEventStore` | [`backend/src/B3.Trading.Infrastructure/Persistence/FileEventStore.cs`](../../backend/src/B3.Trading.Infrastructure/Persistence/FileEventStore.cs) |
| `SegmentReader` (torn-write detection) | [`backend/src/B3.Trading.Infrastructure/Persistence/SegmentReader.cs`](../../backend/src/B3.Trading.Infrastructure/Persistence/SegmentReader.cs) |
| `SegmentWriter` (framing) | [`backend/src/B3.Trading.Infrastructure/Persistence/SegmentWriter.cs`](../../backend/src/B3.Trading.Infrastructure/Persistence/SegmentWriter.cs) |
| `WalEvent` discriminated union | [`backend/src/B3.Trading.Application/Persistence/WalEvents.cs`](../../backend/src/B3.Trading.Application/Persistence/WalEvents.cs) |
| `EventDispatcher.WithSnapshotLock` | [`backend/src/B3.Trading.Application/EventDispatcher.cs`](../../backend/src/B3.Trading.Application/EventDispatcher.cs) |
| `StateSnapshotter` / two-phase capture | [`backend/src/B3.Trading.Application/StateSnapshotter.cs`](../../backend/src/B3.Trading.Application/StateSnapshotter.cs) |
| `DrainState` / health endpoints | [`backend/src/B3.Trading.Host/Lifecycle/HealthEndpoints.cs`](../../backend/src/B3.Trading.Host/Lifecycle/HealthEndpoints.cs) |
| FIXP outbound drain | [`backend/src/B3.Trading.EntryPointListener/Hosting/FixpOutboundChannelWriter.cs`](../../backend/src/B3.Trading.EntryPointListener/Hosting/FixpOutboundChannelWriter.cs) |
| Metrics surface | [`backend/src/B3.Trading.Application/Observability/MetricsRegistry.cs`](../../backend/src/B3.Trading.Application/Observability/MetricsRegistry.cs) |
| Recovery integration tests | [`backend/tests/B3.Trading.Application.Tests/Persistence/`](../../backend/tests/B3.Trading.Application.Tests/Persistence/) |

---

## 6. Chaos drill

The drill lives at [`../../scripts/chaos/run-chaos-drill.sh`](../../scripts/chaos/run-chaos-drill.sh)
with operator-facing docs at
[`../../scripts/chaos/README.md`](../../scripts/chaos/README.md).
The companion CI workflow is
[`../../.github/workflows/chaos-drill.yml`](../../.github/workflows/chaos-drill.yml)
(manual + nightly only; **not** gated on PR merges — chaos is
expensive).

### 6.1 Local invocation

```bash
# Bring up the real stack (matching + marketdata + trading-host).
# Required env vars: TRADING_AUTH_SIGNING_KEY, TRADING_SEED_PASSWORD_HASH,
# TRADING_SEED_PASSWORD_SALT — same as the e2e-smoke and conformance jobs.
docker compose -f docker/docker-compose.yml \
               -f docker/docker-compose.real.yml \
               up -d --wait trading-host

scripts/chaos/run-chaos-drill.sh --scenario host-kill
scripts/chaos/run-chaos-drill.sh --scenario marketdata-kill
scripts/chaos/run-chaos-drill.sh --scenario network-partition
scripts/chaos/run-chaos-drill.sh --scenario wal-backpressure   # optional
```

Or let the script bring the stack up itself:

```bash
scripts/chaos/run-chaos-drill.sh --up --scenario host-kill
```

### 6.2 Scenarios

| Scenario | What it does | Pass criterion |
|---|---|---|
| `host-kill` | `docker kill -s SIGKILL b3-trading-host` → wait 5 s → restart → poll `/health`. | `/ready` returns 200 within `READY_TIMEOUT_S` seconds **and** `persistence.firmId` matches pre-drill. WAL `latest.txt` seq is monotonic across the kill (recovered seq ≥ pre-drill seq). |
| `marketdata-kill` | `docker kill -s SIGKILL b3-marketdata`. Trading-host stays up. | `GET /health` keeps returning 200. `health.exchange` reflects degraded marketdata. No trading-host crash. |
| `network-partition` | `docker network disconnect b3-net b3-trading-host` for 10 s, then reconnect. | After reconnect: `health.exchange.readyForOrders=true`. WAL `CurrentSeq` is monotonic (post >= pre). |
| `wal-backpressure` (optional) | Drives synthetic load to trip `trading_wal_backpressure_total`. | Counter increases; no unbounded queue elsewhere (host RSS stable; no OOM). |

Each scenario writes pre/post-drill state JSON to `./chaos-artifacts/`
in the worktree (configurable via `CHAOS_ARTIFACTS_DIR`). On failure
the script exits non-zero and prints a diff.

### 6.3 CI hook

The workflow [`.github/workflows/chaos-drill.yml`](../../.github/workflows/chaos-drill.yml)
runs the `host-kill` scenario on `workflow_dispatch` and on a nightly
schedule. It reuses the conformance job's compose bringup pattern
(same env vars, including the
`Trading__Reports__Cvm__OwnerHashSalt` placeholder added in Q4.8).
On failure it uploads `docker compose logs` as an artifact.

It deliberately does **not** include `on: pull_request` — the drill
boots a full compose stack and is too expensive to gate every PR.

### 6.4 Validation invariants

The chaos drill checks operational symptoms. The real-stack API/WAL
recovery contract is additionally covered by
[`backend/tests/B3.Trading.Conformance/Spec_Recovery/TradingHostCrashRestartSpecTests.cs`](../../backend/tests/B3.Trading.Conformance/Spec_Recovery/TradingHostCrashRestartSpecTests.cs),
which `docker kill -s SIGKILL`s `b3-trading-host`, waits for the host to
be down, then restarts it and proves both that pre-crash working-order
and cash/position/P&L state are still queryable **and** that a fill
generated during the outage window by the independent FIXP counterparty
session `10102` is replayed on recovery
instead of being lost/stuck as `Working`.
The deeper "no event loss across an ungraceful restart" invariant is
also tested in pure .NET as
`UngracefulStop_NoFlush_RecoversToLastFlushedSeq_NoTornWriteFalsePositives`
in
[`backend/tests/B3.Trading.Application.Tests/Persistence/RecoveryAndSnapshotTests.cs`](../../backend/tests/B3.Trading.Application.Tests/Persistence/RecoveryAndSnapshotTests.cs).
That test:

1. Writes N events through `FileEventStore`, flushing only a prefix.
2. Disposes the store without a final `FlushAsync` (simulates process
   death after partial flush).
3. Re-opens, runs `PersistenceRecovery`.
4. Asserts the recovered state matches the last *flushed* seq — no
   silent advance into in-flight-but-not-flushed records, no torn-
   write false positives stopping replay early.

If you change the WAL framing or snapshot envelope, that test is the
canary.
