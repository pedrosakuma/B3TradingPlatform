# Cancel-as-Replace path audit (issue #430)

**Status:** Audited 2026-05-24 — no live correctness gaps. Defensive chaos test added in
`backend/tests/B3.Trading.Application.Tests/CancelAsReplaceChaosTests.cs`.

**Related:** #241 (root bug fix, PR #242), #247/#248 (margin commit drop, PR #248),
#122 slice 1 (`RegisterReplaceLink`), #122 slice 2 (`PendingReplacementRegistry`),
#16 (idempotent ER), #275 pass-4 (`OrdersHistory_CancelAsReplace_*`), #417 (wire-faithful
bot routing), #299 P1 (cum/leaves baseline hydration), #255 (GTD on replace terminal).

## 1. Problem domain

B3MatchingPlatform implements `OrderCancelReplaceRequest` (FIX 35=G, FIXP equivalent) via
**two distinct venue paths**:

| Venue path | Trigger | Wire ER shape | Processor branch |
|---|---|---|---|
| `priority-kept` | Same price + qty ≤ current | One ER with `ExecType=Replaced`, `OrigClOrdID=orig` | Line 206-212 (`Replaced` + intent) |
| `priority-lost` | Any other change (incl. price-cross, upsize) | `ER_Cancel(new, orig)` + `ER_Trade(new, 0)` and/or `ER_New(new)` — **never** `Replaced` | Line 224-237 (`Canceled` + intent) — issue #241 |

A third venue outcome — replace rejected by the matching engine — comes in as
`ER_Reject(new)` and is handled by line 199-205 (`Rejected` + intent → `ApplyReplaceRejected`).

The shared invariant: the `PendingReplacementRegistry` keyed by `newClOrdId` is the
**single source of truth** that disambiguates "this Cancel/Reject/Replaced is the venue's
answer to my modify" from "this is a standalone cancel/reject". `TryConsume` is one-shot,
so each replace flow resolves exactly once.

## 2. Caminhos cobertos (and where)

### 2.1 Origin (modify request)

`OrderModifyService.ModifyAsync`
(`backend/src/B3.Trading.Application/OrderModifyService.cs`):

1. Allocates `newClOrdId` via `ClOrdIdPrefixRegistry`.
2. `_ownership.RegisterReplaceLink(origId, newClOrdId)` — slice 1 of #122. Enables
   `TryResolve(newClOrdId)` even before any ER lands. Survives gateway clock skew.
3. Risk pipeline + margin `PrepareReplaceAsync` (line 219-274, post-#337 also publishes
   `OrderReplaceRejectedEvent` to the WAL on reject — see
   `docs/rfcs/risk-pipeline-ordering-v0.md`).
4. `_replacements.TryAdd(intent)` registers the `OrderReplacementIntent`.
5. `_gateway.CancelReplaceAsync(...)`.

### 2.2 Venue ER ingress

`ExecutionReportProcessor.Apply`
(`backend/src/B3.Trading.Application/ExecutionReportProcessor.cs:Apply`):

