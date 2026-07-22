# RFC: Risk pipeline ordering — pre-WAL vs post-WAL evaluation

| Field    | Value                                                                                       |
| -------- | ------------------------------------------------------------------------------------------- |
| Status   | **Partially implemented** ([#337](https://github.com/pedrosakuma/B3TradingPlatform/pull/337) closed the WAL/history/replay auditability gap for rejected modifies; drop-copy/CVM visibility remains open under [#429](https://github.com/pedrosakuma/B3TradingPlatform/issues/429)) |
| Tracking | [#262](https://github.com/pedrosakuma/B3TradingPlatform/issues/262)                         |
| Related  | [#261](https://github.com/pedrosakuma/B3TradingPlatform/issues/261) (gpt-5.5 review surfaced this), [#337](https://github.com/pedrosakuma/B3TradingPlatform/pull/337) (WAL/history/replay auditability fix), [#429](https://github.com/pedrosakuma/B3TradingPlatform/issues/429) (still-open drop-copy/CVM visibility gap for rejected modifies) |
| Replaces | n/a — refines the v1 ordering from `pre-trade-risk-v2`                                     |

## 1. Context

Today the submit and modify pipelines disagree on where `RiskPipeline.Evaluate`
runs relative to the WAL append. The original `pre-trade-risk-v2` RFC stayed
neutral; the asymmetry crept in as later sub-issues were merged independently.

### 1.1 Current behaviour — submit (`OrderSubmissionService.SubmitAsync`)

```
parse → BadRequest gates
      → ClOrdIdPrefixRegistry.Generate(owner)          # ID burned
      → WorkingOrderBook.TryGet pre-flight             # dup guard
      → new Order(...)                                 # cross-field invariants
      → _dispatcher.Dispatch(OrderSubmittedEvent)      # WAL APPEND
      → _risk.Evaluate(ctx)                            # ← post-WAL
      → _margin.TryReserveAsync                         # post-WAL
      → on reject: PublishSyntheticRejection
        (= second WAL row: ExecutionReportReceivedEvent { Synthetic = true, ExecKind = Rejected })
      → _gateway.SubmitAsync
```

Source of truth: `backend/src/B3.Trading.Application/OrderSubmissionService.cs:110–283`.

### 1.2 Current behaviour — modify (`OrderModifyService.ModifyAsync`)

```
parse → BadRequest gates
      → ClOrdIdPrefixRegistry.Generate(owner)          # newClOrdId burned
      → _risk.Evaluate(ctx)                            # ← pre-WAL
      → _replaceMargin.PrepareReplaceAsync             # pre-WAL
      → _dispatcher.Dispatch(OrderReplaceRequestedEvent)  # WAL APPEND (only if approved)
      → _gateway.CancelReplaceAsync
```

Source of truth: `backend/src/B3.Trading.Application/OrderModifyService.cs:167–303`.

### 1.3 The asymmetry — concretely

| Aspect                                | Submit (post-WAL)              | Modify (pre-WAL) — **post-#337**                       |
| ------------------------------------- | ------------------------------ | ----------------------------------------------------- |
| ClOrdID burn on risk reject           | Yes                            | Yes (newClOrdId)                                      |
| WAL rows on risk reject               | 2 (`Submitted` + synthetic `Rejected`) | 1 (`OrderReplaceRejectedEvent`)               |
| Observable in `/api/executions/history`   | Yes (`Rejected`, `Synthetic`)   | Yes (via `OrderReplaceRejectedEvent`, `HistoryEndpoints.cs:455`) |
| Observable in `/api/orders/history`       | Yes (terminal `Rejected`)       | N/A — original order keeps pre-modify state           |
| FE rendering                           | Renders STP-local + risk reason | Renders via `ExecutionEvent { Kind=Rejected }` published in same dispatch callback (`OrderModifyService.cs:410-421`) |
| WAL replay reconstructs the reject?   | Yes                            | Yes (replay is a no-op for book/ownership/margin; advances ClOrdId watermark — `StateSnapshotter.cs:1007`) |
| Counter `OrdersRejectedByRisk`        | Bumped with `firmId` tag        | Bumped with `firmId` + `path:"modify"` tag           |

[#337](https://github.com/pedrosakuma/B3TradingPlatform/pull/337) closed the
WAL/history/replay slice of the asymmetry: risk-rejected and margin-rejected
modifies now dispatch `OrderReplaceRejectedEvent` to the WAL and emit a live
`ExecutionEvent` in the same commit callback, which keeps
`/api/executions/history`, FE executions rendering, and the ClOrdId replay
watermark aligned. Coverage:
`backend/tests/B3.Trading.Application.Tests/OrderReplaceRejectedEventTests.cs`.

That did **not** fully close the broader compliance/distribution gap. The live
event is published with the burned `newClOrdId`, so drop-copy still filters it
out, and `CvmReportSource` still only maps fill ERs. TODO([#429](https://github.com/pedrosakuma/B3TradingPlatform/issues/429)):
wire rejected-modify visibility through the drop-copy and CVM-report
consumers separately from the WAL/history/replay fix.

The remaining asymmetry — number of WAL rows (2 vs 1) and the source enum
(`Synthetic ER` vs `OrderReplaceRejected`) — is intentional. The submit-path
synthetic ER reuses the ER replay path; the modify-path uses a dedicated event
because no `OrderReplaceRequestedEvent` was ever appended, so a synthetic
ExecutionReport would be referencing a ClOrdId the WAL has no other trace of.

### 1.4 Risk-check inventory

All registered checks (`backend/src/B3.Trading.Application/Risk/Checks/*.cs`) are pure
`(RiskContext) → RiskDecision` lookups against `IOptions<RiskOptions>` /
`WorkingOrderBook` / `MarketDataCache` / `PositionKeeper` / `KillSwitchState`:

| Check                   | Order | Reads                                          | Pure? | Cost |
| ----------------------- | ----- | ---------------------------------------------- | ----- | ---- |
| `KillSwitchCheck`       | 0     | KillSwitchState                                | ✔     | O(1) |
| `SymbolHaltedCheck`     | 10    | `HaltedSymbols`                                | ✔     | O(1) |
| `SessionPhaseCheck`     | 12    | PhaseStore                                     | ✔     | O(1) |
| `MinTickSizeCheck`      | 50    | Tick ladder                                    | ✔     | O(1) |
| `MaxQuantityCheck`      | 100   | RiskOptions                                    | ✔     | O(1) |
| `RollingNotionalCheck`  | 150   | Per-(firm, owner) ring buffer                  | ✔     | O(1) |
| `MaxOpenOrdersCheck`    | 170   | WorkingOrderBook count                         | ✔     | O(1) |
| `SubAccountLimitsCheck` | 175   | Per-sub-account caps                           | ✔     | O(1) |
| `NoNakedShortCheck`     | 180   | PositionKeeper                                 | ✔     | O(1) |
| `SelfTradePreventionCheck` | 190 | WorkingOrderBook lookup by (firm, owner, symbol) | ✔   | O(open orders) |
| `PositionLimitCheck`    | 200   | PositionKeeper                                 | ✔     | O(1) |
| `StaleReferencePriceCheck` | 295 | MarketDataCache freshness                      | ✔     | O(1) |
| `PriceCollarCheck`      | 300   | MarketDataCache + RiskOptions                  | ✔     | O(1) |
| `StopTriggerCheck`      | 305   | MarketDataCache                                | ✔     | O(1) |

**Every check is pure + cheap.** None has a side effect; none takes a lock
that is held across an awaited call. There is no technical reason any one of
them couldn't run pre-WAL.

`_margin.TryReserveAsync` (`OrderSubmissionService.cs:241`) and
`_replaceMargin.PrepareReplaceAsync` (`OrderModifyService.cs:250`) are the
only **stateful** gates — they reserve / prepare against an in-memory ledger.
They're already idempotent on the abort path
(`_margin.ReleaseReservation`, `_replaceMargin.AbortReplace`).

## 2. Trade-offs

### 2.1 What "post-WAL + synthetic Rejected" buys us

- **`Rejected is an event` invariant.** Every order touch is recoverable from
  the WAL: submit (Submitted), accept (NewAccepted ER), partial fill (Fill
  ER), cancel (Canceled ER), reject (synthetic Rejected ER). EventReplayer
  rebuilds the same `WorkingOrderBook` + `OrderOwnershipMap` + history
  endpoints would have shown live.
- **Single read model.** Anything that subscribes to `IExecutionEventSink` /
  `IEventStore.ReadFromAsync` (FE WebSocket fan-out, /api/executions/history,
  CVM 35/505 report (#308), best-exec touch snapshot (#307), drop-copy feed
  (#306)) sees the rejection automatically. No special-case "and also fetch
  the 4xx response" path.
- **Compliance.** A rejected order is a regulatory event — operators expect
  to see it in audit. Compliance role + CVM exports rely on WAL-level
  completeness.

### 2.2 What it costs

- **ClOrdID burn.** A rejected submit consumes one `ClOrdIdPrefixRegistry`
  slot. Slots are 64-bit and per-end-client; we'd run out around year 2.9e11
  AD at 1M rejected orders/day. **Non-issue.**
- **Two WAL rows per reject.** Submitted (≈ 200 B) + synthetic Rejected (≈
  150 B) ≈ 350 B/reject. At a busy desk's 50 reject/min that's ≈ 25 MB/year.
  Persistence already manages rotation. **Non-issue.**
- **Latency.** The WAL append is `_dispatcher.Dispatch`, currently
  channel-backed (`WalEventChannel`, capacity 4096 by default). Under
  backpressure a rejected submit pays the same `WalBackpressureException`
  path a healthy submit pays — already mapped to HTTP 503. **Currently fine
  but coupled to channel capacity.**
- **Reasoning.** Submitted+Synthetic-Rejected pair is non-trivial to
  understand at the EventReplayer level — readers must know that a
  Submitted followed by a Synthetic Rejected at the next seq is "the order
  never existed for the venue".

### 2.3 What "pre-WAL + drop-or-record" buys us

- **No ClOrdID burn / WAL row** on a synchronously rejected order.
- **Symmetry** with the modify path (which already does this).
- **Slightly less I/O** under fat-finger storms.

### 2.4 What it costs

- **Loses the WAL audit trail unless a new `Rejected*Event` shape is added.**
  Today a rejected submit lives in the WAL; deleting that means /api/executions/history,
  CVM exports, drop-copy, and the FE executions log all stop seeing risk
  rejects.
- **API contract shift.** If rejected modifies stay invisible (status quo)
  but rejected submits become invisible (new), the FE's "show me what
  happened to my order" surface fragments per operation type.
- **Schema migration.** A new `OrderRejectedEvent` / `OrderReplaceRejectedEvent`
  to preserve the audit trail without burning IDs requires WAL schema
  evolution + replay compat + history projector changes + FE column wiring
  + CVM mapping. Non-trivial — easily another sub-issue per concern.

## 3. Options

### Option A — Status quo, document the asymmetry (recommended **short-term**)

- No code change.
- Add a code comment in `OrderModifyService.ModifyAsync` and
  `OrderSubmissionService.SubmitAsync` linking to this RFC and explaining the
  divergence so future readers don't "fix" one side and create a regression.
- Track the remaining modify-side **distribution gap** as a separate sub-issue
  under [#429](https://github.com/pedrosakuma/B3TradingPlatform/issues/429):
  rejected modifies now have WAL/history/replay coverage, but drop-copy and
  CVM consumers still miss them because the event is keyed by the burned
  `newClOrdId` and CVM only maps fills today.

**Pros:** zero risk, addresses the immediate confusion the gpt-5.5 review
flagged.<br/>
**Cons:** does not unify the pipeline; the modify-side drop-copy/CVM visibility
gap remains.

### Option B — Make modify match submit (post-WAL evaluation everywhere)

- Move `_risk.Evaluate` and `_replaceMargin.PrepareReplaceAsync` in modify to
  **after** `_dispatcher.Dispatch(OrderReplaceRequestedEvent)`.
- On reject, emit `OrderReplaceRejectedEvent` (new shape) + a synthetic ER
  with `OrigClOrdId` populated.
- Burns one newClOrdId per rejected modify, but the audit row is now in
  the WAL. EventReplayer needs to know that a Requested followed by
  RequestedRejected at the next seq is a no-op intent.

**Pros:** unifies on "rejected is an event"; preserves the WAL/history/replay
coverage from [#337](https://github.com/pedrosakuma/B3TradingPlatform/pull/337)
and creates a clearer place to hook drop-copy/CVM visibility.<br/>
**Cons:** more WAL rows under heavy reject volume; modest schema +
EventReplayer change.

### Option C — Make submit match modify (pre-WAL evaluation everywhere)

- Move `_risk.Evaluate` + `_margin.TryReserveAsync` in submit to **before**
  `_dispatcher.Dispatch`.
- Drop the `PublishSyntheticRejection` plumbing — or replace it with a
  no-burn `OrderRejectedEvent` shape that records the request without
  allocating a ClOrdID.

**Pros:** no ID burn, no WAL pressure on rejects.<br/>
**Cons:** loses the FE/audit/CVM surface for rejected orders unless we
introduce `OrderRejectedEvent`. Either way it's a schema + projector +
front-end migration spanning several sub-issues. Highest disruption for the
smallest concrete benefit (we're not ID-starved; we're not WAL-saturated).

### Option D — Split the pipeline (RFC §1 option 2)

- Run pure / synchronous checks (kill-switch, halted, phase, tick size, max
  qty) **pre-WAL**.
- Run state-dependent / margin checks (positions, collar, margin) **post-WAL**.
- Combines a fast deny-list for fat-finger guards with the audit-preserving
  reject path for the rest.

**Pros:** marginal latency win on the fat-finger reject path; audit kept for
the state-dependent rejects.<br/>
**Cons:** splits the pipeline into two phases with different semantics;
operators reading the WAL would need to know "kill-switch reject doesn't
appear here". The categorization (which check is "cheap") is also
ambiguous — every check in §1.4 is O(1) lookups, so there's no real cost
inflection point to anchor the split.

## 4. Recommendation

**Adopt Option A now**, and keep [#429](https://github.com/pedrosakuma/B3TradingPlatform/issues/429)
open as the follow-up for the remaining drop-copy/CVM visibility work.

Rationale:

- The "rejected is an event" invariant on submit has paid for itself
  repeatedly — FE executions log, CVM 35/505 reporting (#308), the
  drop-copy feed (#306), and the best-exec touch snapshot (#307) all
  consume rejection rows uniformly. We don't want to weaken it.
- The 64-bit ClOrdID space and the WAL's compaction/rotation make the
  "burn" and "extra row" costs negligible at any plausible reject volume.
- The remaining modify-side drop-copy/CVM visibility gap is a real bug, but
  [#337](https://github.com/pedrosakuma/B3TradingPlatform/pull/337) already
  proved that preserving a WAL event is strictly better than deleting the
  submit-side audit row (Option C / Option D's hybrid).
- No live consumer is asking for pre-WAL synchronous gating today; the
  request originated as a design-review note, not an incident.

## 5. Decision (pending sign-off)

> Decision: keep the current ordering on both submit and modify. Annotate the
> two services with cross-references to this RFC. Keep [#429](https://github.com/pedrosakuma/B3TradingPlatform/issues/429)
> open for the still-missing drop-copy / CVM-report visibility of rejected
> modifies without touching the submit path's invariants.

**Status update (#337 — WAL/history/replay gap closed, distribution gap still
open):** the modify pipeline now dispatches an `OrderReplaceRejectedEvent` WAL
row on both the risk-reject and the margin-reject branches (with
`Source="risk"` / `Source="margin"`) and publishes a synthetic
`ExecKind.Rejected` `ExecutionEvent` to the live sink for the FE blotter.
Replay treats the event as audit-only (advances the ClOrdId watermark; no
book/ownership/margin mutation), and `/api/executions/history` projects the row
with `Kind="Rejected"`. That closes the "no WAL row / no
`/api/executions/history` row / no replay watermark advance" part of the old
modify-side asymmetry. It does **not** put rejected modifies on parity with
submit for downstream consumers: drop-copy still drops the event because it
cannot resolve the burned `newClOrdId`, and CVM reporting still ignores it
because `CvmReportSource` only emits fills today.

## 6. Out of scope

- Reworking `IRiskCheck` to be async — no current check needs I/O.
- Persisting `RiskContext` snapshots — not actionable until a check needs to
  reference historical decisions.
- Pre-WAL deny-list for malformed-shape requests (already handled by the
  `BadRequest` gates that run before `Generate`; this is not what #262 is
  about).
