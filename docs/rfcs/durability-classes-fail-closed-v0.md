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
4. **Local-authoritative mutations are `fsync`-before-success.**
5. **Outbound venue intent is `fsync`-before-send.** #628 owns the detailed
   approved/to-send/sent/acknowledged state machine, but it may not weaken this
   boundary.
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
- **Durable** — the log stream containing the record has completed
  `Flush(flushToDisk: true)`. Durability is a prefix: if sequence `N` is
  durable, every admitted sequence `< N` is also durable.
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
| I8 | A snapshot at WAL sequence `N` reflects exactly the applied prefix through `N`; snapshots remain a cache, never a substitute for a missing durable WAL event. |
| I9 | Fan-out happens only after the corresponding state transition reaches the boundary required by its durability class. |
| I10 | Production order ingress cannot be ready with `NullEventStore` or `FsyncOnFlush=false`. |

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
- derived `FeeAccruedEvent`, `RealizedPnlEvent` and ER-driven stale clearing,
  whose recovery pivot is the parent ER;
- venue-origin trading-halt changes only when that feed has an explicit
  snapshot/replay contract. Otherwise they are Class L.

Boundary:

```text
reserved admission → append → apply → venue progress may advance
                                  ↘ group fsync → durable venue cursor
```

Class V preserves async group commit, but only if the adapter can reconnect
from the last **durable** per-session cursor. A crash may lose the admitted or
appended-but-unfsynced tail locally; the venue must retransmit that tail.
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
- `SubAccountCreatedEvent` and `SubAccountDeactivatedEvent`;
- synthetic ERs and synthetic terminal transitions;
- local algo create/cancel/terminal state;
- `OrderStaledEvent` and operator-driven `OrderStaleClearedEvent`;
- `BotSessionInitializedEvent` and `BotSessionVerAdvancedEvent`;
- an `AuditLogEvent` used as a required audit-first precondition.

Boundary:

```text
admit → append → fsync durable prefix → apply → fan-out/ack
```

The implementation must sequence Class L work so no later apply overtakes the
durability fence. It may use an asynchronous commit sequencer rather than
holding the existing monitor during I/O, but the externally visible order must
remain the same.

If `fsync` fails after append, the operation is not acknowledged, its apply
does not run, the store becomes `Faulted`, and readiness drops. If a crash
occurs after `fsync` but before apply/ack, replay applies the event; a retry may
observe “already applied” and must be idempotent.

### 4.4 Class O — outbound venue intent

The event authorises an irreversible or ambiguous external side effect.

Examples:

- `OrderSubmittedEvent` before `SubmitAsync`;
- `OrderCancelRequestedEvent` before `CancelAsync`;
- `OrderReplaceRequestedEvent` before `CancelReplaceAsync`;
- algo repeg/slice intent that will cause one of those gateway calls.

Boundary:

```text
admit → append → fsync → apply intent/watermark → gateway send
                                                → client ack
                                                → venue ER resolves
```

The durable record must contain enough identity to reconcile without reusing
or inventing a ClOrdID. A crash:

- before `fsync` means the gateway was not called;
- after `fsync` but before send leaves a durable to-send intent;
- after send but before ER leaves an ambiguous sent intent;
- after ER is resolved by Class V processing.

#628 defines the persisted outbound substates, resend proof and
SessionVerId rules. Blindly deleting a `PendingNew` or blindly resending it is
not an acceptable implementation of this RFC.

### 4.5 Event classification summary

| Family | Class | Notes |
| --- | --- | --- |
| Real ER and sequenced venue reject | V | Reserved lane; durable venue cursor required. |
| ER-derived fee/P&L/projection events | V-derived | Parent ER is replay pivot; missing derived tail is deterministically rebuilt. |
| Synthetic reject/cancel/terminal ER | L | No venue can replay it. Memory-only fallback is forbidden. |
| Submit/cancel/replace intent | O | `fsync` before gateway call. |
| Kill/revive, operator halt/phase, cash, sub-account | L | `fsync` before apply and HTTP success. |
| Credential create/revoke/re-pin | L | Plaintext token or revocation result is not exposed before durability. |
| Bot session init/version | L | Version remains `fsync`-before-observation, matching the FIXP listener RFC. |
| Bot outbound sequence checkpoint | R | Best-effort watermark; overflow/version bump remains L. |
| Algo state | L unless purely explanatory | Any state that changes scheduling or restart action is L. |
| Audit | R or L by call site | Admin/security mutation audit-first is L; non-gating telemetry is R. |
| Snapshot/index/EOD materialisation | Derived cache | Failure alerts but does not alter WAL health while append/fsync remains healthy. |

## 5. State machines

### 5.1 Event lifecycle

```text
                         serialization/admission failure
                        ┌──────────────────────────────► Rejected
                        │
New ─► Admitted ─► Appended ─► Durable ─► Applied ─► Acknowledged
          │             │          │
          │             │          └─ Class O ─► Sent ─► Resolved by ER
          │             │
          │             └─ Class V ─► Applied/Acknowledged
          │                              │
          └─ crash/fault                 └─ later group fsync advances cursor
```

