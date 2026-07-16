# RFC: Durability classes and fail-closed persistence semantics v0

| Field | Value |
| --- | --- |
| Status | Proposed |
| Tracking | [#621](https://github.com/pedrosakuma/B3TradingPlatform/issues/621) |
| Parent | [#620](https://github.com/pedrosakuma/B3TradingPlatform/issues/620) |
| Immediate implementation | [#623](https://github.com/pedrosakuma/B3TradingPlatform/issues/623) |
| Follow-ups | [#628](https://github.com/pedrosakuma/B3TradingPlatform/issues/628), [#627](https://github.com/pedrosakuma/B3TradingPlatform/issues/627), [#629](https://github.com/pedrosakuma/B3TradingPlatform/issues/629) |
| Related | [`PERSISTENCE.md`](../PERSISTENCE.md), [`perf-hardening-v0`](perf-hardening-v0.md), [`user-bot-fixp-listener-v0`](user-bot-fixp-listener-v0.md), [`risk-pipeline-ordering-v0`](risk-pipeline-ordering-v0.md), [`integration-real-stack-v0`](integration-real-stack-v0.md), [`runbook-failover-recovery.md`](../operations/runbook-failover-recovery.md) |

## 1. Context

The WAL began as an audit log and boot accelerator for state that could be
reconstructed from the venue's Execution Report (ER) stream. The platform now
also stores facts that **cannot** be reconstructed from B3: kill-switch state,
operator halts, cash adjustments, bot credentials and revocations, FIXP bot
session versions, sub-accounts, algo control state, and outbound order intent.
One durability rule can no longer safely cover every event.

The current implementation has four important boundaries:

1. `EventDispatcher.Dispatch` serialises the event outside its lock, then calls
   `IEventStore.Append` and applies the in-memory mutation under one lock.
2. `FileEventStore.Append` assigns a sequence and performs a non-blocking
   `Channel.TryWrite`; it does **not** write the record to the file or `fsync`.
3. The writer drains up to `GroupCommitMaxRecords` (default 512) or waits for
   `GroupCommitWindow` (default 10 ms), writes the framed records, then calls
   `FileStream.Flush(flushToDisk: true)` when `FsyncOnFlush=true`.
4. `FlushAsync` inserts a fence in the same channel and completes it after the
   active segment has been flushed.

That means the method currently named `Append` is an **admission** boundary,
not a disk-append or durability boundary.

### 1.1 Current gaps

| Gap | Current implementation evidence | Consequence |
| --- | --- | --- |
| Writer faults are not terminal | `FileEventStore.WriterLoopAsync` logs a critical exception, but `Append` can continue accepting records into an undrained channel. | Callers can receive success for records that can never become durable. |
| ERs fall back to memory-only apply | `EntryPointExecutionReportRouter` catches `WalBackpressureException` and calls `ExecutionReportProcessor.Apply` inside `RunExclusive`. | Live order, cash, fee, P&L and position state can move without a replayable record. |
| Synthetic transitions fall back to memory | `OrderSubmissionService`, `OrderModifyService` and `IocFokWatchdog` publish or apply synthetic terminal state after append rejection. | A restart can resurrect an order that clients were told was rejected or cancelled. |
| Readiness ignores persistence and exchange ingress | `/ready` checks drain and identity-directory health only. `/health` reports exchange status separately. | A load balancer may route order traffic to a process that cannot durably accept it or cannot send to a required venue session. |
| Local-only success normally precedes `fsync` | Kill switch, cash, sub-account and credential mutations use ordinary `Dispatch`. | A successful HTTP response can be rolled back by a crash inside the group-commit window. |
| ER replay has no durable application cursor | `ExecutionReportEnvelope` does not carry venue `SeqNum`; `_lastInboundSeqNum` in `B3EntryPointClientGateway` is volatile and advances before subscriber completion. | “The venue will replay it” is not yet tied to the last ER prefix proved durable locally. |
| Snapshots can cover an uncommitted prefix | `WithSnapshotLock` captures `IEventStore.CurrentSeq`, and `SnapshotService` immediately writes that state; `CurrentSeq` includes channel-admitted records that may not have reached `fsync`. | A crash can restore a snapshot containing state whose causal WAL records never became durable. |
| CRC-valid does not prove committed | `SegmentReader` yields every complete CRC-valid frame and `FileEventStore` assigns replay sequences to all yielded frames. Kernel writeback may preserve a pre-`fsync` frame by chance. | Recovery can apply a survivor that was never inside the acknowledged durable prefix unless a commit boundary is persisted. |
| Production can be configured without durable storage | `PersistenceOptions.Enabled=false` wires `NullEventStore`; `FsyncOnFlush=false` is not guarded by environment. | An apparently healthy production host can acknowledge local-only state with RPO greater than zero. |

This RFC defines the target contract. It does not change runtime behaviour;
the first containment implementation is #623.

## 2. Decisions

1. **Durability is selected per operation, not solely per CLR event type.**
   `ExecutionReportReceivedEvent` may be venue-originated or synthetic; an
   `AuditLogEvent` may be advisory or a required precondition for an admin
   mutation. Dispatch must carry explicit durability metadata.
2. **No state mutation may be used as a fallback for a rejected WAL
   admission.** If the event is required to reconstruct or explain the state,
   append failure means no apply, no fan-out, no gateway side effect and no
   success acknowledgement.
3. **Venue-recoverable ingress gets a reserved, bounded lane.** Saturation
   pauses venue progress or tears down the session for retransmission; it does
   not apply the ER only in memory.
4. **Local-authoritative mutations are commit-before-success.** The commit
   includes log `fsync` and durable publication of `lastDurableSeq`.
5. **Outbound venue intent is commit-before-send.** #628 owns the detailed
   approved/to-send/sent/acknowledged state machine, but it may not weaken this
   log-`fsync` + marker boundary.
6. **A writer or `fsync` failure is sticky.** The store becomes terminally
   faulted until process restart after operator remediation.
7. **Readiness means order-ingress capability.** `/ready` is non-200 while
   recovery is incomplete, persistence is saturated/faulted/disabled in a
   durable environment, the host is draining, identity is unavailable, or a
   required Real exchange session cannot accept orders.
8. **`/live` remains process liveness only.** Disk exhaustion must not create
   an automatic restart loop that destroys diagnostic access.

## 3. Terminology and invariants

### 3.1 Boundaries

- **Admitted** — payload serialised, global WAL sequence assigned, and an
  owned record accepted into a bounded in-process lane.
- **Appended** — the complete length/CRC/payload frame has been written to the
  active log stream. It may still exist only in kernel/page cache.
- **Log-fsynced** — every log stream touched through the record has completed
  `Flush(flushToDisk: true)`.
- **Committed / durable** — the log-fsynced prefix has also been published in
  the persisted commit marker. Durability is a prefix: if sequence `N` is
  committed, every admitted sequence `< N` is also committed. A CRC-valid
  frame beyond the marker is not durable.
- **Applied** — the event's in-memory state transition completed.
- **Externally acknowledged** — an HTTP response, bot FIXP response, venue
  receive-progress signal, WebSocket/drop-copy event, or other observation
  tells a peer that the transition was accepted.
- **Wire side effect** — a new/cancel/replace request is handed to the exchange
  gateway where delivery may become ambiguous.

### 3.2 Invariants

| ID | Invariant |
| --- | --- |
| I1 | If event B's apply observes event A's apply, then `seq(B) > seq(A)`. |
| I2 | No replay-significant mutation occurs unless its event was admitted first. |
| I3 | No local-authoritative success is externally acknowledged before its event is durable and applied. |
| I4 | No outbound venue mutation is attempted before its intent is durable. |
| I5 | No venue-recoverable event is applied unless it was admitted into the reserved lane. |
| I6 | The venue replay cursor advances only to a venue sequence covered by the locally durable WAL prefix. |
| I7 | A persistence fault is monotonic for the process lifetime: `Faulted` never returns to `Ready`. |
| I8 | A published snapshot at WAL sequence `N` reflects exactly the applied prefix through `N`, and the persisted commit marker proves `lastDurableSeq >= N`. |
| I9 | Fan-out happens only after the corresponding state transition reaches the boundary required by its durability class. |
| I10 | Production order ingress cannot be ready with `NullEventStore` or `FsyncOnFlush=false`. |
| I11 | Recovery never replays a CRC-valid frame with `seq > lastDurableSeq`; such frames are uncommitted survivors. |

## 4. Durability classes

### 4.1 Class R — rebuildable or advisory

The event does not own business state. Losing it may reduce diagnostics or a
projection, but cannot change risk, authority, money, order lifecycle or a
protocol generation.

Examples:

- non-gating login/access audit envelopes;
- `AlgoVwapSlicedEvent`, `AlgoPeggedRepeggedEvent` and
  `AlgoChildModifiedEvent` where the causal order/cancel/replace event is the
  recovery source;
- `OrderExpiredEvent` when the cancel/terminal event is authoritative;
- `BotSessionSeqAdvancedEvent`, whose existing contract is a best-effort
  replay watermark.

Boundary:

```text
admit → append asynchronously → optional fsync by group commit
```

Class R has no business acknowledgement to gate. It may be rejected under
backpressure with a metric and structured log, but it must not wrap a state
mutation. Compliance-required audit is not Class R; it is Class L.

### 4.2 Class V — venue-recoverable fact

The event is authoritative for local projections but can be retransmitted by
an external sequenced source.

Examples:

- real `ExecutionReportReceivedEvent` (`Synthetic=false`);
- sequenced venue `BusinessRejectReceivedEvent`;
- `RealizedPnlEvent` and ER-driven stale clearing when every input needed to
  reproduce them is already durable in the event stream;
- venue-origin trading-halt changes only when that feed has an explicit
  snapshot/replay contract. Otherwise they are Class L.

Boundary:

```text
reserved admission → append → apply → venue progress may advance
                                  ↘ group log fsync + marker → durable cursor
```

Class V preserves async group commit, but only if the adapter can reconnect
from the last **durable** per-session cursor. A crash may lose the admitted or
appended-but-unfsynced tail locally, and recovery deliberately discards any
log-fsynced-but-unmarked survivor tail; the venue must retransmit that range.
Duplicates are expected and application processing must remain idempotent.

If the SDK cannot reconnect from a durable cursor, the deployment must use the
strict fallback: Class V becomes `fsync`-before-apply/progress for that
session. It is not permitted to assume replay without an executable cursor
contract.

### 4.3 Class L — local-authoritative mutation

The platform is the only authoritative source. Losing the event would roll
back a control, security, accounting or lifecycle decision.

Examples:

- `KillSwitchToggledEvent`;
- operator-origin `SymbolHaltToggledEvent` and
  `SessionPhaseChangedEvent`;
- `UserBotCredentialCreatedEvent`, `UserBotCredentialRevokedEvent` and
  `UserBotCredentialCertBindingChangedEvent`;
- `CashLedgerEvent`;
- `FeeAccruedEvent` under the current fee model;
- `SubAccountCreatedEvent` and `SubAccountDeactivatedEvent`;
- synthetic ERs and synthetic terminal transitions;
- local algo create/cancel/terminal state;
- `OrderStaledEvent` and operator-driven `OrderStaleClearedEvent`;
- `BotSessionInitializedEvent` and `BotSessionVerAdvancedEvent`;
- an `AuditLogEvent` used as a required audit-first precondition.

Boundary:

```text
admit → append → log fsync → marker commit → apply → fan-out/ack
```

The implementation must sequence Class L work so no later apply overtakes the
durability fence. It may use an asynchronous commit sequencer rather than
holding the existing monitor during I/O, but the externally visible order must
remain the same.

If log `fsync` or marker publication fails after append, the operation is not
acknowledged, its apply does not run, the store becomes `Faulted`, and
readiness drops. If a crash occurs after marker commit but before apply/ack,
replay applies the event; a retry may observe “already applied” and must be
idempotent.

`FeeAccruedEvent` is Class L today because replay computes a missing fee from
the **current** `FeeOptions`. The code explicitly documents that changing fee
options between the fill and replay can produce a different amount. A
fill-bearing ER and its fee record therefore form one accounting commit: fee
keeper mutation and accounting fan-out cannot complete until the pre-computed
`FeeAccruedEvent` is durable. It may be reclassified as V-derived only after
the exact fee schedule/version and rounding inputs are themselves durable
(for example, a versioned `FeeRateChangedEvent` plus a schedule identifier on
the ER/fee record).

### 4.4 Class O — outbound venue intent

The event authorises an irreversible or ambiguous external side effect.

Examples:

- `OrderSubmittedEvent` before `SubmitAsync`;
- `OrderCancelRequestedEvent` before `CancelAsync`;
- `OrderReplaceRequestedEvent` before `CancelReplaceAsync`;
- algo repeg/slice intent that will cause one of those gateway calls.

Boundary:

```text
admit → append → log fsync → marker commit → apply intent/watermark
                                            → gateway send → client ack
                                            → venue ER resolves
```

The durable record must contain enough identity to reconcile without reusing
or inventing a ClOrdID. A crash:

- before marker commit means the gateway was not called;
- after marker commit but before send leaves a durable to-send intent;
- after send but before ER leaves an ambiguous sent intent;
- after ER is resolved by Class V processing.

#628 defines the persisted outbound substates, resend proof and
SessionVerId rules. Blindly deleting a `PendingNew` or blindly resending it is
not an acceptable implementation of this RFC.

### 4.5 Event classification summary

| Family | Class | Notes |
| --- | --- | --- |
| Real ER and sequenced venue reject | V | Reserved lane; durable venue cursor required. |
| ER-derived realised P&L/projection events | V-derived only when replay inputs are durable | Parent ER may be the replay pivot only when replay is configuration-independent. |
| `FeeAccruedEvent` | L under the current model | Missing fees are recalculated with live `FeeOptions`, so the fill+fee accounting commit must wait for fee durability. |
| Synthetic reject/cancel/terminal ER | L | No venue can replay it. Memory-only fallback is forbidden. |
| Submit/cancel/replace intent | O | Log `fsync` + marker commit before gateway call. |
| Kill/revive, operator halt/phase, cash, sub-account | L | Log `fsync` + marker commit before apply and HTTP success. |
| Credential create/revoke/re-pin | L | Plaintext token or revocation result is not exposed before durability. |
| Bot session init/version | L | Version remains commit-before-observation, strengthening the FIXP listener RFC's `fsync` fence. |
| Bot outbound sequence checkpoint | R | Best-effort watermark; overflow/version bump remains L. |
| Algo state | L unless purely explanatory | Any state that changes scheduling or restart action is L. |
| Audit | R or L by call site | Admin/security mutation audit-first is L; non-gating telemetry is R. |
| Snapshot/index/EOD materialisation | Derived cache | Failure alerts but does not alter WAL health while append/commit remains healthy. |

## 5. State machines

### 5.1 Event lifecycle

```text
                         serialization/admission failure
                        ┌────────────────────────────────────────► Rejected
                        │
New ─► Admitted ─► Appended ─► Log-fsynced ─► Committed ─► Applied ─► Acknowledged
          │             │                            │
          │             │                            └─ Class O ─► Sent ─► Resolved by ER
          │             │
          │             └─ Class V ─► Applied/Acknowledged
          │                              │
          └─ crash/fault                 └─ later marker commit advances cursor
```

- Class R may stop after `Rejected` without a business effect.
- Class V may apply before committed, but only the durable venue cursor makes
  the replay contract safe.
- Classes L and O cannot reach `Applied` before `Committed`.
- No class transitions from `Rejected` to `Applied`.

### 5.2 Persistence health

```text
Starting ─► Recovering ─► Ready ─► Saturated ─► Ready
    │             │         │          │
    │             └─► ReconciliationRequired
    └─────────────┴─────────┴──────────┴────► Faulted
                                               │
                                               └─ restart after remediation only

Ready/Saturated ─► Draining ─► Stopped
```

- `Saturated` is transient queue pressure with a live writer.
- `ReconciliationRequired` is a recovered process that lacks a covering
  legacy/session baseline; it remains live and unready until §12.2 completes.
- Return from `Saturated` requires the queue below a configured low watermark
  **and** a successful durability fence. A timer alone cannot restore health.
- `Faulted` records the first writer/flush exception, closes all admission
  lanes, fails outstanding fences, and is sticky.
- A snapshot failure does not enter `Faulted`; a WAL write or `fsync` failure
  does.

### 5.3 Persisted commit marker

The writer maintains a per-store commit marker outside the rebuildable sparse
index. At minimum it contains:

```text
formatVersion
walGeneration
lastDurableSeq
committedTail: (segmentId, endOffset)
durable venue cursor(s): (firmId, sessionVerId, inboundSeqNum)
checksum
```

Publishing a durable prefix is ordered:

1. write all records in the prefix;
2. `fsync` every log/sidecar and directory entry whose contents are required
   to enumerate it;
3. write the new marker to a sibling temporary file;
4. `fsync` the temporary marker;
5. atomically replace the prior marker and `fsync` its parent directory.

Only completion of step 5 advances `lastDurableSeq`, completes Class L/O
durability fences, or advances a durable venue cursor. A crash between log
`fsync` and marker publication conservatively leaves the older prefix
committed.

On recovery, complete CRC-valid frames with `seq > lastDurableSeq` are
**uncommitted survivor frames**. They must be ignored and truncated or
quarantined before new appends; the sparse index and sequence sidecars are
then rebuilt as needed. Corruption or a missing frame at or below
`lastDurableSeq` is committed-prefix corruption and fails recovery closed.

### 5.4 Snapshot durability fence

Snapshots may cover only a committed prefix. The normative capture sequence
is:

1. under the dispatcher/commit-sequencer snapshot lock, capture immutable raw
   state and the highest contiguous **applied** sequence as `snapshotSeq`;
2. release the lock and await `FlushThroughAsync(snapshotSeq)`, which does not
   complete until the commit marker proves
   `lastDurableSeq >= snapshotSeq`;
3. if the fence fails or is cancelled, discard the raw capture;
4. only after the fence succeeds, project/write the snapshot and atomically
   publish its pointer.

Events after `snapshotSeq` may continue through the sequencer while projection
and snapshot I/O run, but the captured state remains exactly the prefix
through `snapshotSeq`. `snapshotSeq` comes from `lastAppliedSeq`, not
`IEventStore.CurrentSeq`: an admitted Class L/O record waiting for commit must
not advance the snapshot sequence or appear in captured state. A snapshot must
never use `CurrentSeq` as proof of either application or durability.

Recovery validates a snapshot before restore:

- `snapshot.Seq <= commitMarker.lastDurableSeq`;
- the snapshot's WAL generation/lineage matches the marker;
- the committed WAL prefix needed to replay after the snapshot is present and
  structurally valid.

A snapshot that fails any check is rejected and never applied. Recovery may
fall back to an older valid snapshot or full committed-WAL replay. If neither
can prove a complete state, startup remains failed/unready; it must not restore
the newer snapshot and hope venue replay repairs local-only state.

## 6. ACK, append and `fsync` rules

| Origin/action | Admission | Apply | Required durable boundary | External success boundary |
| --- | --- | --- | --- | --- |
| REST/FIXP-bot new, cancel, replace | Fail fast if local lane unavailable | After intent durability | Intent log-`fsync` + marker commit before gateway | After durable intent and gateway acceptance; exact response semantics refined by #628 |
| Local kill/revive, halt/phase, cash, sub-account | Fail fast | After commit marker | Per-operation log-`fsync` + marker fence | HTTP 2xx only after apply |
| Credential create | Fail fast | After commit marker | Created event committed | Plaintext token shown only after apply |
| Credential revoke/re-pin | Fail fast | After commit marker | Mutation committed | HTTP success and active-session termination only after apply |
| Bot session version bump | Fail fast | After commit marker | Version event committed | New version may appear in FIXP response only afterward |
| Real venue ER | Reserved blocking admission | After admission/append | Async group log-`fsync` + marker; durable venue cursor | Session progress only under §8 replay rules |
| Fill ER with current fee model | Reserved admission as one accounting group | Position/cash/fee apply after fee record commit | ER + pre-computed `FeeAccruedEvent` covered by commit marker | Accounting fan-out/session progress only after the group is safe |
| Synthetic ER/watchdog transition | Fail fast | After commit marker | Event committed | Fan-out/result only after apply |
| Advisory audit/projection | Best effort | No business mutation | Group commit only | No business acknowledgement depends on it |

`FlushAsync` cancellation or caller disconnect does not undo an admitted
record. The caller receives no success, while the event may later become
durable. Therefore every Class L/O endpoint needs an idempotent lookup or
retry rule; “timeout means not applied” is forbidden.

## 7. Backpressure, permanent faults and readiness

### 7.1 Admission lanes

The implementation uses one globally ordered commit stream fed by at least two
bounded admission lanes:

- **local lane** — Classes L/O and ordinary Class R. Admission is fail-fast;
  clients receive a structured 503/reject and no mutation occurs.
- **venue-reserved lane** — Class V venue messages. Capacity is reserved so
  user traffic and advisory audit cannot consume every slot. The producer may
  wait for bounded capacity because the alternative is session
  retransmission, not a memory-only apply.

Lane separation is an admission policy, not separate WAL ordering. The commit
sequencer still assigns one total WAL sequence and must preserve per-firm
venue order.

### 7.2 Saturation

On local-lane saturation:

1. reject the initiating operation;
2. do not apply, publish or call the gateway;
3. enter `Saturated`;
4. make `/ready` return 503;
5. continue draining already admitted work.

On venue-lane saturation, follow §8: pause consumption; if bounded waiting
expires, disconnect without advancing the durable replay cursor.

### 7.3 Writer or `fsync` fault

On the first write, rotation, required sidecar, commit-marker publication or
`fsync` exception:

1. atomically store the first exception and transition to `Faulted`;
2. close every lane and fail every pending durability fence;
3. make future `Append`/admit and `FlushAsync` fail immediately with a
   persistence-fault exception that retains the original cause;
4. stop order/algo/admin mutation ingress;
5. stop or disconnect venue sessions so uncommitted messages replay later;
6. expose the fault through health, metrics and logs;
7. remain live for diagnosis, but never become ready again in that process.

### 7.4 Readiness

`/ready` returns 200 only when all are true:

- startup recovery completed successfully;
- drain is false;
- identity directory is ready;
- persistence is enabled and `Ready` for production-capable compositions;
- every required Real exchange session is established and reports
  `readyForOrders=true`;
- no unresolved recovery condition requires operator reconciliation.

`/health` remains 200 and adds a persistence block equivalent to:

```json
{
  "state": "ready|saturated|faulted|recovering|reconciliationRequired|draining",
  "lastAdmittedSeq": 123,
  "lastDurableSeq": 120,
  "queueDepth": 3,
  "venueReservedDepth": 0,
  "lastSuccessfulFsyncAt": "2026-07-16T17:00:00Z",
  "faultType": null,
  "faultAt": null
}
```

The body must not expose paths, payloads or exception messages that contain
sensitive data. `/live` remains 200 while the process can serve diagnostics.

## 8. ER replay and backpressure

### 8.1 Required cursor

Every venue-sequenced envelope admitted as Class V must carry:

- `FirmId`;
- venue `SessionVerId`;
- venue inbound `SeqNum`;
- message kind and business payload.

`ExecutionReportReceivedEvent` gains optional fields for these values so old
WAL records remain readable. The store tracks a durable cursor per
`(FirmId, SessionVerId)` and advances it only in the same durability boundary
that commits the WAL prefix containing that message.

### 8.2 Live path

1. Read the next venue message in session order.
2. Attempt admission to the reserved lane.
3. If admitted, append and apply in WAL order.
4. Do not advance the durable cursor until the containing prefix is published
   in the commit marker.
5. Duplicates from retransmission are admitted or safely deduplicated only by
   a mechanism that cannot skip a missing durable business effect.

The current `RunExclusive(() => processor.Apply(...))` fallback is removed.
The gateway event loop must not catch a persistence rejection and continue as
if the message were consumed.

### 8.3 Saturation and disconnect

When the reserved lane is full:

- pause reading/dispatch for a bounded interval;
- do not update `_lastInboundSeqNum` or any application-visible cursor before
  successful admission;
- if capacity returns, resume in order;
- if the wait expires or persistence is faulted, cancel the event loop and
  disconnect the firm session;
- reconnect from `durableVenueSeq + 1`.

If the upstream SDK cannot expose the required flow-control/reconnect cursor,
the adapter must use strict log-`fsync` + marker-commit-before-return for venue
messages until the SDK contract is extended. Continuing and relying on an
undocumented replay window is forbidden.

### 8.4 Restart and retransmission

On restart:

1. replay the durable local WAL;
2. restore `lastDurableSeq` and the per-session durable venue cursor from the
   commit marker, not from `CurrentSeq`, CRC-valid tail frames, or a snapshot
   alone;
3. reconnect/reattach the FIXP session;
4. request or permit retransmission from the next venue sequence;
5. process duplicates idempotently;
6. remain unready until retransmission reaches the venue's current head.

If reattach fails and a new `SessionVerId` has no replay window, readiness
stays false for that firm until #628's outbound reconciliation and an
authoritative venue snapshot/operator procedure establish a safe baseline.
The platform must not silently declare old working orders correct or discard
them merely because a session rolled.

## 9. Failure matrix

| Failure point | R | V | L | O | Readiness / recovery |
| --- | --- | --- | --- | --- | --- |
| Serialisation fails | Drop + metric; no mutation | Do not consume; disconnect if unrecoverable | Fail request; no apply | Fail request; no send | Fault only if serializer/store invariant is compromised |
| Local lane full | Drop if explicitly advisory | n/a | 503/reject; no apply | 503/reject; no send | `Saturated`, `/ready=503` |
| Venue lane full | n/a | Pause; timeout disconnect; no apply | n/a | n/a | `/ready=503` until drained/replayed |
| Crash after admission, before file write | May be lost | Venue replays from durable cursor | No success was possible | Gateway was not called | Replay durable prefix |
| Crash after append, before log `fsync` | May be lost | Venue replays | No apply/ack | No apply/send | CRC-valid survivor frames beyond marker are ignored/truncated |
| Crash after log `fsync`, before marker commit | Treat as uncommitted | Venue replays from prior cursor | No apply/ack | No apply/send | Older marker remains authoritative even if every frame survived |
| Crash after marker commit, before apply | Replay if relevant | Replay local record; venue duplicate is no-op | Replay applies; client outcome may be unknown | Recover durable to-send intent | Idempotent retry/reconcile |
| Crash after L apply, before HTTP/FIXP response | n/a | n/a | Retry observes already-applied result | n/a | RPO 0; response outcome unknown |
| Crash after O commit, before gateway send | n/a | n/a | n/a | Durable to-send; #628 determines safe send | Not ready until outbound recovery completes |
| Crash after gateway send, before ER | n/a | Eventual ER | n/a | Durable ambiguous sent intent | Reconcile by ClOrdID/session; never reuse ID |
| Writer throws while draining | Advisory loss allowed | Disconnect; replay uncommitted tail | Pending operation fails; no success | Pending send prohibited/fails | Sticky `Faulted`, `/ready=503` |
| Log/marker `fsync` or marker replace throws | Advisory records not committed | Disconnect; cursor does not advance | No apply/ack | No send | Sticky `Faulted` |
| Snapshot write fails | No effect on WAL | No effect on WAL | No effect on committed mutation | No effect on intent | Alert; readiness may remain 200 |
| Torn/invalid frame strictly after marker | Drop | Venue fills tail | L/O records were never acknowledged/sent | Same | Truncate/quarantine tail and recover committed prefix |
| Missing, bad CRC or bad schema at/below marker | Do not skip | Do not advance cursor | Do not guess | Do not guess | Committed-prefix corruption: boot fails and `/ready` never opens |
| Commit marker missing/corrupt in marker-format WAL | n/a | Cursor unknown | Durable local prefix unknown | Durable outbound prefix unknown | Boot fails closed; legacy migration procedure is separate |
| Snapshot seq exceeds marker or lineage mismatches | n/a | n/a | Snapshot rejected | Snapshot rejected | Use older valid snapshot/full committed WAL; otherwise remain unready |
| `FlushAsync` cancelled/client disconnects | No guarantee | Cursor unchanged until marker commit | No success; event may later commit | No success; send only if fence later completes under owned workflow | Retry must be idempotent |
| Disk repaired while process remains faulted | n/a | n/a | n/a | n/a | Still faulted; controlled restart required |

## 10. RPO and RTO

| Class | RPO after external success | Recovery source | RTO objective |
| --- | --- | --- | --- |
| R | Best effort; explicitly not a business-state guarantee | None or recompute | Does not gate readiness |
| V | Business RPO 0; local WAL may lose only the tail guaranteed replayable from the durable venue cursor | Durable WAL prefix + FIXP retransmission | Normal supported-volume reconnect/replay reaches ready within 60 s; page/escalate if not converged within 5 min |
| L | 0 | Durable WAL + optional snapshot cache | Supported-volume snapshot+tail recovery reaches ready within 60 s; never open readiness early |
| O | 0 once a wire attempt is possible | Durable outbound state + venue reconciliation | Same 60 s target when venue responds; remain unready rather than guess during ambiguity |

The 60-second target matches the existing conformance recovery timeout and is
an objective, not permission to skip recovery. Larger retained WALs may require
an indexed/snapshot tuning slice, but readiness remains closed until recovery
is complete.

## 11. Crash and fault test matrix

Implementation is incomplete until deterministic fault injection covers these
boundaries.

| ID | Injection | Required assertion | Test surface |
| --- | --- | --- | --- |
| C1 | Kill after admission before writer drain | L/O not acknowledged or sent; V replayed | Application persistence test |
| C2 | Persist a complete CRC-valid frame but kill before log `fsync`/marker | Recovery ignores and truncates the valid survivor; V tail retransmits | Application + real-stack |
| C3 | Kill after log `fsync` but before marker replacement | Older `lastDurableSeq` wins; newly fsynced frames are not replayed | Application persistence test |
| C4 | Kill after marker commit before L apply | Replay applies exactly once | Application property test |
| C5 | Capture snapshot at admitted `CurrentSeq` then fail its durability fence | Snapshot is not published | Snapshot test |
| C6 | Present snapshot with `Seq > lastDurableSeq` or wrong generation | Recovery rejects it and uses only an older valid baseline | Recovery test |
| C7 | Kill after L apply before response | Retry is idempotent; state remains applied | API test |
| C8 | Kill after O durability before send | Restart identifies to-send, not generic `PendingNew` | #628 tests |
| C9 | Kill after gateway send before ER | Restart treats send as ambiguous and never reuses ClOrdID | #628 + conformance |
| C10 | Saturate local lane | 503/reject; no state, fan-out or gateway call | Application/API tests |
| C11 | Saturate venue-reserved lane | No memory-only fill; session pauses/disconnects and replays | Infrastructure + conformance |
| C12 | Throw writer `IOException` | Store fault is sticky; future append/flush fail immediately | `FileEventStoreTests` |
| C13 | Throw log or marker `fsync`/replace exception | L apply/O send do not occur; readiness is 503 | Infrastructure/API tests |
| C14 | Replay fill with changed `FeeOptions` and no durable fee event | Recovery refuses config-dependent synthesis; fee event/group must be durable | Application recovery test |
| C15 | Crash after kill-switch success | Kill remains active after restart | API + conformance |
| C16 | Crash after credential revocation success | Revoked credential cannot authenticate after restart | Listener integration |
| C17 | Crash after bot version response | Old version remains rejected after restart | Listener integration |
| C18 | Fill while host down | Reconnect replays fill and converges order/cash/position/P&L | Existing `TradingHostCrashRestartSpecTests` |
| C19 | Fill received, admitted but not durable, then SIGKILL | Durable cursor causes the same fill to retransmit and apply once | New conformance scenario |
| C20 | Corrupt committed WAL record | Startup fails closed; no readiness | Recovery test |
| C21 | Snapshot failure with healthy WAL | WAL continues; readiness stays truthful; alert increments | Snapshot test |
| C22 | Required exchange disconnected | `/live=200`, `/ready=503`, order ingress rejected | Existing/new health lifecycle tests |

Property tests must compare recovered state with the **marker-committed
prefix**, not the merely admitted, written or log-fsynced prefix. Existing
`PropertyDurabilityTests` and
`UngracefulStop_NoFlush_RecoversToLastFlushedSeq_NoTornWriteFalsePositives`
cover the current writer but are insufficient because current recovery accepts
every CRC-valid survivor. Marker-aware tests must add class-aware L/O cases,
snapshot rejection and a durable venue cursor model for V.

## 12. Migration and compatibility

### 12.1 WAL and snapshot compatibility

- Keep the current length/CRC/JSON frame.
- Add the persisted commit marker and WAL generation described in §5.3.
- Add venue session/sequence fields as optional fields on existing events.
- New event kinds follow the existing additive discriminator rule.
- Old WAL events without venue cursor fields remain replayable but cannot
  prove a Class V durable replay cursor.
- The venue cursor is published in the same commit marker as
  `lastDurableSeq`. A snapshot may cache it but cannot be its only source.
- Snapshots gain WAL generation/lineage metadata. Additions are
  optional/defaulted for deserialisation, but a legacy snapshot is not accepted
  as a committed baseline until the upgrade procedure below establishes its
  covering marker.
- Deleting snapshots remains safe; deleting or synthesising the commit marker
  does not.

### 12.2 First upgrade

Legacy WAL has neither a commit marker nor a trustworthy durable venue cursor.
A complete CRC-valid tail is insufficient because it may be a pre-`fsync`
survivor. Migration has two cases.

**Controlled upgrade from a healthy old process:**

1. drain order and venue ingress;
2. run and verify the old process's `FlushAsync`;
3. stop it cleanly without admitting more events;
4. have the new binary `fsync` the discovered valid WAL prefix and publish the
   first marker/generation for exactly that quiesced prefix;
5. validate or reject legacy snapshots against that marker;
6. establish the covering venue baseline below before readiness.

**Unclean/unknown legacy shutdown:** do not automatically promote the highest
CRC-valid sequence to `lastDurableSeq`. Treat the local commit and venue cursor
as unknown and enter `ReconciliationRequired`.

Strict future `fsync` prevents new uncertainty but does **not** cover the
legacy gap. Before `/ready` may return 200, one of these covering baselines
must complete and be durably recorded:

- venue retransmission from a sequence/session point known to precede the
  entire uncertain interval, through the current venue head;
- an authoritative venue order/execution/position snapshot or drop-copy
  extract that covers every surviving working order and accounting effect,
  combined with #628 reconciliation of approved/to-send/sent outbound intent;
- an explicit operator reconciliation that compares venue orders,
  executions, positions and cash to local state, resolves every difference,
  and signs/records a durable reconciliation-baseline event.

The resulting baseline marker records its provenance, covered
`(FirmId, SessionVerId, inboundSeqNum)` and WAL generation. If none of the
three is available, the host remains `ReconciliationRequired` with
`/ready=503`; enabling strict future `fsync`, observing a new ER, or copying
`FileEventStore.CurrentSeq` into a marker is not sufficient.

### 12.3 Configuration

- Non-Development production-capable hosts reject
  `Trading:Persistence:Enabled=false`.
- Non-Development production-capable hosts reject `FsyncOnFlush=false`.
- `NullEventStore` remains available to tests and explicitly ephemeral demos;
  such compositions report persistence as `ephemeral` and cannot claim
  production order-ingress readiness.
- Lane capacities, low watermark and bounded venue wait are tunable, but
  changing them cannot change event classification or durability boundaries.

### 12.4 API and client compatibility

Successful response shapes do not need to change. Failure paths become more
honest:

- HTTP mutations return structured 503 while saturated/faulted/unready;
- FIXP bot requests receive an appropriate business/session reject when a safe
  response can be sent, otherwise the connection is terminated;
- timeouts are outcome-unknown and require idempotent lookup/retry;
- health JSON gains additive fields.

No runtime behaviour changes in #621 itself.

## 13. Implementation slices

1. **Containment — #623.** Persist terminal writer fault; fail future
   append/flush; remove ER and synthetic memory-only fallbacks; gate readiness
   on WAL and required exchange sessions. Preserve current successful-path
   enqueue/group-commit semantics.
2. **Durability-aware store contract.** Add explicit durability class,
   persisted commit marker/WAL generation, last-admitted/last-durable
   sequence, durable fences, survivor-tail truncation, health state and fault
   injection seams. Rename concepts in APIs where practical so “append” no
   longer means only “channel admitted”.
3. **Snapshot committed-prefix fence.** Capture `(state, snapshotSeq)`, commit
   through that sequence before publishing, add lineage metadata, and reject
   snapshots ahead of or outside the committed WAL generation.
4. **Class L durable dispatch.** Add ordered commit-before-apply/ack flow;
   migrate kill switch, local halt/phase, cash, sub-accounts, credentials,
   synthetic transitions, fee accrual and required audits.
5. **Venue reserved lane and cursor.** Carry venue seq/session identity,
   reserve capacity, pause/disconnect on pressure, commit cursor with WAL
   durability, and add replay conformance.
6. **Outbound durability — #628.** Define and implement approved/to-send/sent/
   acknowledged states, commit-before-send, idempotent reconciliation and
   session-roll behaviour.
7. **FIXP bot lifecycle — #627.** Apply Class L fences to credential/session
   generation and compose disconnect/replay rules with the listener.
8. **Operations and release gates — #629.** Alert on saturation/fault/fsync
   age, run the crash matrix, prove application-consistent backup/restore, and
   gate promotion on recovery/conformance.
9. **Documentation cutover.** Once slices 1–8 land, update
   [`PERSISTENCE.md`](../PERSISTENCE.md), the failover runbook, metrics and
   conformance inventory from “current async write-behind” to these normative
   class-aware semantics.

## 14. Rejected alternatives

### 14.1 Keep memory-only ER apply

Rejected. It preserves live state only until the next crash and allows a
snapshot to seal state with no corresponding WAL sequence. A reserved lane
plus retransmission is the correct pressure valve.

### 14.2 `fsync` every event

Rejected as the universal rule. It is safe but needlessly serialises
venue-recoverable throughput. Classes L/O require the fence; Class V uses
group commit plus a durable replay cursor.

### 14.3 Treat every event type as one fixed class

Rejected. Synthetic and real ERs share a type but have different recovery
sources. Audit is advisory in some paths and a required precondition in
others.

### 14.4 Automatically clear a writer fault

Rejected. After a write/rotation/`fsync` exception, the process cannot prove
which admitted records or sidecars reached stable storage. A controlled
restart after remediation is the only safe transition.

### 14.5 Let `/ready` mean only “HTTP server is accepting requests”

Rejected. `/live` already serves process liveness. Readiness must represent
whether the instance can safely accept the workload for which it is selected.

### 14.6 Replay every CRC-valid frame

Rejected. CRC proves framing integrity, not that the record was inside the
prefix for which the process completed its durability promise. Recovery is
bounded by the commit marker; valid frames after it are uncommitted survivors.

## 15. Acceptance checklist

- Every replay-significant event is classified R, V, L or O at dispatch.
- Append-before-mutate and `fsync` requirements are explicit for every class.
- The persisted commit marker defines `lastDurableSeq`; valid survivor frames
  beyond it are never replayed.
- Snapshots are published only after a fence commits their sequence and are
  rejected on recovery when ahead of or outside the committed WAL lineage.
- Kill switch and credential revocation are log-`fsync` +
  marker-commit-before-success.
- Fee accrual is Class L until fee schedule/version inputs are durable.
- Real ERs have reserved admission and a durable replay cursor; no
  memory-only fallback exists.
- Legacy readiness requires a covering replay/snapshot/outbound or signed
  operator-reconciliation baseline; strict future `fsync` alone is insufficient.
- Synthetic transitions are Class L.
- Writer faults are sticky and visible to append, flush, health and readiness.
- `/ready` represents durable order-ingress capability while `/live` remains
  separate.
- The failure matrix and crash matrix are executable through the linked
  implementation slices.
