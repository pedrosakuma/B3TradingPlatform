# RFC: Durable outbound mutations and crash recovery v0

| Field | Value |
| --- | --- |
| Status | Proposed |
| Tracking | [#628](https://github.com/pedrosakuma/B3TradingPlatform/issues/628) |
| Parent | [#620](https://github.com/pedrosakuma/B3TradingPlatform/issues/620) |
| Prerequisite | [durability classes RFC](durability-classes-fail-closed-v0.md) / [#621](https://github.com/pedrosakuma/B3TradingPlatform/issues/621) |
| Listener boundary | [#627](https://github.com/pedrosakuma/B3TradingPlatform/issues/627) owns bot-session reliability; this RFC owns business mutations |
| Active-active boundary | [#29](https://github.com/pedrosakuma/B3TradingPlatform/issues/29) |
| Upstream capability | [B3EntryPointClient#223](https://github.com/pedrosakuma/B3EntryPointClient/issues/223) |
| Implementation | [#637](https://github.com/pedrosakuma/B3TradingPlatform/issues/637)–[#648](https://github.com/pedrosakuma/B3TradingPlatform/issues/648) |
| Related | [`PERSISTENCE.md`](../PERSISTENCE.md), [`ENTRYPOINT_INTEGRATION.md`](../ENTRYPOINT_INTEGRATION.md), [`user-bot-fixp-listener-v0`](user-bot-fixp-listener-v0.md), [`risk-pipeline-ordering-v0`](risk-pipeline-ordering-v0.md), [`runbook-failover-recovery.md`](../operations/runbook-failover-recovery.md) |

## 1. Context

The platform currently records an `OrderSubmittedEvent`, applies a
`PendingNew` order, runs risk and margin, and then calls the exchange gateway.
`EventDispatcher.Dispatch` only admits the event to `FileEventStore`'s bounded
channel; it does not prove disk commit. A crash after the event is admitted or
written but before the gateway call can therefore restore an order whose send
history is unknowable. A crash after the gateway write but before an Execution
Report (ER) is even more dangerous: deleting the order can hide a live venue
order, while resending can create a duplicate.

Wave 1 improved cancel and replace:

- `OrderCancelRequestedEvent` and `OrderReplaceRequestedEvent` preserve
  ClOrdID links across restart;
- typed `ExchangeGatewayPreSendException` failures can be terminalised;
- ambiguous replace failures retain margin and durable reconciliation
  sidecars; and
- `ColdStartLifecycleGuard` drains on unresolved cancel/replace intent.

Those protections do not provide durable pre-call attempt evidence. Plain
pending state cannot distinguish crash-before-call from post-write ambiguity.
New orders remain less safe: `OrderSubmittedEvent` is recorded before risk,
every gateway exception becomes a synthetic rejection, and the Wave 1
cold-start guard covers cancel/replace but not ordinary `PendingNew`.
Algo child retries can therefore multiply an ambiguous live order.

The durability-classes RFC defines Class O as commit-before-send. This RFC
defines the missing business state machine, evidence model and recovery policy.
It does not implement the runtime state machine.

### 1.1 Verified current implementation

The following statements describe `main` at `74ea3c1`:

| Observation | Current evidence | Consequence |
| --- | --- | --- |
| WAL append is channel admission | `EventDispatcher.Dispatch` calls `IEventStore.Append` then applies under one lock; `FileEventStore.Append` uses `Channel.TryWrite`; the writer later calls `SegmentWriter.Flush`. | Existing order intent is not commit-before-send. |
| Snapshots can use admitted sequence | `EventDispatcher.WithSnapshotLock` passes `IEventStore.CurrentSeq`; `StateSnapshotter` captures state against it. | A snapshot may include state ahead of the committed WAL prefix until #621's remaining substrate lands. |
| Submit intent precedes approval | `OrderSubmittedEvent` is documented as post-validation, pre-risk; `OrderSubmissionService` dispatches it before `RiskPipeline.Evaluate` and margin reserve. | `OrderSubmittedEvent` cannot mean approved-to-send. |
| Submit exceptions are terminalised | `OrderSubmissionService` catches every gateway exception, releases margin, and publishes a synthetic rejected ER. | A post-write timeout may hide a live venue order. |
| Cancel/replace have only partial evidence | `OrderCancelService` and `OrderModifyService` distinguish typed pre-send failures from unclassified exceptions, but their requested events precede the gateway call without a durable attempt/session record. | A cold process cannot prove whether the call began. |
| Cold-start guard excludes new | `ColdStartLifecycleGuard` counts `PendingCancelRegistry` and `PendingReplacementRegistry` only. | A restored `PendingNew` is not fenced by the Wave 1 guard. |
| Algo cancel bypass exists | `AlgoEngine.CancelParentAsync` directly allocates/registers and calls `_gateway.CancelAsync`; other algo modify paths also own gateway orchestration. | Algo and manual mutations do not share one durable outbound coordinator. |
| ER evidence is dropped | Upstream events contain `SeqNum`; accepted/modified/cancelled events contain `OrderId`; the gateway knows `SessionId` configuration and `CurrentSessionVerId`. `ExecutionReportEnvelope` retains none of those fields. | Acknowledgment cannot currently prove firm/SessionId/SessionVerId/inbound sequence or venue order identity. |
| Business reject cannot resolve a mutation | `BusinessRejectReceivedEvent` stores `RefSeqNum`, but the platform does not retain the full session-scoped outbound sequence-to-attempt map. | Structural rejects remain audit-only. |
| Session roll policy is heuristic | `FirmSessionRollReconciliation` cancels `PendingNew`; the confirmed-roll reactor stales working orders. | A roll is being treated as evidence about venue state that is insufficient for Class O resolution. |

### 1.2 Verified SDK surface and wire limits

The platform pins `B3.EntryPoint.Client` 0.16.1. The package assembly and the
upstream `main` source at `1fe35a318ae3d546e5e75e7207ad9808028878fc`
(latest release 0.16.2) were inspected for this RFC.

The SDK does provide:

- inbound `EntryPointEvent.SeqNum` and `SendingTime`;
- venue `OrderId` on order ER models;
- `IRetransmitRequestHandler` events, including
  `NotAppliedReceived(FromSeqNo, Count)`;
- same-session Establish/reconnect and inbound retransmission;
- persisted `SessionVerId`, `LastOutboundSeqNum`, `LastInboundSeqNum` and
  outstanding-order summaries in `ISessionStateStore`; and
- serialized reserve/encode/write ordering on current `main`.

The SDK does not provide a public business-send result containing `SessionId`,
`SessionVerId`, reserved outbound `MsgSeqNum`, frame hash and typed completion
stage. `SubmitAsync`/`ReplaceAsync` return the ClOrdID and `CancelAsync` returns
`Task`; current `main` reserves the sequence inside a private helper, writes to
the stream, then persists its own outbound delta. The high-level API has no
exact-original-sequence replay operation and no callback at the
reserve-before-write boundary.

There is also no order-status or mass-status request in B3 EntryPoint 8.4.2.
Upstream issue
[#193](https://github.com/pedrosakuma/B3EntryPointClient/issues/193) verified
that the schema has no such template. This RFC therefore does not invent a
venue query. Same-session retransmission and operational evidence are the only
available recovery sources when no terminal ER exists.

## 2. Goals and non-goals

### 2.1 Goals

1. Give every new/cancel/replace crash point one deterministic restart action.
2. Never enter a gateway before approval and attempt intent are committed, and
   never permit a transport write before frame/session/sequence evidence commits.
3. Bound duplicate risk without pretending transport completion is venue
   acceptance.
4. Correlate ERs, BusinessReject `RefSeqNum`, `NotApplied` ranges and operator
   evidence to a durable mutation/attempt.
5. Compose with the ClOrdID watermark, WAL committed prefix, snapshots, risk
   reservations, algos, REST idempotency and external FIXP ClOrdIDs.
6. Keep the host live but unready whenever safe automated recovery is
   impossible.

### 2.2 Non-goals

- Implementing this state machine in the RFC PR.
- Active-active dispatch, shared-writer consensus or leader election; #29 owns
  that design.
- Changing `B3EntryPointClient` in this repository.
- Inventing a B3 order-status request absent from the schema.
- Treating a session roll, TCP close, timeout or SDK return as venue
  acceptance/rejection.
- Replacing #627's listener handshake, takeover, shutdown or session-sequence
  work.

## 3. Terminology

- **Mutation** — one logical new, cancel or replace request. It has a stable
  `MutationId`, origin and idempotency identity.
- **Attempt** — one bounded try to express a mutation using one never-reused
  ClOrdID. A proven-unsent retry is a new attempt with a new ClOrdID.
- **Recorded pending approval** — immutable business intent exists, but risk,
  margin or lifecycle approval has not authorised a send.
- **Approved to send** — risk/margin/lifecycle checks passed and the exact
  canonical wire-effective command is committed.
- **Attempt intent prepared** — the process epoch took durable ownership of one
  active attempt before entering the gateway. It contains no SDK-assigned
  session/sequence evidence.
- **Frame prepared / sequenced** — inside the gateway, the SDK reserved the
  outbound sequence and encoded the frame, then a required callback committed
  `SessionId`, `SessionVerId`, `MsgSeqNum` and frame hash before the SDK was
  permitted to perform the first transport write.
- **Transport write completed** — the gateway/SDK reports that its local stream
  write (and configured local flush) completed. This is deliberately not named
  `Sent` or `Accepted`.
- **Ambiguous** — local evidence cannot prove either that no wire attempt
  occurred or that the venue acknowledged it.
- **Venue acknowledged** — a committed inbound venue fact resolves the attempt.
- **Proven unsent** — typed evidence proves the frame was not handed to the
  transport and cannot later be written by that invocation.
- **Operator resolved** — an authorised operator records evidence and chooses
  the durable terminal interpretation.
- **Process epoch** — a UUID created after obtaining the single-active-host
  fence. Attempts owned by a dead/different epoch are not locally retryable
  until the ledger classifies intent-only state proven-unsent or frame-prepared
  state ambiguous.
- **Canonical wire-effective command** — versioned, deterministic fields after
  all defaults, inheritance and resolvers, split into non-sensitive plaintext
  plus an encrypted sensitive envelope before SBE encoding and outbound sequence
  assignment.

## 4. Decisions

1. **Use a separate `OutboundMutationLedger`.** Domain order status remains a
   projection; it is not evidence of gateway progress.
2. **Keep `OrderSubmittedEvent` as pre-risk recorded intent.** Add a common
   `OutboundApprovedEvent`; do not reinterpret legacy `order.submitted` rows as
   approval.
3. **Use two pre-write boundaries.** `OutboundAttemptIntentPreparedEvent` is
   committed before gateway entry. Inside the SDK, sequence reservation and
   encoding are followed by a callback that commits
   `OutboundFramePreparedEvent`; no transport write is permitted until that
   callback succeeds. This makes the SDK callback a write gate, not the first
   attempt record.
4. **Name local completion `TransportWriteCompleted`.** `Sent`, `Delivered` and
   `Accepted` overstate what `Stream.WriteAsync` proves.
5. **Use canonical payload persistence in v0, not exact SBE bytes.** The
   plaintext canonical command contains non-sensitive fields and stable
   encrypted-field references only. Persist a schema/version and integrity hash
   over the stored non-sensitive representation plus ciphertext bytes.
   Exact-frame persistence/replay is deferred until the SDK provides a safe
   reserve/write/replay contract.
6. **Sensitive values exist only in ciphertext at rest.** Account, investor
   identity/document and every customer-identifying wire value live exclusively
   in an authenticated encrypted sub-envelope with key id and algorithm
   version. They never appear in `canonicalCommandNonSensitive`, WAL/snapshot
   JSON, admin DTOs, logs or metrics, even as plaintext fallback fields.
7. **No automatic resend after ambiguity in v0.** This includes orphaned
   `FramePrepared`, `TransportWriteCompleted`, unknown legacy pending state,
   timeout/cancellation after the frame-prepared callback, process death and
   session roll.
8. **Proven-unsent is narrow.** It requires either a typed gateway result before
   frame preparation or recovery of a committed `AttemptIntentPrepared` with no
   committed `FramePrepared` under the mandatory SDK guarantee that callback
   failure/death cannot leak a transport write. Generic exceptions, socket
   errors, cancellation or process death after `FramePrepared` are ambiguous.
9. **`NotApplied` is correlation evidence, not automatic retry permission in
   v0.** It becomes actionable only when the exact `(firmId, SessionId,
   SessionVerId, outboundSeqNum)` maps to one attempt and the SDK/venue contract
   proves no automatic exact-sequence replay remains pending.
10. **ER acknowledgment is committed before ledger resolution.** It carries
    firm, SessionId, SessionVerId, inbound SeqNum, ClOrdID/OrigClOrdID, venue
    OrderId when present, sending time and retransmission flags.
11. **BusinessReject resolves only by exact sequence correlation.** Its
    `(firmId, SessionId, SessionVerId, RefSeqNum)` must identify one attempt.
    Otherwise it is retained as unmatched evidence and forces reconciliation;
    text matching is forbidden.
12. **One active attempt per mutation; finite attempts.** Default
    `MaxOutboundAttempts=2` (initial plus one explicitly requested
    proven-unsent retry). Every retry burns a new ClOrdID and preserves prior
    attempt evidence.
13. **REST uses durable idempotency keys.** `Idempotency-Key` becomes required
    for POST/PUT/DELETE order mutations after a compatibility rollout. Scope is
    `(firm, endClient, operation, key)`; the canonical request hash must match
    on reuse. Same key/same hash returns the existing mutation/result; same key/
    different hash is HTTP 409.
14. **Bot business identity and protocol replay identity are distinct.**
    Business duplicate identity is `(credentialId, externalClOrdId)` and remains
    unique throughout correlation retention. Protocol replay identity is
    `(credentialId, inboundSessionVerId, inboundSeqNum)` and is owned by #627.
    A new inbound sequence/session never makes reuse of an external ClOrdID a
    new business mutation; a replay key only retrieves the prior response.
15. **Ambiguous risk is held.** New-order margin and worst-case replace margin
    are not released by TTL. They release only on committed venue resolution,
    proven-unsent resolution, or authoritative-evidence operator resolution.
    Timeout raises alerts and keeps readiness closed.
16. **Session roll is not proof of absence.** It may change available
    retransmission evidence, but does not resolve a business mutation. Venue
    books or downstream effects can outlive the local session.
17. **Authoritative purge evidence is explicit.** Only a terminal ER, an exact
    correlated `NotApplied`/protocol proof under a documented SDK contract, a
    venue mass-action report covering the order and sequence boundary, or an
    operator-attested official venue/drop-copy/back-office extract may prove
    absence or release ambiguous risk. Manual comparison can annotate or keep a
    mutation ambiguous only. Reconnect kind, new SessionVerId and elapsed time
    do not prove absence.
18. **Single active sender is a prerequisite.** V0 requires one process epoch
    to own a firm's gateway and WAL. Startup must acquire an exclusive
    per-deployment/per-firm fence before recovery. Failure keeps the host
    unready. This is fencing, not active-active support.
19. **Startup is recovery-first.** Restore committed snapshot/WAL, rebuild
    ledgers/watermarks, classify orphaned attempts, establish venue evidence,
    then open gateways, algos, listener business intake and `/ready`, in that
    order.

## 5. Durable data model

### 5.1 Mutation

```text
OutboundMutation
  mutationId: Guid
  kind: New | Cancel | Replace
  firmId, endClientRef
  origin: Rest | UserBotFixp | Algo | Scheduler | Operator
  businessOriginIdentity:
    rest: operation + idempotencyKey + requestHash
    botBusiness: credentialId + externalClOrdId
    algo: parentAlgoId + child/slice/repeg sequence
  protocolReplayIdentity: nullable
    bot: credentialId + inboundSessionVerId + inboundSeqNum
  originalClOrdId: nullable (cancel/replace)
  recordedAtUtc
  approval: nullable OutboundApproval
  attempts: ordered non-empty list once approved
  resolution: nullable
```

### 5.2 Approval

```text
OutboundApproval
  approvalVersion
  approvedAtUtc
  riskDecisionId / policy version
  marginReservationId and amount/basis
  canonicalCommandVersion
  canonicalCommandNonSensitive
  sensitiveFieldRefs
  sensitiveCommandCiphertext: nullable iff no sensitive fields
  storedCommandIntegritySha256
```

The non-sensitive command contains immutable firm routing identity, security,
side/type, quantity/price, TIF/stop/expiry, MinQty/MaxFloor, STP instruction,
non-sensitive routing values and original/new ClOrdIDs.
`endClientRef` is a non-reversible internal surrogate, not an external customer
identifier. `sensitiveFieldRefs` contains stable opaque entry names only. CBLC
account, investor identity/document, external end-client identity and any other
customer-identifying value exist exclusively inside
`sensitiveCommandCiphertext`; no plaintext value or plaintext-derived lookup
key is duplicated beside it. Gateway mapping consumes the immutable
non-sensitive command plus decrypted envelope in memory and must not re-resolve
current configuration at send time.

### 5.3 Attempt

```text
OutboundAttempt
  attemptNo
  clOrdId
  processEpochId
  intentPreparedAtUtc
  framePrepared:
    sessionId
    sessionVerId
    outboundSeqNum
    encodedFrameSha256
    preparedAtUtc
  transportWriteCompletedAtUtc: nullable
  gatewayReceiptVersion: nullable
  provenUnsent: nullable Evidence
  ambiguityReason: nullable
```

ClOrdID remains the venue business correlation key. `MutationId` never goes on
the wire unless a future protocol field is explicitly standardised.

### 5.4 Inbound evidence

```text
VenueAcknowledgmentEvidence
  firmId
  sessionId
  sessionVerId
  inboundSeqNum
  sendingTime
  possibleResend
  messageKind
  clOrdId
  origClOrdId
  venueOrderId
  businessRejectRefSeqNum
  walSeq
```

Evidence is valid only after the containing Class V/L group is committed under
#621. A snapshot may cache it but cannot be its sole source.

## 6. State machine

```text
RecordedPendingApproval
    ├─ risk/margin/lifecycle reject ─────────► RejectedBeforeApproval
    └─ OutboundApproved committed ──────────► ApprovedToSend

ApprovedToSend
    └─ AttemptIntentPrepared committed ─────► AttemptIntentPrepared

AttemptIntentPrepared
    ├─ typed pre-frame failure ─────────────► ProvenUnsent
    ├─ no committed frame on dead epoch ────► ProvenUnsent
    └─ FramePrepared callback committed ────► FramePrepared

FramePrepared
    ├─ gateway local write receipt committed► TransportWriteCompleted
    ├─ committed ER/BReject evidence ───────► VenueAcknowledged
    └─ dead epoch / unknown result ─────────► Ambiguous

TransportWriteCompleted
    ├─ committed ER/BReject evidence ───────► VenueAcknowledged
    └─ restart/timeout without evidence ────► Ambiguous

Ambiguous
    ├─ late committed venue evidence ───────► VenueAcknowledged
    └─ authorised durable resolution ───────► OperatorResolved

ProvenUnsent
    ├─ explicit retry + attempts remain ────► ApprovedToSend
    └─ no retry / operator closes ──────────► OperatorResolved
```

`Ambiguous` is a deterministic recovered classification, not evidence that must
have been observed before the crash. A `FramePrepared` attempt owned by another/
dead epoch and not already terminal is ambiguous. A committed
`AttemptIntentPrepared` with no committed `FramePrepared` is proven not written
only because the required SDK callback forbids transport write before the
frame-prepared event commits.

### 6.1 Domain projection

| Ledger state | New order projection | Cancel projection | Replace projection |
| --- | --- | --- | --- |
| RecordedPendingApproval | `PendingApproval` (new status or side projection) | no venue lifecycle change | no venue lifecycle change |
| ApprovedToSend / AttemptIntentPrepared / FramePrepared | `PendingNew` | pending cancel link | pending replace link + margin |
| TransportWriteCompleted | still pending venue ER | still pending | still pending |
| Ambiguous | `ReconciliationRequired`, reservation held | original remains live/unknown; link retained | original/replacement both conservatively exposed; worst-case margin held |
| VenueAcknowledged | normal ER-derived status | ER-derived cancel/reject | ER-derived replace/reject |
| ProvenUnsent | never mark venue-rejected synthetically | original remains | original remains; replace delta released |

Synthetic rejects remain valid for pre-approval local decisions. They are
forbidden for gateway ambiguity.

## 7. Invariants

| ID | Invariant |
| --- | --- |
| O1 | No gateway method is entered before `OutboundApprovedEvent` and `OutboundAttemptIntentPreparedEvent` are marker-committed. |
| O2 | No transport write is possible before the SDK callback marker-commits `OutboundFramePreparedEvent`. |
| O3 | Absence of committed `AttemptIntentPrepared` proves the gateway was not entered; absence of committed `FramePrepared` after an intent proves no frame was written only under O2's SDK contract. |
| O4 | A non-terminal `FramePrepared` attempt owned by a dead/different process epoch is ambiguous. |
| O5 | Gateway return means only local transport write completion. |
| O6 | Generic exception, cancellation, timeout, disconnect or session roll after `FramePrepared` never emits a synthetic venue reject and never authorises resend. |
| O7 | At most one active attempt exists per mutation and one active mutation exists per business idempotency identity. |
| O8 | Every attempt has a unique ClOrdID allocated by `IClOrdIdGenerator`; retry never reuses any prior ID. |
| O9 | The ClOrdID watermark advances in the same committed prefix as every event that burns an ID. |
| O10 | The immutable non-sensitive command and encrypted sensitive envelope are committed before attempt-intent preparation. |
| O11 | Acknowledgment evidence is committed before the ledger/domain resolution it drives. |
| O12 | Late ER correlation survives terminal business mapping reap for the configured retention period; unresolved rows are never TTL-deleted. |
| O13 | Risk/margin capacity is not released while an attempt is ambiguous. |
| O14 | BusinessReject/NotApplied correlation uses exact `(firmId, SessionId, SessionVerId, outboundSeqNum)` identity. |
| O15 | A snapshot cannot contain a ledger/domain state beyond its covering committed WAL prefix. |
| O16 | Startup never opens order ingress, algo scheduling or listener business dispatch before outbound recovery reaches a safe decision. |
| O17 | A session roll alone never transitions a mutation to acknowledged, rejected, proven-unsent or purged. |
| O18 | WAL, snapshots, admin DTOs, logs and metrics never contain plaintext sensitive wire-effective values or idempotency keys. |

## 8. Events and snapshot schema

Additive WAL events:

- `OutboundApprovedEvent`;
- `OutboundAttemptIntentPreparedEvent`;
- `OutboundFramePreparedEvent`;
- `OutboundTransportWriteCompletedEvent`;
- `OutboundProvenUnsentEvent`;
- `OutboundOperatorResolvedEvent`; and
- acknowledgment evidence fields on `ExecutionReportReceivedEvent` and
  `BusinessRejectReceivedEvent`.

Existing `OrderSubmittedEvent`, `OrderCancelRequestedEvent` and
`OrderReplaceRequestedEvent` remain readable and continue to rebuild domain
intent. New events refer to `MutationId`, attempt number and ClOrdID.

`PlatformSnapshot` gains a versioned outbound-ledger section containing active
mutations, terminal correlation tombstones, idempotency records, attempt
evidence and encrypted payload envelopes. Publication follows the committed
snapshot-prefix rule in the durability-classes RFC. Snapshot deletion cannot
delete the WAL/evidence needed to explain unresolved mutations.

## 9. Exact crash and failure matrix

| ID | Crash/failure point | Durable evidence | Restart action |
| --- | --- | --- | --- |
| C1 | Before recorded intent admission | none | Request may be retried by origin identity; no ClOrdID assumed burned unless generator state says otherwise. |
| C2 | Intent admitted/written but not marker-committed | outside committed prefix | Ignore/truncate survivor under #621; gateway was not called. |
| C3 | Intent committed before risk | `RecordedPendingApproval` | Re-run deterministic approval only if all policy/config versions are available; otherwise reject/operate through explicit migration policy. Never send directly. |
| C4 | Risk/margin reject before rejection commit | recorded intent only | Re-evaluate deterministically or remain unready; do not guess prior response. |
| C5 | Approval appended but not committed | recorded intent only | Gateway was not called; recover as pending approval. |
| C6 | Approval committed, before attempt-intent preparation | `ApprovedToSend`, no attempt | Safe to commit an attempt intent and enter the gateway once after startup gates open. |
| C7 | Attempt-intent event appended but not committed | approved only | Gateway was not entered because O1 gates entry; survivor is ignored. |
| C8 | Attempt intent committed; crash before/during gateway with no committed frame-prepared callback | `AttemptIntentPrepared` only | Under O2's mandatory SDK contract, no transport write occurred. Reconcile any reserved SDK sequence state, commit `ProvenUnsent`, and permit only an explicit fresh-ClOrdID retry within the attempt cap. |
| C9 | Frame-prepared callback event appended but not committed | attempt intent only | Callback never succeeded, so O2 forbids transport write. Ignore the survivor, reconcile SDK sequence state and resolve proven-unsent. |
| C10 | Frame-prepared callback committed, crash before/around first transport write | full `(firmId, SessionId, SessionVerId, outboundSeqNum)` + frame hash | `Ambiguous`; the SDK may have written immediately after callback return. No auto-resend. |
| C11 | Gateway returns typed failure before frame preparation | typed no-frame evidence | Commit `ProvenUnsent`; may explicitly retry with fresh ClOrdID within attempt cap. If proof cannot commit, drain. |
| C12 | Stream write throws/timeout/cancels after frame preparation | frame-prepared evidence | Ambiguous, regardless of bytes reportedly written. |
| C13 | Stream write completes, crash before write-completed event | frame-prepared evidence; venue may have frame | Ambiguous; late ER may resolve. |
| C14 | Write-completed event appended but not committed | frame-prepared evidence | Ambiguous; uncommitted survivor ignored. |
| C15 | Write-completed committed, before ER | transport completion | Ambiguous until venue evidence. |
| C16 | ER received but not admitted | outbound unresolved | Disconnect/pause under Class V; venue retransmits from durable cursor. |
| C17 | ER admitted/applied but not committed | outbound unresolved in committed prefix | Discard/replay tail under #621; venue retransmission must redeliver. |
| C18 | ER committed before ledger/domain apply | committed acknowledgment evidence | Replay resolves exactly once. |
| C19 | BusinessReject with matched full session key + `RefSeqNum` | committed exact sequence evidence | Resolve according to reject semantics; never by free-form text. |
| C20 | BusinessReject without sequence map | unmatched venue evidence | Remain ambiguous and unready; operator correlation required. |
| C21 | `NotApplied` covers exact `(firmId, SessionId, SessionVerId, outboundSeqNum)` | negative venue evidence | V0 records evidence but does not retry automatically; operator/SDK-contract resolution required. |
| C22 | Process dies with multiple ledger rows | committed ordered prefix | Classify each independently; one ambiguous mutation blocks only its affected firm/order scope, while global readiness policy remains fail-closed for required firms. |
| C23 | WAL/marker fault during any required transition | prior committed state | Sticky fault; no later gateway call, resolution or capacity release. |
| C24 | Snapshot ahead of committed marker | invalid snapshot | Reject snapshot; recover older committed baseline/full WAL. |
| C25 | Exclusive host fence unavailable | another possible sender | Do not connect gateways or become ready. |

## 10. Restart decision table

| Recovered state | May send? | May release risk? | Readiness |
| --- | --- | --- | --- |
| RecordedPendingApproval | only after deterministic approval commits | according to approval/reject outcome | closed until processed |
| ApprovedToSend, no attempt | yes, once, after attempt-intent preparation commits | no | closed until handed to normal live pipeline |
| AttemptIntentPrepared, no FramePrepared | no direct resend; resolve proven-unsent under O2, then explicit fresh-ID retry only | no until resolution | closed until proven-unsent is committed |
| FramePrepared, current epoch still live | coordinator-owned only; never a second caller | no | normal only while ownership is provable |
| FramePrepared from dead epoch | no | no | reconciliation required |
| TransportWriteCompleted without venue ack | no | no | reconciliation required |
| ProvenUnsent | only an explicit fresh-ID retry within cap | release attempt-specific reservation or retain logical request reservation per policy | may open once resolved/retried |
| VenueAcknowledged | no resend | apply ER-defined release/commit | may open |
| OperatorResolved | no resend unless resolution explicitly creates a new mutation | per signed resolution | may open when all affected rows resolved |
| LegacyUnknown | no | no | reconciliation required |

## 11. Session continuation, retransmission and roll

### 11.1 Same SessionId and SessionVerId

On reattach to the same session:

1. recover the committed outbound ledger and durable inbound cursor;
2. subscribe to SDK retransmission/NotApplied evidence before opening ingress;
3. request/permit inbound retransmission from the durable venue cursor;
4. correlate late ERs by firm + ClOrdID/OrigClOrdID and committed
   `(SessionId, SessionVerId, inboundSeqNum)` evidence; and
5. keep ambiguous attempts blocked.

An exact original-frame resend is allowed only in a future version when all are
true:

- the SDK exposes the original `SessionId`, `SessionVerId` and outbound
  sequence;
- exact encoded bytes/hash are durably bound to the attempt;
- the venue explicitly requests or permits that exact sequence;
- SDK sequencing prevents any new frame from taking the same sequence; and
- crash tests prove the replay is the original attempt, not a new mutation.

### 11.2 Rolled SessionVerId

A rolled session removes the possibility of same-session exact replay but does
not prove that the business effect is absent. Startup:

- preserves every ClOrdID and late-ER correlation row;
- resolves intent-only/no-frame attempts proven-unsent under O2 and classifies
  unresolved frame-prepared/write-completed/legacy attempts ambiguous;
- retains new/replace risk capacity;
- permits inbound recovery and operator evidence collection; and
- remains unready for affected required firms.

The current automatic `PendingNew` cancellation/session-roll staling policy must
be revised during implementation. It may remain an operator-facing stale
projection, but cannot terminally resolve the outbound ledger or release risk
without authoritative evidence.

### 11.3 Venue purge evidence

Accepted evidence, strongest first:

1. terminal ER for the attempt/business order;
2. exact correlated `NotApplied` plus a documented no-later-replay contract;
3. a venue mass-action report that identifies the affected scope and a sequence
   boundary covering the attempt;
4. an official venue/drop-copy/back-office extract imported and attested by an
   authorised operator.

Session timeout, `ReconnectKind.Renegotiated`, a new SessionVerId, local SDK
outstanding-order state and absence from recent traffic are not purge evidence.

## 12. New, cancel and replace policy

### 12.1 New

- `OrderSubmittedEvent` records the request and burns the ClOrdID watermark.
- Risk/margin runs before `OutboundApprovedEvent`.
- Gateway exceptions after frame preparation never synthesize rejection.
- `PendingNew` with no ledger evidence migrates to `LegacyUnknown`.
- An ambiguous new holds its full reservation and prevents algo re-slicing the
  same logical child.

### 12.2 Cancel

- Existing pending-cancel ownership and bot mappings remain evidence caches.
- Approval captures immutable original order identity/security/side and cancel
  ClOrdID.
- Proven-unsent removes the active link but retains the burned-ID tombstone.
- Ambiguity keeps the link so a late cancel/reject ER routes correctly.
- The original order remains conservatively live until venue/operator
  resolution.

### 12.3 Replace

- Approval persists the complete effective replacement, not sparse overrides.
- Ambiguity retains both the original-order risk and the replacement delta
  required by the worst possible venue outcome.
- The current ambiguous-margin TTL may alert/escalate but must not release
  capacity.
- Late Replaced/Rejected ERs resolve through the attempt ledger and then commit/
  abort replace margin exactly once.

## 13. REST and FIXP origin semantics

### 13.1 REST

Order mutation responses include `mutationId`, `clOrdId`, `state` and a lookup
URL. A client timeout means outcome unknown; the client repeats the identical
request with the same `Idempotency-Key` or queries the mutation.

Compatibility rollout:

1. accept absent keys but emit a warning/metric and response header;
2. require keys for new orders;
3. require keys for replace/cancel; and
4. reject mismatched reuse with 409.

Keys are not logged. Store a keyed digest or encrypted value, not plaintext.

### 13.2 External user-bot FIXP

Business duplicate identity is `(credentialId, externalClOrdId)`. The durable
tombstone outlives the live routing map. Protocol replay identity is separately
`(credentialId, inboundSessionVerId, inboundSeqNum)` and is owned by #627. A
second business message using the same external ClOrdID is rejected as duplicate
even when it arrives under a new session version or inbound sequence. A
protocol replay key may retrieve/reproduce only the prior result; it never
authorises a new business mutation or bypasses the external-ClOrdID tombstone.

Listener-facing implementation waits for #627 so startup/takeover and response
replay semantics are composed rather than duplicated.

## 14. Startup recovery and readiness sequencing

Normative boot order:

1. acquire the exclusive active-host/process-epoch fence;
2. recover the #621 commit marker and reject survivor WAL/snapshots;
3. restore snapshot and replay the committed WAL;
4. advance every ClOrdID watermark, including legacy/tombstoned attempts;
5. rebuild risk/margin reservations and outbound ledger;
6. classify legacy and dead-epoch attempts;
7. recover reconciliation sidecars and operator resolutions;
8. connect/reattach venue sessions with business ingress disabled;
9. process required inbound retransmission/NotApplied evidence;
10. run the outbound recovery coordinator;
11. start algo schedulers and listener business dispatch only after their
    dependent mutations are safe; and
12. open REST/FIXP order ingress and `/ready`.

`/live` remains diagnostic. `/ready` is 503 while any required firm is
recovering, has ambiguous/legacy mutations, lacks the single-host fence, lacks
the committed-prefix substrate, or cannot establish required venue evidence.

## 15. Migration and backward compatibility

### 15.1 Legacy classification

On first upgraded boot:

- `PendingNew` derived from `OrderSubmittedEvent` without a matching
  `OutboundApprovedEvent`/attempt history becomes `LegacyUnknown`;
- unresolved `OrderCancelRequestedEvent` becomes `LegacyUnknownCancel`;
- unresolved plain `OrderReplaceRequestedEvent` becomes
  `LegacyUnknownReplace`;
- Wave 1 proven-unsent and ambiguous sidecars are retained and imported as
  evidence, not discarded; and
- every legacy ClOrdID advances the watermark and gets a correlation tombstone.

Legacy unknown never auto-sends or auto-rejects. A controlled upgrade may
establish a baseline only after draining, committing the old WAL, capturing an
official venue comparison and writing a durable operator baseline.

### 15.2 Snapshot/WAL compatibility

- New events use additive discriminators and optional fields on old event
  families.
- Snapshot outbound-ledger version defaults to absent for legacy snapshots.
- A legacy snapshot is accepted only if #621 proves its committed covering
  prefix; acceptance does not convert pending state into send proof.
- Unknown future ledger state fails recovery closed rather than being skipped.

### 15.3 API compatibility

Existing order response fields remain additive during rollout. Requiring
idempotency keys is a versioned operational cutover announced by metrics and
documentation. FIXP duplicate rejection tightens retention but preserves the
existing `DuplicateClOrdId` response family.

## 16. Observability and operator workflow

### 16.1 Metrics

At minimum:

- `trading.outbound.mutations` by kind/state/firm/origin;
- `trading.outbound.attempts_total` by kind/result/stage;
- `trading.outbound.ambiguous` gauge by firm/kind/age bucket;
- `trading.outbound.proven_unsent_total` by evidence type;
- `trading.outbound.ack_latency` from write-completed to committed venue ack;
- `trading.outbound.unmatched_business_reject_total`;
- `trading.outbound.unmatched_er_total`;
- `trading.outbound.idempotency_conflict_total`;
- `trading.outbound.legacy_unknown` gauge; and
- `trading.outbound.oldest_ambiguous_age_seconds`.

Never tag ClOrdID, mutation ID, idempotency key, account, investor identity,
symbol+owner pairs or payload hashes as unbounded metric dimensions.

### 16.2 Operator API

Admin-only endpoints list redacted mutations and evidence, retrieve a single
timeline, attach evidence, and resolve:

```text
POST /api/admin/outbound-mutations/{mutationId}/resolve
{
  "decision": "venue_acknowledged|venue_absent|leave_ambiguous",
  "evidenceType": "terminal_er|contracted_not_applied|venue_mass_action|official_extract|manual_annotation",
  "evidenceReference": "...",
  "reason": "..."
}
```

Resolution requires MFA-capable admin auth, firm scope, maker/checker for
capacity-releasing ambiguous new/replace where available, required audit-first
durability, and an immutable operator/timestamp/evidence digest. The API never
accepts “session rolled” as evidence type. `venue_absent` and any release of
ambiguous new/replace capacity require `terminal_er`,
`contracted_not_applied`, `venue_mass_action` or `official_extract`.
`manual_annotation` is valid only with `leave_ambiguous`; the server rejects it
for `venue_absent`, `venue_acknowledged` or any risk release.

### 16.3 Runbook

For ambiguity:

1. stop affected ingress and algo scheduling;
2. preserve WAL, snapshot, SDK session state and logs;
3. obtain venue evidence by ClOrdID/session/sequence;
4. compare order, execution, position, cash and margin effects;
5. attach manual comparisons as annotations while remaining ambiguous, or
   record a resolution only when authoritative evidence is available;
6. verify any evidence-authorised risk release/commit and late-ER behavior; and
7. reopen readiness only when no unresolved required-firm mutations remain.

## 17. Security and privacy

- Canonical plaintext commands are least-privilege business records containing
  non-sensitive fields plus stable encrypted-field references only. Sensitive
  values are encrypted at application level before WAL serialization with
  rotation-capable key ids; filesystem encryption alone is insufficient.
- Account, investor identity/document and customer-identifying values exist
  exclusively inside `sensitiveCommandCiphertext`. WAL, snapshots and admin
  serialization tests must scan emitted JSON/bytes and prove those plaintext
  values are absent; log-capture/metrics tests must prove the same for errors.
- Idempotency keys are high-entropy bearer-adjacent values. Persist only a
  keyed digest/encrypted form and compare in constant-time where practical.
- Investor/account values, raw frame bytes and official extracts never appear
  in logs, metrics, health or ordinary history APIs.
- Operator evidence references are allow-listed identifiers or encrypted
  attachments, not arbitrary filesystem paths/URLs.
- Access to decrypted payload/evidence is audited and firm-scoped.
- Correlation retention is 30 calendar days by default and configurable only
  upward for unresolved/regulatory needs. Unresolved mutations and their
  watermarks are retained indefinitely until resolution. Purge is auditable and
  cannot reduce the ClOrdID watermark.

## 18. Rejected alternatives

### 18.1 Treat `OrderSubmittedEvent` as approval

Rejected. It is intentionally pre-risk and legacy replay depends on that
meaning. Reinterpreting it would authorise rejected historical requests.

### 18.2 Mark every gateway exception rejected

Rejected. The SDK may have written the frame. A synthetic reject can release
risk and trigger an algo retry while a venue order is live.

### 18.3 Blindly resend every pending mutation

Rejected. ClOrdID uniqueness does not make a second new/cancel/replace harmless,
and current SDK calls cannot reproduce the original outbound sequence.

### 18.4 Assume gateway return means venue acceptance

Rejected. It proves local write/flush completion only. Venue acceptance is an
inbound business fact.

### 18.5 Clear pending state on SessionVerId roll

Rejected. A roll changes protocol recovery options, not the business book's
ground truth.

### 18.6 Release ambiguous replace margin on TTL

Rejected. Time is not evidence. The venue may have accepted the replacement.

### 18.7 Persist only exact encoded frames

Rejected for v0. Current SDK privately owns sequence allocation/encoding and has
no safe exact replay API. Canonical commands are versionable and auditable;
exact frames may be added once the upstream contract exists.

### 18.8 Add an order-status query

Rejected. B3 EntryPoint 8.4.2 has no such wire template, as upstream #193
documents.

### 18.9 Let each service/algo own recovery

Rejected. Multiple orchestrators recreate the current semantic drift. All
business outbound mutations must use one ledger/coordinator.

## 19. Staged rollout and executable implementation slices

Merge order is normative:

1. **[#637](https://github.com/pedrosakuma/B3TradingPlatform/issues/637) —
   committed-prefix prerequisite.** Finish #621's marker-committed WAL fence,
   survivor-tail recovery, `FlushThroughAsync` and Class O commit-before-send
   primitive.
2. **[#638](https://github.com/pedrosakuma/B3TradingPlatform/issues/638) —
   snapshot prerequisite.** Publish snapshots only for committed applied
   prefixes and add lineage/version validation.
3. **[B3EntryPointClient#223](https://github.com/pedrosakuma/B3EntryPointClient/issues/223)
   — SDK capability.** Obtain a typed outbound receipt/pre-write boundary; do
   not implement same-sequence replay without its contract.
4. **[#639](https://github.com/pedrosakuma/B3TradingPlatform/issues/639) —
   ledger and migration.** Add events, versioned snapshot state, process
   epochs, legacy classification, watermarks and retention.
5. **[#640](https://github.com/pedrosakuma/B3TradingPlatform/issues/640) and
   [#641](https://github.com/pedrosakuma/B3TradingPlatform/issues/641) —
   evidence-aware inbound/gateway.** Persist immutable effective commands;
   carry SessionId, SessionVerId, outbound/inbound sequences, OrderId,
   BusinessReject and NotApplied evidence; gate transport write on committed
   frame preparation.
6. **[#642](https://github.com/pedrosakuma/B3TradingPlatform/issues/642) —
   new-order pipeline + REST idempotency.** Separate recorded intent from
   approval, remove synthetic gateway rejects, expose mutation lookup.
7. **[#643](https://github.com/pedrosakuma/B3TradingPlatform/issues/643) —
   cancel/replace consolidation.** Route manual/scheduler paths through the
   coordinator and remove TTL capacity release.
8. **[#644](https://github.com/pedrosakuma/B3TradingPlatform/issues/644) —
   cold-start coordinator.** Enforce boot ordering, session policy,
   single-host fencing and readiness.
9. **[#645](https://github.com/pedrosakuma/B3TradingPlatform/issues/645) —
   algo normalisation.** Route child submit/cancel/replace through the same
   coordinator and block duplicate slicing under ambiguity.
10. **[#646](https://github.com/pedrosakuma/B3TradingPlatform/issues/646) —
    FIXP business identity.** After #627, add durable external-ClOrdID
    tombstones and response replay composition.
11. **[#647](https://github.com/pedrosakuma/B3TradingPlatform/issues/647) —
    operator/observability.** Add redacted admin timelines, resolution, metrics,
    alerts and runbook.
12. **[#648](https://github.com/pedrosakuma/B3TradingPlatform/issues/648) —
    crash/conformance gates.** Fault-inject every §9 boundary and add
    same-session retransmission, session-roll and late-ER real-stack scenarios.

Each slice must specify owned file/test surfaces and may not weaken v0's
no-auto-resend policy while the SDK evidence gap remains.

## 20. Acceptance checklist

- [ ] Every §9 crash point has a deterministic automated test.
- [ ] Gateway entry is impossible before committed approval + attempt intent.
- [ ] SDK transport write is impossible before committed frame preparation.
- [ ] No generic gateway failure creates a synthetic venue rejection.
- [ ] Orphaned frame-prepared attempts recover ambiguous and do not resend;
      intent-only/no-frame attempts resolve proven-unsent under the SDK contract.
- [ ] Proven-unsent evidence is typed and tested before frame preparation and
      for dead-epoch intent-only recovery under the SDK callback contract.
- [ ] ER/BusinessReject/NotApplied correlation includes firm, SessionId,
      SessionVerId and sequence and commits first.
- [ ] Session roll alone never releases risk or resolves a mutation.
- [ ] Legacy pending new/cancel/replace drains fail-closed.
- [ ] ClOrdID watermarks and late correlation survive snapshots/restart/purge.
- [ ] REST and FIXP duplicate identities are durable.
- [ ] Ambiguous new/replace capacity remains held until evidence-based resolution.
- [ ] Startup ordering prevents REST, algo and listener ingress opening early.
- [ ] Sensitive wire-effective fields are encrypted/redacted.
- [ ] WAL/snapshot/admin/log tests prove sensitive values never appear in
      plaintext.
- [ ] Manual comparison cannot prove venue absence or release ambiguous risk.
- [ ] Upstream gaps are tracked without inventing unsupported wire operations.
- [ ] Real-stack conformance proves same-session recovery, rolled-session drain
      and late-ER convergence.
