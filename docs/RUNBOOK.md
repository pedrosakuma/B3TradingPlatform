# Operations Runbook

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

### 1.2 `fixp.outbound.drain.shutdown.abandoned` (warning log)

| Field | Value |
|---|---|
| Source | Structured log line in [`Hosting/FixpOutboundChannelWriter.cs`](../backend/src/B3.Trading.EntryPointListener/Hosting/FixpOutboundChannelWriter.cs) at `LogWarning` (the `_drainLoop.WaitAsync` 250 ms catch) |
| Origin | P8 / F3 — PR [#219](https://github.com/pedrosakuma/B3TradingPlatform/pull/219), RFC §5.3.2 |
| Type   | **Warning log** — *not* an OTel counter today (see follow-up note below) |

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

**Alert.** Any occurrence is operator-visible. Recommended:
`count_over_time({msg=~"fixp.outbound.drain.shutdown.abandoned.*"}[5m]) > 0`
→ **page**. (Translate the LogQL above to your stack — Loki
example shown.) Contrast with the sibling `.timeout` line, which
should be alerted at info-level only — that one is the documented
"slow peer, deadline elapsed" path and is bounded by
`OutboundDrainShutdownTimeout` (§1.3 below).

**Mitigation:**

1. Pull the `connectionId` from the log line and grep recent
   FIXP listener logs for matching `Negotiate`, `Establish`,
   `Terminate` to identify the credential / firm.
2. If isolated to one peer, the peer is misbehaving — coordinate a
   forced session cleanup via `/admin/fixp` (see fixp-listener
   ops doc).
3. If broad — multiple `connectionId`s in a short window — the
   write path itself has regressed in cancellation behaviour. Roll
   back the most recent listener change and open a P0.

> **Follow-up.** Today this is a log line only. Issue
> [#231](https://github.com/pedrosakuma/B3TradingPlatform/issues/231)'s
> table named `fixp.outbound.drain.shutdown.abandoned` as a
> *metric*; it is currently emitted **only** as a structured log.
> A counter equivalent is tracked as a separate follow-up
> (TBD — to be filed). Until then alerting must be log-derived.

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

**Drift detection.** Today the trading-host does **not** emit a
gauge for the running value, so a Prometheus `expr: <gauge> != 1`
rule would silently never fire. Until a `build-info`-style gauge
lands (tracked as a follow-up to #231), enforce drift at deploy
time by diffing the rendered config against the documented
default in CI; see [`ops/perf-v0-alerts.md`](ops/perf-v0-alerts.md)
§1.1 for the skeleton runtime rule to enable later.

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

**Drift detection.** Same constraint as §1.3: no runtime gauge
exists today, so enforce drift at deploy time. Skeleton
Prometheus rule for when the gauge lands is in
[`ops/perf-v0-alerts.md`](ops/perf-v0-alerts.md) §1.1.

---

## 2. Cross-references

- **Alert rules.** [`ops/perf-v0-alerts.md`](ops/perf-v0-alerts.md)
- **Metric inventory.** [`METRICS.md`](METRICS.md)
- **Observability wiring (host / k8s).** [`OBSERVABILITY.md`](OBSERVABILITY.md)
- **FIXP listener ops** (drain, sessions, admin endpoints).
  [`operations/fixp-listener.md`](operations/fixp-listener.md)
- **RFC.** [`rfcs/perf-hardening-v0.md`](rfcs/perf-hardening-v0.md) §4.2
  (durability), §5.3 (per-connection writer / drain), §6.3
  (backpressure policy).
- **Composite results.** [`perf-hardening-v0-results.md`](perf-hardening-v0-results.md)