- Class R may stop after `Rejected` without a business effect.
- Class V may apply before durable, but only the durable venue cursor makes
  the replay contract safe.
- Classes L and O cannot reach `Applied` before `Durable`.
- No class transitions from `Rejected` to `Applied`.

### 5.2 Persistence health

```text
Starting ─► Recovering ─► Ready ─► Saturated ─► Ready
    │             │         │          │
    └─────────────┴─────────┴──────────┴────► Faulted
                                               │
                                               └─ restart after remediation only

Ready/Saturated ─► Draining ─► Stopped
```

- `Saturated` is transient queue pressure with a live writer.
- Return from `Saturated` requires the queue below a configured low watermark
  **and** a successful durability fence. A timer alone cannot restore health.
- `Faulted` records the first writer/flush exception, closes all admission
  lanes, fails outstanding fences, and is sticky.
- A snapshot failure does not enter `Faulted`; a WAL write or `fsync` failure
  does.

## 6. ACK, append and `fsync` rules

| Origin/action | Admission | Apply | Required durable boundary | External success boundary |
| --- | --- | --- | --- | --- |
| REST/FIXP-bot new, cancel, replace | Fail fast if local lane unavailable | After intent durability | Intent `fsync` before gateway | After durable intent and gateway acceptance; exact response semantics refined by #628 |
| Local kill/revive, halt/phase, cash, sub-account | Fail fast | After `fsync` | Per-operation durable fence | HTTP 2xx only after apply |
| Credential create | Fail fast | After `fsync` | Created event durable | Plaintext token shown only after apply |
| Credential revoke/re-pin | Fail fast | After `fsync` | Mutation durable | HTTP success and active-session termination only after apply |
| Bot session version bump | Fail fast | After `fsync` | Version event durable | New version may appear in FIXP response only afterward |
| Real venue ER | Reserved blocking admission | After admission/append | Async group `fsync`; durable venue cursor | Session progress only under §8 replay rules |
| Synthetic ER/watchdog transition | Fail fast | After `fsync` | Event durable | Fan-out/result only after apply |
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

On the first write, rotation, sidecar or `fsync` exception:

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
  "state": "ready|saturated|faulted|recovering|draining",
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
that `fsync`s the WAL prefix containing that message.

### 8.2 Live path

1. Read the next venue message in session order.
2. Attempt admission to the reserved lane.
3. If admitted, append and apply in WAL order.
4. Do not advance the durable cursor until the containing prefix is fsynced.
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
the adapter must use strict `fsync`-before-return for venue messages until the
SDK contract is extended. Continuing and relying on an undocumented replay
window is forbidden.

### 8.4 Restart and retransmission

On restart:

1. replay the durable local WAL;
2. restore the per-session durable venue cursor from a WAL-derived checkpoint
   or a sidecar committed with the WAL prefix, not from a snapshot alone;
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
| Crash after append, before `fsync` | May be lost | Venue replays | No apply/ack | No apply/send | Replay durable prefix |
| Crash after `fsync`, before apply | Replay if relevant | Replay local record; venue duplicate is no-op | Replay applies; client outcome may be unknown | Recover durable to-send intent | Idempotent retry/reconcile |
| Crash after L apply, before HTTP/FIXP response | n/a | n/a | Retry observes already-applied result | n/a | RPO 0; response outcome unknown |
| Crash after O `fsync`, before gateway send | n/a | n/a | n/a | Durable to-send; #628 determines safe send | Not ready until outbound recovery completes |
| Crash after gateway send, before ER | n/a | Eventual ER | n/a | Durable ambiguous sent intent | Reconcile by ClOrdID/session; never reuse ID |
| Writer throws while draining | Advisory loss allowed | Disconnect; replay uncommitted tail | Pending operation fails; no success | Pending send prohibited/fails | Sticky `Faulted`, `/ready=503` |
| `fsync` throws | Advisory records not durable | Disconnect; cursor does not advance | No apply/ack | No send | Sticky `Faulted` |
| Snapshot write fails | No effect on WAL | No effect on WAL | No effect on committed mutation | No effect on intent | Alert; readiness may remain 200 |
| Torn active WAL tail at boot | Ignore/truncate only framing-incomplete tail after last valid frame | Venue fills tail | L/O records beyond durable tail were never acknowledged/sent | Same | Recovery then replay; corrupt complete record fails boot |
| Complete record has bad CRC/schema | Do not skip known corruption | Do not advance cursor past it | Do not guess | Do not guess | Boot fails; `/ready` never opens; runbook repair |
| `FlushAsync` cancelled/client disconnects | No guarantee | Cursor unchanged until actual fsync | No success; event may later commit | No success; send only if fence later completes under owned workflow | Retry must be idempotent |
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
| C2 | Kill after frame write before `fsync` | Only durable prefix recovers; V tail retransmits | Application + real-stack |
| C3 | Kill after `fsync` before L apply | Replay applies exactly once | Application property test |
| C4 | Kill after L apply before response | Retry is idempotent; state remains applied | API test |
| C5 | Kill after O durability before send | Restart identifies to-send, not generic `PendingNew` | #628 tests |
| C6 | Kill after gateway send before ER | Restart treats send as ambiguous and never reuses ClOrdID | #628 + conformance |
| C7 | Saturate local lane | 503/reject; no state, fan-out or gateway call | Application/API tests |
| C8 | Saturate venue-reserved lane | No memory-only fill; session pauses/disconnects and replays | Infrastructure + conformance |
| C9 | Throw writer `IOException` | Store fault is sticky; future append/flush fail immediately | `FileEventStoreTests` |
| C10 | Throw `fsync` exception | L apply/O send do not occur; readiness is 503 | Infrastructure/API tests |
| C11 | Crash after kill-switch success | Kill remains active after restart | API + conformance |
| C12 | Crash after credential revocation success | Revoked credential cannot authenticate after restart | Listener integration |
| C13 | Crash after bot version response | Old version remains rejected after restart | Listener integration |
| C14 | Fill while host down | Reconnect replays fill and converges order/cash/position/P&L | Existing `TradingHostCrashRestartSpecTests` |
| C15 | Fill received, admitted but not durable, then SIGKILL | Durable cursor causes the same fill to retransmit and apply once | New conformance scenario |
| C16 | Corrupt complete WAL record | Startup fails closed; no readiness | Recovery test |
| C17 | Snapshot failure with healthy WAL | WAL continues; readiness stays truthful; alert increments | Snapshot test |
| C18 | Required exchange disconnected | `/live=200`, `/ready=503`, order ingress rejected | Existing/new health lifecycle tests |

