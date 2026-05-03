# RFC: Algo orders v0

| Field    | Value                                                              |
| -------- | ------------------------------------------------------------------ |
| Status   | Draft                                                              |
| Tracking | [#48](https://github.com/pedrosakuma/B3TradingPlatform/issues/48)  |
| Replaces | n/a (new capability on top of `B3.Trading.Application`)            |

## 1. Context

`B3.Trading.Host` ships an order-by-order surface: `POST /orders`
produces exactly one venue order, the `RiskPipeline` runs once,
`WorkingOrderBook` tracks one row, and `ExecutionReportProcessor`
mutates that row from ERs returned by `IExchangeGateway`. Every
non-trivial trader workflow that needs **time** or **shape** —
slicing across a window, hiding size on the book, scheduling around
a benchmark — has to be done client-side or in operator scripts. That
worked for the bootstrap and for the v1 trader UI, but it ceiling-out
on three real cases:

- A trader hand-managing an iceberg by clicking the ticket every time
  the prior slice fills. The mechanics are already proven by users;
  the platform is just absent.
- An algo team wanting to test a TWAP against the conformance stack
  without standing up their own scheduler around HTTP calls into
  `/orders`.
- Post-trade analysis that needs a stable parent identity to group
  related child orders. `clOrdId` is venue-bound and per-child; there
  is no aggregation key today.

This RFC proposes the smallest set of changes that adds an **algo
order layer above the existing order layer**, without rewriting the
order layer or the gateway abstraction. Children remain ordinary
`Order` instances, run through the same risk pipeline, persist via
the same `FileEventStore`, and surface on the same WS topics. What is
new is a parent abstraction (`Algo`) that decomposes into children
deterministically and reacts to their ERs to decide the next action.

## 2. Goals

1. Support two algo types end-to-end in v0: **Iceberg** (reactive,
   no clock) and **TWAP** (scheduled across a window). Pick the two
   that exercise the engine without dragging in a historical-volume
   feed or smart-order-routing logic.
2. Reuse the existing pipeline: every child goes through `RiskPipeline`
   and `IExchangeGateway` exactly as a hand-typed order does. The algo
   layer is composition, not a side-channel.
3. Persist parent state so a host restart does not lose algos in
   flight. Reuse `FileEventStore`; do not introduce a second store.
4. Make the parent–child link **operationally first-class**: an
   operator reading the venue ER stream must be able to map every
   child back to its parent without touching internal logs.
5. Surface algo lifecycle on its own WS topic so the trader UI (later
   PR) can render parents alongside children without re-deriving state
   from `/orders`.

## 3. Non-goals

- **VWAP, POV, Implementation Shortfall, smart-order routing.** All
  three need either historical volume curves per symbol or
  cross-venue routing — neither exists today. Folded into a future
  "algo orders v1" RFC once the inputs land.
- **Editing an algo in flight** (modify quantity, extend window,
  change params). v0 supports `create` and `cancel`; modify becomes
  cancel + recreate.
- **Sidecar engine.** The engine ships in-process. Sidecar is
  re-evaluated only if a profile shows the order hot path competing
  with algo work in a way that matters.
- **Algo pre-trade controls.** Parent-level caps (max parent notional,
  participation rate, child throttle independent of the per-end-client
  rate limit) are out of scope for v0. v0 ships only the
  **engine-level safety invariants** in §4.7 — enough to prevent a
  bad parent from amplifying small bugs into large losses, but not a
  full risk surface. Promoted to "algo risk v1" when there is a
  concrete parent that needs them.
- **A trader-UI surface for algos.** Backend + WS first. The frontend
  picks it up in a follow-up PR after the engine is proven against
  the conformance stack.
- **Rich pricing strategies for TWAP** (peg-to-mid, peg-to-VWAP,
  passive→aggressive ramp). v0 takes an explicit per-child price (or
  market) — one less moving part in the first iteration. Pricing
  strategies become a v1 concern when there is a real comparison to
  measure against.
- **Algo-on-algo composition** (a parent that creates child algos
  instead of child orders). Possible later; explicitly forbidden by
  the v0 schema.

## 4. Detailed design

### 4.1 Domain model

A new aggregate, `Algo`, lives next to `Order` in
`B3.Trading.Domain`:

```csharp
public sealed class Algo
{
    public ulong AlgoId { get; }                // monotonic, host-scoped
    public EndClientId Owner { get; }
    public string FirmId { get; }
    public string Symbol { get; }
    public ulong SecurityId { get; }
    public OrderSide Side { get; }
    public AlgoType Type { get; }               // Iceberg | Twap
    public long TotalQuantity { get; }
    public long FilledQuantity { get; private set; }
    public AlgoStatus Status { get; private set; }
    public AlgoTerminalReason? TerminalReason { get; private set; }
    public AlgoParameters Parameters { get; }   // sealed-class-per-type
    public IReadOnlyList<AlgoSlice> Slices { get; }  // history of slices
}
```

`AlgoId` is **its own monotonic identity**, not a `clOrdId`.
`clOrdId` belongs to venue-bound orders and we deliberately keep
that scope narrow — overloading it would force the EntryPoint
gateway and the conformance suite to special-case parent IDs that
will never reach the venue.

`AlgoStatus` enumerates the lifecycle (see §4.4):

```
PendingNew  → Working  → Cancelling → Cancelled
                       → Suspended  (operator-action-required)
                       → Expired    (TWAP only)
                       → Completed
```

`AlgoTerminalReason` is the durable companion to terminal/suspended
states (`UserCancelled`, `RiskRejected`, `GatewayUnavailable`,
`TwapWindowExpired`, `RetriesExhausted`, `Drained`). Persisting the
reason matters because the same `Cancelled` outcome means very
different things to a UI and an operator depending on whether the
parent had already partially filled.

### 4.2 Child–parent linkage (atomic invariant)

**Invariant:** a child `Order` must be discoverably linked to its
parent `Algo` *before any ExecutionReport for that child can be
processed*. Anything weaker risks an ER landing while the link is
still in flight, mutating the child without advancing the parent
state machine — a class of bug that snapshots and replays are very
bad at fixing after the fact.

Concretely:

- `Order` gains nullable `ParentAlgoId` and `AlgoSliceSeq` fields.
- `OrderSubmittedEvent` carries the same two fields.
- `OrderSnapshot` (used by `StateSnapshotter`) carries them too.
- `OrderDto` and `ExecutionDto` (the WS / HTTP wire DTOs) expose
  `parentAlgoId` and `algoSliceSeq` as nullable fields. Children
  that are not algo-linked simply emit `null` — backward compatible
  with the existing `orders` consumers.
- The algo engine submits a child by calling the **same internal
  submit pipeline** that `POST /orders` uses, with `ParentAlgoId`
  and `AlgoSliceSeq` set. The link is therefore baked into the
  first persisted event for the child; there is no separate "link"
  event to race against the gateway.

There is **no** `AlgoChildLinkedEvent`. The link is a property of
the order, not a fact about the algo.

There **is** a small set of algo-level events (§4.5) but they
record *parent* lifecycle facts (created, cancel-requested,
suspended, terminal-state-recorded), not child-derived facts. Slice
fill / completion are *derived* from child ERs during replay,
exactly as `Order.FilledQuantity` is derived today. Persisting both
would create a double-source-of-truth that the WAL+snapshot model
is poorly equipped to reconcile.

### 4.3 Engine threading boundary

The algo engine is one `IHostedService` (`AlgoEngine`) registered
alongside the existing services. It owns:

- A bounded `Channel<AlgoSignal>` of "something happened that may
  cause a parent to act" notifications.
- A long-running consumer task (single, in v0) that drains the
  channel and processes signals serially per parent.

`AlgoSignal` instances are enqueued from two sources:

- `AlgoCreated` enqueued when `POST /algo` accepts a new parent
  (after the create event has been persisted).
- `ChildExecutionObserved` enqueued from the existing
  `ExecutionReportProcessor` *after* it returns from the dispatcher
  call. The enqueue is non-blocking and never holds the dispatcher
  lock; the engine is the one that decides what to do.

This is the single most important architectural choice in v0. Doing
it the other way — engine submitting the next child synchronously
from inside the ER processing path — would put scheduling, risk
evaluation, WAL append, and (potentially) gateway I/O on the path
that holds the dispatcher lock. The order hot path and ER processing
path would compete with algo refills, with no upper bound on the
work the algo engine can do per signal. The `Channel` boundary
makes that impossible by construction.

The engine itself never holds the dispatcher lock when it submits
children: it goes through the same `OrderSubmissionService` (a small
extraction of the body of `POST /orders`) that the HTTP endpoint
uses, which acquires the dispatcher lock only for the duration of
its own append.

Per-parent serialization is enforced by an in-memory map
`AlgoId → SemaphoreSlim(1)`. Different parents can be processed in
parallel by the consumer task only if a future PR widens the
consumer pool; v0 keeps a single consumer for simplicity.

### 4.4 Parent state machine

```
                            ┌──────────────┐
                            │  PendingNew  │   transient; cleared after
                            └──────┬───────┘   AlgoCreatedEvent persisted
                                   ▼
                            ┌──────────────┐
                ┌──────────►│   Working    │◄────────────┐
                │           └──────┬───────┘             │
                │                  │  child-fill         │
                │   user-cancel    │  (still remaining)  │
                │                  ▼                     │
                │           ┌──────────────┐             │
                │           │  Cancelling  │             │
                │           └──────┬───────┘             │
                │                  ▼                     │
                │           ┌──────────────┐             │
                │           │  Cancelled   │ (terminal)  │
                │           └──────────────┘             │
                │                                        │
                ├── all-filled ──► Completed (terminal) ─┤
                │                                        │
                ├── window-passed ─► Expired (terminal) ─┤   (TWAP only)
                │                                        │
                └── repeated-reject / gateway-down ──► Suspended
                                                      (operator-action-required;
                                                       no auto-retry)
```

Transition rules that v0 must lock down because they bite hardest:

1. **Child risk-rejected after prior fills.** Parent stays
   `Working` only if the rejection is transient and the engine has
   retry budget; otherwise transitions to `Suspended` with reason
   `RiskRejected`. **Default policy in v0: suspend on first reject
   for both Iceberg and TWAP** — a "skip this slice and continue"
   policy hides risk pressure from the operator. Resume becomes a
   v1 concern.
2. **Gateway unavailable on child submit.** Engine retries with
   bounded backoff (3 attempts, 100/300/900ms) and then transitions
   to `Suspended` with reason `GatewayUnavailable`. Already-filled
   quantity is preserved.
3. **Venue cancels the only live child of an Iceberg.** Engine does
   **not** auto-refill; it transitions to `Suspended` with reason
   `VenueCancelled`. v0 deliberately treats unsolicited venue
   cancels as operator-action-required because the cancel can carry
   information (compliance, last look, etc.) the engine cannot
   interpret.
4. **User parent cancel during partial fill.** Parent enters
   `Cancelling`, engine sends cancel for the live child, on cancel
   ack/fill terminal arrival parent enters `Cancelled` with the
   filled quantity preserved.
5. **TWAP window expired during downtime** (see §4.6).
6. **Drain mode active.** Engine refuses to submit new children
   (same posture as `POST /orders` today: 503 / refuse-then-drain).
   Parents stay in their current state; no auto-cancel of live
   children. `DELETE /algo/{id}` continues to be honoured if the
   gateway is up.

### 4.5 Persistence

Three new event types in `FileEventStore`:

- `AlgoCreatedEvent` — captures the parent params at submit time.
  Authoritative source of truth for everything in §4.1 except the
  derived state.
- `AlgoCancelRequestedEvent` — recorded when `DELETE /algo/{id}`
  reaches the engine (before the child cancels are dispatched).
- `AlgoTerminalStateRecordedEvent` — recorded when the parent
  reaches a terminal state (`Completed`, `Cancelled`, `Expired`,
  `Suspended`). Carries the terminal reason.

All three are pure facts about the *parent*. Slice quantities, fill
progress, child status — those are derived during replay from the
existing `OrderSubmittedEvent` + `ExecutionReportReceivedEvent`
stream by walking children whose `ParentAlgoId` matches.

`StateSnapshotter` gains an `Algos` array mirroring the existing
`Orders` / `Positions` arrays. The snapshot is what makes recovery
fast; the WAL is what makes recovery correct.

`AlgoBook` is the in-memory aggregate, indexed by `AlgoId` and
`Owner`, mirroring `WorkingOrderBook`. Replay walks the algo events
and the order events together and rehydrates both books in one
pass.

### 4.6 TWAP scheduling and recovery

A TWAP parent has parameters `(startTime, endTime, sliceCount,
childOrderType, childPrice?)`. At submit time the engine computes a
**deterministic slice plan**: `sliceSeq ∈ [0, sliceCount)`, each
with `plannedAtUtc` evenly spaced across the window and
`plannedQty` derived by §4.8 rounding rules. The plan is implicit
in the parameters — it is not a separate persisted artefact — so
that recovery can reproduce it exactly.

Recovery semantics, decided here so the engine has a precise rule:

- For each slice in the plan, the engine asks: does a child order
  exist with this `(ParentAlgoId, AlgoSliceSeq)`?
  - **No, and `plannedAtUtc <= now`** → submit the child immediately
    if `now < endTime`; skip and mark as missed if `now >= endTime`.
  - **No, and `plannedAtUtc > now`** → wait until `plannedAtUtc`.
  - **Yes** → child is the source of truth; if it is terminal, that
    slice is done; if it is live, the engine just observes.
- **Catch-up policy.** If `now < endTime` but several slices are
  due, the engine submits them one at a time at engine-tick
  granularity (≥100ms apart), **not** in a single burst. The
  invariant is "no slice burst greater than what TWAP would have
  produced if the host had been up the whole time, plus one." This
  prevents a recovered host from dumping minutes of skipped slices
  into the venue at once.
- **Window fully passed** (`now >= endTime`). Engine transitions
  the parent to `Expired` with reason `TwapWindowExpired`. No new
  children. Already-filled quantity is preserved on the parent.
- **Window passed during downtime *and* a child was live when the
  host went down.** Engine first reconciles the live child via the
  ordinary ER path, then evaluates the parent: if `Completed` (the
  child filled the remainder), record terminal `Completed`; else
  record terminal `Expired`.

The combination of "deterministic plan + idempotent reconciliation"
means TWAP recovery has no separate code path. Recovery is just
"run the engine's normal tick on the rehydrated state."

### 4.7 Engine safety invariants (v0)

These are **engine self-protection**, not a parent-level risk
surface. Algo risk v1 is the place for caps that an operator
configures; these are hard-coded invariants that prevent the engine
itself from amplifying upstream bugs:

1. **One live child per Iceberg parent.** Refill happens only after
   the prior child is terminal.
2. **One in-flight submit per parent at a time.** The per-parent
   semaphore from §4.3 enforces this.
3. **Bounded retry on transient gateway errors.** 3 attempts,
   exponential backoff (100/300/900ms), then `Suspended`. No
   unbounded retry loop.
4. **No catch-up burst** for TWAP (§4.6).
5. **Suspend on first risk rejection.** No engine-level
   skip-and-continue. The operator decides whether to cancel or
   wait.
6. **Drain mode = no new children.** Reuses the existing drain
   signal.
7. **Idempotent slice submission.** Submitting `(ParentAlgoId,
   AlgoSliceSeq)` is a no-op if a child for that pair already
   exists. Protects against double-fire from signal duplication.

### 4.8 Quantity rounding

Iceberg `displayQty` and TWAP `totalQty / sliceCount` rarely divide
evenly. v0 fixes the rule:

- Slices `0..n-2` carry `floor(totalQty / n)` rounded down to the
  configured lot size when one is known (pulled from the slice-6
  fat-finger lot-size table).
- Slice `n-1` carries the remainder so the parent total matches
  exactly.
- If the rounded slice quantity is `0` after lot-rounding, the
  parameters are rejected at `POST /algo` time. The validator
  echoes the implied per-slice quantity in the error body so the
  caller can adjust.

For Iceberg, `displayQty` is treated literally: the same value is
re-used for every refill until the remainder is smaller, in which
case the last child carries the remainder.

### 4.9 HTTP / WS surface

```
POST   /algo             {type, symbol, securityId, side, totalQuantity,
                          parameters: {…type-specific…}}
                         → 202 Accepted {algoId, status: "PendingNew"}
                         → 400 BadRequest {error} on param validation
GET    /algo             → [{algoId, type, symbol, side, totalQuantity,
                            filledQuantity, status, terminalReason?,
                            parameters, createdAt, updatedAt}]
GET    /algo/{algoId}    → same shape; 404 if unknown to the caller
DELETE /algo/{algoId}    → 202 Accepted {algoId, status: "Cancelling"}
                         → 409 Conflict {error} if already terminal
```

Authorization mirrors `/orders`: end-clients see only their own
algos; admin role sees all (for `/admin/algo` follow-up if needed).

WS topic `algo` carries the lifecycle as discrete messages:

```json
{ "topic": "algo", "type": "algoCreated",   "data": { algoId, owner, … } }
{ "topic": "algo", "type": "algoSliceSubmitted",
                     "data": { algoId, sliceSeq, childClOrdId } }
{ "topic": "algo", "type": "algoSliceFilled",
                     "data": { algoId, sliceSeq, fillQty, fillPrice } }
{ "topic": "algo", "type": "algoStatusChanged",
                     "data": { algoId, status, terminalReason? } }
```

`algoSliceFilled` is a *derived* projection broadcast by the engine
when it reacts to a child ER — it is not separately persisted (§4.2).
Subscribers that need the full audit trail still get it from the
`orders` topic, where every child fill is published as today.

### 4.10 Conformance against an unavailable gateway

`UnavailableExchangeGateway` (the default in compose) throws on
every submit; it is therefore impossible to assert "iceberg refills
after a fill" against the live conformance stack as currently shaped.
v0 ships a small piece of test infrastructure to close that gap
**before** the iceberg engine PR (see §5):

- A new gateway mode `Simulator` (alongside `Unavailable`, `Stub`,
  `Mock`) implements `IExchangeGateway` by recording submits and
  exposing an admin-gated injection endpoint:
  `POST /admin/simulator/er` accepts a JSON ER (clOrdId, type,
  cumQty, lastQty, lastPx) and replays it through the same
  `EntryPointExecutionReportRouter` an upstream firm would.
- The endpoint is gated to the `admin` role and refuses to register
  unless `Trading:Exchange:Mode == "Simulator"`. It is not exposed
  in `Mode=Unavailable` deployments — production hosts cannot have
  it accidentally enabled.
- Conformance scenarios that need ER-driven behaviour (slice
  refills, TWAP completion) skip when `Mode != Simulator`, exactly
  like admin scenarios already skip when admin creds are not
  configured.

This makes the iceberg / TWAP engines testable from outside the
host without coupling conformance to in-process internals.

## 5. Roadmap (PRs after this RFC)

Each PR is sequenced, autocontido, build/format/test green,
metrics + tests included. Order matters: the simulator lands before
either engine because it is the only honest way to validate the
engines from conformance.

1. **RFC** (this document) — no code.
2. `Algo` domain + `AlgoBook` + new event types + `Order.ParentAlgoId` /
   `AlgoSliceSeq` field additions + snapshot/WAL plumbing. No engine,
   no API. Adds the persistence shape so later PRs can rely on it.
3. HTTP + WS surface (`POST /algo`, `GET /algo`, `DELETE /algo/{id}`,
   topic `algo`) wired to a no-op engine that just accepts and
   refuses to submit children. Locks the wire contract.
4. **Simulator gateway mode + `POST /admin/simulator/er`.** Reuses
   the existing `IExchangeGateway` extension point. Conformance
   gains a test category that runs only when configured.
5. **Iceberg engine** — first reactive engine. Submits the first
   child, refills on terminal-fill, suspends on terminal-cancel.
6. **TWAP engine** — scheduler + recovery. Largest PR; the
   scheduling model in §4.6 is the contract.
7. **Conformance scenarios + Grafana panel** for algo metrics.

The frontend follow-up is intentionally *not* numbered here. It
lands as its own RFC once the engine is proven, on the same posture
as the trader-UI work that followed each backend phase.

## 6. Open questions

- **OQ-1: Per-firm algo throttle.** Should v0 throttle the *total*
  rate at which an `AlgoEngine` consumer task submits children
  (across all parents), to protect the order hot path under heavy
  algo load? Tentative answer: not in v0 — the per-end-client and
  per-firm rate limits already cover this from the risk side. Add
  in algo risk v1 only if measured contention shows up.
- **OQ-2: Slice price selection for TWAP without explicit
  `childPrice`.** `Market` is straightforward. For `Limit` without
  a price, we either (a) reject at submit time, (b) peg to last
  trade from MD with stale-fallback. Tentative answer: (a) — keep
  v0 explicit, defer pegging to v1 with the rest of the pricing
  strategies.
- **OQ-3: WS replay for the algo topic.** The orders topic supports
  catch-up via `?since=` (cf. WEBSOCKET-PROTOCOL.md). Algo events
  are persisted; a future PR can expose the same `since` semantics
  on the `algo` topic. Tentative answer: ship without `since` in
  v0 and add when the trader UI needs it.
- **OQ-4: Suspended → Resume.** Once a parent is `Suspended`, v0
  has no API to resume it; the operator must cancel and recreate.
  Resume needs to decide what the engine does with already-filled
  quantity, and that is non-trivial (especially for TWAP whose
  window has shifted). Tentative answer: punt to v1.