| Order of events | Resulting behaviour |
|---|---|
| **priority-kept**: `Replaced(new, orig)` | `ApplyReplaceAccepted` (line 210). `MarkReplaced(orig)`, `HydrateReplacement(new)` from `intent.NewPrice/NewQuantity`, `CommitReplace`. Subsequent fills land on `new`. |
| **priority-lost happy**: `Canceled(new, orig)` → `Fill(new, orig=0)` | First event takes the cancel-as-replace branch (line 224-237) and funnels through `ApplyReplaceAccepted` with `erLeaves=intent.NewQuantity, erCum=0`. The new order is hydrated in `WorkingOrderBook`. Second event finds the new order present, books the fill. |
| **Replace rejected by venue**: `Rejected(new)` | `ApplyReplaceRejected` (line 203). `AbortReplace(new)`, `MarkRejected(intent.NewClOrdId)` placeholder routed to bot, original stays Working. Tests: `OrderModifyMarginAndProcessorTests.Processor_Replaced_branch_rejectedFlow*`. |
| **Replay / audit-trail Cancel using original linkage** | `_replacements.TryConsume` is one-shot, but `ExecutionReportProcessor` still resolves any later `Canceled(new, orig)` through `lookupId = orig` (line 252-254). That shape therefore re-targets the ORIGINAL order and does **not** prove the hydrated replacement was cancelled. Documented by `Processor_CancelAsReplace_secondCancelReplay_isIdempotentNoOp`, which only asserts no extra commit/abort and no state regression. |
| **Real cancel after Cancel-as-Replace** | A real cancel of the currently-working replacement uses a **fresh cancel-side ClOrdID** linked to the replacement order (for example `Canceled(cancelId, orig=0)` with `cancelId → new` from `OrderCancelService`). That resolves through the cancel-link map to `lookupId = new`, so the standard cancel branch marks the hydrated replacement `Cancelled`. Covered by `OrdersHistory_CancelAsReplaceThenRealCancel_NewIsCancelled` and `CancelAsReplaceChaosTests.Scenario.PriorityLostHappy_FollowedByRealCancel`. |
| **Original partially filled BEFORE Cancel-as-Replace** | `MarkReplaced` preserves `Filled` terminal (line 955-958); fills booked on original are not re-booked on new (PositionKeeper already saw them). The **#299 P1 cum-stale clamp** (line 1020-1029) detects venue-issued `erCum=0` against a non-zero original cum and rewrites `seedCum := origOrder.CumulativeQuantity`, `seedLeaves := intent.NewQuantity - seedCum`. Subsequent Fill ER under the new ClOrdID carries the full cumulative post-fill total (=NewQuantity), so the delta booked into PositionKeeper is exactly `qty - preFill`. Total NetQuantity = preFill + (qty - preFill) = qty (the user's target). Tests: `OrdersHistory_CancelAsReplaceAfterOriginalFilled_OriginalStaysFilled`, `OrdersHistory_CancelAsReplaceWithPartiallyFilledOriginal_ReplacementCumResetsToZero`, `CancelAsReplaceChaosTests.Scenario.OriginalPartiallyFilled_ThenCancelAsReplace`. |
| **ER firm mismatch** | Cross-firm replace guard runs BEFORE the intercept blocks (line 145-191, PR #317 P1). `TryConsume` is NOT invoked when firm mismatches, so a malicious cross-firm Cancel can't drain another firm's pending intent. Test: `CancelAsReplace_withMismatchingFirm_doesNotConsumeIntent_orMutateOriginal`. |

### 2.3 Known limitation (documented, out-of-scope)

**Trade-before-Cancel ordering (FIXP out-of-order delivery)**. If the venue's
`ER_Trade(new, 0)` arrives before `ER_Cancel(new, orig)`, the Trade hits the
`TryGet(new) → false` branch (line 266-277) and increments the
`trading.execution_reports.dropped_known_owner_missing_order` metric with
`kind=Fill` tag. The fix would be a small per-ClOrdID reorder buffer with TTL;
not delivered today and not in scope for this audit. Track separately if observed
in production via the metric.

## 3. Resolution / cleanup paths

Every successful Cancel-as-Replace must call exactly one of `CommitReplace` or
`AbortReplace` on the `IReplaceMarginCoordinator`. The audit checked:

| Cleanup callsite | Calls `Commit` | Calls `Abort` |
|---|---|---|
| `ApplyReplaceAccepted` (Replaced or Cancel-as-Replace happy path) | ✅ line 1071 (after hydrate) | — |
| `ApplyReplaceAccepted` (orig not in book — defensive) | — | ✅ line 947 |
| `ApplyReplaceRejected` (replace rejected by venue) | — | ✅ line 893 |
| `OrderModifyService` (risk reject pre-WAL, margin reject) | — | ✅ via `AbortReplace` in finally |

**Invariant**: for any `intent` added to the registry, exactly one of these branches
fires (registry `TryConsume` is one-shot; intent leak would only happen if all four
miss). The chaos test asserts `Commits + Aborts == intents` per scenario.

## 4. Chaos test design

`backend/tests/B3.Trading.Application.Tests/CancelAsReplaceChaosTests.cs` runs 1000
deterministic iterations (`seed = i`) of a randomly-selected scenario from a closed
set of 6 venue behaviours (happy, replay, real-cancel-after, replace-reject,
priority-kept, partial-fill-then-cancel-as-replace). After each iteration it asserts:

1. **Margin coordinator balance**: `Commits.Count + Aborts.Count == 1` per intent.
   No leak, no double-resolve.
2. **WorkingOrderBook coherence**: for every order present, `LeavesQuantity +
   CumulativeQuantity == OriginalQuantity` and status is consistent
   (`Filled` ⇒ leaves=0; `Cancelled`/`Replaced`/`Rejected` are terminal).
3. **PositionKeeper balance**: `pos.NetQuantity == Σ(fills booked under this owner +
   symbol)`. Verifies no fill lost or double-booked across the replace boundary.
4. **OrderOwnershipMap**: every consumed ClOrdID resolves to its owner.

The intent is **regression net**: any future refactor that breaks one of the six
flows will trip an invariant deterministically (seed reproducible from the
failing iteration index).

## 5. Open follow-ups (recommend NOT in scope of #430)

- Out-of-order Trade-before-Cancel reorder buffer (see §2.3). Wait for production
  signal on the existing metric before investing.
- Concurrent multi-threaded chaos. `ExecutionReportProcessor.Apply` is documented
  as called serially from the gateway ingress pipeline; multi-thread chaos would
  require redesigning the harness around an explicit serializer and risks testing
  the harness rather than the code under test.
