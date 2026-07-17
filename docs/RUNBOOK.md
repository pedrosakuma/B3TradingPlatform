# Operations Runbook

> **Looking for failover / recovery / WAL repair / chaos drill?**
> That lives in
> [`operations/runbook-failover-recovery.md`](operations/runbook-failover-recovery.md)
> (Q4.15 / #315). This file is the **perf-hardening v0** runbook — read
> it when you are paged for a metric alert; read the failover runbook
> when the host is down, hanging, partitioned, or its on-disk state
> looks corrupt.

Top-level operational runbook for the B3 trading-host. Per-subsystem
guides live alongside this file (e.g.
[`operations/fixp-listener.md`](operations/fixp-listener.md));
this document covers cross-cutting "what to watch in prod" items
and shutdown / drain semantics that don't belong to a single
subsystem.

For the OTel metric surface itself see [`METRICS.md`](METRICS.md).
For alert rules in Prometheus / AlertManager YAML form see
[`ops/perf-v0-alerts.md`](ops/perf-v0-alerts.md).

---

## 0. Entra identity provisioning and local-auth retirement (#609)

### Hybrid rollout

1. Keep `Trading:Auth:Mode=Local` until the SQLite directory imports current
   `alice`/`bob`/admin owner IDs and `/health.identityDirectory.ready=true`.
2. If no admin is seeded, add one temporary legacy admin through Key Vault
   `Trading:Auth:Users` password hash/salt, not signup. Start once in Local so
   import preserves the exact `tradingUserId`.
3. Switch to `Hybrid` with signup disabled. Login with the local admin and call
   `POST /admin/identity/users/{id}/external-bindings` with the internal admin
   JWT in `Authorization` and the Entra access token in JSON body.
4. Exchange with `/auth/exchange`, bind existing `alice`/`bob`, verify their
   orders/positions/history remain under the same owner IDs, then disable local
   login/TOTP and switch to `Entra`.

### Break-glass

There is no HTTP break-glass endpoint. Restrict ingress, scale the writer down,
then run the same-image CLI against the PVC:

```bash
dotnet /app/tools/identity-maintenance/B3.Trading.IdentityMaintenance.dll recover-admin \
  --database /var/lib/b3trading/identity/users.db \
  --trading-user-id admin --display-name "Recovery admin" --firm-id FIRM01 \
  --operator <operator> --change-ticket <ticket>
```

Inject the matching temporary password only via Key Vault legacy auth config.
Repair/bind an Entra admin, verify exchange, return to `Entra`, and remove the
temporary credential after the rollback window. Public signup/JIT provisioning
is never used. Entra-managed factors supersede #319 for public human auth.

### Identity directory backup and restore validation

Create the SQLite artifact while trading-host remains live:

```bash
dotnet /app/tools/identity-maintenance/B3.Trading.IdentityMaintenance.dll backup \
  --database /var/lib/b3trading/identity/users.db \
  --destination /backup/users.db
```

The command uses SQLite's online backup API and emits one JSON metadata record
with `destination`, `schemaVersion`, and `createdAtUtc`. Package that artifact
with the matching Data Protection `dp-keys` through the deployment backup job;
do not copy the live `users.db`, `users.db-wal`, or `users.db-shm` files.

For a restore drill, keep the restored database offline and validate it before
mounting it into trading-host:

```bash
dotnet /app/tools/identity-maintenance/B3.Trading.IdentityMaintenance.dll validate \
  --database /restore/users.db
```

`validate` opens an existing file read-only and runs the supported-schema,
managed-schema, full SQLite integrity, foreign-key, and identity invariant
checks. A missing, corrupt, or unsupported database returns non-zero and is
never created, migrated, or reset.

## 1. Perf hardening v0 — what to watch

The perf-hardening v0 RFC ([`rfcs/perf-hardening-v0.md`](rfcs/perf-hardening-v0.md))
introduced a small set of new metrics, log signals, and tunables
that operators must monitor in production. Each item below lists
its source PR, what the signal means, an alert threshold, and the
mitigation path.

> **Composite load-test results that motivated these knobs are in
> [`perf-hardening-v0-results.md`](perf-hardening-v0-results.md).**

### 1.1 `trading.dispatcher.ws_fanout_dropped` (counter)

| Field | Value |
|---|---|
| OTel name | `trading.dispatcher.ws_fanout_dropped` |
| Prom name | `trading_dispatcher_ws_fanout_dropped_total` |
| C# symbol | `MetricsRegistry.WsHubFanOutDropped` ([`Observability/MetricsRegistry.cs`](../backend/src/B3.Trading.Application/Observability/MetricsRegistry.cs)) |
| Origin    | P4 / F2 — PR [#220](https://github.com/pedrosakuma/B3TradingPlatform/pull/220), RFC §5.2 |
| Type      | OTel `Counter<long>`, no tags |

**What it signals.** The WS hub sink (`WebSocketExecutionEventSink`,
`B3.Trading.Api/WebSockets/WebSocketExecutionEventSink.cs`) owns a
**single** bounded channel of 64 K events between the dispatcher
and its drain thread, with `BoundedChannelFullMode.DropOldest` and
an item-dropped callback that bumps this counter. A bump means
**the WS-hub publish→drain queue overflowed** — i.e. the drain
thread can't keep up with the dispatcher's publish rate (the
drain does the per-subscriber walk + DTO build). It is **not** a
per-subscriber slow-consumer signal; individual slow WS clients
are detected on their own per-client channel and disconnected
out-of-band (see `SubscribedClient.Enqueue`'s
`slow_consumer_resync_required` path), without bumping this
counter. This counter is **lossy by design** and should be
**flat at zero** in healthy operation.

**Alert.** `rate(trading_dispatcher_ws_fanout_dropped_total[1m]) > 0`
sustained for **1 minute** → **page**. Any non-zero rate means
the WS hub drain is behind and *all* subscribed WS clients will
observe a gap and have to reconnect-and-replay through the
existing WS recovery path.

**Mitigation, in order:**

1. Check `trading.er.received` rate against recent baseline — a
   producer-side spike (replay storm, ER burst from B3) is the
   most common cause and self-resolves once the burst clears.
2. Look at the host-level signals (CPU, GC pauses, scheduler
   latency) on the trading-host pod — the drain runs on a single
   `Task.Run` thread and is sensitive to runtime stalls.
3. Persistent saturation under known-good load → the 64 K cap is
   genuinely undersized for current participant volume; raise
   `WebSocketExecutionEventSink.ChannelCapacity` (compile-time
   constant, see PR #220) and ship a patch. **Do not** silently
   increase it without an issue tracking the new value — the
   cap is also the worst-case in-memory queue bound.

### 1.2 `trading.fixp.outbound.drain.shutdown.abandoned` (counter + warning log)

| Field | Value |
|---|---|
| Source | OTel `Counter<long>` on the `B3.Trading` meter + structured log line in [`Hosting/FixpOutboundChannelWriter.cs`](../backend/src/B3.Trading.EntryPointListener/Hosting/FixpOutboundChannelWriter.cs) at `LogWarning` (the `_drainLoop.WaitAsync` 250 ms catch) |
| OTel name | `trading.fixp.outbound.drain.shutdown.abandoned` |
| Prom name | `trading_fixp_outbound_drain_shutdown_abandoned_total` |
| Labels | none — untagged on purpose, see "follow-up" below |
| Origin | P8 / F3 — PR [#219](https://github.com/pedrosakuma/B3TradingPlatform/pull/219) (log), issue [#233](https://github.com/pedrosakuma/B3TradingPlatform/issues/233) (counter), RFC §5.3.2 |
| Type   | Counter (Prometheus-native, page-severity) + structured warning log (operator-readable `connectionId`) |

**Message shape.**

```
fixp.outbound.drain.shutdown.abandoned connectionId={ConnectionId} timeoutMs={TimeoutMs}
```

The same file emits a sibling line `fixp.outbound.drain.shutdown.timeout`
when `CompleteAsync` returns past its configured deadline; that
is the **expected** "deadline elapsed" path. The `.abandoned`
line is the harder failure: the drain loop **ignored cancellation**
for >250 ms after `_cts.Cancel()` and the connection cleanup gave
up waiting. The orphaned drain task will exit when its in-flight
`WriteAsync` eventually unblocks; in the meantime no per-frame data
is lost from the bot's perspective — the per-credential
`BotOutboundBuffer` still owns the queued frames and replays them
on the next reconnect (RFC §5.3.2).

**What it signals.** Either (a) the kernel socket buffer is
permanently full because the peer is dead but TCP RST hasn't
fired yet, or (b) a callback in the write path is genuinely
ignoring the cancellation token. Either case means a connection
slot was closed without a clean drain; the credential is freed
for a successor session per §4.5 only after the orphaned task
finally exits.

**Alert.** Any occurrence is operator-visible. Prometheus-native
rule (preferred): `increase(trading_fixp_outbound_drain_shutdown_abandoned_total[5m]) > 0`
→ **page**. The legacy LogQL fallback
(`count_over_time({msg=~"fixp.outbound.drain.shutdown.abandoned.*"}[5m]) > 0`)
remains valid for stacks without the OTel scrape but is
demoted to `info` in [`perf-v0-alerts.md`](ops/perf-v0-alerts.md) §2.
Contrast with the sibling `.timeout` line, which should be
alerted at info-level only — that one is the documented
"slow peer, deadline elapsed" path and is bounded by
`OutboundDrainShutdownTimeout` (§1.3 below).

**Mitigation:**

1. Pull the `connectionId` from the log line and grep recent
   FIXP listener logs for matching `Negotiate`, `Establish`,
   `Terminate` to identify the credential / firm. (The counter
   itself is intentionally untagged; the `connectionId` lives
   on the sibling log line.)
2. If isolated to one peer, the peer is misbehaving — coordinate a
   forced session cleanup via `/admin/fixp` (see fixp-listener
   ops doc).
3. If broad — multiple `connectionId`s in a short window — the
   write path itself has regressed in cancellation behaviour. Roll
   back the most recent listener change and open a P0.

> **Cardinality note.** The counter is emitted **untagged** so
> that the Prometheus series is bounded to one per process. The
> `connectionId` is preserved on the sibling structured log line
> for operator triage; do not promote it to a metric label
> without a follow-up issue weighing the cardinality cost
> (FIXP connection IDs are not bounded over time).

### 1.3 `OutboundDrainShutdownTimeout` (config, default `00:00:01`)

| Field | Value |
|---|---|
| Path  | `Trading:EntryPointListener:Buffers:OutboundDrainShutdownTimeout` |
| Env   | `Trading__EntryPointListener__Buffers__OutboundDrainShutdownTimeout=00:00:01` |
| Code  | [`EntryPointListenerOptions.BuffersOptions`](../backend/src/B3.Trading.EntryPointListener/EntryPointListenerOptions.cs) |
| Origin | P8 / F3 — PR [#219](https://github.com/pedrosakuma/B3TradingPlatform/pull/219), RFC §5.3.2 |

**What it controls.** Maximum wall-clock the per-connection drain
loop will spend flushing already-queued outbound frames on
connection close before giving up and emitting
`fixp.outbound.drain.shutdown.timeout`. Frames still queued at
the deadline remain owned by the per-credential
`BotOutboundBuffer` and are replayed via retransmit on the next
reconnect — they are never silently dropped from the bot's
perspective.

**When to tune.** Default `1s` is sized for the v0 buffer
(`OutboundChannelCapacity=4096`) and a healthy peer. Raise it
only if you observe `.timeout` log lines correlated with **slow
but live** peers (e.g. high-latency transit links) where the
extra time would let drains complete cleanly instead of
deferring frames into the post-reconnect replay path. Do **not**
raise it as a workaround for `.abandoned` — that path is not
deadline-bounded and a longer timeout cannot help.

**Drift detection.** Since #234 the trading-host emits the
build-info gauge
`trading_entrypoint_listener_outbound_drain_shutdown_timeout_seconds`
(OTel meter:
`trading.entrypoint_listener.outbound_drain_shutdown_timeout`,
unit `s`), sourced from
`IOptionsMonitor<EntryPointListenerOptions>.CurrentValue.Buffers.OutboundDrainShutdownTimeout`
so config reloads are reflected on the next scrape without a host
restart. The matching `PerfV0OutboundDrainTimeoutDrift` Prometheus
rule lives in [`ops/perf-v0-alerts.md`](ops/perf-v0-alerts.md) §1.1.

### 1.4 `Trading:Persistence:GroupCommitMaxRecords` (config, default `512`)

| Field | Value |
|---|---|
| Path  | `Trading:Persistence:GroupCommitMaxRecords` |
| Env   | `Trading__Persistence__GroupCommitMaxRecords=512` |
| Code  | [`PersistenceOptions`](../backend/src/B3.Trading.Infrastructure/Persistence/PersistenceOptions.cs) |
| Origin | P5 / F7 — PR [#214](https://github.com/pedrosakuma/B3TradingPlatform/pull/214), RFC §4.2 / §5.7 |

**What it controls.** Maximum records per WAL group-commit batch.
Raised from `64 → 512` in P5 to amortise `fsync` over more records
at participant-volume throughput without breaching the
`GroupCommitWindow` (10 ms) latency cap.

**Worst-case crash exposure.** `ChannelCapacity + GroupCommitMaxRecords`
acked-but-unfsynced records (i.e. with defaults: `4096 + 512 =
4608` records). The platform's invariant is **ack-before-fsync**
on the dispatcher path (RFC §4.2): an order is acknowledged once
its WAL append is enqueued, not once it is durable. This is a
**deliberate** design choice; do not "fix" it by reverting the
batch size.

**When to tune.**

- **Lower** (e.g. back to `256`) only if a regulator requires a
  tighter recoverable-state bound and you have measured the fsync
  amplification cost. Coordinate with the §4.2 owners — this
  changes a documented invariant boundary.
- **Higher** is **not** recommended without a fresh re-run of the
  perf-hardening composite suite ([`perf-hardening-v0-results.md`](perf-hardening-v0-results.md)).
  Larger batches risk exceeding `GroupCommitWindow` under load,
  pushing latency past the §7.3 gate.

**Drift detection.** Since #234 the trading-host emits the
build-info gauge `trading_persistence_group_commit_max_records`
(OTel meter: `trading.persistence.group_commit_max_records`),
sourced from
`IOptionsMonitor<PersistenceOptions>.CurrentValue.GroupCommitMaxRecords`
so config reloads are reflected on the next scrape without a host
restart. The matching `PerfV0GroupCommitMaxRecordsDrift` Prometheus
rule lives in [`ops/perf-v0-alerts.md`](ops/perf-v0-alerts.md) §1.1.

---

## 2. FIXP listener mTLS operations

The user-bot FIXP listener can require client certificates as a second factor
in addition to the bot PAT. mTLS is configured under
`Trading:EntryPointListener:Tls` and uses an operator-managed CA bundle, not
the machine root store.

### 2.1 Provisioning a bot CA and leaf certificate

1. Create or select an offline / near-line bot client CA. Keep its private key
   out of the trading-host container.
2. Export the trusted issuer certificate(s) as PEM and concatenate them into a
   bundle, for example `/certs/bot-ca-bundle.pem`.
3. For each bot, generate a private key + CSR out of band, then issue a client
   leaf from the bot CA. Recommended leaf shape:
   - subject/SAN identifies the credential, e.g. `CN=b3t-bot-<credShortId>`;
   - EKU includes `clientAuth` (`1.3.6.1.5.5.7.3.2`) when
     `Trading__EntryPointListener__Tls__RequireClientAuthEku=true`;
   - short validity, with renewal before expiry.
4. Distribute the bot leaf + private key only to that bot runtime. Distribute
   the server trust anchor separately so the bot can validate the listener.
   Never place bot private keys in the trading-host image or compose file.

### 2.2 Enabling and rollout modes

Use `Optional` first to observe adoption, then flip to `Required` once every
bot presents a valid certificate.

```env
Trading__EntryPointListener__Tls__ClientCertificateMode=Optional
Trading__EntryPointListener__Tls__ClientCa__BundlePath=/certs/bot-ca-bundle.pem
Trading__EntryPointListener__Tls__ClientCa__DenyListPath=/certs/bot-denylist.txt
Trading__EntryPointListener__Tls__ClientCa__ReloadInterval=00:05:00
Trading__EntryPointListener__Tls__RequireClientAuthEku=true
Trading__EntryPointListener__AllowInsecureMtlsInProduction=false
```

| Env var | Values / default | Operational meaning |
|---|---|---|
| `Trading__EntryPointListener__Tls__ClientCertificateMode` | `None` / `Optional` / `Required` (`None`) | `None` is PAT-only; `Optional` requests and validates a cert if present; `Required` rejects connections without a trusted client cert. |
| `Trading__EntryPointListener__Tls__ClientCa__BundlePath` | path | PEM bundle of trusted bot issuer CA(s). Required when mTLS is enabled. |
| `Trading__EntryPointListener__Tls__ClientCa__DenyListPath` | path or empty | Newline-delimited SHA-256 leaf thumbprints to reject at handshake time. |
| `Trading__EntryPointListener__Tls__ClientCa__ReloadInterval` | `TimeSpan`, e.g. `00:05:00` | Poll interval for hot-reloading the CA bundle and deny-list. |
| `Trading__EntryPointListener__Tls__RequireClientAuthEku` | `true` / `false` | Require client leafs to carry the `clientAuth` EKU. |
| `Trading__EntryPointListener__AllowInsecureMtlsInProduction` | `false` / `true` | Explicit audited escape hatch for less-secure production mTLS posture. |

`ClientCertificateMode` only makes sense when server TLS is enabled
(`Trading__EntryPointListener__Tls__Required=true`). In Production, the boot
guard banner should show the selected mTLS mode and CA bundle; treat
`mTLS: None` or `Optional` as a conscious public-exposure risk unless an
upstream private network boundary is doing admission control.

### 2.3 CA rotation without restart

The CA bundle is a concatenated PEM file and is hot-reloaded within
`ClientCa:ReloadInterval`:

1. Append the new CA certificate to the existing bundle, leaving the old CA in
   place for overlap.
2. Wait at least one reload interval and confirm new bot connections succeed
   with leaves issued by the new CA.
3. Re-issue / redistribute bot leaf certificates under the new CA.
4. After the overlap window, remove the old CA from the bundle.
5. Wait one reload interval. New handshakes under the retired CA now fail; no
   listener restart is required.

### 2.4 Fast revocation by SHA-256 deny-list

For network-free emergency revocation, add the compromised leaf certificate's
SHA-256 thumbprint to the deny-list file. New handshakes using that leaf are
rejected after the next `ReloadInterval`, even if the certificate still chains
to a trusted CA.

Deny-list format:

- one SHA-256 thumbprint per line;
- 64 hex characters after normalisation;
- uppercase is canonical, but separators such as `:` or spaces are ignored;
- blank lines and `#` comments are allowed.

Example:

```text
# revoked bot leafs
9F2A6C0E4B1D3A5C7E8F90123456789ABCDEF0123456789ABCDEF0123456789
```

This revokes the certificate globally for the listener. Credential revocation
still happens through the existing user-bot credential flow and rejects at
Negotiate time regardless of certificate validity.

### 2.5 Public-exposure hardening

mTLS chain building happens during TLS handshakes, before the Negotiate
rate-limit is reached. For public listeners, put the service behind an
upstream LB / WAF / firewall with connection-rate controls. The listener also
has an opt-in accept-loop limiter:

```env
Trading__EntryPointListener__AcceptRateLimit__ConnectionsPerSecondPerIp=0
Trading__EntryPointListener__AcceptRateLimit__BurstPerIp=30
```

`ConnectionsPerSecondPerIp=0` disables the in-process limiter by default.
Tune both values only after sizing expected reconnect bursts; the default
production posture relies on upstream LB/WAF controls for internet exposure.

---

## 3. User-bot tenant lifecycle

For public bot operators. All credential APIs are per-user; admin kill /
mass-cancel live under `/admin` (role `admin`). Secret provisioning for the
public overlay is in `docs/operations/fixp-listener.md` and
`docker/docker-compose.public.yml`.

### 3.1 Provision a bot tenant

1. Create the human/bot user (seed via `Trading:Auth:Users` or the auth
   store). Assign a `Firm` so orders route correctly.
2. Mint a credential: `POST /api/user-bot-credentials` `{ "label": "...",
   "boundCertThumbprint": "<sha256 hex|null>" }`. The plaintext PAT
   (`b3t_<shortId>_<secret>`) is shown **once** — hand it to the bot, never
   logged. Pin a cert thumbprint (mTLS, #540) for a second factor.
3. Allocate/read the durable FIXP identity with
   `POST /api/user-bot-credentials/{id}/session`; the response carries
   `sessionId` and `sessionVerId` and returns 404 for another user or a revoked
   credential.
4. The bot connects FIXP, Negotiates with the PAT and those session values,
   then Establishes and presents its client leaf.

### 3.2 Rotate / revoke

- **Revoke (now):** `DELETE /api/user-bot-credentials/{id}` — soft-revoke,
  rejected at next Negotiate. Cross-user → 404 (no id oracle).
- **Re-issue:** mint a new credential, switch the bot, revoke the old. Until
  overlap-window rotation ships (RFC `user-bot-fixp-rotation-v0`, #530) this is
  a brief flag-day; schedule it.
- **Cert rotation:** repin thumbprint, or add the leaf to the deny-list (§2.4)
  for instant revoke; PAT stays valid.

### 3.3 Incident response — rogue bot or session

1. **Stop the orders:** kill-switch the tenant —
   `POST /admin/kill/end-client/{id}` (or `/admin/kill/firm/{id}`). Working
   orders cancel; new submits rejected until `DELETE` revives.
2. **Cut access:** revoke the credential (§3.2). Next Negotiate fails;
   in-flight session has no working orders left after kill.
3. **Cert-level:** add the leaf thumbprint to the deny-list (§2.4) — global,
   network-free, ~one reload interval.
4. **Confirm:** `GET /admin/kill` shows killed end-clients/firms; reject-reason
   metrics (#533) show `reject:credentials` climb. Mark stuck venue orders
   stale via `/admin/firms/{firmId}/orders/{clOrdId}/mark-stale` if needed.

---

## 4. Cross-references

- **Alert rules.** [`ops/perf-v0-alerts.md`](ops/perf-v0-alerts.md)
- **Metric inventory.** [`METRICS.md`](METRICS.md)
- **Observability wiring (host / k8s).** [`OBSERVABILITY.md`](OBSERVABILITY.md)
- **FIXP listener ops** (drain, sessions, admin endpoints).
  [`operations/fixp-listener.md`](operations/fixp-listener.md)
- **RFC.** [`rfcs/perf-hardening-v0.md`](rfcs/perf-hardening-v0.md) §4.2
  (durability), §5.3 (per-connection writer / drain), §6.3
  (backpressure policy).
- **Composite results.** [`perf-hardening-v0-results.md`](perf-hardening-v0-results.md)
- **Sandbox / legal framing.** [`SANDBOX-AND-LEGAL.md`](SANDBOX-AND-LEGAL.md)
- **mTLS RFC.** [`rfcs/user-bot-fixp-mtls-v0.md`](rfcs/user-bot-fixp-mtls-v0.md)
- **Rotation RFC.** [`rfcs/user-bot-fixp-rotation-v0.md`](rfcs/user-bot-fixp-rotation-v0.md)
- **Edge-topology RFC.** [`rfcs/user-bot-fixp-edge-topology-v0.md`](rfcs/user-bot-fixp-edge-topology-v0.md)