Property tests must compare recovered state with the **durable prefix**, not
the merely admitted prefix. Existing `PropertyDurabilityTests` and
`UngracefulStop_NoFlush_RecoversToLastFlushedSeq_NoTornWriteFalsePositives`
are the baseline; they need class-aware cases for L/O and a durable venue
cursor model for V.

## 12. Migration and compatibility

### 12.1 WAL and snapshot compatibility

- Keep the current length/CRC/JSON frame.
- Add venue session/sequence fields as optional fields on existing events.
- New event kinds follow the existing additive discriminator rule.
- Old WAL events without venue cursor fields remain replayable but cannot
  prove a Class V durable replay cursor.
- The venue cursor is WAL-derived or committed as a sidecar in the same
  `fsync` boundary. A snapshot may cache it but cannot be its only source.
- Snapshot schema additions are optional/defaulted; deleting snapshots remains
  safe.

### 12.2 First upgrade

On the first boot of a deployment with legacy ER events:

1. replay the legacy WAL normally;
2. treat the durable venue cursor as unknown;
3. establish a fresh session recovery baseline and accept duplicates
   idempotently;
4. keep order ingress unready until the adapter proves it has reached the
   venue head;
5. write the first trusted cursor only after a successful WAL `fsync`.

If the venue cannot provide replay/snapshot from a known point, use strict
`fsync`-before-progress mode; do not manufacture a cursor from
`FileEventStore.CurrentSeq`.

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
   last-admitted/last-durable sequence, durable fences, health state and fault
   injection seams. Rename concepts in APIs where practical so “append” no
   longer means only “channel admitted”.
3. **Class L durable dispatch.** Add ordered `fsync`-before-apply/ack flow;
   migrate kill switch, local halt/phase, cash, sub-accounts, credentials,
   synthetic transitions and required audits.
4. **Venue reserved lane and cursor.** Carry venue seq/session identity,
   reserve capacity, pause/disconnect on pressure, commit cursor with WAL
   durability, and add replay conformance.
5. **Outbound durability — #628.** Define and implement approved/to-send/sent/
   acknowledged states, `fsync`-before-send`, idempotent reconciliation and
   session-roll behaviour.
6. **FIXP bot lifecycle — #627.** Apply Class L fences to credential/session
   generation and compose disconnect/replay rules with the listener.
7. **Operations and release gates — #629.** Alert on saturation/fault/fsync
   age, run the crash matrix, prove application-consistent backup/restore, and
   gate promotion on recovery/conformance.
8. **Documentation cutover.** Once slices 1–7 land, update
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

## 15. Acceptance checklist

- Every replay-significant event is classified R, V, L or O at dispatch.
- Append-before-mutate and `fsync` requirements are explicit for every class.
- Kill switch and credential revocation are `fsync`-before-success.
- Real ERs have reserved admission and a durable replay cursor; no
  memory-only fallback exists.
- Synthetic transitions are Class L.
- Writer faults are sticky and visible to append, flush, health and readiness.
- `/ready` represents durable order-ingress capability while `/live` remains
  separate.
- The failure matrix and crash matrix are executable through the linked
  implementation slices.

